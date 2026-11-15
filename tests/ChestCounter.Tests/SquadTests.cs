using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChestCounter.Engine;
using ChestCounter.Shared;

namespace ChestCounter.Tests;

/// <summary>A registered device talking to the test server over plain HTTP.</summary>
public sealed class Device
{
    public HttpClient Http { get; }
    public string UserId { get; }
    public string PlayerId { get; }
    public string Name { get; }

    private Device(HttpClient http, string userId, string playerId, string name)
    {
        Http = http;
        UserId = userId;
        PlayerId = playerId;
        Name = name;
    }

    public static async Task<Device> Register(TestServer server, string name, string? playerId = null)
    {
        var http = server.Http();
        var response = await http.PostAsJsonAsync("api/v1/devices", new RegisterRequest(name), Wire.Json);
        response.EnsureSuccessStatusCode();
        var registered = (await response.Content.ReadFromJsonAsync<RegisterResponse>(Wire.Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token);
        var device = new Device(http, registered.UserId, playerId ?? ("epic-" + name.ToLowerInvariant()), name);
        // Like the real client on connect: announce the player id found in the local history.
        (await http.PutAsJsonAsync("api/v1/me", new HelloRequest(name, new() { new AccountDto(device.PlayerId, name) }), Wire.Json)).EnsureSuccessStatusCode();
        return device;
    }

    /// <summary>The same device after a server restart: same token, new connection.</summary>
    public Device On(TestServer server)
    {
        var http = server.Http();
        http.DefaultRequestHeaders.Authorization = Http.DefaultRequestHeaders.Authorization;
        return new Device(http, UserId, PlayerId, Name);
    }

    public async Task<SquadStateDto> CreateSquad(string name) =>
        (await (await Http.PostAsJsonAsync("api/v1/squads", new CreateSquadRequest(name), Wire.Json)).Content.ReadFromJsonAsync<SquadStateDto>(Wire.Json))!;

    public async Task<InviteDto> Invite(string squadId, int? maxUses = null) =>
        (await (await Http.PostAsJsonAsync($"api/v1/squads/{squadId}/invites", new CreateInviteRequest(MaxUses: maxUses), Wire.Json)).Content.ReadFromJsonAsync<InviteDto>(Wire.Json))!;

    public Task<HttpResponseMessage> Join(string code) => Http.PostAsJsonAsync("api/v1/squads/join", new JoinSquadRequest(code), Wire.Json);

    public async Task<SquadStateDto> State(string squadId) => (await Http.GetFromJsonAsync<SquadStateDto>($"api/v1/squads/{squadId}", Wire.Json))!;

    /// <summary>This player's own result for a match, with the teammates their replay lists.</summary>
    public ContributionDto Result(string matchId, int chests, IEnumerable<Device>? mates = null, int parser = 1, int observedOfEachMate = 0,
        DateTime? start = null, string playlist = "Playlist_DefaultSquad") => new()
    {
        MatchId = matchId,
        PlayerId = PlayerId,
        PlayerName = Name,
        ChestsOpened = chests,
        MatchStartUtc = start ?? new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc),
        DurationSeconds = 1200,
        Playlist = playlist,
        ParserVersion = parser,
        Roster = (mates ?? Array.Empty<Device>()).Where(m => m != this).Select(m => new RosterEntry(m.PlayerId, m.Name, false, observedOfEachMate)).ToList(),
    };

    public async Task<List<UploadResult>> Upload(params ContributionDto[] results)
    {
        var response = await Http.PutAsJsonAsync("api/v1/contributions", new UploadRequest(results.ToList()), Wire.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadResponse>(Wire.Json))!.Results;
    }
}

/// <summary>Squad aggregation and synchronization, against the real server running in memory.</summary>
public sealed class SquadTests : IDisposable
{
    private readonly TestServer _server = new();
    public void Dispose() => _server.Dispose();

    /// <summary>Four registered devices in one squad.</summary>
    private async Task<(Device[] Players, string SquadId)> Squad(int size = 4)
    {
        var names = new[] { "Ana", "Ben", "Cleo", "Dev" };
        var players = new List<Device>();
        for (var i = 0; i < size; i++) players.Add(await Device.Register(_server, names[i]));
        var squad = await players[0].CreateSquad("Loot Goblins");
        var invite = await players[0].Invite(squad.SquadId);
        foreach (var p in players.Skip(1)) (await p.Join(invite.Code)).EnsureSuccessStatusCode();
        return (players.ToArray(), squad.SquadId);
    }

