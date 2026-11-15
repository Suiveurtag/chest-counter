// Development host: runs the engine and the local API WITHOUT the desktop window and WITHOUT Fortnite, on fixture
// "replays" (small JSON files, see tests/FakeReplays.cs), so the interface can be worked on in a browser.
//
//   dotnet run --project tools/DevHost -- [--data <dir>] [--port 47821] [--server http://localhost:8090] [--empty]
//
// Everything it shows is FIXTURE DATA. It is not part of the shipped application.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChestCounter.Engine;
using ChestCounter.LocalApi;
using ChestCounter.Shared;
using ChestCounter.Tests;

string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var data = Arg("--data") ?? Path.Combine(Path.GetTempPath(), "cc-devhost");
var port = int.TryParse(Arg("--port"), out var p) ? p : LocalServer.DefaultPort;
var squadServer = Arg("--server");
var demos = Path.Combine(data, "Demos");
var webRoot = Arg("--web") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ChestCounter.Host", "wwwroot"));
Directory.CreateDirectory(demos);

const string Me = "11110000111100001111000011110000";
var mates = new[] { ("Mika", "2222000022220000222200002222aaaa"), ("Soren", "3333000033330000333300003333bbbb"), ("Inès", "4444000044440000444400004444cccc") };
var sharedMatches = new List<(string Id, DateTime Start, int Players, string Playlist)>();

// Fixture treasures: rolled per chest with the hunt's odds (golden prizes carry the real prize ids of their week).
var huntWeeks = new (DateTime From, string Pickaxe, string Jam, string Loading)[]
{
    (new DateTime(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc), "DelilahPrize:dpid_item2", "DelilahPrize:dpid_item3", "DelilahPrize:dpid_item4"),
    (new DateTime(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc), "DelilahPrize:dpid_item15", "DelilahPrize:dpid_item16", "DelilahPrize:dpid_item17"),
    (new DateTime(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc), "DelilahPrize:dpid_item20", "DelilahPrize:dpid_item21", "DelilahPrize:dpid_item22"),
};
List<TreasureInfo> RollTreasures(Random rng, DateTime start, int chests, int duration)
{
    var list = new List<TreasureInfo>();
    var week = huntWeeks.LastOrDefault(w => start >= w.From);
    if (week.From == default) return list;
    string[] gizmos = { "WID_RelicExtractor_Athena", "WID_RelicFinder_Athena", "WID_LuckyLocator_Athena", "Unknown:fixture" };
    string[] sources = { Kinds.Chest, Kinds.Chest, Kinds.Chest, Kinds.Chest, Kinds.SpriteChest, Kinds.BuriedChest, Kinds.RareChest };
    for (var i = 0; i < chests; i++)
    {
        var t = (float)Math.Round(duration * rng.NextDouble());
        var src = sources[rng.Next(sources.Length)];
        var roll = rng.NextDouble();
        if (roll < 1.0 / 127)
        {
            var g = gizmos[rng.Next(gizmos.Length)];
            list.Add(new TreasureInfo(g.StartsWith("Unknown") ? g : null, g.StartsWith("Unknown") ? "GA_TreasureHandler" : g, t, src, false, true, rng.NextDouble() < 0.2));
        }
        else if (roll < 1.0 / 127 + 1.0 / 272) list.Add(new TreasureInfo(week.Loading, week.Loading, t, Kinds.Chest, true, true));
        else if (roll < 1.0 / 127 + 1.0 / 272 + 1.0 / 391) list.Add(new TreasureInfo(week.Jam, week.Jam, t, Kinds.Chest, true, true));
        else if (roll < 1.0 / 127 + 1.0 / 272 + 1.0 / 391 + 1.0 / 568) list.Add(new TreasureInfo(week.Pickaxe, week.Pickaxe, t, Kinds.Chest, true, true));
    }
    return list.OrderBy(x => x.TimeSeconds).ToList();
}

if (!args.Contains("--empty") && !Directory.EnumerateFiles(demos, "*.replay").Any())
{
    var rng = new Random(7);
    var playlists = new[] { "Playlist_DefaultSquad", "Playlist_NoBuildBR_Squad", "Playlist_DefaultDuo", "Playlist_DefaultSolo" };
    var n = 0;
    for (var day = 44; day >= 0; day--)
    {
        if (rng.NextDouble() < 0.35) continue;
        var games = rng.Next(1, 5);
        var start = DateTime.UtcNow.Date.AddDays(-day).AddHours(18 + rng.Next(0, 3));
        for (var g = 0; g < games; g++)
        {
            var playlist = playlists[rng.Next(playlists.Length)];
            var team = playlist.Contains("Squad") ? rng.Next(2, 4) : playlist.Contains("Duo") ? 1 : 0;
            var id = $"fixture-{++n:000}";
            var roster = mates.Take(team).Select(m => new RosterEntry(m.Item2, m.Item1, false, rng.Next(0, 9))).ToList();
            if (team > 0) sharedMatches.Add((id, start, team, playlist));
            var chests = rng.Next(6, 62);
            var duration = rng.Next(600, 1500);
            new FakeReplay
            {
                SessionId = id, PlayerId = Me, PlayerName = "Nova", StartUtc = start, Duration = duration, Playlist = playlist,
                Chests = chests, Destroyed = rng.Next(0, 4), Ammo = rng.Next(2, 16), Other = rng.Next(0, 5), Roster = roster,
                VehicleLoot = rng.NextDouble() < 0.06 ? 3 : 0, ChestEvents = true,
                Placement = rng.NextDouble() < 0.18 ? 1 : rng.Next(2, playlist.Contains("Solo") ? 100 : playlist.Contains("Duo") ? 50 : 25),
                TotalPlayers = playlist.Contains("Solo") ? 100 : playlist.Contains("Duo") ? 50 : 25,
                Treasures = RollTreasures(rng, start, chests, duration),
                TeamFinds = team > 0 && start >= huntWeeks[0].From && rng.NextDouble() < 0.25
                    ? new() { new TeamTreasureInfo(null, "GA_TreasureHandler", (float)rng.Next(60, duration), roster[0].Name, false) } : new(),
            }.Write(demos, id, TimeSpan.FromDays(day) + TimeSpan.FromHours(1));
            start = start.AddMinutes(rng.Next(22, 35));
        }
    }
    new FakeReplay { SessionId = "fixture-og", PlayerId = Me, PlayerName = "Nova", Playlist = "Playlist_Figment_Squad", Chests = 20 }.Write(demos, "fixture-og", TimeSpan.FromDays(3));
    new FakeReplay { Corrupt = true }.Write(demos, "fixture-corrupt", TimeSpan.FromDays(5));
    new FakeReplay { Live = true, KeyMissing = true }.Write(demos, "fixture-crashed", TimeSpan.FromDays(9));
}

