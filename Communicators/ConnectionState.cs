namespace TcpSerialComm.Communicators
{
    /// <summary>通信状态机</summary>
    public enum ConnectionState
    {
        Disconnected = 0,
        Connecting,
        Connected,
        Reconnecting,
        Disconnecting,
        Error
    }
}
