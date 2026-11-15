using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChestCounter.Shared;

namespace ChestCounter.Engine;

public sealed record EngineOptions
{
    public required string DataDir { get; init; }
    public IReplayAnalyzer Analyzer { get; init; } = new FortniteReplayAnalyzer();
    public IReplayProbe Probe { get; init; } = new FortniteReplayProbe();
    public IClock Clock { get; init; } = new SystemClock();
    public ISyncTransport Transport { get; init; } = new NetworkTransport();
    /// <summary>Used when no folder was chosen. Null = Fortnite's own folder.</summary>
    public string? DefaultDemosFolder { get; init; }
    /// <summary>Fetch newer treasure hunt data and prize pictures (off in tests).</summary>
    public bool OnlineContent { get; init; }
}

// ---- what the interface reads -----------------------------------------------------------------------------------

public sealed record MatchSummary(string MatchId, DateTime When, DateTime? StartUtc, string? Playlist, int? DurationSeconds, int? Placement,
    int? TotalPlayers, int? Eliminations, int Chests, int ChestsDestroyed, int AmmoBoxes, int OtherContainers, int TeamObserved,
    bool Counted, string? NotCountedReason, bool PossiblyIncomplete, bool IdentityReliable, int ParserVersion, string? PlayerName,
    string? ReplayFile, long? SessionId, List<TreasureTag>? Treasures = null);

public sealed record SessionTotals(long? Id, DateTime StartedUtc, int Chests, int Matches);

public sealed record Totals(int Chests, int Matches, double Average, int ChestsDestroyed, int AmmoBoxes, int OtherContainers,
    int PossiblyIncompleteMatches, MatchSummary? Best, MatchSummary? LastMatch, SessionTotals Session, int ManualChests = 0, int Treasures = 0);

public sealed record RecordingHint(string State, string? Detail);

public sealed record ImportStatus(int Files, int Counted, int Duplicates, int NotCounted, int Incomplete, int Errors, int Waiting);

public sealed record AppSettings(string DemosFolder, bool DemosFolderIsDefault, string? Profile, string Language, string TimeFormat,
    bool OnboardingDone, bool MinimizeToTray, bool StartMinimized, bool LaunchWithWindows, bool LaunchWithFortnite, OverlaySettings Overlay);

public sealed record OverlaySettings
{
    public bool Enabled { get; init; }
    public bool Locked { get; init; }
    public int Opacity { get; init; } = 90;
    public int Scale { get; init; } = 100;
    public int? X { get; init; }
    public int? Y { get; init; }
    public bool ShowPersonal { get; init; } = true;
    public bool ShowSquad { get; init; } = true;
    public bool ShowSession { get; init; } = true;
    public bool ShowLastMatch { get; init; } = true;
    public bool ShowMembers { get; init; }
    /// <summary>"combined" (members' lifetime totals) or "shared" (matches played together).</summary>
    public string SquadScope { get; init; } = "combined";
    public string ToggleHotkey { get; init; } = "Ctrl+Shift+O";
    public string LockHotkey { get; init; } = "Ctrl+Shift+L";
}

public sealed record AppState(string Version, int ParserVersion, string DataDir, AppSettings Settings, TrackingStatus Tracking, ImportStatus Import,
    RecordingHint Recording, ProfileRow? Profile, List<ProfileRow> Profiles, Totals Totals, List<MatchSummary> Recent, SyncStatus Sync,
    SquadStateDto? Squad);

public sealed record TimelinePoint(string Date, int Chests, int Matches, int AmmoBoxes);

public sealed record OverlayMember(string Name, int Chests, int? LastMatch, bool Online);

public sealed record OverlayData(int Personal, int? Squad, string SquadScope, int Session, int? LastMatch, bool LastMatchPartial,
    List<OverlayMember> Members, string? PlayerName, string? SquadName, OverlaySettings Settings);

public sealed record Diagnostics(Dictionary<string, int> FileCounts, List<FileRow> Problems, List<MatchSummary> NotCounted, List<LogRow> Log,
    List<OutboxRow> RefusedUploads, string DatabasePath, long DatabaseBytes);

/// <summary>
/// The whole local application without its window: history, replay tracking, squad sync. The desktop shell and the
/// tests both drive it through this class.
/// </summary>
public sealed class ChestEngine : IDisposable
{
    public const string AppVersion = "1.0.0";

