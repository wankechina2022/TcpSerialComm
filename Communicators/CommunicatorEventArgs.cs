using System;

namespace TcpSerialComm.Communicators
{
    public sealed class DataReceivedEventArgs : EventArgs
    {
        public byte[] Data { get; }
        public DateTime Timestamp { get; }
        public DataReceivedEventArgs(byte[] data)
        {
            Data = data ?? new byte[0];
            Timestamp = DateTime.Now;
        }
    }

    public sealed class ConnectionStateChangedEventArgs : EventArgs
    {
        public ConnectionState OldState { get; }
        public ConnectionState NewState { get; }
        public string Message { get; }
        public ConnectionStateChangedEventArgs(ConnectionState oldState, ConnectionState newState, string message = null)
        {
            OldState = oldState;
            NewState = newState;
            Message = message;
        }
    }

    public sealed class CommunicatorErrorEventArgs : EventArgs
    {
        public Exception Exception { get; }
        public string Operation { get; }
        public CommunicatorErrorEventArgs(Exception exception, string operation)
        {
            Exception = exception;
            Operation = operation ?? "";
        }
    }
}
