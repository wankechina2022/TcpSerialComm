using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using TcpSerialComm.Common;

namespace TcpSerialComm.Communicators
{
    /// <summary>
    /// 工业级串口读写类（设计同 TcpCommunicator）。
    /// 采用 BaseStream.ReadAsync 后台循环读取（DataReceived 事件在部分 .NET 版本不可靠、易丢/乱数据）；
    /// 写加锁 + 最小间隔防黏连；拔线/断线自动重连 + 看门狗。
    /// </summary>
    public sealed class SerialPortCommunicator : ICommunicator
    {
        private readonly SerialPortCommunicatorConfig _cfg;
        private SerialPort _port;
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
        private int _reconnecting;
        private bool _closing;
        private bool _disposed;
        private SynchronizationContext _syncContext;
        private readonly FrameBuilder _frameBuilder;
        private readonly List<TaskCompletionSource<byte[]>> _pendingReaders = new List<TaskCompletionSource<byte[]>>();
        private readonly object _pendingLock = new object();

        public SerialPortCommunicatorConfig Config => _cfg;
        public ConnectionState State { get { lock (_stateLock) return _state; } }
        public bool IsConnected => State == ConnectionState.Connected;

        public event EventHandler<ConnectionStateChangedEventArgs> StateChanged;
        public event EventHandler<DataReceivedEventArgs> DataReceived;
        public event EventHandler<CommunicatorErrorEventArgs> Error;

        public SynchronizationContext SyncContext { get => _syncContext; set => _syncContext = value; }

        public SerialPortCommunicator(SerialPortCommunicatorConfig config)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            // 注意：PortName 由调用方在 Open 前设置（运行时选择），此处不强制非空
            _frameBuilder = new FrameBuilder(_cfg.Framing, _cfg.FrameDelimiter);
            _heartbeatTimer = new System.Timers.Timer { AutoReset = true };
            _heartbeatTimer.Elapsed += HeartbeatTick;
        }

