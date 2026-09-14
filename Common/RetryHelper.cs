using System;
using System.Threading;
using System.Threading.Tasks;

namespace TcpSerialComm.Common
{
    /// <summary>Generic retry helper (configurable attempts + interval, cancellable) for reuse by other business logic.</summary>
    public static class RetryHelper
    {
        public static async Task<T> RetryAsync<T>(Func<Task<T>> func, int retryCount, int intervalMs, CancellationToken ct = default)
        {
            Guard.ArgumentNotNull(func, nameof(func));
            Guard.InRange(retryCount, 0, int.MaxValue, nameof(retryCount));
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await func().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) when (attempt < retryCount && !ct.IsCancellationRequested)
                {
                    attempt++;
                    try { await Task.Delay(intervalMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
                }
            }
        }

        public static async Task RetryAsync(Func<Task> action, int retryCount, int intervalMs, CancellationToken ct = default)
        {
            await RetryAsync(async () => { await action().ConfigureAwait(false); return true; }, retryCount, intervalMs, ct).ConfigureAwait(false);
        }
    }
}
