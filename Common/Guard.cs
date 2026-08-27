using System;

namespace TcpSerialComm.Common
{
    /// <summary>参数 / 状态防御性校验公共方法</summary>
    public static class Guard
    {
        public static void ArgumentNotNull(object value, string paramName)
        {
            if (value == null) throw new ArgumentNullException(paramName);
        }

        public static void ArgumentNotNullOrEmpty(string value, string paramName)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException($"参数 {paramName} 不能为空。", paramName);
        }

        public static void InRange(int value, int min, int max, string paramName)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(paramName, $"参数 {paramName} 必须介于 {min} 与 {max} 之间，当前为 {value}。");
        }

        public static void InRange(long value, long min, long max, string paramName)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(paramName, $"参数 {paramName} 必须介于 {min} 与 {max} 之间，当前为 {value}。");
        }
    }
}
