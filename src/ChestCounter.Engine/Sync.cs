using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using ChestCounter.Shared;

namespace ChestCounter.Engine;

/// <summary>How the client reaches a squad server. Replaced in tests by an in-memory server.</summary>
public interface ISyncTransport
{
    HttpClient CreateHttp(Uri server);
    Task<WebSocket> ConnectAsync(Uri socket, string token, CancellationToken ct);
}

public sealed class NetworkTransport : ISyncTransport
{
    public HttpClient CreateHttp(Uri server) => new() { BaseAddress = server, Timeout = TimeSpan.FromSeconds(20) };

    public async Task<WebSocket> ConnectAsync(Uri socket, string token, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await ws.ConnectAsync(socket, ct);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }
}

public sealed class SyncException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record SyncStatus
{
    /// <summary>"disabled" (no server set), "connecting", "online" or "offline".</summary>
    public string State { get; init; } = "disabled";
    public string? Server { get; init; }
    public string? UserId { get; init; }
    public string? DisplayName { get; init; }
    public string? SquadId { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastSyncUtc { get; init; }
    /// <summary>Results waiting to be uploaded.</summary>
    public int Pending { get; init; }
    /// <summary>Results the server refused (their reason is in the diagnostics).</summary>
    public int Refused { get; init; }
}

/// <summary>
/// Sends this PC's own results to the squad server and receives the squad's state.
///
/// Uploads go through a durable outbox: a result is queued in the same transaction that stores the match, sent when
/// the server is reachable, and only removed once the server confirmed it. Uploads are upserts keyed by
/// (match, player), so sending one twice changes nothing. Solo tracking never waits for any of this.
/// </summary>
public sealed class SyncService : IDisposable
{
    private const string KServer = "sync.server", KToken = "sync.token", KUser = "sync.userId", KName = "sync.name",
        KSquad = "sync.squadId", KState = "sync.squadState", KLastSync = "sync.lastSyncUtc", KPayload = "sync.payload";

    /// <summary>What uploads carry. Bump when a field is added, so the history is sent once more with it. 2: treasures.</summary>
    private const string CurrentPayload = "2";

    private readonly LocalDb _db;
    private readonly ISyncTransport _transport;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _http = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private CancellationTokenSource? _connection;
    private volatile string _state = "disabled";
    private volatile string? _lastError;
    private volatile SquadStateDto? _squad;

    public SyncService(LocalDb db, ISyncTransport transport, IClock clock)
    {
        _db = db;
        _transport = transport;
        _clock = clock;
        if (_db.GetSetting(KState) is { } cached)
        {
            try { _squad = Wire.Deserialize<SquadStateDto>(cached); } catch { /* stale cache */ }
        }
        _state = Configured ? "offline" : "disabled";
    }

    public event Action? Changed;

    public bool Configured => !string.IsNullOrEmpty(_db.GetSetting(KServer)) && !string.IsNullOrEmpty(_db.GetSetting(KToken));

    /// <summary>Last squad state received (kept across restarts, so the squad page is not empty while offline).</summary>
    public SquadStateDto? Squad => _db.GetSetting(KSquad) == null ? null : _squad;

    public SyncStatus Status
    {
        get
        {
            var (pending, refused) = _db.OutboxCounts();
            return new SyncStatus
            {
                State = Configured ? _state : "disabled",
                Server = _db.GetSetting(KServer),
                UserId = _db.GetSetting(KUser),
                DisplayName = _db.GetSetting(KName),
                SquadId = _db.GetSetting(KSquad),
                LastError = _lastError,
                LastSyncUtc = _db.GetSetting(KLastSync) is { } s ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
                Pending = pending,
                Refused = refused,
            };
        }
    }

    // ---- lifecycle --------------------------------------------------------------------------------------------

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Something to do: a result was queued, the server was set, the network may be back.</summary>
    public void Wake()
    {
        try { _signal.Release(); } catch (SemaphoreFullException) { /* already asked */ }
    }

