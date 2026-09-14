using System;

namespace TcpSerialComm.Common
{
    /// <summary>Defensive validation helpers for arguments and state.</summary>
    public static class Guard
    {
        public static void ArgumentNotNull(object value, string paramName)
        {
            if (value == null) throw new ArgumentNullException(paramName);
        }

        public static void ArgumentNotNullOrEmpty(string value, string paramName)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException($"Argument '{paramName}' cannot be null or empty.", paramName);
        }

        public static void InRange(int value, int min, int max, string paramName)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(paramName, $"Argument '{paramName}' must be between {min} and {max}, but was {value}.");
        }

        public static void InRange(long value, long min, long max, string paramName)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(paramName, $"Argument '{paramName}' must be between {min} and {max}, but was {value}.");
        }
    }
}
