using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TcpSerialComm.Common
{
    /// <summary>Shared conversion helpers between byte[] / text / hexadecimal strings.</summary>
    public static class ByteArrayConverter
    {
        public static byte[] FromText(string text, Encoding encoding = null)
        {
            if (text == null) return new byte[0];
            return (encoding ?? Encoding.UTF8).GetBytes(text);
        }

        public static string ToText(byte[] data, Encoding encoding = null)
        {
            if (data == null || data.Length == 0) return string.Empty;
            return (encoding ?? Encoding.UTF8).GetString(data);
        }

        /// <summary>
        /// Converts a hexadecimal string to a byte array. Accepts "AA BB CC", "AABBCC",
        /// "AA-BB" and "AA,BB" (separators are optional).
        /// Throws FormatException on invalid characters or odd length.
        /// </summary>
        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return new byte[0];
            var sb = new StringBuilder(hex.Length);
            foreach (char c in hex)
            {
                if (char.IsWhiteSpace(c) || c == '-' || c == ',' || c == ':') continue;
                sb.Append(c);
            }
            string s = sb.ToString();
            if (s.Length % 2 != 0)
                throw new FormatException($"Hexadecimal string length must be even, but was {s.Length}.");
            var result = new byte[s.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return result;
        }

        public static string ToHex(byte[] data, string separator = " ")
        {
            if (data == null || data.Length == 0) return string.Empty;
            var parts = new List<string>(data.Length);
            foreach (byte b in data) parts.Add(b.ToString("X2", CultureInfo.InvariantCulture));
            return string.Join(separator, parts);
        }
    }
}
