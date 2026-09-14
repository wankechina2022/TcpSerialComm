using System;

namespace TcpSerialComm.Communicators
{
    /// <summary>Inbound framing mode (solves packet coalescing / fragmentation).</summary>
    public enum FramingMode
    {
        /// <summary>Pass-through: every chunk read is emitted as-is.</summary>
        Raw = 0,
        /// <summary>Split by delimiter (e.g. line break, STX/ETX).</summary>
        Delimiter,
        /// <summary>2-byte little-endian length prefix followed by the payload.</summary>
        LengthPrefix
    }

    /// <summary>Common configuration shared by TCP and Serial communicators. Every option has a default value.</summary>
    public abstract class CommunicatorConfig
    {
        /// <summary>Whether to reconnect automatically after a disconnection.</summary>
        public bool AutoReconnect { get; set; } = true;
        /// <summary>Maximum reconnect attempts; 0 means unlimited.</summary>
        public int MaxReconnectAttempts { get; set; } = 0;
        /// <summary>Base reconnect delay (ms); the effective delay backs off as base * 2^attempt.</summary>
        public int ReconnectBaseDelayMs { get; set; } = 1000;
        /// <summary>Maximum reconnect delay (ms).</summary>
        public int ReconnectMaxDelayMs { get; set; } = 30000;
        /// <summary>Framing mode. Delimiter = split by delimiter (e.g. CRLF); Raw = pass-through; LengthPrefix = 2-byte length header.</summary>
        public FramingMode Framing { get; set; } = FramingMode.Delimiter;
        /// <summary>Frame delimiter (effective when Framing = Delimiter). Defaults to CRLF (\r\n): one message ends with CRLF.</summary>
        public byte[] FrameDelimiter { get; set; } = new byte[] { (byte)'\r', (byte)'\n' };
        /// <summary>Whether to automatically append FrameDelimiter to the payload when sending (common for line protocols). Set false to send raw data (e.g. hex pass-through).</summary>
        public bool AppendDelimiterOnWrite { get; set; } = true;
        /// <summary>Receive buffer size.</summary>
        public int ReceiveBufferSize { get; set; } = 4096;
        /// <summary>Upper bound (bytes) of the inbound framing buffer, guarding against unbounded growth when the peer never sends a delimiter or the stream is malformed; 0 = unlimited.</summary>
        public int MaxFrameBufferBytes { get; set; } = 1048576;

        /// <summary>
        /// Wraps an outgoing payload according to the current Framing mode:
        ///  - LengthPrefix: prepend a 2-byte little-endian length header (excluding the header itself);
        ///  - Delimiter with AppendDelimiterOnWrite: append FrameDelimiter (line protocol terminator);
        ///  - otherwise: return as-is.
        /// Both WriteAsync and the heartbeat go through this method, keeping the send path symmetric
        /// with the receive path assembled by FrameBuilder.
        /// </summary>
        public byte[] BuildSendPayload(byte[] data)
        {
            if (data == null) return new byte[0];
            if (Framing == FramingMode.LengthPrefix)
            {
                var p = new byte[data.Length + 2];
                p[0] = (byte)(data.Length & 0xFF);
                p[1] = (byte)((data.Length >> 8) & 0xFF);
                Array.Copy(data, 0, p, 2, data.Length);
                return p;
            }
            if (AppendDelimiterOnWrite && Framing == FramingMode.Delimiter
                && FrameDelimiter != null && FrameDelimiter.Length > 0)
            {
                var p = new byte[data.Length + FrameDelimiter.Length];
                Array.Copy(data, 0, p, 0, data.Length);
                Array.Copy(FrameDelimiter, 0, p, data.Length, FrameDelimiter.Length);
                return p;
            }
            return (byte[])data.Clone();
        }
        /// <summary>Whether the heartbeat and its watchdog are enabled.</summary>
        public bool HeartbeatEnabled { get; set; } = false;
        /// <summary>Heartbeat send interval (ms).</summary>
        public int HeartbeatIntervalMs { get; set; } = 30000;
        /// <summary>Watchdog: silence for longer than this many milliseconds is treated as a disconnection.</summary>
        public int HeartbeatSilenceTimeoutMs { get; set; } = 15000;
        /// <summary>Heartbeat request payload, wrapped on send by BuildSendPayload according to the current Framing mode (delimiter appended / length header prepended).</summary>
        public byte[] HeartbeatRequest { get; set; } = System.Text.Encoding.ASCII.GetBytes("PING");
        /// <summary>Number of write retries after a failed write.</summary>
        public int WriteRetryCount { get; set; } = 3;
        /// <summary>Interval between write retries (ms).</summary>
        public int WriteRetryIntervalMs { get; set; } = 30;
        /// <summary>Minimum interval between two writes (ms) to prevent packet coalescing (recommended 20-50 ms).</summary>
        public int WriteMinIntervalMs { get; set; } = 20;
    }

    /// <summary>TCP-specific configuration.</summary>
    public sealed class TcpCommunicatorConfig : CommunicatorConfig
    {
        /// <summary>Remote host or IP address.</summary>
        public string Host { get; set; } = "127.0.0.1";
        /// <summary>Remote TCP port.</summary>
        public int Port { get; set; } = 502;
        /// <summary>Connect timeout (ms).</summary>
        public int ConnectTimeoutMs { get; set; } = 5000;
        /// <summary>Disable Nagle's algorithm for lower latency (TcpClient.NoDelay).</summary>
        public bool NoDelay { get; set; } = true;
        /// <summary>Enable the OS-level TCP keep-alive probe.</summary>
        public bool KeepAlive { get; set; } = true;
        /// <summary>TCP keep-alive idle time before the first probe (seconds).</summary>
        public int KeepAliveTimeSec { get; set; } = 30;
        /// <summary>TCP keep-alive probe interval (seconds).</summary>
        public int KeepAliveIntervalSec { get; set; } = 10;
        /// <summary>Number of failed keep-alive probes before the connection is dropped.</summary>
        public int KeepAliveRetryCount { get; set; } = 3;
        /// <summary>Socket send buffer size (bytes).</summary>
        public int SendBufferSize { get; set; } = 8192;
    }

    /// <summary>Serial-port-specific configuration.</summary>
    public sealed class SerialPortCommunicatorConfig : CommunicatorConfig
    {
        /// <summary>Port name, e.g. "COM3". Must be set before opening.</summary>
        public string PortName { get; set; } = "";
        /// <summary>Baud rate.</summary>
        public int BaudRate { get; set; } = 9600;
        /// <summary>Parity.</summary>
        public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.None;
        /// <summary>Data bits.</summary>
        public int DataBits { get; set; } = 8;
        /// <summary>Stop bits.</summary>
        public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
        /// <summary>Request-to-send handshaking line.</summary>
        public bool RtsEnable { get; set; } = false;
        /// <summary>Data-terminal-ready handshaking line.</summary>
        public bool DtrEnable { get; set; } = false;
        /// <summary>
        /// Read timeout (ms); 0 = infinite. Note: SerialPort.ReadTimeout does NOT apply to the
        /// asynchronous BaseStream.ReadAsync path used by this class (.NET known behavior), and is
        /// kept only for compatibility. Disconnection is detected via stream failure and the watchdog.
        /// </summary>
        public int ReadTimeout { get; set; } = 0;
        /// <summary>Write timeout (ms); 0 = infinite.</summary>
        public int WriteTimeout { get; set; } = 0;
    }
}