    [Fact] // requirement 6
    public async Task Four_players_with_40_50_60_70_give_220()
    {
        var (p, squad) = await Squad();
        var counts = new[] { 40, 50, 60, 70 };
        for (var i = 0; i < 4; i++) await p[i].Upload(p[i].Result("match-1", counts[i], p));

        var state = await p[0].State(squad);

        var match = Assert.Single(state.SharedMatches);
        Assert.Equal(SharedMatchStatus.Complete, match.Status);
        Assert.Equal(220, match.ConfirmedChests);
        Assert.Equal(220, state.SharedChests);
        Assert.Equal(220, state.CombinedMemberChests);
        Assert.Equal(counts, state.Members.Select(m => m.LifetimeChests).ToArray());
        Assert.Empty(match.Missing);
    }

    [Fact] // requirement 7
    public async Task Overlapping_teammate_observations_do_not_inflate_the_total()
    {
        var (p, squad) = await Squad();
        var counts = new[] { 40, 50, 60, 70 };
        // Every replay also "saw" 35 chests opened by each of the three teammates: 420 chests of overlap in total.
        for (var i = 0; i < 4; i++) await p[i].Upload(p[i].Result("match-1", counts[i], p, observedOfEachMate: 35));
        // And everything is uploaded a second time for good measure.
        for (var i = 0; i < 4; i++) await p[i].Upload(p[i].Result("match-1", counts[i], p, observedOfEachMate: 35));

        var state = await p[0].State(squad);

        Assert.Equal(220, Assert.Single(state.SharedMatches).ConfirmedChests);
        Assert.Equal(220, state.SharedChests);
        Assert.Equal(220, state.CombinedMemberChests);
    }

    [Fact] // requirement 8
    public async Task Missing_teammate_gives_an_explicitly_partial_result()
    {
        var (p, squad) = await Squad();
        await p[0].Upload(p[0].Result("match-1", 40, p, observedOfEachMate: 12));
        await p[1].Upload(p[1].Result("match-1", 50, p, observedOfEachMate: 9));

        var state = await p[0].State(squad);

        var match = Assert.Single(state.SharedMatches);
        Assert.Equal(SharedMatchStatus.Partial, match.Status);
        Assert.Equal(90, match.ConfirmedChests); // the two uploaded results, nothing invented for the others
        Assert.Equal(1, state.PartialMatchCount);
        Assert.Equal(new[] { p[2].UserId, p[3].UserId }.Order(), match.Missing.Select(m => m.UserId).Order());
        Assert.All(match.Missing, m => Assert.Equal(12, m.ObservedEstimate)); // a labelled hint, outside the total

        // The late teammates finish and upload: the match completes.
        await p[2].Upload(p[2].Result("match-1", 60, p));
        await p[3].Upload(p[3].Result("match-1", 70, p));
        match = Assert.Single((await p[0].State(squad)).SharedMatches);
        Assert.Equal(SharedMatchStatus.Complete, match.Status);
        Assert.Equal(220, match.ConfirmedChests);
    }

    [Fact] // 5.4: scopes stay separate
    public async Task Solo_games_count_for_the_member_but_not_as_shared_matches()
    {
        var (p, squad) = await Squad(2);
        await p[0].Upload(p[0].Result("solo-a", 30), p[0].Result("together", 10, p));
        await p[1].Upload(p[1].Result("solo-b", 25), p[1].Result("together", 15, p));

        var state = await p[0].State(squad);

        Assert.Equal(80, state.CombinedMemberChests); // 40 + 40, whoever they played with
        Assert.Equal(25, state.SharedChests);         // only the match played together
        Assert.Equal("together", Assert.Single(state.SharedMatches).MatchId);
        Assert.Equal(new[] { 10, 15 }, state.Members.Select(m => m.SharedChests).ToArray());
    }

    [Fact] // 5.4: same lobby is not the same team
    public async Task Members_who_were_opponents_in_the_same_lobby_are_not_a_shared_match()
    {
        var (p, squad) = await Squad(2);
        await p[0].Upload(p[0].Result("lobby", 30)); // each replay lists no squad member as teammate
        await p[1].Upload(p[1].Result("lobby", 25));

        var state = await p[0].State(squad);

        Assert.Empty(state.SharedMatches);
        Assert.Equal(55, state.CombinedMemberChests);
    }

