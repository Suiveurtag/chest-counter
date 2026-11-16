using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using ChestCounter.Server;
using ChestCounter.Shared;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

// "--healthcheck": used by the container's HEALTHCHECK (the runtime image has no curl). Exit code 0 = healthy.
if (args.Contains("--healthcheck"))
{
    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var port = new Uri((Environment.GetEnvironmentVariable("CC_LISTEN") ?? "http://0.0.0.0:8080").Split(';', ',')[0].Replace("0.0.0.0", "127.0.0.1").Replace("+", "127.0.0.1").Replace("*", "127.0.0.1")).Port;
        return (await probe.GetAsync($"http://127.0.0.1:{port}/healthz")).IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
var options = ServerOptions.FromConfiguration(builder.Configuration);
if (builder.Configuration["urls"] == null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") == null)
{
    builder.WebHost.UseUrls(options.Listen.Split(';', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerStore>();
builder.Services.AddSingleton<SquadHub>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = Wire.Json.DefaultIgnoreCondition;
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind a reverse proxy (TLS termination): believe its X-Forwarded-* headers. Set CC_TRUST_PROXY=1 only then.
    if (builder.Configuration["CC_TRUST_PROXY"] == "1")
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    }
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "local";
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("register", c =>
        RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1) }));
});

var app = builder.Build();
var store = app.Services.GetRequiredService<ServerStore>();
var hub = app.Services.GetRequiredService<SquadHub>();
var clock = app.Services.GetRequiredService<TimeProvider>();
var secret = options.ResolveSecret(app.Logger);
DateTime Now() => clock.GetUtcNow().UtcDateTime;

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// Errors always leave as { error, message }.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (StoreException ex)
    {
        context.Response.StatusCode = ex.Status;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(ex.Code, ex.Message), Wire.Json);
    }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new ErrorResponse("bad_request", "The request could not be read."), Wire.Json);
    }
});

string HashToken(string token) => Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(token)));

string BaseUrl(HttpContext c) => options.PublicUrl.Length > 0 ? options.PublicUrl : $"{c.Request.Scheme}://{c.Request.Host}";

UserRow? Authenticate(HttpContext c)
{
    var header = c.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }
    var user = store.FindUserByTokenHash(HashToken(header[7..].Trim()));
    if (user != null && (user.LastSeenUtc == null || Now() - user.LastSeenUtc > TimeSpan.FromMinutes(1)))
    {
        store.Touch(user.Id, Now());
    }
    return user;
}

static IResult Unauthorized() => Results.Json(new ErrorResponse("unauthorized", "This device is not registered on this server."), Wire.Json, statusCode: 401);
static IResult Fail(int status, string code, string message) => Results.Json(new ErrorResponse(code, message), Wire.Json, statusCode: status);

static string? CleanName(string? name, int max)
{
    var cleaned = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
    return cleaned.Length is >= 1 && cleaned.Length <= max ? cleaned : null;
}

// ---- health and landing ---------------------------------------------------------------------------------------

app.MapGet("/healthz", () => store.Healthy()
    ? Results.Json(new { status = "ok", protocol = Wire.ProtocolVersion, timeUtc = Now() }, Wire.Json)
    : Results.Json(new { status = "database_unavailable" }, Wire.Json, statusCode: 503));

app.MapGet("/", () => Results.Text("Chest Counter squad server. See /healthz.", "text/plain"));

app.MapGet("/join/{code}", (string code, HttpContext c) =>
{
    var shown = System.Net.WebUtility.HtmlEncode(code.ToUpperInvariant());
    var link = System.Net.WebUtility.HtmlEncode($"{BaseUrl(c)}/join/{code.ToUpperInvariant()}");
    var valid = store.SquadOfInvite(code, Now()) != null;
    return Results.Content($$"""
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Squad invite</title>
        <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0c14;color:#e9eaf2;font:16px/1.5 system-ui,sans-serif}
        main{max-width:420px;padding:32px;text-align:center}code{display:block;font-size:34px;letter-spacing:.12em;margin:20px 0;color:#7ee0ff}
        p{color:#9a9db4}small{color:#6c6f86;word-break:break-all}</style>
        <main><h1>{{(valid ? "You are invited to a squad" : "This invite is no longer valid")}}</h1>
        <code>{{shown}}</code>
        <p>{{(valid ? "Open Chest Counter Squad, go to Squad, choose Join and paste this page's address or the code above." : "Ask your squad for a new invite.")}}</p>
        <small>{{link}}</small></main></html>
        """, "text/html");
});

// ---- devices --------------------------------------------------------------------------------------------------

var api = app.MapGroup("/api/v1");

