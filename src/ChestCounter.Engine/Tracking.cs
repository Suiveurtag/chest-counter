using ChestCounter.Shared;

namespace ChestCounter.Engine;

public sealed record PendingFile(string FileName, string Reason);

public static class PendingReason
{
    /// <summary>Fortnite is still recording this match: nothing can be read before it ends.</summary>
    public const string Recording = "recording";
    /// <summary>The match is over; waiting for the file to stop changing.</summary>
    public const string Settling = "settling";
}

public sealed record FileOutcome(string FileName, string Status, MatchRow? Match = null, bool Replaced = false, string? Detail = null);

public sealed record ScanSummary(int Total, int Counted, int Replaced, int Duplicates, int NotCounted, int Incomplete, int Errors, List<FileOutcome> Outcomes)
{
    public static readonly ScanSummary Empty = new(0, 0, 0, 0, 0, 0, 0, new());
}

/// <summary>What the tracker is doing right now, for the interface.</summary>
public sealed record TrackingStatus
{
    /// <summary>"idle", "scanning" or "paused" (replay folder missing).</summary>
    public string State { get; init; } = "idle";
    public bool FolderFound { get; init; }
    public int ReplayFiles { get; init; }
    public int ScanIndex { get; init; }
    public int ScanTotal { get; init; }
    public string? CurrentFile { get; init; }
    public List<PendingFile> Pending { get; init; } = new();
    public FileOutcome? LastOutcome { get; init; }
    public DateTime? LastOutcomeUtc { get; init; }
    public DateTime? LastScanUtc { get; init; }
}

