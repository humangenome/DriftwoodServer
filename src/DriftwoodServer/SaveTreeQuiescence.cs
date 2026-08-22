using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace DriftwoodServer;

internal sealed record SaveTreeFingerprint(int FileCount, long TotalBytes, string MetadataSha256);

internal sealed class SaveTreeQuiescenceTracker
{
    private readonly int _requiredMatchingObservations;
    private SaveTreeFingerprint? _last;
    private int _matchingObservations;

    public SaveTreeQuiescenceTracker(int requiredMatchingObservations)
    {
        if (requiredMatchingObservations < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredMatchingObservations));
        }
        _requiredMatchingObservations = requiredMatchingObservations;
    }

    public bool Observe(SaveTreeFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint == _last)
        {
            _matchingObservations++;
        }
        else
        {
            _last = fingerprint;
            _matchingObservations = 1;
        }
        return _matchingObservations >= _requiredMatchingObservations;
    }
}

internal static class SaveTreeQuiescence
{
    private const int MaximumFingerprintFiles = 100_000;

    public static bool TryCapture(string root, out SaveTreeFingerprint fingerprint)
    {
        return TryCapture(root, long.MaxValue, out fingerprint);
    }

    private static bool TryCapture(
        string root,
        long deadlineMilliseconds,
        out SaveTreeFingerprint fingerprint)
    {
        try
        {
            Directory.CreateDirectory(root);
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.PlatformDefault
            };
            List<(string RelativePath, long Length, long LastWriteTicks)> files = [];
            long totalBytes = 0;
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                if (Environment.TickCount64 >= deadlineMilliseconds || files.Count >= MaximumFingerprintFiles)
                {
                    fingerprint = new SaveTreeFingerprint(0, 0, string.Empty);
                    return false;
                }
                FileInfo info = new(path);
                totalBytes = checked(totalBytes + info.Length);
                files.Add((
                    Path.GetRelativePath(root, path),
                    info.Length,
                    info.LastWriteTimeUtc.Ticks));
            }
            files.Sort((left, right) => string.Compare(
                left.RelativePath,
                right.RelativePath,
                StringComparison.Ordinal));
            if (Environment.TickCount64 >= deadlineMilliseconds)
            {
                fingerprint = new SaveTreeFingerprint(0, 0, string.Empty);
                return false;
            }
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach ((string relativePath, long length, long lastWriteTicks) in files)
            {
                if (Environment.TickCount64 >= deadlineMilliseconds)
                {
                    fingerprint = new SaveTreeFingerprint(0, 0, string.Empty);
                    return false;
                }
                string metadata = string.Concat(
                    relativePath, "\0",
                    length.ToString(System.Globalization.CultureInfo.InvariantCulture), "\0",
                    lastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "\n");
                hash.AppendData(Encoding.UTF8.GetBytes(metadata));
            }
            fingerprint = new SaveTreeFingerprint(
                files.Count,
                totalBytes,
                Convert.ToHexString(hash.GetHashAndReset()));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or OverflowException)
        {
            fingerprint = new SaveTreeFingerprint(0, 0, string.Empty);
            return false;
        }
    }

    public static async Task<bool> WaitAsync(
        string root,
        TimeSpan maximumWait,
        TimeSpan sampleInterval,
        int requiredMatchingObservations,
        CancellationToken cancellationToken)
    {
        if (maximumWait <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumWait));
        if (sampleInterval <= TimeSpan.Zero || sampleInterval > maximumWait)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        }

        SaveTreeQuiescenceTracker tracker = new(requiredMatchingObservations);
        long deadline = Environment.TickCount64 + checked((long)maximumWait.TotalMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCapture(root, deadline, out SaveTreeFingerprint fingerprint) && tracker.Observe(fingerprint))
            {
                return true;
            }
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return false;
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(remaining, sampleInterval.TotalMilliseconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
