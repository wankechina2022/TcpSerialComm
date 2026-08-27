using System;
using System.Threading;
using System.Threading.Tasks;

namespace TcpSerialComm.Communicators
{
    /// <summary>统一通信接口：TCP / 串口共用同一套调用方式，便于迁移与替换。</summary>
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
    }
}