        private bool IsActive => State == ConnectionState.Connecting || State == ConnectionState.Connected || State == ConnectionState.Reconnecting;

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SerialPortCommunicator));
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
            if (_port != null)
            {
                try { if (_port.IsOpen) _port.Close(); } catch { }
                try { _port.Dispose(); } catch { }
                _port = null;
            }
        }

        private async Task<bool> TryConnectAsync(CancellationToken ct)
        {
            CleanupConnectionObjects();
            SerialPort port = null;
            try
            {
                port = new SerialPort(_cfg.PortName, _cfg.BaudRate, _cfg.Parity, _cfg.DataBits, _cfg.StopBits)
                {
                    RtsEnable = _cfg.RtsEnable,
                    DtrEnable = _cfg.DtrEnable
                };
                if (_cfg.ReadTimeout > 0) port.ReadTimeout = _cfg.ReadTimeout;
                if (_cfg.WriteTimeout > 0) port.WriteTimeout = _cfg.WriteTimeout;
                port.Open();
                _port = port;
                _frameBuilder.Reset();
                _lastReceiveTime = DateTime.UtcNow;
                _lastWriteTime = DateTime.MinValue;
                return true;
            }
            catch (Exception ex)
            {
                try { port?.Close(); } catch { }
                try { port?.Dispose(); } catch { }
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "Open")));
                CleanupConnectionObjects();
                return false;
            }
        }

        private void StartReadLoop()
        {
            try { _masterCts?.Dispose(); } catch { }  // 释放上一次遗留的 CTS（重连/重开场景），避免泄漏
            _masterCts = new CancellationTokenSource();
            var token = _masterCts.Token;
            _readTask = Task.Run(async () => { await ReadLoopAsync(token).ConfigureAwait(false); }, token);
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buf = new byte[_cfg.ReceiveBufferSize];
            try
            {
                while (!ct.IsCancellationRequested && _port != null && _port.IsOpen)
                {
                    int n = await _port.BaseStream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    if (n == 0) break;
                    _lastReceiveTime = DateTime.UtcNow;
                    var frames = _frameBuilder.Push(buf, n);
                    foreach (var f in frames)
                    {
                        // ===== 双通道分发（核心）=====
                        // 【拉模式优先】若此刻有代码正在 await ReadAsync() 等待数据，
                        // 就把这一帧直接塞给它（TryDispatchToWaiter 返回 true），不再走事件，
                        // 确保"发指令→等回包"的调用能精确拿到对应这一帧。
                        if (TryDispatchToWaiter(f)) continue;
                        // 【推模式】没有人在等 → 通过 DataReceived 事件把数据"推"给所有订阅者。
                        // 适合设备主动上报（无需调用方干预）的场景。
                        var data = f;
                        Post(() => DataReceived?.Invoke(this, new DataReceivedEventArgs(data)));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException ex)
            {
                // 正常关闭/释放导致的异常静默处理，不刷错误日志
                if (_closing || _disposed) return;
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "Read")));
            }
            catch (Exception ex)
            {
                if (_closing || _disposed) return;
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
            if (_heartbeatTimer.Interval != _cfg.HeartbeatIntervalMs) _heartbeatTimer.Interval = _cfg.HeartbeatIntervalMs;
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
            CleanupConnectionObjects();
        }

        private void BeginReconnectLoop()
        {
            if (_disposed || _closing) return;
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
            if (_state == ConnectionState.Connected) return;
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
                Interlocked.Exchange(ref _reconnecting, 0);
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
            _reconnectAttempts = 0;  // 每次手动重开都重置重连计数，避免沿用上次失败计数
            // 安全约定3：串口不允许二次连接（重复 Open 会抛异常）
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
                throw new InvalidOperationException($"串口 {_cfg.PortName} 打开失败，且未启用自动重连。");
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
                // 主动释放串口强制解除阻塞读，避免读循环在关闭时挂起
                try { ForceDisconnectForReconnect(); } catch { }
                if (_readTask != null) { try { await Task.WhenAny(_readTask, Task.Delay(2000)).ConfigureAwait(false); } catch { } }
                if (_reconnectTask != null) { try { await Task.WhenAny(_reconnectTask, Task.Delay(2000)).ConfigureAwait(false); } catch { } }
            }
            finally
            {
                CancelPendingReaders();
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
            if (_state != ConnectionState.Connected || _port == null || !_port.IsOpen)
            {
                if (_cfg.AutoReconnect) BeginReconnectLoop();
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(
                    new InvalidOperationException("串口未连接，无法写入。"), "Write")));
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

                var since = (DateTime.UtcNow - _lastWriteTime).TotalMilliseconds;
                if (since < _cfg.WriteMinIntervalMs)
                    await Task.Delay((int)(_cfg.WriteMinIntervalMs - since), ct).ConfigureAwait(false);

                for (int attempt = 1; attempt <= _cfg.WriteRetryCount; attempt++)
                {
                    try
                    {
                        if (_state != ConnectionState.Connected || _port == null || !_port.IsOpen)
                            throw new InvalidOperationException("串口已断开");
                        await _port.BaseStream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                        await _port.BaseStream.FlushAsync(ct).ConfigureAwait(false);
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
                        ForceDisconnectForReconnect();
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

        /// <summary>【拉模式】等待并返回下一帧完整数据（请求-响应模式）。无数据且已断开时返回 null。可取消。</summary>
        public async Task<byte[]> ReadAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_state != ConnectionState.Connected) return null;
            // 创建一个"未来才拿到数据"的承诺任务，并把自己登记进等待队列 _pendingReaders
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) _pendingReaders.Add(tcs);
            // 若外部取消（ct），从等待队列移除自己，避免悬挂泄漏
            using (ct.Register(() =>
            {
                lock (_pendingLock) { _pendingReaders.Remove(tcs); }
                try { tcs.TrySetCanceled(); } catch { }
            }))
            {
                // 阻塞（异步等待）直到读循环把一帧通过 TrySetResult 塞进来，本方法随即返回该帧
                try { return await tcs.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }

        /// <summary>双通道核心：尝试把一帧数据交给正在【拉模式】等待的调用方（ReadAsync）。</summary>
        private bool TryDispatchToWaiter(byte[] frame)
        {
            TaskCompletionSource<byte[]> waiter = null;
            lock (_pendingLock)
            {
                // 取等待队列中第一个（FIFO：先等先得）的 ReadAsync 调用
                if (_pendingReaders.Count > 0)
                {
                    waiter = _pendingReaders[0];
                    _pendingReaders.RemoveAt(0);
                }
            }
            if (waiter != null)
            {
                // 把这一帧交给它 → 对应的 ReadAsync 立即返回该帧
                waiter.TrySetResult(frame);
                return true;   // 已交付，调用方不再走事件推送
            }
            return false;
        }

        private void CancelPendingReaders()
        {
            lock (_pendingLock)
            {
                foreach (var w in _pendingReaders) { try { w.TrySetCanceled(); } catch { } }
                _pendingReaders.Clear();
            }
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
                try { ForceDisconnectForReconnect(); } catch { }  // 强制解除阻塞读
                try { _readTask?.Wait(500); } catch { }
                try { _reconnectTask?.Wait(500); } catch { }
            }
            catch { }
            finally
            {
                CancelPendingReaders();
                CleanupConnectionObjects();
                _writeLock?.Dispose();
                _heartbeatTimer?.Dispose();
                _masterCts?.Dispose();
                _reconnectCts?.Dispose();
            }
        }
    }
}
