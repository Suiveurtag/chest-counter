using System.Net.WebSockets;
using System.Text.Json;
using ChestCounter.Engine;
using ChestCounter.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChestCounter.Tests;

/// <summary>A temp folder holding a "Demos" folder and a data folder, deleted at the end of the test.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N")[..12]);
    public string Demos => Path.Combine(Root, "Demos");
    public string Data => Path.Combine(Root, "data");
    public FakeAnalyzer Analyzer { get; } = new();
    public FakeClock Clock { get; } = new();

    public Sandbox()
    {
        Directory.CreateDirectory(Demos);
        Directory.CreateDirectory(Data);
    }

    public LocalDb OpenDb() => new(Path.Combine(Data, "history.db"));

    public ReplayTracker Tracker(LocalDb db, bool sync = false) => new(db, Analyzer, new FakeProbe(), Clock, () => Demos, () => sync);

    public ChestEngine Engine(ISyncTransport? transport = null, IClock? clock = null) => new(new EngineOptions
    {
        DataDir = Data,
        Analyzer = Analyzer,
        Probe = new FakeProbe(),
        Clock = clock ?? Clock,
        Transport = transport ?? new NetworkTransport(),
        DefaultDemosFolder = Demos,
    });

    public static int Total(LocalDb db, string? profile = null) =>
        db.Matches(new LocalDb.MatchFilter(ProfileKey: profile, Limit: int.MaxValue)).Sum(m => m.Chests);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* a handle may linger: temp folder anyway */ }
    }
}

/// <summary>The real server, in memory, on a database file that survives "restarts" of the factory.</summary>
public sealed class TestServer : IDisposable
{
    private WebApplicationFactory<Program> _factory;
    public string Database { get; }
    private readonly Dictionary<string, string> _settings;

    public TestServer(string? database = null, Dictionary<string, string>? settings = null)
    {
        Database = database ?? Path.Combine(Path.GetTempPath(), "cc-tests", "server-" + Guid.NewGuid().ToString("N")[..12], "squads.db");
        _settings = settings ?? new();
        _factory = Create();
    }

    private WebApplicationFactory<Program> Create() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("CC_DATABASE", Database);
        b.UseSetting("CC_PUBLIC_URL", "http://squad.test");
        foreach (var (k, v) in _settings) b.UseSetting(k, v);
    });

    public WebApplicationFactory<Program> Factory => _factory;

    /// <summary>Stops the server and starts a new one on the same database.</summary>
    public void Restart()
    {
        _factory.Dispose();
        _factory = Create();
    }

    public HttpClient Http() => _factory.CreateClient();

    public Transport NewTransport() => new(this);

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(Database)!, recursive: true); } catch { /* temp */ }
    }

    /// <summary>Connects an engine to the in-memory server; <see cref="Offline"/> simulates a lost connection.</summary>
    public sealed class Transport(TestServer server) : ISyncTransport
    {
        public volatile bool Offline;

        public HttpClient CreateHttp(Uri _) => Offline
            ? new HttpClient(new DeadHandler()) { BaseAddress = new Uri("http://squad.test/") }
            : new HttpClient(server.Factory.Server.CreateHandler()) { BaseAddress = new Uri("http://squad.test/") };

        public async Task<WebSocket> ConnectAsync(Uri socket, string token, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            var client = server.Factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
            return await client.ConnectAsync(new Uri("ws://squad.test/ws"), ct);
        }

        private sealed class DeadHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
        }
    }
}

public static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails the test with <paramref name="what"/> otherwise.</summary>
    public static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("timed out waiting for: " + what);
    }
}