api.MapPost("/devices", (RegisterRequest body) =>
{
    if (options.RegistrationKey.Length > 0 && !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(body.RegistrationKey ?? ""), Encoding.UTF8.GetBytes(options.RegistrationKey)))
    {
        return Fail(403, "registration_key_required", "This server needs a registration key. Ask its owner.");
    }
    if (CleanName(body.DisplayName, 32) is not { } name)
    {
        return Fail(400, "bad_name", "Choose a display name of 1 to 32 characters.");
    }
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var id = store.CreateUser(name, HashToken(token), Now());
    return Results.Json(new RegisterResponse(id, token, Wire.ProtocolVersion), Wire.Json);
}).RequireRateLimiting("register");

api.MapPut("/me", async (HelloRequest body, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    var changed = false;
    if (CleanName(body.DisplayName, 32) is { } name && name != user.DisplayName)
    {
        store.SetDisplayName(user.Id, name);
        user = user with { DisplayName = name };
        changed = true;
    }
    foreach (var account in (body.Accounts ?? new()).Take(16))
    {
        if (CleanName(account.PlayerId, 64) is { } playerId)
        {
            changed |= store.ClaimAccount(user.Id, playerId.ToLowerInvariant(), CleanName(account.PlayerName, 64) ?? "", Now());
        }
    }
    var squads = store.SquadsOf(user.Id);
    if (changed)
    {
        foreach (var s in squads)
        {
            store.Bump(s.SquadId);
            await hub.BroadcastAsync(s.SquadId, BaseUrl(c));
        }
    }
    return Results.Json(new MeResponse(user.Id, user.DisplayName, store.AccountsOf(user.Id), squads), Wire.Json);
});

// ---- squads ---------------------------------------------------------------------------------------------------

api.MapPost("/squads", (CreateSquadRequest body, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    if (CleanName(body.Name, 40) is not { } name) return Fail(400, "bad_name", "Choose a squad name of 1 to 40 characters.");
    if (store.SquadsOf(user.Id).Count >= 8) return Fail(409, "too_many_squads", "Leave a squad before creating another one.");
    var id = store.CreateSquad(name, user.Id, Now());
    return Results.Json(store.State(id, hub.IsOnline, Now(), BaseUrl(c)), Wire.Json);
});

api.MapPost("/squads/join", async (JoinSquadRequest body, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    var id = store.Join(body.Code ?? "", user.Id, Now());
    await hub.BroadcastAsync(id, BaseUrl(c));
    return Results.Json(store.State(id, hub.IsOnline, Now(), BaseUrl(c)), Wire.Json);
});

api.MapGet("/squads/{id}", (string id, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    if (!store.IsMember(id, user.Id)) return Fail(404, "not_a_member", "You are not a member of this squad.");
    return Results.Json(store.State(id, hub.IsOnline, Now(), BaseUrl(c)), Wire.Json);
});

api.MapPut("/squads/{id}", async (string id, RenameSquadRequest body, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    if (store.RoleOf(id, user.Id) != "owner") return Fail(403, "owner_only", "Only the squad's owner can rename it.");
    if (CleanName(body.Name, 40) is not { } name) return Fail(400, "bad_name", "Choose a squad name of 1 to 40 characters.");
    store.RenameSquad(id, name);
    await hub.BroadcastAsync(id, BaseUrl(c));
    return Results.Json(store.State(id, hub.IsOnline, Now(), BaseUrl(c)), Wire.Json);
});

api.MapPost("/squads/{id}/leave", async (string id, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    if (!store.IsMember(id, user.Id)) return Results.NoContent();
    var gone = store.RemoveMember(id, user.Id);
    if (!gone) await hub.BroadcastAsync(id, BaseUrl(c));
    return Results.NoContent();
});

api.MapDelete("/squads/{id}/members/{memberId}", async (string id, string memberId, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    if (store.RoleOf(id, user.Id) != "owner") return Fail(403, "owner_only", "Only the squad's owner can remove a member.");
    if (memberId == user.Id) return Fail(400, "use_leave", "To leave the squad yourself, use Leave.");
    if (!store.IsMember(id, memberId)) return Results.NoContent();
    store.RemoveMember(id, memberId);
    await hub.NotifyRemovedAsync(new[] { memberId }, id, "removed");
    await hub.BroadcastAsync(id, BaseUrl(c));
    return Results.NoContent();
});

api.MapPost("/squads/{id}/invites", async (string id, CreateInviteRequest body, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    if (!store.IsMember(id, user.Id)) return Fail(404, "not_a_member", "You are not a member of this squad.");
    var now = Now();
    if (store.ActiveInvites(id, now, BaseUrl(c)).Count >= 10) return Fail(409, "too_many_invites", "Revoke an invite before creating another one.");
    var ttl = Math.Clamp(body.TtlHours ?? options.InviteTtlHours, 1, 24 * 90);
    var uses = Math.Clamp(body.MaxUses ?? options.MaxSquadSize - 1, 1, options.MaxSquadSize);
    var invite = store.CreateInvite(id, user.Id, now, ttl, uses, BaseUrl(c));
    await hub.BroadcastAsync(id, BaseUrl(c));
    return Results.Json(invite, Wire.Json);
});