using var engine = new ChestEngine(new EngineOptions
{
    DataDir = data, Analyzer = new FakeAnalyzer { Version = 17 }, Probe = new FakeProbe(), DefaultDemosFolder = demos, OnlineContent = true,
});
engine.Start();
await using var local = new LocalServer(engine, new NoShell(), token: "dev");
await local.StartAsync(webRoot, port);

if (squadServer != null && !engine.Sync.Configured)
{
    // Three fixture teammates register on the real server and upload their own results for the shared matches.
    await engine.Sync.ConnectServerAsync(squadServer, "Nova");
    await engine.Sync.CreateSquadAsync("Loot Goblins");
    var invite = await engine.Sync.CreateInviteAsync();
    var rng = new Random(11);
    for (var i = 0; i < mates.Length; i++)
    {
        using var http = new HttpClient { BaseAddress = new Uri(SyncService.NormalizeServer(squadServer)) };
        var reg = await (await http.PostAsJsonAsync("api/v1/devices", new RegisterRequest(mates[i].Item1), Wire.Json)).Content.ReadFromJsonAsync<RegisterResponse>(Wire.Json);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg!.Token);
        await http.PutAsJsonAsync("api/v1/me", new HelloRequest(mates[i].Item1, new() { new AccountDto(mates[i].Item2, mates[i].Item1) }), Wire.Json);
        await http.PostAsJsonAsync("api/v1/squads/join", new JoinSquadRequest(invite.Code), Wire.Json);
        var uploads = sharedMatches.Where(m => m.Players > i)
            // The last teammate has not uploaded the two most recent matches: they show as partial.
            .Where(m => !(i == 2 && sharedMatches.OrderByDescending(x => x.Start).Take(2).Any(x => x.Id == m.Id)))
            .Select(m => new ContributionDto
            {
                MatchId = m.Id, PlayerId = mates[i].Item2, PlayerName = mates[i].Item1, ChestsOpened = rng.Next(5, 58), AmmoBoxesOpened = rng.Next(1, 12),
                MatchStartUtc = m.Start, DurationSeconds = 1200, Playlist = m.Playlist, ParserVersion = 17,
                Roster = new() { new RosterEntry(Me, "Nova", false, rng.Next(0, 6)) },
            })
            .Concat(Enumerable.Range(0, 25 + i * 9).Select(k => new ContributionDto
            {
                MatchId = $"solo-{i}-{k}", PlayerId = mates[i].Item2, PlayerName = mates[i].Item1, ChestsOpened = rng.Next(4, 50),
                MatchStartUtc = DateTime.UtcNow.AddDays(-rng.Next(1, 60)), ParserVersion = 17,
            }))
            .ToList();
        await http.PutAsJsonAsync("api/v1/contributions", new UploadRequest(uploads), Wire.Json);
    }
    engine.Sync.Wake();
}

if (args.Contains("--live"))
{
    // A fixture match every few minutes: recorded (the feed shows "match in progress"), then finalized and counted.
    _ = Task.Run(async () =>
    {
        var rng = new Random();
        for (var k = 1; ; k++)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            var id = $"live-{DateTime.UtcNow:HHmmss}";
            var start = DateTime.UtcNow;
            new FakeReplay { Live = true, KeyMissing = true, SessionId = id }.Write(demos, id, TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromSeconds(25));
            var chests = rng.Next(12, 48);
            var roster = mates.Take(3).Select(m => new RosterEntry(m.Item2, m.Item1, false, rng.Next(2, 14))).ToList();
            var treasures = RollTreasures(rng, start, chests, 1300);
            if (k % 2 == 1 && !treasures.Any(x => x.FromGoldenChest)) treasures.Add(new TreasureInfo(huntWeeks[2].Jam, huntWeeks[2].Jam, 488, Kinds.Chest, true, true));
            new FakeReplay
            {
                SessionId = id, PlayerId = Me, PlayerName = "Nova", StartUtc = start, Duration = 1300, Playlist = "Playlist_NoBuildBR_Squad",
                Chests = chests, Destroyed = rng.Next(0, 5), Ammo = rng.Next(4, 14), Other = rng.Next(0, 4), Roster = roster, ChestEvents = true,
                Placement = rng.Next(1, 4) == 1 ? 1 : rng.Next(2, 20), TotalPlayers = 80, Treasures = treasures,
            }.Write(demos, id, TimeSpan.FromMinutes(2));
            await Task.Delay(TimeSpan.FromSeconds(150));
        }
    });
}

Console.WriteLine($"FIXTURE DATA. Interface: {local.Url}?token={local.Token}   OBS overlay: {local.ObsUrl}");
Console.WriteLine("Press Ctrl+C to stop.");
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
await stop.Task;
