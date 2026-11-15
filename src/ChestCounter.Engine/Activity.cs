using System.Text.Json;
using ChestCounter.Shared;

namespace ChestCounter.Engine;

public sealed record ActivityItem(long Id, DateTime AtUtc, string Kind, JsonElement Data);

/// <summary>
/// The story of what the counter did, for the activity feed: a match being recorded, its replay read, what it gave
/// (chests, treasures, the total after it), big imports summed up in one line. Kept in the database (last 600).
/// Each item is data in codes and numbers; the interface words it.
/// </summary>
public sealed class ActivityFeed
{
    private readonly LocalDb _db;
    private readonly IClock _clock;
    private readonly Func<HuntCatalog> _catalog;
    private readonly Func<string, int> _total;
    private readonly HashSet<string> _announced = new();
    private readonly object _gate = new();
    private bool _batch;
    private int _bCounted, _bReplaced, _bChests, _bTreasures, _bSkipped, _bErrors;

    /// <summary>A line was added.</summary>
    public event Action? Changed;

    public ActivityFeed(LocalDb db, IClock clock, Func<HuntCatalog> catalog, Func<string, int> personalTotal)
    {
        _db = db;
        _clock = clock;
        _catalog = catalog;
        _total = personalTotal;
    }

    public void Add(string kind, object data)
    {
        try
        {
            _db.AddActivity(kind, JsonSerializer.Serialize(data, Wire.Json), _clock.UtcNow);
            Changed?.Invoke();
        }
        catch
        {
            // the feed must never stop counting
        }
    }

    public List<ActivityItem> Recent(int limit) => _db.Activities(Math.Clamp(limit, 1, 600))
        .Select(a =>
        {
            JsonElement data;
            try { data = JsonDocument.Parse(a.Data).RootElement.Clone(); }
            catch { data = JsonDocument.Parse("{}").RootElement.Clone(); }
            return new ActivityItem(a.Id, a.AtUtc, a.Kind, data);
        }).ToList();

    public void Attach(ReplayTracker tracker)
    {
        tracker.Changed += () => OnStatus(tracker.Status);
        tracker.ScanStarting += n =>
        {
            lock (_gate)
            {
                _batch = n >= 3;
                _bCounted = _bReplaced = _bChests = _bTreasures = _bSkipped = _bErrors = 0;
            }
            if (_batch) Add("importing", new { files = n });
        };
        tracker.FileStarting += (name, bytes) =>
        {
            if (!_batch) Add("analyzing", new { file = name, bytes });
        };
        tracker.FileProcessed += OnFile;
        tracker.ScanFinished += s =>
        {
            if (!_batch) return;
            lock (_gate)
            {
                Add("import", new { files = s.Total, matches = _bCounted, replaced = _bReplaced, chests = _bChests, treasures = _bTreasures, skipped = _bSkipped, errors = _bErrors });
                _batch = false;
            }
        };
    }

    /// <summary>A replay Fortnite is still recording is announced once: a match is in progress.</summary>
    private void OnStatus(TrackingStatus status)
    {
        List<string> fresh;
        lock (_gate)
        {
            var recording = status.Pending.Where(p => p.Reason == PendingReason.Recording).Select(p => p.FileName).ToHashSet();
            fresh = recording.Where(_announced.Add).ToList();
            _announced.RemoveWhere(f => !recording.Contains(f) && !status.Pending.Any(p => p.FileName == f));
        }
        foreach (var file in fresh) Add("recording", new { file });
    }

    private void OnFile(FileOutcome o)
    {
        if (_batch)
        {
            lock (_gate)
            {
                switch (o.Status)
                {
                    case FileStatus.Counted when o.Match != null:
                        if (o.Replaced) _bReplaced++; else _bCounted++;
                        if (!o.Replaced)
                        {
                            _bChests += o.Match.Chests;
                            _bTreasures += _db.GetMatchDetail(o.Match.MatchId)?.Treasures.Count ?? 0;
                        }
                        break;
                    case FileStatus.Error or FileStatus.Incomplete: _bErrors++; break;
                    case "retry": break;
                    default: _bSkipped++; break;
                }
            }
            return;
        }
        switch (o.Status)
        {
            case FileStatus.Counted when o.Match != null:
                Add("match", MatchData(o.Match, o.Replaced));
                break;
            case FileStatus.NotCounted:
                Add("skipped", new { file = o.FileName, reason = o.Detail, playlist = o.Match?.Playlist });
                break;
            case FileStatus.Duplicate:
                Add("duplicate", new { file = o.FileName });
                break;
            case FileStatus.Incomplete:
                Add("incomplete", new { file = o.FileName });
                break;
            case FileStatus.Error:
                Add("error", new { file = o.FileName, detail = o.Detail });
                break;
        }
    }

    private object MatchData(MatchRow m, bool replaced)
    {
        var detail = _db.GetMatchDetail(m.MatchId);
        var catalog = _catalog();
        var treasures = (detail?.Treasures ?? new()).Select(t => new
        {
            tag = Treasures.Describe(t.PrizeId, t.RawItem, t.FromGoldenChest, false, catalog),
            time = t.TimeSeconds,
            source = t.SourceKind,
            destroyed = t.SourceDestroyed,
        }).ToList();
        var mates = (detail?.Roster ?? new()).Where(r => !r.IsBot && r.ObservedChests > 0).OrderByDescending(r => r.ObservedChests)
            .Select(r => new { name = r.Name, chests = r.ObservedChests }).ToList();
        return new
        {
            matchId = m.MatchId,
            when = m.When,
            playlist = m.Playlist,
            placement = m.Placement,
            totalPlayers = m.TotalPlayers,
            chests = m.Chests,
            destroyed = m.ChestsDestroyed,
            total = _total(m.ProfileKey),
            possiblyIncomplete = m.PossiblyIncomplete,
            replaced,
            treasures,
            teamTreasures = detail?.TeamTreasures ?? 0,
            squad = mates.Count == 0 ? null : new { chests = mates.Sum(x => x.chests), members = mates },
        };
    }
}
