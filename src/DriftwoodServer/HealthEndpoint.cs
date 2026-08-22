using System.Net;
using System.Text;
using System.Text.Json;

namespace DriftwoodServer;

// The consumer of the readiness signal, and the reason the signal is worth anything.
//
// Playbook 1d requirement 3 has a near-miss attached to it: Lodestone's plugin already computed
// readiness correctly and exposed it - behind an admin gate that shipped disabled, so nothing read
// it. This endpoint exists so that never applies here. It is loopback-only by default, needs no
// authentication because it exposes nothing secret, and answers the three questions the panel and
// the launcher actually ask: is it up, is the world running, and is it full.
//
// >>> PORT + 1 IS THE HOST MOD'S, AND THIS COMPONENT MUST BE GIVEN ITS OWN PORT BEFORE IT SHIPS.
//
// As of 2026-08-22 the launcher-facing API (/health, /players, /manifest, /console, /snapshots)
// lives in DriftwoodHost, the BepInEx plugin, and binds 0.0.0.0:<game port + 1> on a raw
// TcpListener. It was put there deliberately: which supervisor is the product is an OPEN question
// (review item S1 - production runs an inline PowerShell loop and this project is not packaged at
// all), and a customer-visible feature must not depend on a decision nobody has taken.
//
// PluginConfigWriter hands the plugin the same HttpPort this class is constructed with, and the
// example config pins both to 22004. If this ever runs alongside the plugin on that number, the
// second bind fails - loudly now rather than silently, because the plugin holds the port with a
// wildcard TcpListener rather than an http.sys prefix that could be out-ranked. Give this its own
// port (the +2..+9 band is reserved and empty) or do not run it.
//
// It also answers a DIFFERENT shape from the one the launcher parses: "players"/"slots" here
// versus "player_count"/"max_players" on the mod's /health. Nothing consumes this one today; if it
// ever faces a launcher, it has to speak the mod's shape, not its own.
internal sealed class HealthEndpoint : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly StatusStore _status;
    private readonly int _port;

    public HealthEndpoint(int port, StatusStore status)
    {
        _port = port;
        _status = status;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_port <= 0) return;
        _listener.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try { _listener.Stop(); } catch { }
        });
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            try
            {
                Respond(context);
            }
            catch (Exception)
            {
                try { context.Response.Abort(); } catch { }
            }
        }
    }

    private void Respond(HttpListenerContext context)
    {
        HostStatus? status = _status.Latest;
        string path = context.Request.Url?.AbsolutePath ?? "/";

        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) || path == "/")
        {
            // 200 ONLY when the world is genuinely running. A bound port is not a hosted world,
            // and a poller that treats "the process answered" as health is exactly the check this
            // product's failure mode defeats.
            bool healthy = status is { Phase: HostPhase.Hosting, WorldRunning: true };
            Write(context, healthy ? 200 : 503, JsonSerializer.Serialize(new
            {
                ok = healthy,
                phase = status?.Phase.ToString() ?? "Unknown",
                reason = status?.Reason ?? "The supervisor has not reported yet.",
                worldRunning = status?.WorldRunning ?? false,
                players = status?.Players ?? 0,
                slots = status?.Slots ?? 0,
                full = status?.Full ?? false,
                world = status?.WorldName ?? string.Empty,
                gamePort = status?.GamePort ?? 0,
                pluginVersion = status?.PluginVersion ?? string.Empty,
                gameVersion = status?.GameVersion ?? string.Empty,
                supervisorVersion = status?.SupervisorVersion ?? string.Empty,
                pinnedBuildId = status?.PinnedBuildId ?? string.Empty,
                swallowedExceptions = status?.SwallowedExceptions ?? 0,
                patchesFailed = status?.PatchesFailed ?? []
            }));
            return;
        }

        if (path.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            Write(context, 200, JsonSerializer.Serialize(status));
            return;
        }

        Write(context, 404, "{\"error\":\"not found\"}");
    }

    private static void Write(HttpListenerContext context, int statusCode, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch { }
    }
}
