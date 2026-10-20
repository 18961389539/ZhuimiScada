using System.Security.Cryptography;
using System.Text;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Coordination;

public sealed class FileRecipeApplyLock(string directory) : IRecipeApplyLock
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(75);

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string resourceKey, TimeSpan waitTimeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
            throw new ArgumentException("Lock resource key cannot be empty.", nameof(resourceKey));
        if (waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        Directory.CreateDirectory(directory);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceKey)));
        var lockPath = Path.Combine(directory, $"{digest}.lock");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileStream stream;
            try
            {
                stream = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 128, FileOptions.Asynchronous);
            }
            catch (IOException) when (elapsed.Elapsed < waitTimeout)
            {
                var remaining = waitTimeout - elapsed.Elapsed;
                await Task.Delay(remaining < RetryInterval ? remaining : RetryInterval, ct);
                continue;
            }
            catch (IOException)
            {
                return null;
            }

            try
            {
                stream.SetLength(0);
                var owner = Encoding.UTF8.GetBytes(
                    $"{Environment.ProcessId}\t{DateTimeOffset.UtcNow:O}");
                await stream.WriteAsync(owner, ct);
                await stream.FlushAsync(ct);
                return new FileLease(stream);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class FileLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
