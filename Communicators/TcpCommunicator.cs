using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpSerialComm.Common;

namespace TcpSerialComm.Communicators
{
    /// <summary>
    /// 工业级 TCP 读写类。
    /// 打开 / 关闭 / 读 / 写 / 断线检测 / 自动重连 / 心跳看门狗 / 组帧防黏包。
    /// 设计要点（来自最佳实践调研）：
    ///  1) 不依赖 TcpClient.Connected 缓存值判断存活，改用读失败 + 心跳看门狗检测；
    ///  2) 启用 TCP KeepAlive 作为系统级辅助（默认 2 小时太长，配置为短周期）；
    ///  3) 全程 async/await + CancellationToken，读循环不卡 UI；
    ///  4) 写加 SemaphoreSlim 锁 + 最小写间隔，防止数据包黏连（PV 操作 + 间隔）；
    ///  5) 指数退避自动重连，不空转打满 CPU；
    ///  6) 收包用 FrameBuilder 按分隔符/长度头组帧，解决黏包/拆包；
    ///  7) 事件经 SynchronizationContext 回到 UI 线程，类本身不依赖 WinForms/WPF，可直接迁移。
    /// </summary>
    public sealed class TcpCommunicator : ICommunicator
    {
        private readonly TcpCommunicatorConfig _cfg;
        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _masterCts;
        private CancellationTokenSource _reconnectCts;
        private Task _readTask;
        private Task _reconnectTask;
        private readonly System.Timers.Timer _heartbeatTimer;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new object();
        private ConnectionState _state = ConnectionState.Disconnected;
        private DateTime _lastReceiveTime = DateTime.MinValue;
        private DateTime _lastWriteTime = DateTime.MinValue;
        private int _reconnectAttempts;
        private volatile bool _reconnecting;
        private bool _closing;
        private bool _disposed;
        private SynchronizationContext _syncContext;
        private readonly FrameBuilder _frameBuilder;

        public TcpCommunicatorConfig Config => _cfg;
        public ConnectionState State { get { lock (_stateLock) return _state; } }
        public bool IsConnected => State == ConnectionState.Connected;

        public event EventHandler<ConnectionStateChangedEventArgs> StateChanged;
        public event EventHandler<DataReceivedEventArgs> DataReceived;
        public event EventHandler<CommunicatorErrorEventArgs> Error;

        /// <summary>设置后，事件会 Post 回该上下文（如 UI 线程），调用方无需手动 Invoke。</summary>
        public SynchronizationContext SyncContext { get => _syncContext; set => _syncContext = value; }

        public TcpCommunicator(TcpCommunicatorConfig config)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            Guard.ArgumentNotNullOrEmpty(_cfg.Host, nameof(_cfg.Host));
            _frameBuilder = new FrameBuilder(_cfg.Framing, _cfg.FrameDelimiter);
            _heartbeatTimer = new System.Timers.Timer { AutoReset = true };
            _heartbeatTimer.Elapsed += HeartbeatTick;
        }

