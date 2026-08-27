using System;

namespace TcpSerialComm.Communicators
{
    /// <summary>收包组帧方式（解决数据包黏连/拆包）</summary>
    public enum FramingMode
    {
        /// <summary>原样透传，每读到一段就抛出</summary>
        Raw = 0,
        /// <summary>按分隔符切分（如换行、STX/ETX）</summary>
        Delimiter,
        /// <summary>2字节小端长度头 + 负载</summary>
        LengthPrefix
    }

    /// <summary>通信器公共配置（TCP / 串口共用）。所有项均有默认值。</summary>
    public abstract class CommunicatorConfig
    {
        /// <summary>断线是否自动重连</summary>
        public bool AutoReconnect { get; set; } = true;
        /// <summary>最大重连次数，0 表示无限重连</summary>
        public int MaxReconnectAttempts { get; set; } = 0;
        /// <summary>重连基础间隔(ms)，实际为 base*2^attempt 指数退避</summary>
        public int ReconnectBaseDelayMs { get; set; } = 1000;
        /// <summary>重连最大间隔(ms)</summary>
        public int ReconnectMaxDelayMs { get; set; } = 30000;
        /// <summary>组帧方式。Delimiter=按分隔符切分（如回车换行）；Raw=原样；LengthPrefix=2字节长度头</summary>
        public FramingMode Framing { get; set; } = FramingMode.Delimiter;
        /// <summary>分隔符（Framing=Delimiter 时生效）。默认回车换行 \r\n：一条消息以 CRLF 结束</summary>
        public byte[] FrameDelimiter { get; set; } = new byte[] { (byte)'\r', (byte)'\n' };
        /// <summary>发送时是否在负载末尾自动追加 FrameDelimiter（行协议常用）。设为 false 可发送裸数据（如 Hex 透传）</summary>
        public bool AppendDelimiterOnWrite { get; set; } = true;
        /// <summary>接收缓冲大小</summary>
        public int ReceiveBufferSize { get; set; } = 4096;
        /// <summary>是否启用心跳 + 看门狗</summary>
        public bool HeartbeatEnabled { get; set; } = false;
        /// <summary>心跳发送间隔(ms)</summary>
        public int HeartbeatIntervalMs { get; set; } = 30000;
        /// <summary>看门狗：超过该静默毫秒数判定断线(ms)</summary>
        public int HeartbeatSilenceTimeoutMs { get; set; } = 15000;
        /// <summary>心跳请求报文（不含分隔符，发送时由 AppendDelimiterOnWrite 统一补结束符）</summary>
        public byte[] HeartbeatRequest { get; set; } = System.Text.Encoding.ASCII.GetBytes("PING");
        /// <summary>写入失败重试次数</summary>
        public int WriteRetryCount { get; set; } = 3;
        /// <summary>写入重试间隔(ms)</summary>
        public int WriteRetryIntervalMs { get; set; } = 30;
        /// <summary>两次写入最小间隔(ms)，防止数据包黏连（约定 20-50ms）</summary>
        public int WriteMinIntervalMs { get; set; } = 20;
    }

    public sealed class TcpCommunicatorConfig : CommunicatorConfig
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 502;
        public int ConnectTimeoutMs { get; set; } = 5000;
        public bool NoDelay { get; set; } = true;
        public bool KeepAlive { get; set; } = true;
        public int KeepAliveTimeSec { get; set; } = 30;
        public int KeepAliveIntervalSec { get; set; } = 10;
        public int KeepAliveRetryCount { get; set; } = 3;
        public int SendBufferSize { get; set; } = 8192;
    }

    public sealed class SerialPortCommunicatorConfig : CommunicatorConfig
    {
        public string PortName { get; set; } = "";
        public int BaudRate { get; set; } = 9600;
        public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.None;
        public int DataBits { get; set; } = 8;
        public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
        public bool RtsEnable { get; set; } = false;
        public bool DtrEnable { get; set; } = false;
        public int ReadTimeout { get; set; } = 0;
        public int WriteTimeout { get; set; } = 0;
    }
}
