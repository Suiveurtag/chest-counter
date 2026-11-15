using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ChestCounter.Engine;
using ChestCounter.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ChestCounter.LocalApi;

/// <summary>What the interface can ask of the desktop shell. The defaults do nothing (headless, tests).</summary>
public interface IShell
{
    /// <summary>Shows a native folder picker; null when cancelled or unavailable.</summary>
    Task<string?> PickFolderAsync(string? start) => Task.FromResult<string?>(null);
    void OpenPath(string path) { }
    /// <summary>Settings changed: re-apply what lives outside the engine (overlay window, start with Windows...).</summary>
    void SettingsChanged() { }
}

public sealed class NoShell : IShell;

/// <summary>
/// The local HTTP server the interface talks to. It listens on the loopback address only, and every data request
/// must carry a token that only the application's own window (or the person who started it) has: another program's
/// web page cannot read or change anything. The read-only overlay feed for OBS is the one exception.
/// </summary>
public sealed class LocalServer : IAsyncDisposable
{
    public const int DefaultPort = 47821;

    private readonly ChestEngine _engine;
    private readonly IShell _shell;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _listeners = new();
    private WebApplication? _app;

    public int Port { get; private set; }
    public string Token { get; }
    public string Url => $"http://127.0.0.1:{Port}/";
    public string ObsUrl => $"http://127.0.0.1:{Port}/obs";

    public LocalServer(ChestEngine engine, IShell? shell = null, string? token = null)
    {
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _engine = engine;
        _shell = shell ?? new NoShell();
        _engine.Changed += Publish;
    }

    private void Publish(string topic)
    {
        foreach (var listener in _listeners.Values)
        {
            listener.Writer.TryWrite(topic);
        }
    }

    private static bool PortFree(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task StartAsync(string? webRoot, int? port = null)
    {
        // A fixed port keeps the OBS address stable; fall back to any free one when it is taken.
        Port = port ?? (PortFree(DefaultPort) ? DefaultPort : 0);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, Port));
        var app = builder.Build();

