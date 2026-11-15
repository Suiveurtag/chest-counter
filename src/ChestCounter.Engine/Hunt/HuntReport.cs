namespace ChestCounter.Engine;

/// <summary>Chests the player added by hand (opened before the app counted them). Never sent to the squad.</summary>
/// <param name="WeekFromUtc">Start of the hunt week they were opened in, when the player said "opened this week".</param>
public sealed record ManualEntry(string Id, int Chests, DateTime AtUtc, DateTime? WeekFromUtc);

/// <summary>
/// What a treasure was, in language-independent codes. Kind: a catalog kind ("jam", "pirate-shop"...), "gizmo"
/// (Item names which one), "golden" (a golden chest prize not known yet, Item = its id) or "unknown" (the replay only
/// says a treasure was received).
/// </summary>
public sealed record TreasureTag(string Kind, string? Item, string? Name, string? PrizeKey, bool Golden, bool Team);

public sealed record TreasureView(string MatchId, DateTime MatchWhen, TreasureTag Tag, float? TimeSeconds, bool TimeExact,
    string? SourceKind, bool SourceDestroyed, bool SourceIsChest, bool SourceCertain, string? Teammate);

public sealed record SourceCount(string Kind, bool Destroyed, bool IsChest, int Count);

/// <param name="P">Chance of having had it at least once.</param>
/// <param name="LogNever">ln of the chance of never: still exact where <paramref name="P"/> rounds to 1.</param>
/// <param name="AtPace">The same chance at the end of the week (or hunt) at the player's pace; null when not worth saying.</param>
public sealed record ChanceView(string Kind, int OneIn, bool Weekly, double P, double LogNever, double Expected, int Chests, double? AtPace);

public sealed record HuntPrizeView(string Id, string Kind, string Name, string Image, string? Obtained);

public sealed record HuntWeekView(int Number, DateTime From, DateTime To, string Title, List<HuntPrizeView> Prizes);

public sealed record HuntReport
{
    public int Revision { get; init; }
    public string OddsSource { get; init; } = "";
    public string HoursSource { get; init; } = "";
    public DateTime NowUtc { get; init; }
    public DateTime HuntStart { get; init; }
    public DateTime HuntEnd { get; init; }
    /// <summary>Two weeks after the end: odds and prizes no longer apply.</summary>
    public bool Over { get; init; }
    public List<HuntHour> Hours { get; init; } = new();
    public List<HuntOdds> Odds { get; init; } = new();

    public HuntWeekView? Week { get; init; }
    public int WeekChests { get; init; }
    public int WeekManual { get; init; }
    public int SeasonChests { get; init; }
    public int Manual { get; init; }
    public int BoostedChests { get; init; }
    public long PirateHalfway { get; init; }
    /// <summary>Weekly kinds over this week's chests.</summary>
    public List<ChanceView> WeekChances { get; init; } = new();
    /// <summary>Every kind over every chest of the hunt.</summary>
    public List<ChanceView> SeasonChances { get; init; } = new();
    public int GizmosFound { get; init; }
    public int? ChestsSinceGizmo { get; init; }

    public HuntPrizeView? PirateShop { get; init; }
    public List<HuntWeekView> Weeks { get; init; } = new();
    public int CollectionGot { get; init; }
    public int CollectionTotal { get; init; }

    public List<TreasureView> Finds { get; init; } = new();
    public int Treasures { get; init; }
    public int TeamTreasures { get; init; }
    public List<SourceCount> Sources { get; init; } = new();
    public Dictionary<string, int> ByKind { get; init; } = new();
    public int Destroyed { get; init; }
    public int Matches { get; init; }
    public int Chests { get; init; }
    public double PerMatch { get; init; }
    public List<ManualEntry> ManualEntries { get; init; } = new();
}

public static class Treasures
{
    private static readonly HashSet<string> ChestKinds = new()
    {
        Kinds.Chest, Kinds.RareChest, Kinds.BuriedChest, Kinds.SpriteChest, Kinds.PosterChest, Kinds.OtherChest,
    };

    public static bool IsChestKind(string? kind) => kind != null && ChestKinds.Contains(kind);