api.MapDelete("/squads/{id}/invites/{code}", async (string id, string code, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    if (!store.IsMember(id, user.Id)) return Fail(404, "not_a_member", "You are not a member of this squad.");
    if (store.RevokeInvite(id, code)) await hub.BroadcastAsync(id, BaseUrl(c));
    return Results.NoContent();
});

// ---- results --------------------------------------------------------------------------------------------------

api.MapPut("/contributions", async (UploadRequest body, HttpContext c) =>
{
    if (Authenticate(c) is not { } user) return Unauthorized();
    var list = body.Contributions ?? new();
    if (list.Count > 200) return Fail(413, "too_many", "Send at most 200 results per request.");

    var results = new List<UploadResult>();
    var accepted = false;
    foreach (var item in list)
    {
        var valid = item is { MatchId.Length: >= 1 and <= 128, PlayerId.Length: >= 1 and <= 64 }
                    && item.ChestsOpened is >= 0 and <= 5000 && item.ChestsDestroyed >= 0 && item.ChestsDestroyed <= item.ChestsOpened
                    && item.AmmoBoxesOpened is >= 0 and <= 5000 && item.OtherContainersOpened is >= 0 and <= 5000
                    && item.ParserVersion >= 0 && (item.Roster?.Count ?? 0) <= 32
                    && (item.Treasures?.Count ?? 0) <= 64 && (item.Treasures?.All(t => t != null && t.RawItem != null) ?? true);
        if (!valid)
        {
            results.Add(new UploadResult(item?.MatchId ?? "", item?.PlayerId ?? "", UploadStatus.Invalid));
            continue;
        }
        // Normalize what identifies things, and bound what is only text.
        var clean = item! with
        {
            MatchId = item.MatchId.Trim(),
            PlayerId = item.PlayerId.Trim().ToLowerInvariant(),
            PlayerName = CleanName(item.PlayerName, 64) ?? "",
            Playlist = CleanName(item.Playlist, 96),
            MatchStartUtc = item.MatchStartUtc?.ToUniversalTime(),
            Roster = (item.Roster ?? new()).Select(r => new RosterEntry(CleanName(r.PlayerId, 64)?.ToLowerInvariant(), CleanName(r.Name, 64) ?? "", r.IsBot, Math.Clamp(r.ObservedChests, 0, 5000))).ToList(),
            Treasures = item.Treasures?.Select(t => new TreasureDto(CleanName(t.PrizeId, 128), CleanName(t.RawItem, 200) ?? "",
                t.TimeSeconds is { } s && float.IsFinite(s) ? Math.Clamp(s, 0, 7200) : null, t.FromGoldenChest)).ToList(),
        };
        var status = store.Upsert(user.Id, clean, Now());
        accepted |= status == UploadStatus.Accepted;
        results.Add(new UploadResult(item.MatchId, item.PlayerId, status));
    }

    if (accepted)
    {
        store.MarkSynced(user.Id, Now());
        foreach (var squad in store.SquadsOf(user.Id))
        {
            await hub.BroadcastAsync(squad.SquadId, BaseUrl(c));
        }
    }
    return Results.Json(new UploadResponse(results, Now()), Wire.Json);
});

// ---- live channel ---------------------------------------------------------------------------------------------

app.Map("/ws", async (HttpContext c) =>
{
    if (!c.WebSockets.IsWebSocketRequest)
    {
        c.Response.StatusCode = 400;
        return;
    }
    if (Authenticate(c) is not { } user)
    {
        c.Response.StatusCode = 401;
        return;
    }
    using var socket = await c.WebSockets.AcceptWebSocketAsync();
    await hub.RunAsync(user.Id, socket, BaseUrl(c), c.RequestAborted);
});

// ---- administration (only when CC_ADMIN_TOKEN is set) -----------------------------------------------------------

bool IsAdmin(HttpContext c)
{
    var header = c.Request.Headers.Authorization.ToString();
    return options.AdminToken.Length > 0 && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..].Trim()), Encoding.UTF8.GetBytes(options.AdminToken));
}

api.MapGet("/admin/stats", (HttpContext c) => IsAdmin(c)
    ? Results.Json(new { stats = store.AdminStats(), connections = hub.ConnectionCount, timeUtc = Now(), options.MaxSquadSize, options.InviteTtlHours }, Wire.Json)
    : Fail(404, "not_found", "Not found."));

// A player id is kept by the first member who uploads it. This lets the server's owner undo a wrong claim.
api.MapDelete("/admin/accounts/{playerId}", (string playerId, HttpContext c) => IsAdmin(c)
    ? Results.Json(new { released = store.ReleaseAccount(playerId.ToLowerInvariant()) }, Wire.Json)
    : Fail(404, "not_found", "Not found."));

app.Logger.LogInformation("Chest Counter squad server: database {Database}, max squad size {Size}", options.Database, options.MaxSquadSize);
await app.RunAsync();
return 0;

public partial class Program;