    [Fact] // 5.3: uncertain matches are surfaced, not merged
    public async Task Uploads_that_disagree_about_the_match_are_flagged_ambiguous_and_kept_out_of_the_total()
    {
        var (p, squad) = await Squad(2);
        var start = new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);
        await p[0].Upload(p[0].Result("m", 30, p, start: start));
        await p[1].Upload(p[1].Result("m", 25, p, start: start.AddHours(3)));

        var state = await p[0].State(squad);

        var match = Assert.Single(state.SharedMatches);
        Assert.Equal(SharedMatchStatus.Ambiguous, match.Status);
        Assert.Contains(SquadAggregator.WarnStartTime, match.Warnings);
        Assert.Equal(0, state.SharedChests);
        Assert.Equal(1, state.AmbiguousMatchCount);
        Assert.Equal(55, state.CombinedMemberChests); // each member's own total is unaffected
    }

    [Fact] // requirement 10, server side
    public async Task Reprocessed_match_replaces_its_previous_contribution()
    {
        var (p, squad) = await Squad(2);
        await p[0].Upload(p[0].Result("m", 40, p, parser: 1));
        await p[1].Upload(p[1].Result("m", 50, p, parser: 1));

        var newer = await p[0].Upload(p[0].Result("m", 44, p, parser: 2));
        Assert.Equal(UploadStatus.Accepted, newer.Single().Status);
        Assert.Equal(94, (await p[0].State(squad)).SharedChests);

        // An old client then re-sends its older analysis: it must not undo the newer one.
        var older = await p[0].Upload(p[0].Result("m", 40, p, parser: 1));
        Assert.Equal(UploadStatus.Stale, older.Single().Status);
        Assert.Equal(94, (await p[0].State(squad)).SharedChests);

        // A re-analysis that decides the match does not count withdraws it.
        await p[0].Upload(p[0].Result("m", 44, p, parser: 3) with { Counted = false });
        var state = await p[0].State(squad);
        Assert.Equal(50, state.CombinedMemberChests);
        Assert.Empty(state.SharedMatches); // one member left in that match: no longer a shared match
    }

    [Fact] // 5.5: authorization
    public async Task A_member_cannot_upload_results_in_a_teammates_name()
    {
        var (p, squad) = await Squad(2);
        await p[1].Upload(p[1].Result("m", 50, p));

        var forged = p[1].Result("m", 999, p); // Ben's player id...
        var results = await p[0].Upload(forged); // ...sent by Ana

        Assert.Equal(UploadStatus.PlayerClaimed, results.Single().Status);
        Assert.Equal(50, (await p[0].State(squad)).CombinedMemberChests);
    }

    [Fact]
    public async Task Outsiders_cannot_read_a_squad_and_unknown_tokens_are_refused()
    {
        var (p, squad) = await Squad(2);
        var stranger = await Device.Register(_server, "Mallory");

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"api/v1/squads/{squad}")).StatusCode);
        var anonymous = _server.Http();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"api/v1/squads/{squad}")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("api/v1/contributions", new UploadRequest(new()), Wire.Json)).StatusCode);
    }

    [Fact]
    public async Task Invites_are_limited_to_four_members_and_can_be_revoked()
    {
        var (p, squad) = await Squad();
        var fifth = await Device.Register(_server, "Eve");
        var invite = await p[0].Invite(squad);

        var full = await fifth.Join(invite.Code);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal("squad_full", (await full.Content.ReadFromJsonAsync<ErrorResponse>(Wire.Json))!.Error);

        // Someone leaves; the invite is revoked before Eve tries again.
        (await p[3].Http.PostAsync($"api/v1/squads/{squad}/leave", null)).EnsureSuccessStatusCode();
        (await p[0].Http.DeleteAsync($"api/v1/squads/{squad}/invites/{invite.Code}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Gone, (await fifth.Join(invite.Code)).StatusCode);

        var fresh = await p[0].Invite(squad);
        Assert.StartsWith("http://squad.test/join/", fresh.Link);
        (await fifth.Join(fresh.Code.ToLowerInvariant().Replace("-", " "))).EnsureSuccessStatusCode(); // typed loosely
        (await fifth.Join(fresh.Code)).EnsureSuccessStatusCode(); // joining twice changes nothing
        var state = await p[0].State(squad);
        Assert.Equal(4, state.Members.Count);
        Assert.Equal(1, state.Invites.Single(i => i.Code == fresh.Code).Uses);
    }

    [Fact]
    public async Task Expired_invite_is_refused()
    {
        using var server = new TestServer(settings: new() { ["CC_INVITE_TTL_HOURS"] = "1" });
        var owner = await Device.Register(server, "Ana");
        var guest = await Device.Register(server, "Ben");
        var squad = await owner.CreateSquad("S");
        var invite = await owner.Invite(squad.SquadId);
        Assert.True(invite.ExpiresUtc - invite.CreatedUtc <= TimeSpan.FromHours(1));

        // Age the invite in the database, as if an hour had passed.
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={server.Database};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE invites SET expires_utc = '2020-01-01T00:00:00.0000000Z'";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(HttpStatusCode.Gone, (await guest.Join(invite.Code)).StatusCode);
    }

    [Fact] // requirement 12, server half
    public async Task Squad_history_survives_a_server_restart()
    {
        var (p, squad) = await Squad();
        var counts = new[] { 40, 50, 60, 70 };
        for (var i = 0; i < 4; i++) await p[i].Upload(p[i].Result("match-1", counts[i], p));
        var before = await p[0].State(squad);

        _server.Restart();
        var after = await p[0].On(_server).State(squad);

        Assert.Equal(220, after.SharedChests);
        Assert.Equal(before.Members.Select(m => (m.UserId, m.LifetimeChests)), after.Members.Select(m => (m.UserId, m.LifetimeChests)));
        Assert.Equal(before.SharedMatches.Single().MatchId, after.SharedMatches.Single().MatchId);
        // The same uploads again after the restart: still one row per (match, player).
        var again = p.Select(x => x.On(_server)).ToArray();
        for (var i = 0; i < 4; i++) await again[i].Upload(again[i].Result("match-1", counts[i], p));
        Assert.Equal(220, (await again[0].State(squad)).SharedChests);
    }

    [Fact]
    public async Task Invalid_uploads_are_refused_one_by_one_without_blocking_valid_ones()
    {
        var (p, squad) = await Squad(2);
        var results = await p[0].Upload(
            p[0].Result("ok", 10),
            p[0].Result("negative", -5),
            p[0].Result("absurd", 1_000_000),
            p[0].Result("x", 3) with { MatchId = new string('m', 500) });

        Assert.Equal(new[] { UploadStatus.Accepted, UploadStatus.Invalid, UploadStatus.Invalid, UploadStatus.Invalid }, results.Select(r => r.Status).ToArray());
        Assert.Equal(10, (await p[0].State(squad)).CombinedMemberChests);
    }

    [Fact]
    public async Task Health_and_admin_endpoints()
    {
        using var server = new TestServer(settings: new() { ["CC_ADMIN_TOKEN"] = "s3cret-admin", ["CC_REGISTRATION_KEY"] = "friends-only" });
        var http = server.Http();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/v1/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("api/v1/devices", new RegisterRequest("Ana"), Wire.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/v1/devices", new RegisterRequest("Ana", "friends-only"), Wire.Json)).StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret-admin");
        var stats = await http.GetStringAsync("api/v1/admin/stats");
        Assert.Contains("\"users\":1", stats);
    }

    [Fact] // the aggregation itself, without a server: deterministic and order independent
    public void Aggregation_is_independent_of_upload_order()
    {
        var members = new[] { "u1", "u2", "u3", "u4" }
            .Select((u, i) => new MemberInfo(u, "P" + i, i == 0 ? "owner" : "member", DateTime.UnixEpoch, null, null, new[] { new AccountDto("p" + i, "P" + i) }))
            .ToList();
        var counts = new[] { 40, 50, 60, 70 };
        var records = members.Select((m, i) => new ContributionRecord(m.UserId, new ContributionDto
        {
            MatchId = "m",
            PlayerId = "p" + i,
            PlayerName = "P" + i,
            ChestsOpened = counts[i],
            MatchStartUtc = DateTime.UnixEpoch,
            Roster = Enumerable.Range(0, 4).Where(j => j != i).Select(j => new RosterEntry("p" + j, "P" + j, false, 99)).ToList(),
        }, DateTime.UnixEpoch.AddMinutes(i))).ToList();
        var squad = new SquadInfo("s", "S", 1, 4);

        var forward = SquadAggregator.Build(squad, members, records, _ => false, DateTime.UnixEpoch);
        var backward = SquadAggregator.Build(squad, members, records.AsEnumerable().Reverse().Concat(records).ToList(), _ => false, DateTime.UnixEpoch);

        Assert.Equal(220, forward.SharedChests);
        Assert.Equal(220, backward.SharedChests); // duplicates in the input change nothing either
        Assert.Equal(Wire.Serialize(forward.SharedMatches), Wire.Serialize(backward.SharedMatches));
    }
}
