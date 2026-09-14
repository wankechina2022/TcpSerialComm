using System;
using System.Threading;
using System.Threading.Tasks;

namespace TcpSerialComm.Communicators
{
    /// <summary>Unified communicator interface: TCP and Serial share the same calling convention, easing migration and substitution.</summary>
    public interface ICommunicator : IDisposable
    {
        ConnectionState State { get; }
        bool IsConnected { get; }
        event EventHandler<ConnectionStateChangedEventArgs> StateChanged;
        event EventHandler<DataReceivedEventArgs> DataReceived;
        event EventHandler<CommunicatorErrorEventArgs> Error;
        Task OpenAsync(CancellationToken ct = default);
        Task CloseAsync(CancellationToken ct = default);
        Task<bool> WriteAsync(byte[] data, CancellationToken ct = default);
        /// <summary>Waits for the next complete frame (request-response pattern). Returns null when disconnected or no data. Cancellable.</summary>
        Task<byte[]> ReadAsync(CancellationToken ct = default);
    }
}