    private void SetState(string state, string? error = null)
    {
        var changed = _state != state || _lastError != error;
        _state = state;
        _lastError = error;
        if (changed) Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            if (!Configured)
            {
                SetState("disabled");
                try { await _signal.WaitAsync(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                SetState("connecting", _lastError);
                _connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await SessionAsync(_connection.Token, () => backoff = TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SetState("offline", ex is SyncException se ? se.Message : Describe(ex));
            }

            if (ct.IsCancellationRequested) return;
            if (Configured && _state != "offline") SetState("offline", _lastError);
            try { await _signal.WaitAsync(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException or WebSocketException or TaskCanceledException or IOException => "server unreachable",
        _ => ex.Message,
    };

    /// <summary>One connection: reconcile over REST, then listen on the WebSocket until it drops.</summary>
    private async Task SessionAsync(CancellationToken ct, Action connected)
    {
        await ReconcileAsync(ct);
        var server = new Uri(_db.GetSetting(KServer)!);
        var socketUri = new UriBuilder(new Uri(server, "ws")) { Scheme = server.Scheme == "https" ? "wss" : "ws" }.Uri;
        using var ws = await _transport.ConnectAsync(socketUri, _db.GetSetting(KToken)!, ct);
        SetState("online");
        connected();

        var buffer = new byte[64 * 1024];
        var receive = ReceiveAsync(ws, buffer, ct);
        var lastReconcile = _clock.UtcNow;
        // One waiter at a time on the signal, cancelled with the session: an abandoned waiter would swallow a wake-up.
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelWaiter = new Deferred(waiting.Cancel);
        Task<bool>? wake = null;
        while (true)
        {
            wake ??= _signal.WaitAsync(TimeSpan.FromMinutes(5), waiting.Token);
            var done = await Task.WhenAny(receive, wake);
            if (done == receive)
            {
                var text = await receive; // throws when the socket failed
                if (text == null)
                {
                    throw new WebSocketException("closed by the server");
                }
                Handle(text);
                receive = ReceiveAsync(ws, buffer, ct);
                continue;
            }
            // Woken (new result queued) or five quiet minutes: upload what is waiting, and resync now and then.
            var woken = await wake;
            wake = null;
            await FlushOutboxAsync(ct);
            if (!woken || _clock.UtcNow - lastReconcile > TimeSpan.FromMinutes(5))
            {
                await RefreshSquadAsync(ct);
                lastReconcile = _clock.UtcNow;
            }
        }
    }

    private sealed class Deferred(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private static async Task<string?> ReceiveAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }

    private void Handle(string text)
    {
        ServerMessage? message;
        try { message = Wire.Deserialize<ServerMessage>(text); } catch { return; }
        var mine = _db.GetSetting(KSquad);
        switch (message?.Type)
        {
            case ServerMessage.SquadState when message.State != null && message.State.SquadId == mine:
                StoreSquad(message.State);
                break;
            case ServerMessage.SquadRemoved when message.SquadId == mine:
                _db.SetSetting(KSquad, null);
                StoreSquad(null);
                break;
        }
    }

    private void StoreSquad(SquadStateDto? state)
    {
        // An older snapshot can arrive after a newer one (REST answer racing a broadcast): keep the newest.
        if (state != null && _squad != null && _squad.SquadId == state.SquadId && state.Revision < _squad.Revision)
        {
            return;
        }
        _squad = state;
        _db.SetSetting(KState, state == null ? null : Wire.Serialize(state));
        Changed?.Invoke();
    }

    /// <summary>Tells the server who we are, uploads everything waiting, and fetches the squad's state.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var me = await SendAsync<MeResponse>(HttpMethod.Put, "api/v1/me", new HelloRequest(_db.GetSetting(KName), _db.Accounts()), ct);
        var squad = _db.GetSetting(KSquad);
        if (squad != null && me!.Squads.All(s => s.SquadId != squad))
        {
            _db.SetSetting(KSquad, null); // removed from it, or it was deleted, while this PC was away
            StoreSquad(null);
            squad = null;
        }
        if (squad == null && me!.Squads.Count > 0)
        {
            _db.SetSetting(KSquad, me.Squads[0].SquadId); // joined from another install of the same identity
        }
        if (_db.GetSetting(KPayload) != CurrentPayload)
        {
            // Results uploaded by an older version lack fields the squad now shows (treasures): send them again once.
            _db.EnqueueAll(_clock.UtcNow);
            _db.SetSetting(KPayload, CurrentPayload);
        }
        await FlushOutboxAsync(ct);
        await RefreshSquadAsync(ct);
    }

    /// <summary>Uploads the queued results. Returns how many the server took.</summary>
    public async Task<int> FlushOutboxAsync(CancellationToken ct = default)
    {
        var sent = 0;
        while (true)
        {
            var batch = _db.PendingUploads(50);
            if (batch.Count == 0)
            {
                break;
            }
            UploadResponse? response;
            try
            {
                var payload = new UploadRequest(batch.Select(b => Wire.Deserialize<ContributionDto>(b.Payload)!).ToList());
                response = await SendAsync<UploadResponse>(HttpMethod.Put, "api/v1/contributions", payload, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.UploadFailed(batch, Describe(ex));
                throw;
            }

            var results = response!.Results.ToDictionary(r => (r.MatchId, r.PlayerId), r => r.Status);
            foreach (var row in batch)
            {
                switch (results.GetValueOrDefault((row.MatchId, row.PlayerId), UploadStatus.Invalid))
                {
                    case UploadStatus.Accepted:
                    case UploadStatus.Stale: // the server already holds a newer analysis of this match: nothing to add
                        _db.UploadDone(row);
                        sent++;
                        break;
                    case var refused:
                        _db.UploadRefused(row, refused);
                        _db.Log("warn", "sync", $"the server refused the result of match {row.MatchId} for player {row.PlayerId}: {refused}", _clock.UtcNow);
                        break;
                }
            }
            _db.SetSetting(KLastSync, _clock.UtcNow.ToString("O"));
        }
        if (sent > 0) Changed?.Invoke();
        return sent;
    }

    public async Task RefreshSquadAsync(CancellationToken ct = default)
    {
        if (_db.GetSetting(KSquad) is not { } squad)
        {
            StoreSquad(null);
            return;
        }
        StoreSquad(await SendAsync<SquadStateDto>(HttpMethod.Get, $"api/v1/squads/{squad}", null, ct));
    }

    // ---- actions from the interface ----------------------------------------------------------------------------

    public static string NormalizeServer(string url)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || url.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + url;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new SyncException("bad_url", "That is not a valid server address.");
        }
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static readonly Regex InviteLink = new(@"^(?<server>https?://[^/\s]+)/join/(?<code>[A-Za-z0-9-]+)/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Splits "https://server/join/ABCD-EFGH" into server and code; a bare code gives a null server.</summary>
    public static (string? Server, string Code) ParseInvite(string codeOrLink)
    {
        var text = codeOrLink.Trim();
        var link = InviteLink.Match(text);
        return link.Success ? (NormalizeServer(link.Groups["server"].Value), link.Groups["code"].Value.ToUpperInvariant()) : (null, text.ToUpperInvariant());
    }

    /// <summary>Registers this PC on a server (no account, no password: the server issues a device token).</summary>
    public async Task ConnectServerAsync(string url, string displayName, string? registrationKey = null, CancellationToken ct = default)
    {
        var server = NormalizeServer(url);
        displayName = displayName.Trim();
        if (displayName.Length is < 1 or > 32)
        {
            throw new SyncException("bad_name", "Choose a display name of 1 to 32 characters.");
        }
        if (Configured && _db.GetSetting(KServer) == server)
        {
            _db.SetSetting(KName, displayName);
            Wake();
            return;
        }
        if (Configured)
        {
            throw new SyncException("already_connected", "Disconnect from the current server first.");
        }

        using var http = _transport.CreateHttp(new Uri(server));
        RegisterResponse? registered;
        try
        {
            using var response = await http.PostAsJsonAsync("api/v1/devices", new RegisterRequest(displayName, registrationKey), Wire.Json, ct);
            await EnsureOk(response, ct);
            registered = await response.Content.ReadFromJsonAsync<RegisterResponse>(Wire.Json, ct);
        }
        catch (HttpRequestException)
        {
            throw new SyncException("unreachable", "The server could not be reached. Check the address and that it is running.");
        }

        _db.SetSetting(KServer, server);
        _db.SetSetting(KToken, registered!.Token);
        _db.SetSetting(KUser, registered.UserId);
        _db.SetSetting(KName, displayName);
        _db.EnqueueAll(_clock.UtcNow); // the whole local history, once
        _db.SetSetting(KPayload, CurrentPayload);
        Wake();
        Changed?.Invoke();
    }

    /// <summary>Forgets the server on this PC. Local history is untouched; what was uploaded stays on the server.</summary>
    public void DisconnectServer()
    {
        foreach (var key in new[] { KServer, KToken, KUser, KSquad, KState, KLastSync, KPayload })
        {
            _db.SetSetting(key, null);
        }
        _db.ClearOutbox();
        _squad = null;
        _connection?.Cancel();
        SetState("disabled");
        Changed?.Invoke();
        Wake();
    }

    public async Task CreateSquadAsync(string name, CancellationToken ct = default) =>
        Adopt(await SendAsync<SquadStateDto>(HttpMethod.Post, "api/v1/squads", new CreateSquadRequest(name), ct));

    public async Task JoinSquadAsync(string code, CancellationToken ct = default) =>
        Adopt(await SendAsync<SquadStateDto>(HttpMethod.Post, "api/v1/squads/join", new JoinSquadRequest(ParseInvite(code).Code), ct));

    private void Adopt(SquadStateDto? state)
    {
        _db.SetSetting(KSquad, state!.SquadId);
        _squad = null;
        StoreSquad(state);
        Wake();
    }

    public async Task LeaveSquadAsync(CancellationToken ct = default)
    {
        if (_db.GetSetting(KSquad) is { } squad)
        {
            await SendAsync<object>(HttpMethod.Post, $"api/v1/squads/{squad}/leave", null, ct);
            _db.SetSetting(KSquad, null);
            StoreSquad(null);
        }
    }

    public async Task RenameSquadAsync(string name, CancellationToken ct = default) =>
        StoreSquad(await SendAsync<SquadStateDto>(HttpMethod.Put, $"api/v1/squads/{RequireSquad()}", new RenameSquadRequest(name), ct));

    public async Task<InviteDto> CreateInviteAsync(CancellationToken ct = default)
    {
        var invite = await SendAsync<InviteDto>(HttpMethod.Post, $"api/v1/squads/{RequireSquad()}/invites", new CreateInviteRequest(), ct);
        await RefreshSquadAsync(ct);
        return invite!;
    }

    public async Task RevokeInviteAsync(string code, CancellationToken ct = default)
    {
        await SendAsync<object>(HttpMethod.Delete, $"api/v1/squads/{RequireSquad()}/invites/{Uri.EscapeDataString(code)}", null, ct);
        await RefreshSquadAsync(ct);
    }

    public async Task RemoveMemberAsync(string userId, CancellationToken ct = default)
    {
        await SendAsync<object>(HttpMethod.Delete, $"api/v1/squads/{RequireSquad()}/members/{Uri.EscapeDataString(userId)}", null, ct);
        await RefreshSquadAsync(ct);
    }

    /// <summary>Queues the whole local history again (the server keeps one result per match and player anyway).</summary>
    public int ResyncAll()
    {
        var count = _db.EnqueueAll(_clock.UtcNow);
        Wake();
        return count;
    }

    private string RequireSquad() => _db.GetSetting(KSquad) ?? throw new SyncException("no_squad", "You are not in a squad.");

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!Configured)
        {
            throw new SyncException("not_connected", "No squad server is set.");
        }
        await _http.WaitAsync(ct);
        try
        {
            using var http = _transport.CreateHttp(new Uri(_db.GetSetting(KServer)!));
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _db.GetSetting(KToken));
            if (body != null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: Wire.Json);
            }
            using var response = await http.SendAsync(request, ct);
            await EnsureOk(response, ct);
            if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength == 0)
            {
                return default;
            }
            return await response.Content.ReadFromJsonAsync<T>(Wire.Json, ct);
        }
        catch (HttpRequestException)
        {
            throw new SyncException("unreachable", "The squad server could not be reached.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException("unreachable", "The squad server did not answer in time.");
        }
        finally
        {
            _http.Release();
        }
    }

    private static async Task EnsureOk(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        ErrorResponse? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Wire.Json, ct); } catch { /* not JSON */ }
        throw new SyncException(error?.Error ?? $"http_{(int)response.StatusCode}", error?.Message ?? $"The server answered {(int)response.StatusCode}.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* stopping */ }
    }
}