        // Only requests addressed to this machine by its loopback name (defeats DNS rebinding).
        app.Use(async (c, next) =>
        {
            if (c.Request.Host.Host is not ("127.0.0.1" or "localhost"))
            {
                c.Response.StatusCode = 403;
                return;
            }
            var path = c.Request.Path.Value ?? "";
            var open = !path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/api/overlay", StringComparison.Ordinal);
            if (!open)
            {
                var given = c.Request.Headers["X-CC-Token"].FirstOrDefault() ?? c.Request.Query["token"].FirstOrDefault() ?? "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(Token)))
                {
                    c.Response.StatusCode = 401;
                    return;
                }
            }
            try
            {
                await next();
            }
            catch (SyncException ex)
            {
                c.Response.StatusCode = 400;
                await c.Response.WriteAsJsonAsync(new ErrorResponse(ex.Code, ex.Message), Wire.Json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _engine.Db.Log("error", "interface", $"{c.Request.Method} {path}: {ex.Message}", DateTime.UtcNow);
                if (!c.Response.HasStarted)
                {
                    c.Response.StatusCode = 500;
                    await c.Response.WriteAsJsonAsync(new ErrorResponse("failed", ex.Message), Wire.Json);
                }
            }
        });

        MapApi(app);

        if (webRoot != null && Directory.Exists(webRoot))
        {
            var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                // Built assets carry a content hash in their name; the page that names them must never be cached,
                // or an updated application would keep loading the previous interface.
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
                    ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-cache" : "public, max-age=31536000, immutable",
            });
        }
        else
        {
            app.MapGet("/", () => Results.Text("The interface files are missing (wwwroot). Build the web project: see README.", "text/plain"));
        }

        await app.StartAsync();
        Port = new Uri(app.Urls.First()).Port;
        _app = app;
    }

    private static IResult Json(object? value) => Results.Json(value, Wire.Json);

    private static DateTime? Date(string? s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;

    private void MapApi(WebApplication app)
    {
        var e = _engine;
        app.MapGet("/api/state", () => Json(e.GetState()));
        app.MapGet("/api/matches", (string? from, string? to, long? session, string? counted, int? limit, int? offset) =>
            Json(e.GetMatches(Date(from), Date(to), session, counted switch { "all" => null, "false" => false, _ => true }, limit ?? 200, offset ?? 0)));
        app.MapGet("/api/matches/{id}", (string id) => e.GetMatch(id) is { } m ? Json(m) : Results.NotFound());
        app.MapGet("/api/sessions", () => Json(e.GetSessions()));
        app.MapGet("/api/timeline", (string? from, string? to, string? bucket) => Json(e.GetTimeline(Date(from), Date(to), bucket == "week" ? "week" : "day")));
        app.MapGet("/api/diagnostics", () => Json(e.GetDiagnostics()));

        // The settings alone: what the desktop shell re-reads on every "settings" change (cheaper than /api/state).
        app.MapGet("/api/settings", () => Json(e.Settings));
        app.MapPost("/api/settings", (Dictionary<string, JsonElement> patch) =>
        {
            e.UpdateSettings(patch);
            _shell.SettingsChanged();
            return Json(e.Settings);
        });

        app.MapPost("/api/actions/{name}", async (string name) =>
        {
            switch (name)
            {
                case "scan": e.ScanNow(); return Json(new { ok = true });
                case "reanalyze": e.ReanalyzeAll(); return Json(new { ok = true });
                case "retry-failed": e.RetryFailed(); return Json(new { ok = true });
                case "backup": { var path = e.Backup(); _shell.OpenPath(Path.GetDirectoryName(path)!); return Json(new { ok = true, path }); }
                case "export": { var path = e.ExportCsv(); _shell.OpenPath(Path.GetDirectoryName(path)!); return Json(new { ok = true, path }); }
                case "open-data-folder": _shell.OpenPath(e.GetState().DataDir); return Json(new { ok = true });
                case "open-replay-folder": _shell.OpenPath(e.DemosFolder); return Json(new { ok = true });
                case "pick-folder":
                    var picked = await _shell.PickFolderAsync(e.DemosFolder);
                    if (picked != null)
                    {
                        e.UpdateSettings(new() { ["demosFolder"] = JsonSerializer.SerializeToElement(picked) });
                    }
                    return Json(new { ok = picked != null, path = picked });
                case "reset-folder":
                    e.UpdateSettings(new() { ["demosFolder"] = JsonSerializer.SerializeToElement("") });
                    return Json(new { ok = true });
                default: return Results.NotFound();
            }
        });

        // ---- squad ----
        app.MapPost("/api/squad/connect", async (ConnectBody b) =>
        {
            await e.Sync.ConnectServerAsync(b.Server ?? "", b.DisplayName ?? "", string.IsNullOrWhiteSpace(b.RegistrationKey) ? null : b.RegistrationKey);
            return Json(e.Sync.Status);
        });
        app.MapPost("/api/squad/create", async (NameBody b) => { await e.Sync.CreateSquadAsync(b.Name ?? ""); return Json(e.Sync.Squad); });
        app.MapPost("/api/squad/join", async (JoinBody b) =>
        {
            // A full invite link carries the server's address: joining from a fresh install takes that one paste.
            var (server, code) = SyncService.ParseInvite(b.Code ?? "");
            if (!e.Sync.Configured)
            {
                if (server == null && string.IsNullOrWhiteSpace(b.Server))
                {
                    throw new SyncException("need_server", "Paste the full invite link, or enter the server address as well.");
                }
                await e.Sync.ConnectServerAsync(server ?? b.Server!, b.DisplayName ?? "", string.IsNullOrWhiteSpace(b.RegistrationKey) ? null : b.RegistrationKey);
            }
            else if (server != null && server != e.Sync.Status.Server)
            {
                throw new SyncException("other_server", "This invite is for another server. Disconnect from the current one first.");
            }
            await e.Sync.JoinSquadAsync(code);
            return Json(e.Sync.Squad);
        });
        app.MapPost("/api/squad/leave", async () => { await e.Sync.LeaveSquadAsync(); return Json(new { ok = true }); });
        app.MapPost("/api/squad/rename", async (NameBody b) => { await e.Sync.RenameSquadAsync(b.Name ?? ""); return Json(e.Sync.Squad); });
        app.MapPost("/api/squad/invite", async () => Json(await e.Sync.CreateInviteAsync()));
        app.MapPost("/api/squad/revoke", async (JoinBody b) => { await e.Sync.RevokeInviteAsync(b.Code ?? ""); return Json(new { ok = true }); });
        app.MapPost("/api/squad/remove", async (RemoveBody b) => { await e.Sync.RemoveMemberAsync(b.UserId ?? ""); return Json(new { ok = true }); });
        app.MapPost("/api/squad/disconnect", () => { e.Sync.DisconnectServer(); return Json(new { ok = true }); });
        app.MapPost("/api/squad/resync", () => Json(new { queued = e.Sync.ResyncAll() }));

        // ---- live updates (server-sent events) ----
        app.MapGet("/api/events", (HttpContext c) => Stream(c, _ => true));

        // ---- overlay feed: read-only, no token, for the OBS browser source ----
        app.MapGet("/api/overlay", () => Json(e.GetOverlay()));
        app.MapGet("/api/overlay/events", (HttpContext c) => Stream(c, _ => true));
        app.MapGet("/obs", () => Results.Content(ObsPage.Html, "text/html; charset=utf-8"));
    }

    private async Task Stream(HttpContext c, Func<string, bool> filter)
    {
        c.Response.Headers.ContentType = "text/event-stream";
        c.Response.Headers.CacheControl = "no-cache";
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        _listeners[id] = channel;
        try
        {
            await c.Response.WriteAsync("data: hello\n\n", c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted);
            while (!c.RequestAborted.IsCancellationRequested)
            {
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
                beat.CancelAfter(TimeSpan.FromSeconds(25));
                string line;
                try
                {
                    var topic = await channel.Reader.ReadAsync(beat.Token);
                    if (!filter(topic)) continue;
                    line = $"data: {topic}\n\n";
                }
                catch (OperationCanceledException) when (!c.RequestAborted.IsCancellationRequested)
                {
                    line = ": keep-alive\n\n";
                }
                await c.Response.WriteAsync(line, c.RequestAborted);
                await c.Response.Body.FlushAsync(c.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // the page went away
        }
        finally
        {
            _listeners.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Changed -= Publish;
        foreach (var listener in _listeners.Values)
        {
            listener.Writer.TryComplete();
        }
        if (_app != null)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(limit.Token);
            }
            catch { /* stopping */ }
            await _app.DisposeAsync();
        }
    }

    private sealed record ConnectBody(string? Server, string? DisplayName, string? RegistrationKey);
    private sealed record NameBody(string? Name);
    private sealed record JoinBody(string? Code, string? Server, string? DisplayName, string? RegistrationKey);
    private sealed record RemoveBody(string? UserId);
}
