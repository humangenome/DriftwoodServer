using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace DriftwoodServer;

// Backup hook. ORDER IS LOAD-BEARING: save first, snapshot second.
//
// A snapshot taken before the flush captures the stale file the flush was about to replace, and it
// is completely silent - the zip is valid, the sizes look right, and the customer's last hour is
// simply not in it. The kill path makes this worse, because a forced kill skips the game's own
// OnApplicationQuit save entirely.
//
// So: ask the running server to save through its own API, wait for it to answer, confirm the save
// tree has actually settled, and only then copy.
internal sealed class SaveSnapshot
{
    private readonly HostOptions _options;
    private readonly HttpClient _http;

    public SaveSnapshot(HostOptions options)
    {
        _options = options;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public sealed record Result(bool Ok, string Reason, string Path, long Bytes);

    public async Task<Result> CaptureAsync(string authToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BackupRoot))
        {
            return new Result(false, "No backup folder is configured for this server.", string.Empty, 0);
        }

        // 1. Flush.
        bool flushed = await RequestSaveAsync(authToken, cancellationToken).ConfigureAwait(false);

        // 2. Confirm the flush finished. Copying a save tree that is still being written produces
        //    an archive that is structurally fine and semantically torn.
        bool quiescent = await SaveTreeQuiescence.WaitAsync(
            _options.SaveRoot,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(2),
            3,
            cancellationToken).ConfigureAwait(false);
        if (!quiescent)
        {
            return new Result(false,
                "The world files were still being written after 20 seconds, so no backup was taken rather than taking a torn one.",
                string.Empty, 0);
        }

        // 3. Copy.
        Directory.CreateDirectory(_options.BackupRoot);
        string name = $"{_options.InstanceId}-{_options.WorldName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        string path = Path.Combine(_options.BackupRoot, name);
        string staging = path + ".partial";
        try
        {
            if (File.Exists(staging)) File.Delete(staging);
            ZipFile.CreateFromDirectory(_options.SaveRoot, staging, CompressionLevel.Optimal, false);

            // Verify by READING BACK, never by a successful write. A publish that only checks its
            // own return value is a claim about the write, not about what comes out.
            using (ZipArchive archive = ZipFile.OpenRead(staging))
            {
                string expected = _options.WorldName + ".txt";
                bool hasWorld = archive.Entries.Any(entry =>
                    string.Equals(entry.Name, expected, StringComparison.OrdinalIgnoreCase));
                if (!hasWorld)
                {
                    return new Result(false,
                        $"The backup was written but does not contain {expected}, so it would not restore this server's world.",
                        string.Empty, 0);
                }
            }
            File.Move(staging, path, true);
            long bytes = new FileInfo(path).Length;
            string flushNote = flushed ? string.Empty : " (the server did not confirm a save first, so this snapshot may be up to one auto-save interval old)";
            return new Result(true, "Backup taken" + flushNote + ".", path, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Result(false, $"The backup could not be written: {exception.Message}", string.Empty, 0);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { }
        }
    }

    private async Task<bool> RequestSaveAsync(string authToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authToken)) return false;
        try
        {
            int httpPort = _options.HttpPort > 0 ? _options.HttpPort : _options.GamePort + 1;
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{httpPort}/api/v1/save")
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };
            request.Headers.Add("X-Driftwood-Auth", authToken);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
