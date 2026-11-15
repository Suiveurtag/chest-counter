using ChestCounter.Engine;

namespace ChestCounter.Tests;

/// <summary>Treasure hunt: catalog, naming, chances, collection, chests added by hand, activity feed.</summary>
public sealed class HuntTests : IDisposable
{
    private readonly Sandbox _s = new();
    public void Dispose() => _s.Dispose();

    private static readonly DateTime Week3 = new(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Built_in_catalog_is_valid_and_names_golden_chest_prizes()
    {
        var c = HuntCatalog.Embedded();
        Assert.True(c.Revision >= 3);
        Assert.Equal(5, c.Weeks.Count);
        Assert.Equal(5, c.Weeks[2].Prizes.Count);
        Assert.Equal("w3-jam", c.Find("delilahprize:DPID_ITEM21")?.Id);
        Assert.Equal("pirate-shop", c.Find("9f73f94d-2e61-4523-97d7-d61235d3365e")?.Id);
        Assert.Equal(10, c.Boost(HuntCatalog.VehicleDecal, new DateTime(2026, 11, 21, 19, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(1, c.Boost(HuntCatalog.VehicleDecal, new DateTime(2026, 11, 21, 18, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Catalog_refuses_unsafe_data()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ChestCounter.Engine", "Hunt", "content.json"));
        Assert.NotNull(HuntCatalog.Parse(json, out _));
        Assert.Null(HuntCatalog.Parse(json.Replace("https://cdn.fortnite.bot/s43treasure/w1-car.webp", "http://evil.example/x.webp"), out _));
        Assert.Null(HuntCatalog.Parse(json.Replace("\"oneIn\": 272", "\"oneIn\": 0"), out _));
        Assert.Null(HuntCatalog.Parse(json.Replace("\"schema\": 1", "\"schema\": 2"), out _));
    }

    [Theory]
    [InlineData("DelilahPrize:dpid_item21", "x", true, "jam", null)]
    [InlineData(null, "WID_RelicExtractor_Athena", false, "gizmo", "extractor")]
    [InlineData("Unknown:2026-11-15_17.13@2m29s", "GA_TreasureHandler", false, "unknown", null)]
    [InlineData("DelilahPrize:dpid_item99", "DelilahPrize:dpid_item99", true, "golden", "dpid_item99")]
    public void Treasures_are_named_from_what_the_replay_kept(string? prizeId, string raw, bool golden, string kind, string? item)
    {
        var tag = Treasures.Describe(prizeId, raw, golden, false, HuntCatalog.Embedded());
        Assert.Equal(kind, tag.Kind);
        Assert.Equal(item, tag.Item);
    }

    [Fact]
    public void Chances_follow_the_chests_of_the_week_and_the_collection_ticks_itself()
    {
        var catalog = HuntCatalog.Embedded();
        var start = Week3.AddHours(5); // Sunday 19:00 UTC: no Treasure Hour
        var match = new MatchRow { MatchId = "m1", ProfileKey = "p", StartUtc = start, DurationSeconds = 1000, Chests = 100, Counted = true, FirstAnalyzedUtc = start, AnalyzedUtc = start };
        var detail = new MatchDetail(new() { [Kinds.Chest] = 100 }, Enumerable.Range(0, 100).Select(i => new ChestEvent(i * 5, Kinds.Chest, false)).ToList(), new(),
            new() { new TreasureInfo("DelilahPrize:dpid_item21", "DelilahPrize:dpid_item21", 488, Kinds.Chest, true, true) }, 0);
        var manual = new List<ManualEntry> { new("a", 50, start, Week3), new("b", 30, start, null) };

        var report = Treasures.Build(catalog, new[] { (match, (MatchDetail?)detail) }, manual, new HashSet<string> { "w1-car" }, start.AddHours(2));

        Assert.Equal(150, report.WeekChests);       // 100 read + 50 "opened this week"
        Assert.Equal(180, report.SeasonChests);     // + 30 without a week
        var jam = report.WeekChances.Single(c => c.Kind == HuntCatalog.JamTrack);
        Assert.Equal(1 - Math.Pow(1 - 1.0 / 391, 150), jam.P, 9);
        Assert.Equal(1, report.Treasures);
        Assert.Equal("PPPP", report.Finds[0].Tag.Name);
        Assert.Equal("auto", report.Weeks[2].Prizes.Single(p => p.Id == "w3-jam").Obtained);
        Assert.Equal("manual", report.Weeks[0].Prizes.Single(p => p.Id == "w1-car").Obtained);
        Assert.Equal(2, report.CollectionGot);
        Assert.Equal(26, report.CollectionTotal);
    }

    [Fact]
    public void Chests_added_by_hand_count_locally_and_can_be_undone()
    {
        using var engine = _s.Engine();
        new FakeReplay { Chests = 20 }.Write(_s.Demos, "a");
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);

        var entry = engine.AddManual(300, thisWeek: false);
        Assert.Equal(300, engine.GetState().Totals.ManualChests);
        Assert.Equal(320, engine.GetOverlay().Personal);
        Assert.Equal(20, engine.GetState().Totals.Chests); // replay figures stay apart

        engine.RemoveManual(entry.Id);
        Assert.Equal(0, engine.GetState().Totals.ManualChests);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.AddManual(0, false));
    }

    [Fact]
    public void Activity_feed_tells_each_match_and_sums_up_big_imports()
    {
        using var engine = _s.Engine();
        for (var i = 0; i < 4; i++) new FakeReplay { Chests = 10 }.Write(_s.Demos, "r" + i, TimeSpan.FromHours(5 - i));
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);
        var import = engine.GetActivity(10).First();
        Assert.Equal("import", import.Kind);
        Assert.Equal(4, import.Data.GetProperty("matches").GetInt32());
        Assert.Equal(40, import.Data.GetProperty("chests").GetInt32());

        new FakeReplay { Chests = 7 }.Write(_s.Demos, "new");
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);
        var items = engine.GetActivity(10);
        Assert.Equal("match", items[0].Kind);
        Assert.Equal("analyzing", items[1].Kind);
        Assert.Equal(7, items[0].Data.GetProperty("chests").GetInt32());
        Assert.Equal(47, items[0].Data.GetProperty("total").GetInt32());
    }
}
