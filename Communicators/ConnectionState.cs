namespace TcpSerialComm.Communicators
{
    /// <summary>Connection state machine.</summary>
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
