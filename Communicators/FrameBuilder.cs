using System;
using System.Collections.Generic;

namespace TcpSerialComm.Communicators
{
    /// <summary>
    /// 接收缓冲组帧器：解决 TCP/串口“数据包黏连/拆包”问题。
    /// 支持 Raw（原样）、Delimiter（分隔符）、LengthPrefix（2字节小端长度头）。
    /// </summary>
    internal sealed class FrameBuilder
    {
        private readonly FramingMode _mode;
        private readonly byte[] _delimiter;
        private readonly int _maxBufferBytes;
        private readonly List<byte> _buffer = new List<byte>();
        private readonly object _lock = new object();

        public FrameBuilder(FramingMode mode, byte[] delimiter, int maxBufferBytes = 0)
        {
            _mode = mode;
            _delimiter = delimiter ?? new byte[0];
            _maxBufferBytes = maxBufferBytes;
        }

        /// <summary>推入一段原始数据，返回本次可以交付的完整帧（不足一帧的剩余部分保留）</summary>
        public List<byte[]> Push(byte[] chunk, int count)
        {
            var frames = new List<byte[]>();
            if (chunk == null || count <= 0) return frames;
            lock (_lock)
            {
                for (int i = 0; i < count; i++) _buffer.Add(chunk[i]);

                // 畸形流防御：对端迟迟不发分隔符/长度头导致缓冲无限增长时，清零重置避免 OOM
                if (_maxBufferBytes > 0 && _buffer.Count > _maxBufferBytes)
                {
                    _buffer.Clear();
                    return frames;
                }

                if (_mode == FramingMode.Raw || _delimiter.Length == 0)
                {
                    if (_buffer.Count > 0)
                    {
                        frames.Add(_buffer.ToArray());
                        _buffer.Clear();
                    }
                    return frames;
                }

                if (_mode == FramingMode.Delimiter)
                {
                    int idx;
                    while ((idx = IndexOfDelimiter()) >= 0)
                    {
                        frames.Add(_buffer.GetRange(0, idx).ToArray());
                        _buffer.RemoveRange(0, idx + _delimiter.Length);
                    }
                    return frames;
                }

                if (_mode == FramingMode.LengthPrefix)
                {
                    // 2字节小端长度头（不含头本身长度）
                    while (_buffer.Count >= 2)
                    {
                        int len = _buffer[0] | (_buffer[1] << 8);
                        int total = 2 + len;
                        if (_buffer.Count < total) break;
                        frames.Add(_buffer.GetRange(2, len).ToArray());
                        _buffer.RemoveRange(0, total);
                    }
                    return frames;
                }
            }
            return frames;
        }

        private int IndexOfDelimiter()
        {
            if (_delimiter.Length == 0) return -1;
            int max = _buffer.Count - _delimiter.Length;
            for (int i = 0; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < _delimiter.Length; j++)
                {
                    if (_buffer[i + j] != _delimiter[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        public void Reset()
        {
            lock (_lock) _buffer.Clear();
        }
    }
}
