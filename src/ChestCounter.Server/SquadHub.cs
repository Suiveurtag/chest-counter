using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using ChestCounter.Shared;

namespace ChestCounter.Server;

/// <summary>
/// The live connections. Presence ("online") is simply: this member has an open WebSocket. Every change to a squad is
/// pushed as a full state snapshot: small (four members), and a client that missed one is repaired by the next.
/// </summary>
public sealed class SquadHub(ServerStore store, TimeProvider time, ILogger<SquadHub> log)
{
    private sealed class Connection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Connection>> _byUser = new();

    public bool IsOnline(string userId) => _byUser.TryGetValue(userId, out var set) && !set.IsEmpty;
    public int ConnectionCount => _byUser.Values.Sum(s => s.Count);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Serves one client socket until it closes.</summary>
    public async Task RunAsync(string userId, WebSocket socket, string baseUrl, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var connection = new Connection(socket);
        _byUser.GetOrAdd(userId, _ => new()).TryAdd(id, connection);
        store.Touch(userId, Now);
        try
        {
            // The newcomer gets the state; the others learn that it is online.
            foreach (var squad in store.SquadsOf(userId))
            {
                await BroadcastAsync(squad.SquadId, baseUrl);
            }

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                // Clients have nothing to say on this channel: everything they send goes through the REST API.
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // dropped connection: normal
        }
        finally
        {
            if (_byUser.TryGetValue(userId, out var set))
            {
                set.TryRemove(id, out _);
            }
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
                }
            }
            catch { /* already gone */ }
            store.Touch(userId, Now);
            foreach (var squad in store.SquadsOf(userId))
            {
                await BroadcastAsync(squad.SquadId, baseUrl);
            }
        }
    }

    /// <summary>Sends the current state of a squad to every connected member.</summary>
    public async Task BroadcastAsync(string squadId, string baseUrl)
    {
        var state = store.State(squadId, IsOnline, Now, baseUrl);
        if (state == null)
        {
            return;
        }
        await SendAsync(state.Members.Select(m => m.UserId), new ServerMessage(ServerMessage.SquadState, state));
    }

    public Task NotifyRemovedAsync(IEnumerable<string> userIds, string squadId, string reason) =>
        SendAsync(userIds, new ServerMessage(ServerMessage.SquadRemoved, SquadId: squadId, Reason: reason));

    private async Task SendAsync(IEnumerable<string> userIds, ServerMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(Wire.Serialize(message));
        foreach (var user in userIds)
        {
            if (!_byUser.TryGetValue(user, out var set))
            {
                continue;
            }
            foreach (var connection in set.Values)
            {
                await connection.SendLock.WaitAsync();
                try
                {
                    if (connection.Socket.State == WebSocketState.Open)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
                    }
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "send to {User} failed", user); // its receive loop will clean it up
                }
                finally
                {
                    connection.SendLock.Release();
                }
            }
        }
    }
}
