using System.Text;

namespace AutoPilot.Services
{
    /// <summary>Retry budget for <see cref="AtomicFile.MoveWithRetryAsync(string, string, FileRetryPolicy, CancellationToken)"/>.</summary>
    /// <param name="MaxAttempts">Maximum number of move attempts (at least 1).</param>
    /// <param name="InitialDelay">Delay before the second attempt; doubles after each failed attempt.</param>
    internal readonly record struct FileRetryPolicy(int MaxAttempts, TimeSpan InitialDelay)
    {
        /// <summary>8 attempts starting at 15 ms: a budget of about 1.9 s.</summary>
        internal static FileRetryPolicy Default { get; } = new(8, TimeSpan.FromMilliseconds(15));
    }

    /// <summary>
    /// File helpers for the JSON stores:     share-friendly reads (opened without denying other handles read, write or delete access)
        /// and a bounded retry around the replace-move. On Windows a replace-move is denied while any handle is open on the target,
        /// so a replace that races an open reader or an external scanner (e.g. Defender, the search indexer) is absorbed by the retry.
    /// Callers are expected to serialize writes themselves (the stores use a write gate).
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>Reads a UTF-8 text file (BOM detected), opening without denying other handles read, write or delete access.
        /// A replace-move can still be denied while this handle is open; the bounded retry absorbs that.</summary>
        internal static string ReadAllText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>Moves with <see cref="FileRetryPolicy.Default"/>.</summary>
        internal static Task MoveWithRetryAsync(string source, string destination, CancellationToken ct) =>
            MoveWithRetryAsync(source, destination, FileRetryPolicy.Default, ct);

        /// <summary>
        /// Moves <paramref name="source"/> over <paramref name="destination"/>, retrying transient
        /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> failures with exponential backoff.
        /// The last failure is rethrown unchanged. Missing files and directories are not retried.
        /// </summary>
        internal static async Task MoveWithRetryAsync(string source, string destination, FileRetryPolicy policy, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaxAttempts, 1);

            var delay = policy.InitialDelay;
            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Move(source, destination, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < policy.MaxAttempts
                    && ex is UnauthorizedAccessException or IOException
                    && ex is not (FileNotFoundException or DirectoryNotFoundException))
                {
                    await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5)), ct);
                    delay *= 2;
                }
            }
        }

        /// <summary>Best-effort delete of a temp file; never throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
        internal static void TryDelete(string path)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 3)
                    {
                        Thread.Sleep(20);
                    }
                }
            }
        }
    }
}
