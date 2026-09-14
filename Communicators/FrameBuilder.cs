using System;
using System.Collections.Generic;

namespace TcpSerialComm.Communicators
{
    /// <summary>
    /// Inbound frame assembler: solves the "packet coalescing / fragmentation" problem on TCP and serial links.
    /// Supports Raw (pass-through), Delimiter (delimiter based) and LengthPrefix (2-byte little-endian length header).
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

        /// <summary>Pushes a raw chunk and returns every complete frame that can be delivered now (a partial tail is retained).</summary>
        public List<byte[]> Push(byte[] chunk, int count)
        {
            var frames = new List<byte[]>();
            if (chunk == null || count <= 0) return frames;
            lock (_lock)
            {
                for (int i = 0; i < count; i++) _buffer.Add(chunk[i]);

                // Malformed-stream guard: if the peer never sends a delimiter / length header the buffer would
                // grow without bound, so reset it to avoid OOM.
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
                    // 2-byte little-endian length header (the header itself is not counted in the length).
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
