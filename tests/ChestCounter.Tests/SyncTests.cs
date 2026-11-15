using ChestCounter.Engine;
using ChestCounter.Shared;

namespace ChestCounter.Tests;

/// <summary>The whole chain: fake replay on disk -> engine -> outbox -> server -> the other member's engine.</summary>
public sealed class SyncTests : IDisposable
{
    private const string Server = "http://squad.test";
    private readonly TestServer _server = new();
    private readonly List<Sandbox> _sandboxes = new();

    public void Dispose()
    {
        _sandboxes.ForEach(s => s.Dispose());
        _server.Dispose();
    }

    private Sandbox NewSandbox()
    {
        var s = new Sandbox();
        _sandboxes.Add(s);
        return s;
    }

    private const string AnaId = "aaaa1111aaaa1111aaaa1111aaaa1111", BenId = "bbbb2222bbbb2222bbbb2222bbbb2222";

    private static FakeReplay Ana(string session, int chests, bool withBen = false) => new()
    {
        SessionId = session, PlayerId = AnaId, PlayerName = "Ana", Chests = chests,
        Roster = withBen ? new() { new RosterEntry(BenId, "Ben", false, 3) } : new(),
    };

    private static FakeReplay Ben(string session, int chests, bool withAna = false) => new()
    {
        SessionId = session, PlayerId = BenId, PlayerName = "Ben", Chests = chests,
        Roster = withAna ? new() { new RosterEntry(AnaId, "Ana", false, 5) } : new(),
    };

    [Fact] // requirement 9, step by step
    public async Task Results_made_offline_are_queued_and_synchronized_after_reconnection()
    {
        var box = NewSandbox();
        var transport = _server.NewTransport();
        using var engine = box.Engine(transport);
        engine.Begin();
        await engine.Sync.ConnectServerAsync(Server, "Ana");
        await engine.Sync.CreateSquadAsync("Loot Goblins");

        transport.Offline = true;
        Ana("m1", 40).Write(box.Demos, "m1");
        Ana("m2", 12).Write(box.Demos, "m2");
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);

        // Solo tracking is unaffected by the dead server, and the two results wait in the outbox.
        Assert.Equal(52, engine.GetState().Totals.Chests);
        Assert.Equal(2, engine.Sync.Status.Pending);
        await Assert.ThrowsAnyAsync<Exception>(() => engine.Sync.FlushOutboxAsync());
        Assert.Equal(2, engine.Sync.Status.Pending); // a failed attempt loses nothing

        transport.Offline = false;
        await engine.Sync.ReconcileAsync();

        Assert.Equal(0, engine.Sync.Status.Pending);
        Assert.Equal(52, engine.Sync.Squad!.CombinedMemberChests);
        Assert.Equal(2, engine.Sync.Squad.Members.Single().LifetimeMatches);

