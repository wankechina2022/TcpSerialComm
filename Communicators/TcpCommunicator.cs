using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpSerialComm.Common;

namespace TcpSerialComm.Communicators
{
    /// <summary>
    /// Industrial-grade TCP read/write class.
    /// Open / Close / Read / Write / disconnection detection / auto-reconnect / heartbeat watchdog / framing.
    /// Design highlights (from best-practice research):
    ///  1) Never trust the cached TcpClient.Connected value; detect disconnection via read failure + heartbeat watchdog.
    ///  2) Enable TCP KeepAlive as an OS-level assist (the 2-hour default is too long, so short cycles are configured).
    ///  3) Fully async/await + CancellationToken, so the read loop never blocks the UI.
    ///  4) Writes take a SemaphoreSlim lock plus a minimum write interval to prevent packet coalescing.
    ///  5) Exponential-backoff auto-reconnect that does not spin the CPU.
    ///  6) Inbound framing via FrameBuilder (delimiter / length header) to solve coalescing and fragmentation.
    ///  7) Events are marshalled back to the UI thread through SynchronizationContext; the class itself
    ///     does not depend on WinForms/WPF and can be migrated as-is.
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
        private int _reconnecting;
        private int _openInProgress;
        private volatile bool _closing;
        private volatile bool _disposed;
        private SynchronizationContext _syncContext;
        private readonly FrameBuilder _frameBuilder;
        private readonly List<TaskCompletionSource<byte[]>> _pendingReaders = new List<TaskCompletionSource<byte[]>>();
        private readonly object _pendingLock = new object();

        public TcpCommunicatorConfig Config => _cfg;
        public ConnectionState State { get { lock (_stateLock) return _state; } }
        public bool IsConnected => State == ConnectionState.Connected;

        public event EventHandler<ConnectionStateChangedEventArgs> StateChanged;
        public event EventHandler<DataReceivedEventArgs> DataReceived;
        public event EventHandler<CommunicatorErrorEventArgs> Error;

        /// <summary>When set, events are posted back to this context (e.g. the UI thread) so callers need no manual Invoke.</summary>
        public SynchronizationContext SyncContext { get => _syncContext; set => _syncContext = value; }