/// <summary>
/// Reads every finished replay of the folder exactly once and stores the result.
///
/// Two identities are kept apart. A FILE is "name|size|last write": an unchanged file is never opened again. A MATCH
/// is the game session id inside the replay: a renamed or copied replay is another file but the same match, and
/// changes nothing. The readiness rules (live flag, key written, size stable) are those of the reference tracker.
/// </summary>
public sealed class ReplayTracker
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinalizedQuietPeriod = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan OldReplayAge = TimeSpan.FromMinutes(1);
    /// <summary>Still flagged live but untouched for this long: the recording was cut and will never be finished.</summary>
    private static readonly TimeSpan AbandonedLiveAge = TimeSpan.FromMinutes(10);

    private readonly LocalDb _db;
    private readonly IReplayAnalyzer _analyzer;
    private readonly IReplayProbe _probe;
    private readonly IClock _clock;
    private readonly Func<string> _folder;
    private readonly Func<bool> _syncEnabled;
    private readonly Dictionary<string, long> _lastSeenSize = new();
    private readonly object _scanGate = new();
    private volatile TrackingStatus _status = new();

    public ReplayTracker(LocalDb db, IReplayAnalyzer analyzer, IReplayProbe probe, IClock clock, Func<string> folder, Func<bool>? syncEnabled = null)
    {
        _db = db;
        _analyzer = analyzer;
        _probe = probe;
        _clock = clock;
        _folder = folder;
        _syncEnabled = syncEnabled ?? (() => false);
    }

    public TrackingStatus Status => _status;
    public long? SessionId { get; set; }
    public DateTime SessionStartUtc { get; set; } = DateTime.MaxValue;

    /// <summary>Raised after each file and whenever the status changes. Handlers must be quick.</summary>
    public event Action? Changed;

    /// <summary>Raised when a stored match was added or replaced.</summary>
    public event Action<MatchRow>? MatchSaved;

    /// <summary>A pass is about to read this many replays (the activity feed groups big imports).</summary>
    public event Action<int>? ScanStarting;

    /// <summary>One replay is about to be read (its name and size).</summary>
    public event Action<string, long>? FileStarting;

    /// <summary>One replay was read.</summary>
    public event Action<FileOutcome>? FileProcessed;

    /// <summary>A pass that read at least one replay is over.</summary>
    public event Action<ScanSummary>? ScanFinished;

    private void Raise(Action notify)
    {
        try { notify(); } catch { /* a listener must not stop the import */ }
    }

    /// <summary>True while a replay is waiting (a match is being recorded or has just ended): check again soon.</summary>
    public bool HasPending => _status.Pending.Count > 0;

    public static string FileKey(FileInfo f) => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    private IEnumerable<FileInfo> ReplayFiles()
    {
        DirectoryInfo dir;
        try
        {
            dir = new DirectoryInfo(_folder());
        }
        catch
        {
            return Enumerable.Empty<FileInfo>();
        }
        return dir.Exists ? dir.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc).ToList() : Enumerable.Empty<FileInfo>();
    }

    /// <summary>After a parser update: replays read by an older version are read again (their matches get replaced).</summary>
    public int RequeueOutdated() => _db.ForgetFiles(new[] { FileStatus.Counted, FileStatus.Duplicate, FileStatus.NotCounted, FileStatus.Error }, _analyzer.Version);

    /// <summary>
    /// One pass over the folder. Safe to call at any time and any number of times: only files not read yet are opened.
    /// </summary>
    /// <param name="ignoreQuietPeriod">Read finalized replays without waiting for them to settle (import, tests).</param>
    /// <param name="force">Replace stored matches even when nothing says the new reading is better (manual re-analysis).</param>
    public ScanSummary ScanOnce(bool ignoreQuietPeriod = false, bool force = false, CancellationToken ct = default)
    {
        lock (_scanGate)
        {
            var folderFound = Directory.Exists(SafeFolder());
            var all = ReplayFiles().ToList();
            var known = _db.ProcessedFileKeys();
            var pending = new List<PendingFile>();
            var ready = new List<FileInfo>();
            foreach (var file in all)
            {
                if (known.Contains(FileKey(file)))
                {
                    continue;
                }
                var reason = NotReady(file, ignoreQuietPeriod);
                if (reason != null)
                {
                    pending.Add(new PendingFile(file.Name, reason));
                }
                else
                {
                    ready.Add(file);
                }
            }

            var now = _clock.UtcNow;
            if (ready.Count == 0)
            {
                Publish(_status with { State = folderFound ? "idle" : "paused", FolderFound = folderFound, ReplayFiles = all.Count, Pending = pending, ScanIndex = 0, ScanTotal = 0, CurrentFile = null, LastScanUtc = now });
                return ScanSummary.Empty;
            }

            var outcomes = new List<FileOutcome>();
            Raise(() => ScanStarting?.Invoke(ready.Count));
            for (var i = 0; i < ready.Count && !ct.IsCancellationRequested; i++)
            {
                var file = ready[i];
                Publish(_status with { State = "scanning", FolderFound = true, ReplayFiles = all.Count, Pending = pending, ScanIndex = i, ScanTotal = ready.Count, CurrentFile = file.Name });
                Raise(() => FileStarting?.Invoke(file.Name, file.Length));
                var outcome = Process(file, force);
                Raise(() => FileProcessed?.Invoke(outcome));
                outcomes.Add(outcome);
                _lastSeenSize.Remove(file.FullName);
                _status = _status with { LastOutcome = outcome, LastOutcomeUtc = _clock.UtcNow, ScanIndex = i + 1 };
                if (outcome.Match != null && outcome.Status == FileStatus.Counted || outcome.Replaced)
                {
                    try { MatchSaved?.Invoke(outcome.Match!); } catch { /* a listener must not stop the import */ }
                }
                Changed?.Invoke();
            }

            Publish(_status with { State = "idle", ScanIndex = 0, ScanTotal = 0, CurrentFile = null, LastScanUtc = _clock.UtcNow });
            var summary = new ScanSummary(outcomes.Count,
                outcomes.Count(o => o.Status == FileStatus.Counted && !o.Replaced),
                outcomes.Count(o => o.Replaced),
                outcomes.Count(o => o.Status == FileStatus.Duplicate),
                outcomes.Count(o => o.Status == FileStatus.NotCounted),
                outcomes.Count(o => o.Status == FileStatus.Incomplete),
                outcomes.Count(o => o.Status == FileStatus.Error),
                outcomes);
            Raise(() => ScanFinished?.Invoke(summary));
            return summary;
        }
    }

    private string SafeFolder()
    {
        try { return _folder(); } catch { return ""; }
    }

    private void Publish(TrackingStatus status)
    {
        _status = status;
        Changed?.Invoke();
    }

    /// <summary>Null when the file can be read now, else why it has to wait.</summary>
    private string? NotReady(FileInfo file, bool ignoreQuietPeriod)
    {
        var probe = _probe.Probe(file.FullName);
        var quietFor = _clock.UtcNow - file.LastWriteTimeUtc;
        if (probe.IsLive == true)
        {
            // Being recorded, unless the recording was cut long ago (Process marks it incomplete).
            return quietFor > AbandonedLiveAge ? null : PendingReason.Recording;
        }
        if (probe.IsLive == false && (ignoreQuietPeriod || quietFor > OldReplayAge))
        {
            return null;
        }

        // The size must be the same on two checks in a row.
        var stable = _lastSeenSize.TryGetValue(file.FullName, out var size) && size == file.Length;
        _lastSeenSize[file.FullName] = file.Length;

        if (probe.IsLive == null)
        {
            // Header unreadable: Fortnite may have just created the file. Be patient before calling it broken.
            return stable && quietFor > TimeSpan.FromMinutes(2) ? null : PendingReason.Settling;
        }
        // Fortnite clears the live flag and writes the real key when it stops recording.
        var wait = probe.KeyMissing ? QuietPeriod : FinalizedQuietPeriod;
        return stable && quietFor >= wait ? null : PendingReason.Settling;
    }

    private FileOutcome Process(FileInfo file, bool force)
    {
        var key = FileKey(file);
        var now = _clock.UtcNow;
        var version = _analyzer.Version;
        FileRow Row(string status, string? fingerprint, string? matchId, string? detail) =>
            new(key, file.Name, file.Length, fingerprint, status, matchId, detail, version, now);

        try
        {
            var probe = _probe.Probe(file.FullName);
            if (probe.IsLive == true && probe.KeyMissing)
            {
                _db.SaveFile(Row(FileStatus.Incomplete, null, null, "recording cut during the match: Fortnite never wrote the decryption key"));
                _db.Log("warn", "tracker", $"{file.Name}: incomplete replay (recording cut during the match), it cannot be counted", now);
                return new FileOutcome(file.Name, FileStatus.Incomplete);
            }

            var fingerprint = Fingerprint.Of(file.FullName);

            // Same bytes already read by this parser version (renamed or copied replay): nothing to decode again.
            if (!force && _db.FindByFingerprint(fingerprint, version) is { } twin)
            {
                _db.SaveFile(Row(FileStatus.Duplicate, fingerprint, twin.MatchId, $"same content as {twin.FileName}"));
                return new FileOutcome(file.Name, FileStatus.Duplicate, twin.MatchId == null ? null : _db.GetMatch(twin.MatchId), Detail: twin.FileName);
            }

            AnalyzedMatch match;
            try
            {
                match = _analyzer.Analyze(file.FullName, fingerprint);
            }
            catch (Exception ex)
            {
                var message = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
                _db.SaveFile(Row(FileStatus.Error, fingerprint, null, message));
                _db.Log("error", "tracker", $"{file.Name}: unreadable replay ({file.Length} bytes): {ex.GetType().Name}: {message}", now);
                return new FileOutcome(file.Name, FileStatus.Error, Detail: message);
            }

            var whyNot = ModeRules.WhyNot(match);
            var existing = _db.GetMatch(match.MatchId);

            // The stored result stands unless this reading is better: a newer parser, or a longer recording of the
            // same match. Otherwise this file is one more copy of a match already counted.
            var better = existing == null || force || match.ParserVersion > existing.ParserVersion
                         || (match.ParserVersion == existing.ParserVersion && file.Length > existing.SourceSize);
            if (!better)
            {
                _db.SaveFile(Row(FileStatus.Duplicate, fingerprint, match.MatchId, "match already stored"));
                return new FileOutcome(file.Name, FileStatus.Duplicate, existing);
            }

            // A match belongs to the tracking session it was played in; imported history belongs to none.
            var end = (match.StartUtc ?? now).AddSeconds(match.DurationSeconds ?? 0);
            var session = existing?.SessionId ?? (match.StartUtc != null && end >= SessionStartUtc ? SessionId : null);

            ContributionDto? upload = null;
            if (_syncEnabled() && match.PlayerId != null && (whyNot == null || existing?.Counted == true))
            {
                // Counted: send it. No longer counted but it was: withdraw it from the squad too.
                upload = new ContributionDto
                {
                    MatchId = match.MatchId,
                    PlayerId = match.PlayerId,
                    PlayerName = match.PlayerName ?? "",
                    ChestsOpened = match.ChestsOpened,
                    ChestsDestroyed = match.ChestsDestroyed,
                    AmmoBoxesOpened = match.AmmoBoxesOpened,
                    OtherContainersOpened = match.OtherContainersOpened,
                    MatchStartUtc = match.StartUtc,
                    DurationSeconds = match.DurationSeconds,
                    Playlist = match.Playlist,
                    ParserVersion = match.ParserVersion,
                    Counted = whyNot == null,
                    PossiblyIncomplete = match.PossiblyIncomplete,
                    IdentityReliable = match.IdentityReliable,
                    Roster = match.Roster,
                    Treasures = LocalDb.TreasuresForUpload(match.Treasures),
                };
            }

            var status = whyNot == null ? FileStatus.Counted : FileStatus.NotCounted;
            var saved = _db.SaveAnalysis(match, whyNot, Row(status, fingerprint, match.MatchId, whyNot), replace: true, file.Length, session, now, upload);
            if (existing != null)
            {
                _db.Log("info", "tracker", $"{file.Name}: match {match.MatchId} re-analyzed (parser {existing.ParserVersion} -> {match.ParserVersion}), chests {existing.Chests} -> {match.ChestsOpened}", now);
            }
            return new FileOutcome(file.Name, status, saved, Replaced: existing != null, Detail: whyNot);
        }
        catch (IOException ex)
        {
            // Locked or vanished between two steps: not recorded, the next scan tries again.
            _db.Log("warn", "tracker", $"{file.Name}: could not be opened ({ex.Message}), will retry", now);
            return new FileOutcome(file.Name, "retry", Detail: ex.Message);
        }
    }
}