    private readonly EngineOptions _o;
    private readonly TrackerLoop _loop;
    private SessionRow? _session;
    private bool _started;

    public LocalDb Db { get; }
    public ReplayTracker Tracker { get; }
    public SyncService Sync { get; }
    public HuntContent Hunt { get; }
    public ActivityFeed Activity { get; }
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Something the interface shows has changed. The argument names what: "tracking", "matches", "sync", "settings".</summary>
    public event Action<string>? Changed;

    public object ImportOriginal(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Matches", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid Chest Counter data.json: Matches is missing.");
        var matches = new List<(AnalyzedMatch Match, DateTime Analyzed)>();
        var skipped = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var original = row.Deserialize<global::ChestCounter.MatchRecord>() ?? throw new InvalidDataException("Invalid match in data.json.");
            // The original's fallback identifier is filename-based and cannot be deduplicated reliably.
            if (string.IsNullOrWhiteSpace(original.SessionId) || original.SessionId.EndsWith("-" + original.ReplayFile, StringComparison.Ordinal))
            { skipped++; continue; }
            var match = FortniteReplayAnalyzer.Convert(original, original.ReplayFile, "");
            if (match.ChestsOpened < 0 || match.AmmoBoxesOpened < 0 || match.OtherContainersOpened < 0)
                throw new InvalidDataException("Invalid negative count in data.json.");
            if (ModeRules.WhyNot(match) != null) { skipped++; continue; }
            matches.Add((match, original.AnalyzedAtUtc == default ? _o.Clock.UtcNow : original.AnalyzedAtUtc));
        }
        var imported = Db.ImportOriginal(matches);
        if (imported > 0)
        {
            if (Sync.Configured) Sync.ResyncAll();
            Changed?.Invoke("matches");
        }
        return new { imported, duplicates = matches.Count - imported, skipped };
    }

    public ChestEngine(EngineOptions options)
    {
        _o = options;
        Directory.CreateDirectory(options.DataDir);
        Db = new LocalDb(Path.Combine(options.DataDir, "history.db"));
        Sync = new SyncService(Db, options.Transport, options.Clock);
        Tracker = new ReplayTracker(Db, options.Analyzer, options.Probe, options.Clock, () => DemosFolder, () => Sync.Configured);
        _loop = new TrackerLoop(Tracker, () => DemosFolder, ex => Db.Log("error", "tracker", ex.ToString(), options.Clock.UtcNow));
        Tracker.Changed += () => Changed?.Invoke("tracking");
        Tracker.MatchSaved += _ =>
        {
            Changed?.Invoke("matches");
            Sync.Wake();
        };
        Sync.Changed += () => Changed?.Invoke("sync");
        Hunt = new HuntContent(options.DataDir, options.OnlineContent);
        Hunt.Changed += () =>
        {
            Activity!.Add("content", new { revision = Hunt.Catalog.Revision });
            Changed?.Invoke("hunt");
        };
        Activity = new ActivityFeed(Db, options.Clock, () => Hunt.Catalog, key => GetTotals(key).Chests + ManualChests);
        Activity.Attach(Tracker);
        Activity.Changed += () => Changed?.Invoke("activity");
    }

    // ---- treasure hunt ------------------------------------------------------------------------------------------

    public List<ManualEntry> ManualEntries
    {
        get
        {
            try { return Db.GetSetting("manualChests") is { } json ? Wire.Deserialize<List<ManualEntry>>(json) ?? new() : new(); }
            catch { return new(); }
        }
    }

    public int ManualChests => ManualEntries.Sum(e => e.Chests);

    public HashSet<string> ObtainedPrizes
    {
        get
        {
            try { return Db.GetSetting("obtainedPrizes") is { } json ? Wire.Deserialize<HashSet<string>>(json) ?? new() : new(); }
            catch { return new(); }
        }
    }

    /// <summary>Adds chests by hand (a negative number removes some). Local only: never sent to the squad.</summary>
    public ManualEntry AddManual(int chests, bool thisWeek)
    {
        if (chests == 0 || Math.Abs(chests) > 1_000_000) throw new ArgumentOutOfRangeException(nameof(chests), "Enter a number between -1,000,000 and 1,000,000.");
        var now = _o.Clock.UtcNow;
        var entry = new ManualEntry(Guid.NewGuid().ToString("N")[..12], chests, now, thisWeek ? Hunt.Catalog.WeekOf(now)?.From : null);
        var list = ManualEntries;
        list.Add(entry);
        Db.SetSetting("manualChests", Wire.Serialize(list));
        Activity.Add("manual", new { chests, thisWeek = entry.WeekFromUtc != null, total = PersonalTotal() });
        Changed?.Invoke("matches");
        return entry;
    }

    public void RemoveManual(string id)
    {
        var list = ManualEntries;
        var entry = list.FirstOrDefault(e => e.Id == id);
        if (entry == null) return;
        list.Remove(entry);
        Db.SetSetting("manualChests", list.Count == 0 ? null : Wire.Serialize(list));
        Activity.Add("manualRemoved", new { chests = entry.Chests, total = PersonalTotal() });
        Changed?.Invoke("matches");
    }

    public void SetObtained(string prizeId, bool on)
    {
        var prize = Hunt.Catalog.Prize(prizeId) ?? throw new ArgumentException("Unknown treasure.");
        var set = ObtainedPrizes;
        if (on ? !set.Add(prizeId) : !set.Remove(prizeId)) return;
        Db.SetSetting("obtainedPrizes", Wire.Serialize(set));
        if (on) Activity.Add("collected", new { id = prize.Id, name = prize.Name, kind = prize.Kind });
        Changed?.Invoke("hunt");
    }

    private int PersonalTotal() => GetTotals(CurrentProfile()?.Key).Chests + ManualChests;

    public HuntReport GetHunt()
    {
        var key = CurrentProfile()?.Key;
        var matches = key == null ? new() : Db.CountedWithDetail(key);
        return Treasures.Build(Hunt.Catalog, matches, ManualEntries, ObtainedPrizes, _o.Clock.UtcNow);
    }

    public List<ActivityItem> GetActivity(int limit) => Activity.Recent(limit);

    private async Task RefreshContentLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            await Hunt.RefreshAsync(_stop.Token);
            try { await Task.Delay(TimeSpan.FromHours(6), _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Opens a tracking session and prepares the re-analysis a parser update calls for. No threads yet.</summary>
    public void Begin()
    {
        if (_session != null) return;
        _session = Db.StartSession(_o.Clock.UtcNow);
        Tracker.SessionId = _session.Id;
        Tracker.SessionStartUtc = _session.StartedUtc;
        var requeued = Tracker.RequeueOutdated();
        if (requeued > 0)
        {
            Db.Log("info", "tracker", $"parser updated to version {_o.Analyzer.Version}: {requeued} replay(s) will be analyzed again", _o.Clock.UtcNow);
        }
    }

    /// <summary>Starts the background work: folder watching and squad sync.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        Begin();
        Activity.Add("started", new { version = AppVersion, folder = DemosFolder });
        _loop.Start();
        Sync.Start();
        _ = Task.Run(RefreshContentLoop);
    }

    // ---- settings ---------------------------------------------------------------------------------------------

    public static string FortniteDemosFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    private string DefaultFolder => _o.DefaultDemosFolder ?? FortniteDemosFolder;
    public string DemosFolder => Db.GetSetting("demosFolder") is { Length: > 0 } f ? f : DefaultFolder;

    private bool Flag(string key, bool fallback = false) => Db.GetSetting(key) is { } v ? v == "1" : fallback;

    public OverlaySettings Overlay
    {
        get
        {
            try { return Db.GetSetting("overlay") is { } json ? Wire.Deserialize<OverlaySettings>(json) ?? new() : new(); }
            catch { return new(); }
        }
    }

    public AppSettings Settings => new(DemosFolder, Db.GetSetting("demosFolder") is not { Length: > 0 }, Db.GetSetting("profile"),
        Db.GetSetting("language") ?? "en", Db.GetSetting("timeFormat") ?? "auto", Flag("onboardingDone"), Flag("minimizeToTray", true),
        Flag("startMinimized"), Flag("launchWithWindows"), Flag("launchWithFortnite"), Overlay);

    /// <summary>Applies the settings present in <paramref name="patch"/> (others are left as they are).</summary>
    public void UpdateSettings(Dictionary<string, System.Text.Json.JsonElement> patch)
    {
        foreach (var (key, value) in patch)
        {
            switch (key)
            {
                case "demosFolder":
                    var folder = value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString()?.Trim() : null;
                    Db.SetSetting("demosFolder", string.IsNullOrEmpty(folder) ? null : folder);
                    _loop.Wake(force: false);
                    break;
                case "profile":
                    Db.SetSetting("profile", value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null);
                    break;
                case "language" when value.GetString() is "en" or "fr":
                case "timeFormat" when value.GetString() is "auto" or "12" or "24":
                    Db.SetSetting(key, value.GetString());
                    break;
                case "onboardingDone" or "minimizeToTray" or "startMinimized" or "launchWithWindows" or "launchWithFortnite":
                    Db.SetSetting(key, value.GetBoolean() ? "1" : "0");
                    break;
                case "overlay":
                    var o = System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>(value.GetRawText(), Wire.Json) ?? new();
                    o = o with { Opacity = Math.Clamp(o.Opacity, 20, 100), Scale = Math.Clamp(o.Scale, 60, 250), SquadScope = o.SquadScope == "shared" ? "shared" : "combined" };
                    Db.SetSetting("overlay", Wire.Serialize(o));
                    break;
            }
        }
        Changed?.Invoke("settings");
    }

    // ---- actions ----------------------------------------------------------------------------------------------

    public void ScanNow() => _loop.Wake();

    /// <summary>Reads every replay still on disk again and replaces its match. Matches whose replay is gone are kept.</summary>
    public void ReanalyzeAll()
    {
        var n = Db.ForgetFiles();
        Db.Log("info", "tracker", $"re-analysis requested: {n} file record(s) cleared", _o.Clock.UtcNow);
        _loop.Wake(force: true);
    }

    public void RetryFailed()
    {
        Db.ForgetFiles(new[] { FileStatus.Error, FileStatus.Incomplete });
        _loop.Wake();
    }

    public string Backup(string? folder = null)
    {
        folder ??= Path.Combine(_o.DataDir, "backups");
        var path = Path.Combine(folder, $"history-{_o.Clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.db");
        Db.BackupTo(path);
        return path;
    }

    public string ExportCsv(string? folder = null)
    {
        folder ??= Path.Combine(_o.DataDir, "exports");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"matches-{_o.Clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("match_id,start_utc,player,player_id,playlist,duration_s,placement,chests,chests_destroyed,ammo_boxes,other_containers,counted,not_counted_reason,possibly_incomplete,parser_version,replay_file\n");
        static string Q(string? s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
        foreach (var m in Db.Matches(new LocalDb.MatchFilter(Counted: null, Limit: int.MaxValue)))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{Q(m.MatchId)},{m.StartUtc:O},{Q(m.PlayerName)},{Q(m.PlayerId)},{Q(m.Playlist)},{m.DurationSeconds},{m.Placement},{m.Chests},{m.ChestsDestroyed},{m.AmmoBoxes},{m.OtherContainers},{(m.Counted ? 1 : 0)},{Q(m.NotCountedReason)},{(m.PossiblyIncomplete ? 1 : 0)},{m.ParserVersion},{Q(m.ReplayFile)}\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    // ---- views ------------------------------------------------------------------------------------------------

    public static MatchSummary Summary(MatchRow m) => new(m.MatchId, m.When, m.StartUtc, m.Playlist, m.DurationSeconds, m.Placement, m.TotalPlayers,
        m.Eliminations, m.Chests, m.ChestsDestroyed, m.AmmoBoxes, m.OtherContainers, m.TeamObserved, m.Counted, m.NotCountedReason,
        m.PossiblyIncomplete, m.IdentityReliable, m.ParserVersion, m.PlayerName, m.ReplayFile, m.SessionId);

    /// <summary>The selected profile when it has matches, else the most recently played account.</summary>
    public ProfileRow? CurrentProfile(List<ProfileRow>? profiles = null)
    {
        profiles ??= Db.Profiles();
        var chosen = Db.GetSetting("profile");
        return profiles.FirstOrDefault(p => p.Key == chosen) ?? profiles.FirstOrDefault();
    }

    public Totals GetTotals(string? profileKey)
    {
        var all = profileKey == null ? new List<MatchRow>() : Db.Matches(new LocalDb.MatchFilter(ProfileKey: profileKey, Limit: int.MaxValue));
        var session = _session;
        var inSession = session == null ? new List<MatchRow>() : all.Where(m => m.SessionId == session.Id).ToList();
        var best = all.OrderByDescending(m => m.Chests).ThenByDescending(m => m.When).FirstOrDefault();
        return new Totals(
            all.Sum(m => m.Chests), all.Count, all.Count == 0 ? 0 : Math.Round((double)all.Sum(m => m.Chests) / all.Count, 1),
            all.Sum(m => m.ChestsDestroyed), all.Sum(m => m.AmmoBoxes), all.Sum(m => m.OtherContainers), all.Count(m => m.PossiblyIncomplete),
            best == null ? null : Summary(best), all.Count == 0 ? null : Summary(all[0]),
            new SessionTotals(session?.Id, session?.StartedUtc ?? _o.Clock.UtcNow, inSession.Sum(m => m.Chests), inSession.Count),
            ManualChests, profileKey == null ? 0 : Db.TreasureCount(profileKey));
    }

    /// <summary>The summary of a match with the treasures of its replay, named.</summary>
    public MatchSummary SummaryWithTreasures(MatchRow m) =>
        Summary(m) with { Treasures = Treasures.Tags(Db.GetMatchDetail(m.MatchId), Hunt.Catalog) };

    public AppState GetState()
    {
        var profiles = Db.Profiles();
        var profile = CurrentProfile(profiles);
        var counts = Db.FileStatusCounts();
        var tracking = Tracker.Status;
        var recent = profile == null ? new() : Db.Matches(new LocalDb.MatchFilter(ProfileKey: profile.Key, Limit: 8)).Select(SummaryWithTreasures).ToList();
        return new AppState(AppVersion, _o.Analyzer.Version, _o.DataDir, Settings, tracking,
            new ImportStatus(tracking.ReplayFiles, counts.GetValueOrDefault(FileStatus.Counted), counts.GetValueOrDefault(FileStatus.Duplicate),
                counts.GetValueOrDefault(FileStatus.NotCounted), counts.GetValueOrDefault(FileStatus.Incomplete), counts.GetValueOrDefault(FileStatus.Error),
                tracking.Pending.Count),
            GetRecordingHint(), profile, profiles, GetTotals(profile?.Key), recent, Sync.Status, Sync.Squad);
    }

    public List<MatchSummary> GetMatches(DateTime? fromUtc, DateTime? toUtc, long? sessionId, bool? counted, int limit, int offset) =>
        Db.Matches(new LocalDb.MatchFilter(counted == false ? null : CurrentProfile()?.Key, fromUtc, toUtc, sessionId, counted, Math.Clamp(limit, 1, 1000), Math.Max(0, offset)))
            .Select(SummaryWithTreasures).ToList();

    public object? GetMatch(string matchId) => Db.GetMatch(matchId) is { } m ? new { match = SummaryWithTreasures(m), detail = Db.GetMatchDetail(matchId), m.Map, m.GameVersion, m.PlayerId, m.FirstAnalyzedUtc, m.AnalyzedUtc } : null;

    public List<SessionTotals> GetSessions()
    {
        var key = CurrentProfile()?.Key;
        if (key == null) return new();
        var matches = Db.Matches(new LocalDb.MatchFilter(ProfileKey: key, Limit: int.MaxValue)).Where(m => m.SessionId != null).ToLookup(m => m.SessionId!.Value);
        return Db.SessionsWithMatches().Where(s => matches.Contains(s.Id))
            .Select(s => new SessionTotals(s.Id, s.StartedUtc, matches[s.Id].Sum(m => m.Chests), matches[s.Id].Count())).ToList();
    }

    /// <summary>Chests per local calendar day (days without a match are included, at zero) or per week (Monday).</summary>
    public List<TimelinePoint> GetTimeline(DateTime? fromUtc, DateTime? toUtc, string bucket = "day", TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var key = CurrentProfile()?.Key;
        if (key == null) return new();
        var matches = Db.Matches(new LocalDb.MatchFilter(ProfileKey: key, FromUtc: fromUtc, ToUtc: toUtc, Limit: int.MaxValue));
        if (matches.Count == 0) return new();

        DateOnly Day(DateTime utc)
        {
            var d = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
            return bucket == "week" ? d.AddDays(-(((int)d.DayOfWeek + 6) % 7)) : d;
        }

        var groups = matches.GroupBy(m => Day(m.When)).ToDictionary(g => g.Key, g => g.ToList());
        var first = fromUtc != null ? Day(fromUtc.Value) : groups.Keys.Min();
        var last = toUtc != null ? Day(toUtc.Value.AddTicks(-1)) : groups.Keys.Max();
        var step = bucket == "week" ? 7 : 1;
        var points = new List<TimelinePoint>();
        for (var d = first; d <= last && points.Count < 1500; d = d.AddDays(step))
        {
            var list = groups.GetValueOrDefault(d);
            points.Add(new TimelinePoint(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), list?.Sum(m => m.Chests) ?? 0, list?.Count ?? 0, list?.Sum(m => m.AmmoBoxes) ?? 0));
        }
        return points;
    }

    public OverlayData GetOverlay()
    {
        var profile = CurrentProfile();
        var totals = GetTotals(profile?.Key);
        var squad = Sync.Squad;
        var settings = Overlay;
        var lastShared = squad?.SharedMatches.FirstOrDefault(s => s.Status != SharedMatchStatus.Ambiguous);
        // "Last match": the latest shared squad match when it is this player's latest match too, else their own.
        var useShared = lastShared != null && totals.LastMatch != null && lastShared.MatchId == totals.LastMatch.MatchId;
        return new OverlayData(
            totals.Chests + totals.ManualChests,
            squad == null ? null : settings.SquadScope == "shared" ? squad.SharedChests : squad.CombinedMemberChests,
            settings.SquadScope,
            totals.Session.Chests,
            useShared ? lastShared!.ConfirmedChests : totals.LastMatch?.Chests,
            useShared && lastShared!.Status == SharedMatchStatus.Partial,
            squad?.Members.Select(m => new OverlayMember(m.DisplayName, settings.SquadScope == "shared" ? m.SharedChests : m.LifetimeChests,
                useShared ? lastShared!.Contributions.FirstOrDefault(c => c.UserId == m.UserId)?.Chests : null, m.Online)).ToList() ?? new(),
            profile?.Name, squad?.Name, settings);
    }

    public Diagnostics GetDiagnostics()
    {
        var problems = Db.Files(FileStatus.Error, 100).Concat(Db.Files(FileStatus.Incomplete, 100)).OrderByDescending(f => f.ProcessedUtc).ToList();
        long size = 0;
        try { size = new FileInfo(Db.Path).Length; } catch { /* just a figure */ }
        return new Diagnostics(Db.FileStatusCounts(), problems,
            Db.Matches(new LocalDb.MatchFilter(Counted: false, Limit: 100)).Select(Summary).ToList(),
            Db.Logs(200), Db.RefusedUploads(), Db.Path, size);
    }

    /// <summary>
    /// Whether Fortnite seems to be saving replays. There is no reliable switch to read, so this is evidence only:
    /// a recent replay in the folder, and the replay setting of Fortnite's own settings file when it can be found.
    /// </summary>
    public RecordingHint GetRecordingHint()
    {
        try
        {
            var dir = new DirectoryInfo(DemosFolder);
            if (!dir.Exists)
            {
                return new RecordingHint("no_folder", null);
            }
            var newest = dir.EnumerateFiles("*.replay").Select(f => (DateTime?)f.LastWriteTimeUtc).DefaultIfEmpty(null).Max();
            var ini = Path.Combine(Path.GetDirectoryName(DemosFolder.TrimEnd('\\', '/')) ?? "", "Config", "WindowsClient", "GameUserSettings.ini");
            bool? setting = null;
            if (File.Exists(ini))
            {
                foreach (var line in File.ReadLines(ini))
                {
                    var m = Regex.Match(line, @"^\s*b\w*Record\w*Replay\w*\s*=\s*(True|False)\s*$", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        setting = (setting ?? false) || m.Groups[1].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            if (newest is { } n && _o.Clock.UtcNow - n < TimeSpan.FromDays(14))
            {
                return new RecordingHint("likely_on", n.ToString("O"));
            }
            if (setting == false)
            {
                return new RecordingHint("likely_off", null);
            }
            return new RecordingHint(newest == null ? "no_replays" : "stale", newest?.ToString("O"));
        }
        catch
        {
            return new RecordingHint("unknown", null);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _loop.Dispose();
        Hunt.Dispose();
        Sync.Dispose();
        if (_session != null)
        {
            try { Db.EndSession(_session.Id, _o.Clock.UtcNow); } catch { /* closing */ }
        }
        Db.Dispose();
    }
}