        public TcpCommunicator(TcpCommunicatorConfig config)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            Guard.ArgumentNotNullOrEmpty(_cfg.Host, nameof(_cfg.Host));
            _frameBuilder = new FrameBuilder(_cfg.Framing, _cfg.FrameDelimiter, _cfg.MaxFrameBufferBytes);
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
            // When the state leaves "Connected", wake up every caller waiting inside ReadAsync();
            // otherwise those callers would hang forever while the link is down.
            if (newState != ConnectionState.Connected) CancelPendingReaders();
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
            try { _masterCts?.Dispose(); } catch { }  // Release the CTS left over from a previous run to avoid leaking it.
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
                    if (n == 0) // The remote peer closed the connection gracefully.
                    {
                        Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(new Exception("Remote peer closed the connection (0 bytes read)."), "Read")));
                        break;
                    }
                    _lastReceiveTime = DateTime.UtcNow;
                    var frames = _frameBuilder.Push(buf, n);
                    foreach (var f in frames)
                    {
                        // ===== Dual-channel dispatch (core) =====
                        // [Pull mode first] If some code is currently awaiting ReadAsync(), hand this frame
                        // directly to it (TryDispatchToWaiter returns true) and skip the event, so a
                        // "send command -> await reply" call always receives exactly its own frame.
                        if (TryDispatchToWaiter(f)) continue;
                        // [Push mode] Nobody is waiting -> publish the data to all subscribers through the
                        // DataReceived event. Best suited to devices that report spontaneously.
                        var data = f;
                        Post(() => DataReceived?.Invoke(this, new DataReceivedEventArgs(data)));
                    }
                }
            }
            catch (OperationCanceledException) { /* Normal shutdown. */ }
            catch (Exception ex)
            {
                // Exceptions caused by a normal close/dispose are swallowed instead of polluting the error log.
                if (_closing || _disposed) return;
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "Read")));
            }

            if (!ct.IsCancellationRequested && !_closing && _cfg.AutoReconnect)
                BeginReconnectLoop();
            else if (!ct.IsCancellationRequested && !_closing)
                SetState(ConnectionState.Disconnected, "Read loop ended.");
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
                        new TimeoutException($"Heartbeat watchdog: silence of {silent:0} ms exceeded the threshold of {_cfg.HeartbeatSilenceTimeoutMs} ms."), "Heartbeat")));
                    ForceDisconnectForReconnect();
                    return;
                }
                _ = Task.Run(async () => { try { await WriteAsync(_cfg.HeartbeatRequest).ConfigureAwait(false); } catch { } });
            }
        }

        private void ForceDisconnectForReconnect()
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
            // The read loop will fail on the closed/null stream and trigger a reconnect; _masterCts is intentionally not cancelled here.
        }

        private void BeginReconnectLoop()
        {
            if (_disposed || _closing) return;
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
            if (_state == ConnectionState.Connected) return;
            SetState(ConnectionState.Reconnecting, "Starting automatic reconnect.");
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
                        SetState(ConnectionState.Disconnected, $"Maximum reconnect attempts reached ({_cfg.MaxReconnectAttempts}).");
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
                        SetState(ConnectionState.Connected, "Reconnected successfully.");
                        return;
                    }
                    _reconnectAttempts++;
                    SetState(ConnectionState.Reconnecting, $"Reconnect failed (attempt {_reconnectAttempts}, retrying in {delay} ms).");
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
            // Safety rule 3: hardware must not be opened twice. Interlocked gives an atomic claim, removing the
            // check-then-act race (TOCTOU) where two concurrent Open calls could both pass IsActive and build two links.
            if (Interlocked.CompareExchange(ref _openInProgress, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent open is not allowed: another open/connect operation is already in progress.");
            try
            {
            _reconnectAttempts = 0;  // Every manual open resets the reconnect counter so a previous failure count is not reused.
            // Safety rule 3: hardware must not be opened twice.
            if (IsActive) throw new InvalidOperationException("Duplicate connection is not allowed: the device is already connected, connecting or reconnecting.");
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
                SetState(ConnectionState.Disconnected, "Connection failed.");
                throw new InvalidOperationException($"TCP connection to {_cfg.Host}:{_cfg.Port} failed and automatic reconnect is disabled.");
            }
            }
            finally
            {
                Interlocked.Exchange(ref _openInProgress, 0);
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
                // A cancellation token may not interrupt a blocking socket read, so release the underlying stream to force it out.
                try { ForceDisconnectForReconnect(); } catch { }
                if (_readTask != null) { try { await Task.WhenAny(_readTask, Task.Delay(2000)).ConfigureAwait(false); } catch { } }
                if (_reconnectTask != null) { try { await Task.WhenAny(_reconnectTask, Task.Delay(2000)).ConfigureAwait(false); } catch { } }
            }
            finally
            {
                CancelPendingReaders();
                CleanupConnectionObjects();
                _closing = false;
                SetState(ConnectionState.Disconnected, "Closed.");
            }
        }

        public async Task<bool> WriteAsync(byte[] data, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (data == null || data.Length == 0) return false;
            // Safety rule 4: verify connectivity before writing.
            if (_state != ConnectionState.Connected)
            {
                if (_cfg.AutoReconnect) BeginReconnectLoop();
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(
                    new InvalidOperationException("Not connected; the write was rejected."), "Write")));
                return false;
            }

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Wrap the outgoing payload according to the current Framing mode (delimiter appended / length header prepended / as-is).
                byte[] payload = _cfg.BuildSendPayload(data);

                // Minimum write interval (lock + spacing) to prevent packet coalescing.
                var since = (DateTime.UtcNow - _lastWriteTime).TotalMilliseconds;
                if (since < _cfg.WriteMinIntervalMs)
                    await Task.Delay((int)(_cfg.WriteMinIntervalMs - since), ct).ConfigureAwait(false);

                for (int attempt = 1; attempt <= _cfg.WriteRetryCount; attempt++)
                {
                    try
                    {
                        if (_state != ConnectionState.Connected || _stream == null)
                            throw new InvalidOperationException("The connection has been closed.");
                        await _stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                        await _stream.FlushAsync(ct).ConfigureAwait(false);
                        _lastWriteTime = DateTime.UtcNow;
                        return true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (attempt < _cfg.WriteRetryCount)
                    {
                        Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, $"Write retry attempt {attempt}")));
                        try { await Task.Delay(_cfg.WriteRetryIntervalMs, ct).ConfigureAwait(false); } catch { }
                    }
                    catch (Exception ex)
                    {
                        Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(ex, "Write")));
                        ForceDisconnectForReconnect(); // A failed write triggers a reconnect.
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

        /// <summary>[Pull mode] Waits for and returns the next complete frame (request-response pattern). Returns null when disconnected. Cancellable.</summary>
        public async Task<byte[]> ReadAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_state != ConnectionState.Connected) return null;
            // Create a promise that will be completed later, and register it in the _pendingReaders wait queue.
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) _pendingReaders.Add(tcs);
            // If the caller cancels (ct), remove ourselves from the queue to avoid a dangling entry.
            using (ct.Register(() =>
            {
                lock (_pendingLock) { _pendingReaders.Remove(tcs); }
                try { tcs.TrySetCanceled(); } catch { }
            }))
            {
                // Await (asynchronously) until the read loop pushes a frame in via TrySetResult; this method then returns it.
                try { return await tcs.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }

        /// <summary>Dual-channel core: tries to hand a frame to a caller currently waiting inside [pull-mode] ReadAsync.</summary>
        private bool TryDispatchToWaiter(byte[] frame)
        {
            TaskCompletionSource<byte[]> waiter = null;
            lock (_pendingLock)
            {
                // Take the first waiter in the queue (FIFO: first to wait, first to be served).
                if (_pendingReaders.Count > 0)
                {
                    waiter = _pendingReaders[0];
                    _pendingReaders.RemoveAt(0);
                }
            }
            if (waiter != null)
            {
                // Hand the frame over -> the corresponding ReadAsync returns it immediately.
                waiter.TrySetResult(frame);
                return true;   // Delivered, so the caller must not fall through to the event push.
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
            try
            {
                // Reuse CloseAsync to tear the connection down (waiting for the read/reconnect loops and releasing the
                // underlying stream), with a 2 s cap. It must run before _disposed is set, otherwise CloseAsync would
                // throw straight out of ThrowIfDisposed.
                try { CloseAsync().Wait(2000); } catch { }
            }
            finally
            {
                _disposed = true;
                try { CancelPendingReaders(); } catch { }   // Fallback wake-up for any remaining waiters.
                try { CleanupConnectionObjects(); } catch { }
                _writeLock?.Dispose();
                _heartbeatTimer?.Dispose();
                _masterCts?.Dispose();
                _reconnectCts?.Dispose();
            }
        }
    }
}