/// <summary>
/// Keeps the tracker running in the background: file system notifications wake it up at once, and a periodic
/// reconciliation scan catches whatever a notification missed. Idle cost: one directory listing per interval.
/// </summary>
public sealed class TrackerLoop : IDisposable
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingInterval = TimeSpan.FromSeconds(3);

    private readonly ReplayTracker _tracker;
    private readonly Func<string> _folder;
    private readonly Action<Exception> _onError;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private FileSystemWatcher? _watcher;
    private string? _watched;
    private Thread? _thread;
    private volatile bool _force;

    public TrackerLoop(ReplayTracker tracker, Func<string> folder, Action<Exception> onError)
    {
        _tracker = tracker;
        _folder = folder;
        _onError = onError;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ReplayTracker", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Asks for a scan now (a file changed, the folder setting changed, the user clicked "scan").</summary>
    public void Wake(bool force = false)
    {
        _force |= force;
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already asked */ }
    }

    private void Run()
    {
        var first = true;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Watch();
                var force = _force;
                _force = false;
                // The first pass imports what is already there: nothing to wait for on finalized replays.
                _tracker.ScanOnce(ignoreQuietPeriod: first || force, force: force, _stop.Token);
                first = false;
            }
            catch (Exception ex)
            {
                _onError(ex);
            }

            try
            {
                _wake.Wait(_tracker.HasPending ? PendingInterval : IdleInterval, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Watch()
    {
        string folder;
        try { folder = _folder(); } catch { folder = ""; }
        if (folder == _watched && _watcher != null)
        {
            return;
        }
        _watcher?.Dispose();
        _watcher = null;
        _watched = folder;
        if (!Directory.Exists(folder))
        {
            return;
        }
        try
        {
            var watcher = new FileSystemWatcher(folder, "*.replay")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            watcher.Created += (_, _) => Wake();
            watcher.Changed += (_, _) => Wake();
            watcher.Renamed += (_, _) => Wake();
            watcher.Error += (_, _) => { _watched = null; Wake(); }; // buffer overflow or folder gone: rebuild, rescan
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            _onError(ex); // notifications unavailable (network drive...): the periodic scan still runs
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Wake();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _watcher?.Dispose();
    }
}