    /// <summary>Names a treasure from what the replay kept: a golden chest prize id, or the item that showed up.</summary>
    public static TreasureTag Describe(string? prizeId, string rawItem, bool golden, bool team, HuntCatalog catalog)
    {
        if ((catalog.Find(prizeId) ?? catalog.Find(rawItem)) is { } prize)
        {
            return new TreasureTag(prize.Kind, null, prize.Name, prize.Id, golden || prize.PrizeIds.Count > 0, team);
        }
        var item = (rawItem ?? "").ToLowerInvariant();
        string? gizmo = item.Contains("flameceleb") ? "fireworks"
            : item.Contains("relicextractor") ? "extractor"
            : item.Contains("relicfinder") ? "finder"
            : item.Contains("luckylocator") ? "locator"
            : item.Contains("smugglerextractionkey") || item.Contains("extractionaccelerat") ? "accelerator"
            : null;
        if (gizmo != null)
        {
            return new TreasureTag(HuntCatalog.Gizmo, gizmo, null, null, false, team);
        }
        var id = prizeId ?? rawItem ?? "";
        if (id.StartsWith("Unknown:", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Inconnu:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("GA_TreasureHandler", StringComparison.Ordinal) || id.Length == 0)
        {
            return new TreasureTag("unknown", null, null, null, golden, team);
        }
        if (golden || id.Contains(':') || Guid.TryParse(id, out _))
        {
            return new TreasureTag("golden", id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id[..Math.Min(8, id.Length)], null, null, true, team);
        }
        return new TreasureTag("unknown", null, null, null, golden, team);
    }

    public static List<TreasureTag> Tags(MatchDetail? detail, HuntCatalog catalog)
    {
        if (detail == null) return new();
        var own = detail.Treasures.Select(t => Describe(t.PrizeId, t.RawItem, t.FromGoldenChest, false, catalog));
        var team = (detail.TeamFinds ?? new()).Select(t => Describe(t.PrizeId, t.RawItem, t.FromGoldenChest, true, catalog));
        var listed = detail.TeamFinds?.Count ?? 0;
        var unlisted = Enumerable.Range(0, Math.Max(0, detail.TeamTreasures - listed)).Select(_ => new TreasureTag("unknown", null, null, null, false, true));
        return own.Concat(team).Concat(unlisted).ToList();
    }

    /// <summary>When each counted chest was opened (UTC). A match read without chest times puts its chests mid-match.</summary>
    public static IEnumerable<DateTime> ChestTimes(MatchRow m, MatchDetail? d)
    {
        var start = DateTime.SpecifyKind(m.When, DateTimeKind.Utc);
        var events = d?.Chests ?? new();
        foreach (var c in events) yield return start.AddSeconds(c.TimeSeconds);
        for (var i = events.Count; i < m.Chests; i++) yield return start.AddSeconds((m.DurationSeconds ?? 1200) / 2.0);
    }

    public static HuntReport Build(HuntCatalog catalog, IReadOnlyList<(MatchRow Match, MatchDetail? Detail)> matches, IReadOnlyList<ManualEntry> manualEntries,
        IReadOnlySet<string> obtained, DateTime now)
    {
        var huntStart = catalog.HuntStart;
        var huntEnd = catalog.HuntEnd;
        var huntTimes = matches.SelectMany(x => ChestTimes(x.Match, x.Detail)).Where(t => t >= huntStart).OrderBy(t => t).ToList();
        var manual = manualEntries.Sum(e => e.Chests);
        var seasonChests = Math.Max(0, huntTimes.Count + manual);
        var week = catalog.ShownWeek(now);
        var weekTimes = week == null ? new List<DateTime>() : huntTimes.Where(t => t >= week.From && t < week.To).ToList();
        var weekManual = week == null ? 0 : Math.Max(0, manualEntries.Where(e => e.WeekFromUtc == week.From).Sum(e => e.Chests));
        var weekChests = weekTimes.Count + weekManual;

        // Pace: chests a day this week (from its second day), else over the hunt.
        static double PerDay(int count, DateTime from, DateTime to) => (to - from).TotalDays is var days && days >= 1 ? count / days : 0;
        var seasonPace = huntTimes.Count > 0 ? PerDay(huntTimes.Count, huntTimes[0], now) : 0;
        var weekPace = week != null ? PerDay(weekChests, week.From, now) : 0;
        if (weekPace <= 0) weekPace = seasonPace;

        ChanceView Chance(HuntOdds odds, IReadOnlyCollection<DateTime> times, long plain, bool weekScope)
        {
            var log = catalog.LogNever(odds, times, plain);
            var p = -double.ExpM1(log);
            double? atPace = null;
            var end = weekScope ? week?.To ?? now : huntEnd;
            var pace = weekScope ? weekPace : seasonPace;
            if (pace > 0 && now < end && p < 0.75)
            {
                var projected = log + pace * (end - now).TotalDays * double.LogP1(-1.0 / odds.OneIn);
                atPace = -double.ExpM1(projected);
            }
            return new ChanceView(odds.Kind, odds.OneIn, odds.Weekly, p, log, catalog.Expected(odds, times, plain), (int)(times.Count + Math.Max(0, plain)), atPace);
        }

        // Season: a negative adjustment by hand removes chests whose boosts are then unknown.
        ChanceView Season(HuntOdds o) => manual >= 0
            ? Chance(o, huntTimes, manual, false)
            : Chance(o, Array.Empty<DateTime>(), seasonChests, false);

        var own = matches.SelectMany(x => (x.Detail?.Treasures ?? new()).Select(t => new TreasureView(x.Match.MatchId, x.Match.When,
            Describe(t.PrizeId, t.RawItem, t.FromGoldenChest, false, catalog), t.TimeSeconds, t.TimeExact, t.SourceKind, t.SourceDestroyed,
            t.SourceIsChest ?? IsChestKind(t.SourceKind), t.SourceCertain, null))).ToList();
        var team = matches.SelectMany(x =>
        {
            var listed = (x.Detail?.TeamFinds ?? new()).Select(t => new TreasureView(x.Match.MatchId, x.Match.When,
                Describe(t.PrizeId, t.RawItem, t.FromGoldenChest, true, catalog), t.TimeSeconds, false, null, false, true, false, t.Teammate));
            var missing = Math.Max(0, (x.Detail?.TeamTreasures ?? 0) - (x.Detail?.TeamFinds?.Count ?? 0));
            return listed.Concat(Enumerable.Range(0, missing).Select(_ => new TreasureView(x.Match.MatchId, x.Match.When,
                new TreasureTag("unknown", null, null, null, false, true), null, false, null, false, true, false, null)));
        }).ToList();

        var gizmoTimes = own.Where(f => !f.Tag.Golden)
            .Select(f => DateTime.SpecifyKind(f.MatchWhen, DateTimeKind.Utc).AddSeconds(f.TimeSeconds ?? 0))
            .Where(t => t >= huntStart).OrderBy(t => t).ToList();

        // The collection: a golden chest seen in a replay ticks its prize; a teammate's counts too (shared with the team).
        var seen = own.Concat(team).Select(f => f.Tag.PrizeKey).Where(k => k != null).ToHashSet();
        HuntPrizeView View(HuntPrize p) => new(p.Id, p.Kind, p.Name, $"/treasure-img/{p.Id}",
            seen.Contains(p.Id) ? "auto" : obtained.Contains(p.Id) ? "manual" : null);
        var weeks = catalog.Weeks.Select(w => new HuntWeekView(w.Number, w.From, w.To, w.Title, w.Prizes.Select(View).ToList())).ToList();
        var pirate = View(catalog.PirateShopAccess);
        var allPrizes = weeks.SelectMany(w => w.Prizes).Prepend(pirate).ToList();

        var byKind = new Dictionary<string, int>();
        foreach (var (m, d) in matches)
        {
            var kinds = d?.ByKind is { Count: > 0 } k ? k : new Dictionary<string, int> { [Kinds.Chest] = m.Chests, [Kinds.AmmoBox] = m.AmmoBoxes, [Kinds.OtherContainer] = m.OtherContainers };
            foreach (var (kind, count) in kinds) byKind[kind] = byKind.GetValueOrDefault(kind) + count;
        }

        var chests = matches.Sum(x => x.Match.Chests);
        return new HuntReport
        {
            Revision = catalog.Revision,
            OddsSource = catalog.OddsSource,
            HoursSource = catalog.HoursSource,
            NowUtc = now,
            HuntStart = huntStart,
            HuntEnd = huntEnd,
            Over = now >= huntEnd.AddDays(14),
            Hours = catalog.Hours.ToList(),
            Odds = catalog.Odds.ToList(),
            Week = week == null ? null : weeks.First(w => w.Number == week.Number && w.From == week.From),
            WeekChests = weekChests,
            WeekManual = weekManual,
            SeasonChests = seasonChests,
            Manual = manual,
            BoostedChests = huntTimes.Count(t => catalog.Boost(HuntCatalog.GoldOutfit, t) > 1),
            PirateHalfway = HuntCatalog.ChestsFor(catalog.OddsOf(HuntCatalog.PirateShop).OneIn, 0.5),
            WeekChances = catalog.Odds.Where(o => o.Weekly).Select(o => Chance(o, weekTimes, weekManual, true)).ToList(),
            SeasonChances = catalog.Odds.Select(Season).ToList(),
            GizmosFound = gizmoTimes.Count,
            ChestsSinceGizmo = gizmoTimes.Count > 0 ? huntTimes.Count(t => t > gizmoTimes[^1]) : null,
            PirateShop = pirate,
            Weeks = weeks,
            CollectionGot = allPrizes.Count(p => p.Obtained != null),
            CollectionTotal = allPrizes.Count,
            Finds = own.Concat(team).OrderByDescending(f => f.MatchWhen).ThenByDescending(f => f.TimeSeconds ?? 0).ToList(),
            Treasures = own.Count,
            TeamTreasures = team.Count,
            Sources = own.GroupBy(f => (f.SourceKind ?? Kinds.OtherContainer, f.SourceDestroyed, f.SourceIsChest))
                .Select(g => new SourceCount(g.Key.Item1, g.Key.SourceDestroyed, g.Key.SourceIsChest, g.Count()))
                .OrderByDescending(s => s.IsChest).ThenByDescending(s => s.Count).ToList(),
            ByKind = byKind,
            Destroyed = matches.Sum(x => x.Match.ChestsDestroyed),
            Matches = matches.Count,
            Chests = chests,
            PerMatch = matches.Count == 0 ? 0 : Math.Round((double)chests / matches.Count, 1),
            ManualEntries = manualEntries.OrderByDescending(e => e.AtUtc).ToList(),
        };
    }
}