        // Sending everything again (full resync) is harmless.
        Assert.Equal(2, engine.Sync.ResyncAll());
        await engine.Sync.ReconcileAsync();
        Assert.Equal(52, engine.Sync.Squad!.CombinedMemberChests);
    }

    [Fact] // requirement 9, hands off: the background loops do it by themselves
    public async Task Background_sync_recovers_by_itself_when_the_server_comes_back()
    {
        var box = NewSandbox();
        var transport = _server.NewTransport();
        using var engine = box.Engine(transport, new SystemClock());
        await engine.Sync.ConnectServerAsync(Server, "Ana");
        await engine.Sync.CreateSquadAsync("Loot Goblins");
        transport.Offline = true;
        engine.Start();

        Ana("m1", 40).Write(box.Demos, "m1");
        await Wait.Until(() => engine.Sync.Status.Pending == 1 && engine.GetState().Totals.Chests == 40, "the match to be counted locally and queued");
        Assert.NotEqual("online", engine.Sync.Status.State);

        transport.Offline = false;
        await Wait.Until(() => engine.Sync.Status is { Pending: 0, State: "online" }, "the queued result to be uploaded after reconnection", 40);
        await Wait.Until(() => engine.Sync.Squad?.CombinedMemberChests == 40, "the squad state to include it");
    }

    [Fact] // 5.2: a processed match reaches the connected squad members
    public async Task A_processed_match_reaches_the_other_connected_member()
    {
        var (boxA, boxB) = (NewSandbox(), NewSandbox());
        using var ana = boxA.Engine(_server.NewTransport(), new SystemClock());
        using var ben = boxB.Engine(_server.NewTransport(), new SystemClock());
        await ana.Sync.ConnectServerAsync(Server, "Ana");
        await ana.Sync.CreateSquadAsync("Loot Goblins");
        var invite = await ana.Sync.CreateInviteAsync();
        await ben.Sync.ConnectServerAsync(Server, "Ben");
        await ben.Sync.JoinSquadAsync(invite.Code);
        // Ben has played before: his first upload is what tells the server which player id is his.
        Ben("ben-earlier", 5).Write(boxB.Demos, "earlier");
        ana.Start();
        ben.Start();
        await Wait.Until(() => ana.Sync.Squad?.Members.Count(m => m.Online) == 2, "both members to be seen online");
        await Wait.Until(() => ana.Sync.Squad?.Members.Any(m => m.Accounts.Any(a => a.PlayerId == BenId)) == true, "Ben's player id to be known to the squad");

        // They finish the same match; Ana's PC processes its replay first.
        Ana("shared-1", 52, withBen: true).Write(boxA.Demos, "a");
        await Wait.Until(() => ben.Sync.Squad?.SharedMatches.FirstOrDefault() is { Status: SharedMatchStatus.Partial, ConfirmedChests: 52 },
            "Ben's app to show the match as partial, with only Ana's result");
        Assert.Equal(BenId, ben.Sync.Squad!.SharedMatches[0].Missing.Single().PlayerId);

        Ben("shared-1", 46, withAna: true).Write(boxB.Demos, "b");
        await Wait.Until(() => ana.Sync.Squad?.SharedMatches.FirstOrDefault() is { Status: SharedMatchStatus.Complete, ConfirmedChests: 98 },
            "Ana's app to receive Ben's result over the WebSocket");

        Assert.Equal(98, ana.Sync.Squad!.SharedChests);
        Assert.Equal(98, ben.Sync.Squad!.SharedChests);
        var overlay = ana.GetOverlay();
        Assert.Equal(52, overlay.Personal);
        Assert.Equal(103, overlay.Squad); // combined member total: 52 + (46 + 5), not the shared total
        Assert.Equal(98, overlay.LastMatch);
    }

    [Fact] // requirement 12
    public async Task Squad_history_is_correct_after_application_and_server_restarts()
    {
        var box = NewSandbox();
        var benDevice = await Device.Register(_server, "Ben", BenId);
        string squadId;
        using (var engine = box.Engine(_server.NewTransport()))
        {
            engine.Begin();
            Ana("solo", 20).Write(box.Demos, "solo");
            Ana("duo", 40, withBen: true).Write(box.Demos, "duo");
            engine.Tracker.ScanOnce(ignoreQuietPeriod: true); // history made before any server was set
            await engine.Sync.ConnectServerAsync(Server, "Ana");
            await engine.Sync.CreateSquadAsync("Loot Goblins");
            var invite = await engine.Sync.CreateInviteAsync();
            (await benDevice.Join(invite.Code)).EnsureSuccessStatusCode();
            await benDevice.Upload(benDevice.Result("duo", 50) with { Roster = new() { new RosterEntry(AnaId, "Ana", false, 0) } });
            await engine.Sync.ReconcileAsync(); // uploads the whole local history
            squadId = engine.Sync.Squad!.SquadId;
            Assert.Equal(90, engine.Sync.Squad.SharedChests);
            Assert.Equal(110, engine.Sync.Squad.CombinedMemberChests);
        }

        _server.Restart();

        // The app restarts while the server is unreachable: the squad page still shows the last known state.
        var offline = _server.NewTransport();
        offline.Offline = true;
        using (var engine = box.Engine(offline))
        {
            engine.Begin();
            Assert.Equal(0, engine.Tracker.ScanOnce(ignoreQuietPeriod: true).Total);
            Assert.Equal(60, engine.GetState().Totals.Chests);
            Assert.Equal(90, engine.Sync.Squad!.SharedChests);
            Assert.Equal(0, engine.Sync.Status.Pending);
        }

        // And once it is reachable again, both sides agree, with nothing counted twice.
        using (var engine = box.Engine(_server.NewTransport()))
        {
            engine.Begin();
            engine.Sync.ResyncAll();
            await engine.Sync.ReconcileAsync();
            var state = engine.Sync.Squad!;
            Assert.Equal(squadId, state.SquadId);
            Assert.Equal(90, state.SharedChests);
            Assert.Equal(110, state.CombinedMemberChests);
            Assert.Equal(SharedMatchStatus.Complete, state.SharedMatches.Single().Status);
        }
    }

    [Fact] // requirement 10, end to end
    public async Task Local_reanalysis_replaces_the_contribution_on_the_server()
    {
        var box = NewSandbox();
        using var engine = box.Engine(_server.NewTransport());
        engine.Begin();
        await engine.Sync.ConnectServerAsync(Server, "Ana");
        await engine.Sync.CreateSquadAsync("S");
        Ana("m1", 40).Write(box.Demos, "m1");
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);
        await engine.Sync.ReconcileAsync();
        Assert.Equal(40, engine.Sync.Squad!.CombinedMemberChests);

        box.Analyzer.Version = 2;
        box.Analyzer.Recount = (r, _) => r.Chests + 3;
        engine.Tracker.RequeueOutdated();
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);
        await engine.Sync.ReconcileAsync();

        Assert.Equal(43, engine.GetState().Totals.Chests);
        Assert.Equal(43, engine.Sync.Squad!.CombinedMemberChests);
        Assert.Equal(1, engine.Sync.Squad.Members.Single().LifetimeMatches);
    }

    [Fact] // 5.5: a member changes Fortnite account
    public async Task A_member_who_changes_account_keeps_one_membership_with_both_accounts()
    {
        var box = NewSandbox();
        using var engine = box.Engine(_server.NewTransport());
        engine.Begin();
        await engine.Sync.ConnectServerAsync(Server, "Ana");
        await engine.Sync.CreateSquadAsync("S");
        Ana("m1", 40).Write(box.Demos, "m1");
        (Ana("m2", 15) with { PlayerId = "cccc3333cccc3333cccc3333cccc3333", PlayerName = "AnaAlt" }).Write(box.Demos, "m2");
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);
        await engine.Sync.ReconcileAsync();

        var member = engine.Sync.Squad!.Members.Single();
        Assert.Equal(2, member.Accounts.Count);
        Assert.Equal(55, member.LifetimeChests);
        Assert.Equal(2, engine.Db.Profiles().Count); // locally they stay two separate profiles
    }

    [Fact] // 5.5: two members with very different system clocks
    public async Task Wrong_system_clocks_do_not_affect_match_matching()
    {
        var (boxA, boxB) = (NewSandbox(), NewSandbox());
        boxA.Clock.UtcNow = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        boxB.Clock.UtcNow = new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        using var ana = boxA.Engine(_server.NewTransport());
        using var ben = boxB.Engine(_server.NewTransport());
        ana.Begin();
        ben.Begin();
        await ana.Sync.ConnectServerAsync(Server, "Ana");
        await ana.Sync.CreateSquadAsync("S");
        await ben.Sync.ConnectServerAsync(Server, "Ben");
        await ben.Sync.JoinSquadAsync((await ana.Sync.CreateInviteAsync()).Link); // the full link works as well as the code

        Ana("m", 30, withBen: true).Write(boxA.Demos, "a");
        Ben("m", 20, withAna: true).Write(boxB.Demos, "b");
        ana.Tracker.ScanOnce(ignoreQuietPeriod: true);
        ben.Tracker.ScanOnce(ignoreQuietPeriod: true);
        await ana.Sync.ReconcileAsync();
        await ben.Sync.ReconcileAsync();

        var match = ben.Sync.Squad!.SharedMatches.Single();
        Assert.Equal(SharedMatchStatus.Complete, match.Status);
        Assert.Equal(50, match.ConfirmedChests);
        // "Last sync" is the server's time, not either client's.
        Assert.All(ben.Sync.Squad.Members, m => Assert.InRange(m.LastSyncUtc!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task Leaving_and_being_removed_are_reflected_on_the_client()
    {
        var (boxA, boxB) = (NewSandbox(), NewSandbox());
        using var ana = boxA.Engine(_server.NewTransport());
        using var ben = boxB.Engine(_server.NewTransport());
        await ana.Sync.ConnectServerAsync(Server, "Ana");
        await ana.Sync.CreateSquadAsync("S");
        await ben.Sync.ConnectServerAsync(Server, "Ben");
        await ben.Sync.JoinSquadAsync((await ana.Sync.CreateInviteAsync()).Code);

        await ana.Sync.RemoveMemberAsync(ben.Sync.Status.UserId!);
        await ben.Sync.ReconcileAsync(); // Ben's PC was away when it happened

        Assert.Null(ben.Sync.Squad);
        Assert.Null(ben.Sync.Status.SquadId);
        Assert.Single(ana.Sync.Squad!.Members);

        await ana.Sync.LeaveSquadAsync();
        Assert.Null(ana.Sync.Squad);
        ana.Sync.DisconnectServer();
        Assert.Equal("disabled", ana.Sync.Status.State);
    }

    [Fact]
    public void Invite_links_and_codes_are_both_understood()
    {
        Assert.Equal(("https://squad.example.com/", "ABCD-EFGH"), SyncService.ParseInvite(" https://squad.example.com/join/abcd-efgh "));
        Assert.Equal((null, "ABCD-EFGH"), SyncService.ParseInvite("abcd-efgh"));
        Assert.Equal("http://localhost:8080/", SyncService.NormalizeServer("localhost:8080"));
        Assert.Equal("https://squad.example.com/", SyncService.NormalizeServer("squad.example.com/some/path"));
        Assert.Throws<SyncException>(() => SyncService.NormalizeServer("ftp://nope"));
    }
}
