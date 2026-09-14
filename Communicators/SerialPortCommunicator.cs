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
    /// Industrial-grade serial-port read/write class (same design as TcpCommunicator).
    /// Uses a background BaseStream.ReadAsync loop (the SerialPort.DataReceived event is unreliable on some
    /// .NET versions and can drop or interleave data); writes are locked and spaced to prevent coalescing;
    /// cable-pull / disconnection triggers automatic reconnect plus a watchdog.
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
        private int _openInProgress;
        private volatile bool _closing;
        private volatile bool _disposed;
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
            // Note: PortName is chosen by the caller at runtime before Open; it is intentionally not required here.
            _frameBuilder = new FrameBuilder(_cfg.Framing, _cfg.FrameDelimiter, _cfg.MaxFrameBufferBytes);
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
            // When the state leaves "Connected", wake up every caller waiting inside ReadAsync();
            // otherwise those callers would hang forever while the link is down.
            if (newState != ConnectionState.Connected) CancelPendingReaders();
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
                // Note: SerialPort.ReadTimeout/WriteTimeout only affect the synchronous Read/Write calls. This
                // class reads through BaseStream.ReadAsync, so those timeouts do not apply. Serial reads therefore
                // do not rely on them; disconnection is detected via stream failure / the watchdog.
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
                while (!ct.IsCancellationRequested && _port != null && _port.IsOpen)
                {
                    int n = await _port.BaseStream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    if (n == 0) break;
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
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException ex)
            {
                // Exceptions caused by a normal close/dispose are swallowed instead of polluting the error log.
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
                SetState(ConnectionState.Disconnected, "Read loop ended.");
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
                        new TimeoutException($"Heartbeat watchdog: silence of {silent:0} ms exceeded the threshold of {_cfg.HeartbeatSilenceTimeoutMs} ms."), "Heartbeat")));
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
            string msg = _cfg.MaxReconnectAttempts == 0
                ? "Starting automatic reconnect (unlimited retries)."
                : $"Starting automatic reconnect (max {_cfg.MaxReconnectAttempts} retries).";
            SetState(ConnectionState.Reconnecting, msg);
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
            // Safety rule 3: a serial port must not be opened twice. Interlocked gives an atomic claim, removing the
            // check-then-act race (TOCTOU) where two concurrent Open calls could both pass IsActive and build two links.
            if (Interlocked.CompareExchange(ref _openInProgress, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent open is not allowed: another open/connect operation is already in progress.");
            try
            {
            _reconnectAttempts = 0;  // Every manual open resets the reconnect counter so a previous failure count is not reused.
            // Safety rule 3: a serial port must not be opened twice (a duplicate Open throws).
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
                throw new InvalidOperationException($"Failed to open serial port {_cfg.PortName} and automatic reconnect is disabled.");
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
                // Actively release the serial port to force the blocking read out, so the read loop cannot hang during close.
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
            if (_state != ConnectionState.Connected || _port == null || !_port.IsOpen)
            {
                if (_cfg.AutoReconnect) BeginReconnectLoop();
                Post(() => Error?.Invoke(this, new CommunicatorErrorEventArgs(
                    new InvalidOperationException("The serial port is not connected; the write was rejected."), "Write")));
                return false;
            }

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Wrap the outgoing payload according to the current Framing mode (delimiter appended / length header prepended / as-is).
                byte[] payload = _cfg.BuildSendPayload(data);

                var since = (DateTime.UtcNow - _lastWriteTime).TotalMilliseconds;
                if (since < _cfg.WriteMinIntervalMs)
                    await Task.Delay((int)(_cfg.WriteMinIntervalMs - since), ct).ConfigureAwait(false);

                for (int attempt = 1; attempt <= _cfg.WriteRetryCount; attempt++)
                {
                    try
                    {
                        if (_state != ConnectionState.Connected || _port == null || !_port.IsOpen)
                            throw new InvalidOperationException("The serial port has been closed.");
                        await _port.BaseStream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                        await _port.BaseStream.FlushAsync(ct).ConfigureAwait(false);
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