        private bool IsActive => State == ConnectionState.Connecting || State == ConnectionState.Connected || State == ConnectionState.Reconnecting;

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpCommunicator));
        }

        private void Post(Action action)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => { try { action(); } catch { } }, null);
            else
            {
                try { action(); } catch { }
            }
        }

        private void SetState(ConnectionState newState, string msg = null)
        {
            ConnectionState old;
            lock (_stateLock)
            {
                old = _state;
                if (old == newState) return;
                _state = newState;
            }
            var e = new ConnectionStateChangedEventArgs(old, newState, msg);
            Post(() => StateChanged?.Invoke(this, e));
        }

        private void CleanupConnectionObjects()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            try { _client?.Dispose(); } catch { }
            _stream = null;
            _client = null;
        }

        private async Task<bool> TryConnectAsync(CancellationToken ct)
        {
            CleanupConnectionObjects();
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                client.NoDelay = _cfg.NoDelay;
                if (_cfg.ReceiveBufferSize > 0) client.ReceiveBufferSize = _cfg.ReceiveBufferSize;
                if (_cfg.SendBufferSize > 0) client.SendBufferSize = _cfg.SendBufferSize;

                using var timeoutCts = new CancellationTokenSource(_cfg.ConnectTimeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                await client.ConnectAsync(_cfg.Host, _cfg.Port, linked.Token).ConfigureAwait(false);

                if (_cfg.KeepAlive)
                {
                    try
                    {
                        var sock = client.Client;
                        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, _cfg.KeepAliveTimeSec);
                        sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, _cfg.KeepAliveIntervalSec);
                        sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, _cfg.KeepAliveRetryCount);
                    }
                    catch (Exception ex)
                    {
                        Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "KeepAlive")));
                    }
                }

                _client = client;
                _stream = client.GetStream();
                _frameBuilder.Reset();
                _lastReceiveTime = DateTime.UtcNow;
                _lastWriteTime = DateTime.MinValue;
                return true;
            }
            catch (Exception ex)
            {
                try { client?.Close(); } catch { }
                try { client?.Dispose(); } catch { }
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "Connect")));
                CleanupConnectionObjects();
                return false;
            }
        }

        private void StartReadLoop()
        {
            _masterCts = new CancellationTokenSource();
            var token = _masterCts.Token;
            _readTask = Task.Run(async () => { await ReadLoopAsync(token).ConfigureAwait(false); }, token);
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buf = new byte[_cfg.ReceiveBufferSize];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = await _stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    if (n == 0) // 远端优雅关闭
                    {
                        Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(new Exception("远端关闭连接（读到 0 字节）"), "Read")));
                        break;
                    }
                    _lastReceiveTime = DateTime.UtcNow;
                    var frames = _frameBuilder.Push(buf, n);
                    foreach (var f in frames)
                    {
                        var data = f;
                        Post(() => DataReceived?.Invoke(this, new DataReceivedEventArgs(data)));
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常退出 */ }
            catch (Exception ex)
            {
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "Read")));
            }

            if (!ct.IsCancellationRequested && !_closing && _cfg.AutoReconnect)
                BeginReconnectLoop();
            else if (!ct.IsCancellationRequested && !_closing)
                SetState(ConnectionState.Disconnected, "读循环结束");
        }

        private void StartHeartbeat()
        {
            if (!_cfg.HeartbeatEnabled) return;
            if (_heartbeatTimer.Interval != _cfg.HeartbeatIntervalMs)
                _heartbeatTimer.Interval = _cfg.HeartbeatIntervalMs;
            if (!_heartbeatTimer.Enabled) _heartbeatTimer.Start();
        }

        private void HeartbeatTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed || _closing || _state != ConnectionState.Connected) return;
            if (_cfg.HeartbeatEnabled)
            {
                var silent = (DateTime.UtcNow - _lastReceiveTime).TotalMilliseconds;
                if (silent > _cfg.HeartbeatSilenceTimeoutMs)
                {
                    Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(
                        new TimeoutException($"心跳看门狗：静默 {silent:0}ms 超过阈值 {_cfg.HeartbeatSilenceTimeoutMs}ms"), "Heartbeat")));
                    ForceDisconnectForReconnect();
                    return;
                }
                _ = Task.Run(async () => { try { await WriteAsync(_cfg.HeartbeatRequest).ConfigureAwait(false); } catch { } });
            }
        }

        private void ForceDisconnectForReconnect()
        {
            try { _stream?.Dispose(); } catch { }
            // 读循环会因 stream 关闭而抛异常并触发重连；此处不取消 _masterCts
        }

        private void BeginReconnectLoop()
        {
            if (_disposed || _closing) return;
            if (_reconnecting) return;
            if (_state == ConnectionState.Connected) return;
            _reconnecting = true;
            SetState(ConnectionState.Reconnecting, "开始自动重连");
            try { _reconnectCts?.Cancel(); } catch { }
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;
            _reconnectTask = Task.Run(async () => { await ReconnectLoopAsync(token).ConfigureAwait(false); }, token);
        }

        private async Task ReconnectLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && !_disposed)
                {
                    if (_cfg.MaxReconnectAttempts > 0 && _reconnectAttempts >= _cfg.MaxReconnectAttempts)
                    {
                        SetState(ConnectionState.Disconnected, $"已达最大重连次数 {_cfg.MaxReconnectAttempts}");
                        return;
                    }
                    int delay = CalculateBackoff(_reconnectAttempts);
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    bool ok = await TryConnectAsync(ct).ConfigureAwait(false);
                    if (ok)
                    {
                        _reconnectAttempts = 0;
                        StartReadLoop();
                        StartHeartbeat();
                        SetState(ConnectionState.Connected, "重连成功");
                        return;
                    }
                    _reconnectAttempts++;
                    SetState(ConnectionState.Reconnecting, $"重连失败（第{_reconnectAttempts}次，{delay}ms 后重试）");
                }
            }
            finally
            {
                _reconnecting = false;
            }
        }

        private int CalculateBackoff(int attempt)
        {
            long delay = (long)_cfg.ReconnectBaseDelayMs * (1L << Math.Min(attempt, 16));
            if (delay > _cfg.ReconnectMaxDelayMs) delay = _cfg.ReconnectMaxDelayMs;
            int jitter = _cfg.ReconnectBaseDelayMs > 0 ? new Random().Next(0, _cfg.ReconnectBaseDelayMs) : 0;
            return (int)Math.Min(delay + jitter, int.MaxValue);
        }

        public async Task OpenAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            // 安全约定3：硬件不允许二次连接
            if (IsActive) throw new InvalidOperationException("禁止二次连接：设备已连接或正在连接/重连中。");
            _closing = false;
            SetState(ConnectionState.Connecting);
            bool ok = await TryConnectAsync(ct).ConfigureAwait(false);
            if (ok)
            {
                _reconnectAttempts = 0;
                StartReadLoop();
                StartHeartbeat();
                SetState(ConnectionState.Connected);
            }
            else if (_cfg.AutoReconnect)
            {
                BeginReconnectLoop();
            }
            else
            {
                SetState(ConnectionState.Disconnected, "连接失败");
                throw new InvalidOperationException($"TCP 连接 {_cfg.Host}:{_cfg.Port} 失败，且未启用自动重连。");
            }
        }

        public async Task CloseAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_state == ConnectionState.Disconnected || _state == ConnectionState.Disconnecting) return;
            _closing = true;
            SetState(ConnectionState.Disconnecting);
            try
            {
                _heartbeatTimer.Stop();
                try { _reconnectCts?.Cancel(); } catch { }
                try { _masterCts?.Cancel(); } catch { }
                if (_readTask != null) { try { await _readTask.ConfigureAwait(false); } catch { } }
                if (_reconnectTask != null) { try { await _reconnectTask.ConfigureAwait(false); } catch { } }
            }
            finally
            {
                CleanupConnectionObjects();
                _closing = false;
                SetState(ConnectionState.Disconnected, "已关闭");
            }
        }

        public async Task<bool> WriteAsync(byte[] data, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (data == null || data.Length == 0) return false;
            // 安全约定4：写入前做连通性测试
            if (_state != ConnectionState.Connected)
            {
                if (_cfg.AutoReconnect) BeginReconnectLoop();
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(
                    new InvalidOperationException("未处于已连接状态，无法写入。"), "Write")));
                return false;
            }

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 行协议：发送时在负载末尾自动追加分隔符（回车换行等）
                byte[] payload = data;
                if (_cfg.AppendDelimiterOnWrite && _cfg.Framing == FramingMode.Delimiter
                    && _cfg.FrameDelimiter != null && _cfg.FrameDelimiter.Length > 0)
                {
                    payload = new byte[data.Length + _cfg.FrameDelimiter.Length];
                    Array.Copy(data, 0, payload, 0, data.Length);
                    Array.Copy(_cfg.FrameDelimiter, 0, payload, data.Length, _cfg.FrameDelimiter.Length);
                }

                // 最小写间隔（PV 操作 + 间隔），防止数据包黏连
                var since = (DateTime.UtcNow - _lastWriteTime).TotalMilliseconds;
                if (since < _cfg.WriteMinIntervalMs)
                    await Task.Delay((int)(_cfg.WriteMinIntervalMs - since), ct).ConfigureAwait(false);

                for (int attempt = 1; attempt <= _cfg.WriteRetryCount; attempt++)
                {
                    try
                    {
                        if (_state != ConnectionState.Connected || _stream == null)
                            throw new InvalidOperationException("连接已断开");
                        await _stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                        await _stream.FlushAsync(ct).ConfigureAwait(false);
                        _lastWriteTime = DateTime.UtcNow;
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (attempt < _cfg.WriteRetryCount)
                    {
                        Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, $"Write 第{attempt}次重试")));
                        try { await Task.Delay(_cfg.WriteRetryIntervalMs, ct).ConfigureAwait(false); } catch { }
                    }
                    catch (Exception ex)
                    {
                        Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "Write")));
                        ForceDisconnectForReconnect(); // 写失败 → 触发重连
                        return false;
                    }
                }
            }
            finally
            {
                _writeLock.Release();
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _closing = true;
            try
            {
                _heartbeatTimer.Stop();
                try { _reconnectCts?.Cancel(); } catch { }
                try { _masterCts?.Cancel(); } catch { }
                try { _readTask?.Wait(500); } catch { }
                try { _reconnectTask?.Wait(500); } catch { }
            }
            catch { }
            finally
            {
                CleanupConnectionObjects();
                _writeLock?.Dispose();
                _heartbeatTimer?.Dispose();
                _masterCts?.Dispose();
                _reconnectCts?.Dispose();
            }
        }
    }
}
