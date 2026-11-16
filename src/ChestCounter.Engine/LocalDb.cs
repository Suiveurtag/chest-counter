using System.Globalization;
using ChestCounter.Shared;
using Microsoft.Data.Sqlite;

namespace ChestCounter.Engine;

public static class FileStatus
{
    public const string Counted = "counted";
    /// <summary>Same match as one already stored (renamed or copied replay, or nothing newer to say).</summary>
    public const string Duplicate = "duplicate";
    /// <summary>Read fine, but the match does not count (mode, custom match).</summary>
    public const string NotCounted = "not_counted";
    /// <summary>Recording cut during the match: Fortnite never wrote the key, nothing can be read.</summary>
    public const string Incomplete = "incomplete";
    public const string Error = "error";
}

/// <summary>A stored match, as the UI lists it.</summary>
public sealed record MatchRow
{
    public required string MatchId { get; init; }
    public bool IdentityReliable { get; init; }
    public string? PlayerId { get; init; }
    public string? PlayerName { get; init; }
    public string ProfileKey { get; init; } = "";
    public DateTime? StartUtc { get; init; }
    public int? DurationSeconds { get; init; }
    public string? Playlist { get; init; }
    public string? Map { get; init; }
    public string? GameVersion { get; init; }
    public int? Placement { get; init; }
    public int? TotalPlayers { get; init; }
    public int? Eliminations { get; init; }
    public int Chests { get; init; }
    public int ChestsDestroyed { get; init; }
    public int AmmoBoxes { get; init; }
    public int OtherContainers { get; init; }
    public int TeamObserved { get; init; }
    public int UnrecordedVehicleLoot { get; init; }
    public bool Counted { get; init; }
    public string? NotCountedReason { get; init; }
    public int ParserVersion { get; init; }
    public DateTime FirstAnalyzedUtc { get; init; }
    public DateTime AnalyzedUtc { get; init; }
    public string? ReplayFile { get; init; }
    public long SourceSize { get; init; }
    public long? SessionId { get; init; }
    public bool PossiblyIncomplete => UnrecordedVehicleLoot >= 3;
    /// <summary>When the match was played, or failing that, analyzed.</summary>
    public DateTime When => StartUtc ?? FirstAnalyzedUtc;
}

/// <summary>The parts of a match kept as JSON: only read when one match is opened.</summary>
public sealed record MatchDetail(Dictionary<string, int> ByKind, List<ChestEvent> Chests, List<RosterEntry> Roster, List<TreasureInfo> Treasures, int TeamTreasures,
    List<TeamTreasureInfo>? TeamFinds = null);

/// <summary>One line of the activity feed. <paramref name="Data"/> is JSON whose shape depends on <paramref name="Kind"/>.</summary>
public sealed record ActivityRow(long Id, DateTime AtUtc, string Kind, string Data);

public sealed record FileRow(string FileKey, string FileName, long Size, string? Fingerprint, string Status, string? MatchId, string? Detail,
    int ParserVersion, DateTime ProcessedUtc);

public sealed record OutboxRow(string MatchId, string PlayerId, string Payload, string QueuedUtc, int Attempts, string? LastError);
public sealed record LogRow(long Id, DateTime AtUtc, string Level, string Source, string Message);
public sealed record SessionRow(long Id, DateTime StartedUtc, DateTime? EndedUtc);
public sealed record ProfileRow(string Key, string Name, int Matches, int Chests, DateTime LastPlayedUtc);

/// <summary>
/// The durable local history (SQLite, one file). Every counting update is a single transaction keyed by match id,
/// so running it twice gives the same result as running it once.
/// </summary>
public sealed class LocalDb : IDisposable
{
    private const int SchemaVersion = 2;
    private readonly SqliteConnection _c;
    private readonly object _gate = new();

    public string Path { get; }

    public LocalDb(string path)
    {
        Path = path;
        if (path != ":memory:")
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        }
        _c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _c.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Migrate();
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version") ?? 0);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"This history was written by a newer version of the application (schema {version}).");
        }
        if (version < 1)
        {
            Exec("""
                BEGIN;
                CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, started_utc TEXT NOT NULL, ended_utc TEXT);
                CREATE TABLE matches(
                    match_id TEXT PRIMARY KEY, identity_reliable INTEGER NOT NULL, player_id TEXT, player_name TEXT,
                    profile_key TEXT NOT NULL, start_utc TEXT, duration_s INTEGER, playlist TEXT, map TEXT, game_version TEXT,
                    placement INTEGER, total_players INTEGER, eliminations INTEGER,
                    chests INTEGER NOT NULL, chests_destroyed INTEGER NOT NULL, ammo_boxes INTEGER NOT NULL,
                    other_containers INTEGER NOT NULL, team_observed INTEGER NOT NULL, unrecorded_vehicle_loot INTEGER NOT NULL,
                    counted INTEGER NOT NULL, not_counted_reason TEXT, parser_version INTEGER NOT NULL,
                    first_analyzed_utc TEXT NOT NULL, analyzed_utc TEXT NOT NULL, replay_file TEXT, source_size INTEGER NOT NULL,
                    session_id INTEGER, detail_json TEXT NOT NULL);
                CREATE INDEX matches_profile ON matches(profile_key, counted, start_utc);
                CREATE TABLE files(
                    file_key TEXT PRIMARY KEY, file_name TEXT NOT NULL, size INTEGER NOT NULL, fingerprint TEXT,
                    status TEXT NOT NULL, match_id TEXT, detail TEXT, parser_version INTEGER NOT NULL, processed_utc TEXT NOT NULL);
                CREATE INDEX files_fingerprint ON files(fingerprint);
                CREATE TABLE outbox(
                    match_id TEXT NOT NULL, player_id TEXT NOT NULL, payload TEXT NOT NULL, queued_utc TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT, PRIMARY KEY(match_id, player_id));
                CREATE TABLE log(id INTEGER PRIMARY KEY AUTOINCREMENT, at_utc TEXT NOT NULL, level TEXT NOT NULL, source TEXT NOT NULL, message TEXT NOT NULL);
                PRAGMA user_version = 1;
                COMMIT;
                """);
        }
        if (version < 2)
        {
            Exec("""
                BEGIN;
                CREATE TABLE activity(id INTEGER PRIMARY KEY AUTOINCREMENT, at_utc TEXT NOT NULL, kind TEXT NOT NULL, data TEXT NOT NULL);
                PRAGMA user_version = 2;
                COMMIT;
                """);
        }
    }

    // ---- activity feed ----------------------------------------------------------------------------------------

    public long AddActivity(string kind, string data, DateTime utc)
    {
        lock (_gate)
        {
            Exec("INSERT INTO activity(at_utc,kind,data) VALUES($a,$k,$d)", ("$a", Iso(utc)), ("$k", kind), ("$d", data));
            var id = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
            Exec("DELETE FROM activity WHERE id <= $i - 600", ("$i", id));
            return id;
        }
    }

    public List<ActivityRow> Activities(int limit) => Query("SELECT id,at_utc,kind,data FROM activity ORDER BY id DESC LIMIT $l",
        r => new ActivityRow(r.GetInt64(0), ParseDate(r.GetValue(1))!.Value, r.GetString(2), r.GetString(3)), ("$l", limit));

    // ---- plumbing ---------------------------------------------------------------------------------------------

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static DateTime? ParseDate(object? value) => value is string s && s.Length > 0
        ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
        : null;

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

    private int Exec(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            var value = cmd.ExecuteScalar();
            return value is DBNull ? null : value;
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while (reader.Read())
            {
                list.Add(map(reader));
            }
            return list;
        }
    }

    /// <summary>Runs <paramref name="work"/> in one transaction: all of it is saved, or none of it.</summary>
    private void Transaction(Action work)
    {
        lock (_gate)
        {
            if (_inTransaction)
            {
                work(); // already inside one: part of it
                return;
            }
            Exec("BEGIN IMMEDIATE");
            _inTransaction = true;
            try
            {
                work();
                Exec("COMMIT");
            }
            catch
            {
                try { Exec("ROLLBACK"); } catch { /* the original error matters */ }
                throw;
            }
            finally
            {
                _inTransaction = false;
            }
        }
    }

    private bool _inTransaction;

    private static object? Val(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetValue(i);
    }

    private static int? Int(SqliteDataReader r, string column) => Val(r, column) is { } v ? Convert.ToInt32(v) : null;

    // ---- settings ---------------------------------------------------------------------------------------------

    public string? GetSetting(string key) => Scalar("SELECT value FROM settings WHERE key=$k", ("$k", key)) as string;

    public void SetSetting(string key, string? value)
    {
        if (value == null)
        {
            Exec("DELETE FROM settings WHERE key=$k", ("$k", key));
        }
        else
        {
            Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
        }
    }

    public Dictionary<string, string> AllSettings() =>
        Query("SELECT key,value FROM settings", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);

    // ---- sessions ---------------------------------------------------------------------------------------------

    public SessionRow StartSession(DateTime utc)
    {
        lock (_gate)
        {
            // A session left open by a crash ends where its last match was analyzed.
            Exec("UPDATE sessions SET ended_utc = COALESCE((SELECT MAX(analyzed_utc) FROM matches WHERE session_id = sessions.id), started_utc) WHERE ended_utc IS NULL");
            Exec("INSERT INTO sessions(started_utc) VALUES($s)", ("$s", Iso(utc)));
            return new SessionRow(Convert.ToInt64(Scalar("SELECT last_insert_rowid()")), utc, null);
        }
    }

    public void EndSession(long id, DateTime utc) => Exec("UPDATE sessions SET ended_utc=$e WHERE id=$i", ("$e", Iso(utc)), ("$i", id));

    /// <summary>Sessions in which at least one match was played, most recent first.</summary>
    public List<SessionRow> SessionsWithMatches() => Query(
        "SELECT id, started_utc, ended_utc FROM sessions WHERE id IN (SELECT session_id FROM matches WHERE counted=1) ORDER BY id DESC",
        r => new SessionRow(r.GetInt64(0), ParseDate(r.GetValue(1))!.Value, ParseDate(r.IsDBNull(2) ? null : r.GetValue(2))));

    // ---- files ------------------------------------------------------------------------------------------------

    public FileRow? GetFile(string fileKey) => Query("SELECT * FROM files WHERE file_key=$k", ReadFile, ("$k", fileKey)).FirstOrDefault();

    public HashSet<string> ProcessedFileKeys() => Query("SELECT file_key FROM files", r => r.GetString(0)).ToHashSet();

    /// <summary>A replay with this exact content that was already read with this parser version.</summary>
    public FileRow? FindByFingerprint(string fingerprint, int parserVersion) => Query(
        "SELECT * FROM files WHERE fingerprint=$f AND parser_version=$v AND status IN ('counted','duplicate','not_counted') ORDER BY processed_utc LIMIT 1",
        ReadFile, ("$f", fingerprint), ("$v", parserVersion)).FirstOrDefault();

    public void SaveFile(FileRow f) => Exec("""
        INSERT INTO files(file_key,file_name,size,fingerprint,status,match_id,detail,parser_version,processed_utc)
        VALUES($k,$n,$s,$f,$st,$m,$d,$v,$p)
        ON CONFLICT(file_key) DO UPDATE SET file_name=excluded.file_name, size=excluded.size, fingerprint=excluded.fingerprint,
            status=excluded.status, match_id=excluded.match_id, detail=excluded.detail, parser_version=excluded.parser_version, processed_utc=excluded.processed_utc
        """, ("$k", f.FileKey), ("$n", f.FileName), ("$s", f.Size), ("$f", f.Fingerprint), ("$st", f.Status), ("$m", f.MatchId), ("$d", f.Detail),
        ("$v", f.ParserVersion), ("$p", Iso(f.ProcessedUtc)));

    public List<FileRow> Files(string? status = null, int limit = 500) => status == null
        ? Query("SELECT * FROM files ORDER BY processed_utc DESC LIMIT $l", ReadFile, ("$l", limit))
        : Query("SELECT * FROM files WHERE status=$s ORDER BY processed_utc DESC LIMIT $l", ReadFile, ("$s", status), ("$l", limit));

    public Dictionary<string, int> FileStatusCounts() =>
        Query("SELECT status, COUNT(*) FROM files GROUP BY status", r => (r.GetString(0), r.GetInt32(1))).ToDictionary(x => x.Item1, x => x.Item2);

    /// <summary>
    /// Forgets that files were read, so the next scan reads them again. Matches are kept: a re-read replaces its
    /// match, and a match whose replay is gone from disk simply stays as it is.
    /// </summary>
    /// <param name="statuses">Null = every file.</param>
    /// <param name="olderThanParser">Only files read by a parser version below this one.</param>
    public int ForgetFiles(IReadOnlyCollection<string>? statuses = null, int? olderThanParser = null)
    {
        var where = new List<string> { "1=1" };
        if (statuses != null)
        {
            where.Add("status IN (" + string.Join(",", statuses.Select(s => "'" + s.Replace("'", "") + "'")) + ")");
        }
        if (olderThanParser != null)
        {
            where.Add("parser_version < " + olderThanParser.Value.ToString(CultureInfo.InvariantCulture));
        }
        return Exec("DELETE FROM files WHERE " + string.Join(" AND ", where));
    }

    private static FileRow ReadFile(SqliteDataReader r) => new(
        (string)Val(r, "file_key")!, (string)Val(r, "file_name")!, Convert.ToInt64(Val(r, "size")), Val(r, "fingerprint") as string,
        (string)Val(r, "status")!, Val(r, "match_id") as string, Val(r, "detail") as string, Convert.ToInt32(Val(r, "parser_version")),
        ParseDate(Val(r, "processed_utc"))!.Value);

    // ---- matches ----------------------------------------------------------------------------------------------

    public MatchRow? GetMatch(string matchId) => Query("SELECT * FROM matches WHERE match_id=$m", ReadMatch, ("$m", matchId)).FirstOrDefault();

    public int ImportOriginal(IEnumerable<(AnalyzedMatch Match, DateTime Analyzed)> matches)
    {
        var imported = 0;
        Transaction(() =>
        {
            foreach (var (match, analyzed) in matches)
            {
                if (GetMatch(match.MatchId) != null) continue;
                var file = new FileRow("original:" + match.MatchId, "Imported from Chest Counter", 0, null,
                    FileStatus.Counted, match.MatchId, null, match.ParserVersion, analyzed);
                SaveAnalysis(match, null, file, true, 0, null, analyzed, null);
                imported++;
            }
        });
        return imported;
    }

    /// <summary>Every counted match of a profile with its detail, most recent first (one query).</summary>
    public List<(MatchRow Match, MatchDetail? Detail)> CountedWithDetail(string profileKey) => Query(
        "SELECT * FROM matches WHERE profile_key=$pk AND counted=1 ORDER BY COALESCE(start_utc, first_analyzed_utc) DESC, match_id",
        r => (ReadMatch(r), ReadDetail(Val(r, "detail_json") as string)), ("$pk", profileKey));

    /// <summary>Treasures the player obtained in counted matches (read inside the stored detail, no decoding).</summary>
    public int TreasureCount(string profileKey) => Convert.ToInt32(Scalar(
        "SELECT COALESCE(SUM(COALESCE(json_array_length(detail_json, '$.treasures'), 0)), 0) FROM matches WHERE profile_key=$pk AND counted=1",
        ("$pk", profileKey)) ?? 0);

    /// <summary>Treasures teammates received in this player's counted matches (as their replays saw them).</summary>
    public int TeamTreasureCount(string profileKey) => Convert.ToInt32(Scalar(
        "SELECT COALESCE(SUM(COALESCE(json_extract(detail_json, '$.teamTreasures'), 0)), 0) FROM matches WHERE profile_key=$pk AND counted=1",
        ("$pk", profileKey)) ?? 0);

    private static MatchDetail? ReadDetail(string? json)
    {
        if (json == null) return null;
        try { return Wire.Deserialize<MatchDetail>(json); } catch { return null; }
    }

    public MatchDetail? GetMatchDetail(string matchId) =>
        Scalar("SELECT detail_json FROM matches WHERE match_id=$m", ("$m", matchId)) is string json ? Wire.Deserialize<MatchDetail>(json) : null;

    /// <summary>
    /// Saves the result of one replay atomically: the match (inserted, or replaced when <paramref name="replace"/>),
    /// the file that produced it, and the pending upload. Returns the stored row.
    /// </summary>
    public MatchRow SaveAnalysis(AnalyzedMatch m, string? notCountedReason, FileRow file, bool replace, long sourceSize, long? sessionId,
        DateTime nowUtc, ContributionDto? upload)
    {
        Transaction(() =>
        {
            if (replace)
            {
                var detail = Wire.Serialize(new MatchDetail(m.ByKind, m.Chests, m.Roster, m.Treasures, m.TeamTreasures, m.TeamFinds));
                Exec("""
                    INSERT INTO matches(match_id,identity_reliable,player_id,player_name,profile_key,start_utc,duration_s,playlist,map,game_version,
                        placement,total_players,eliminations,chests,chests_destroyed,ammo_boxes,other_containers,team_observed,unrecorded_vehicle_loot,
                        counted,not_counted_reason,parser_version,first_analyzed_utc,analyzed_utc,replay_file,source_size,session_id,detail_json)
                    VALUES($id,$rel,$pid,$pn,$pk,$st,$dur,$pl,$map,$gv,$plc,$tp,$el,$ch,$cd,$ab,$oc,$to,$uv,$cnt,$why,$pv,$now,$now,$rf,$ss,$ses,$dj)
                    ON CONFLICT(match_id) DO UPDATE SET identity_reliable=excluded.identity_reliable, player_id=excluded.player_id,
                        player_name=excluded.player_name, profile_key=excluded.profile_key, start_utc=excluded.start_utc, duration_s=excluded.duration_s,
                        playlist=excluded.playlist, map=excluded.map, game_version=excluded.game_version, placement=excluded.placement,
                        total_players=excluded.total_players, eliminations=excluded.eliminations, chests=excluded.chests,
                        chests_destroyed=excluded.chests_destroyed, ammo_boxes=excluded.ammo_boxes, other_containers=excluded.other_containers,
                        team_observed=excluded.team_observed, unrecorded_vehicle_loot=excluded.unrecorded_vehicle_loot, counted=excluded.counted,
                        not_counted_reason=excluded.not_counted_reason, parser_version=excluded.parser_version, analyzed_utc=excluded.analyzed_utc,
                        replay_file=excluded.replay_file, source_size=excluded.source_size, detail_json=excluded.detail_json
                    """,
                    ("$id", m.MatchId), ("$rel", m.IdentityReliable ? 1 : 0), ("$pid", m.PlayerId), ("$pn", m.PlayerName), ("$pk", m.ProfileKey),
                    ("$st", m.StartUtc is { } s ? Iso(s) : null), ("$dur", m.DurationSeconds), ("$pl", m.Playlist), ("$map", m.Map), ("$gv", m.GameVersion),
                    ("$plc", m.Placement), ("$tp", m.TotalPlayers), ("$el", m.Eliminations), ("$ch", m.ChestsOpened), ("$cd", m.ChestsDestroyed),
                    ("$ab", m.AmmoBoxesOpened), ("$oc", m.OtherContainersOpened), ("$to", m.Roster.Sum(r => r.ObservedChests)), ("$uv", m.UnrecordedVehicleLoot),
                    ("$cnt", notCountedReason == null ? 1 : 0), ("$why", notCountedReason), ("$pv", m.ParserVersion), ("$now", Iso(nowUtc)),
                    ("$rf", file.FileName), ("$ss", sourceSize), ("$ses", sessionId), ("$dj", detail));
            }
            SaveFile(file);
            if (upload != null)
            {
                Enqueue(upload, nowUtc);
            }
        });
        return GetMatch(m.MatchId)!;
    }

    public sealed record MatchFilter(string? ProfileKey = null, DateTime? FromUtc = null, DateTime? ToUtc = null, long? SessionId = null,
        bool? Counted = true, int Limit = 200, int Offset = 0);

    public List<MatchRow> Matches(MatchFilter f)
    {
        var where = new List<string> { "1=1" };
        var args = new List<(string, object?)>();
        if (f.ProfileKey != null) { where.Add("profile_key=$pk"); args.Add(("$pk", f.ProfileKey)); }
        if (f.Counted != null) { where.Add("counted=$c"); args.Add(("$c", f.Counted.Value ? 1 : 0)); }
        if (f.FromUtc != null) { where.Add("COALESCE(start_utc, first_analyzed_utc) >= $from"); args.Add(("$from", Iso(f.FromUtc.Value))); }
        if (f.ToUtc != null) { where.Add("COALESCE(start_utc, first_analyzed_utc) < $to"); args.Add(("$to", Iso(f.ToUtc.Value))); }
        if (f.SessionId != null) { where.Add("session_id=$ses"); args.Add(("$ses", f.SessionId)); }
        args.Add(("$l", f.Limit));
        args.Add(("$o", f.Offset));
        return Query($"SELECT * FROM matches WHERE {string.Join(" AND ", where)} ORDER BY COALESCE(start_utc, first_analyzed_utc) DESC, match_id LIMIT $l OFFSET $o",
            ReadMatch, args.ToArray());
    }

    public List<ProfileRow> Profiles() => Query("""
        SELECT profile_key,
               (SELECT player_name FROM matches m2 WHERE m2.profile_key = m.profile_key AND m2.player_name IS NOT NULL AND m2.player_name <> ''
                ORDER BY COALESCE(m2.start_utc, m2.first_analyzed_utc) DESC LIMIT 1),
               COUNT(*), SUM(chests), MAX(COALESCE(start_utc, first_analyzed_utc))
        FROM matches m WHERE counted=1 GROUP BY profile_key ORDER BY 5 DESC
        """, r => new ProfileRow(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetInt32(2), r.GetInt32(3), ParseDate(r.GetValue(4))!.Value));

    /// <summary>Player ids (Epic account ids) the local history holds counted matches for.</summary>
    public List<AccountDto> Accounts() => Query("""
        SELECT player_id, MAX(player_name) FROM matches WHERE counted=1 AND player_id IS NOT NULL GROUP BY player_id
        """, r => new AccountDto(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));

    private static MatchRow ReadMatch(SqliteDataReader r) => new()
    {
        MatchId = (string)Val(r, "match_id")!,
        IdentityReliable = Convert.ToInt32(Val(r, "identity_reliable")) != 0,
        PlayerId = Val(r, "player_id") as string,
        PlayerName = Val(r, "player_name") as string,
        ProfileKey = (string)Val(r, "profile_key")!,
        StartUtc = ParseDate(Val(r, "start_utc")),
        DurationSeconds = Int(r, "duration_s"),
        Playlist = Val(r, "playlist") as string,
        Map = Val(r, "map") as string,
        GameVersion = Val(r, "game_version") as string,
        Placement = Int(r, "placement"),
        TotalPlayers = Int(r, "total_players"),
        Eliminations = Int(r, "eliminations"),
        Chests = Int(r, "chests") ?? 0,
        ChestsDestroyed = Int(r, "chests_destroyed") ?? 0,
        AmmoBoxes = Int(r, "ammo_boxes") ?? 0,
        OtherContainers = Int(r, "other_containers") ?? 0,
        TeamObserved = Int(r, "team_observed") ?? 0,
        UnrecordedVehicleLoot = Int(r, "unrecorded_vehicle_loot") ?? 0,
        Counted = Convert.ToInt32(Val(r, "counted")) != 0,
        NotCountedReason = Val(r, "not_counted_reason") as string,
        ParserVersion = Int(r, "parser_version") ?? 0,
        FirstAnalyzedUtc = ParseDate(Val(r, "first_analyzed_utc"))!.Value,
        AnalyzedUtc = ParseDate(Val(r, "analyzed_utc"))!.Value,
        ReplayFile = Val(r, "replay_file") as string,
        SourceSize = Convert.ToInt64(Val(r, "source_size") ?? 0L),
        SessionId = Val(r, "session_id") is { } s ? Convert.ToInt64(s) : null,
    };

    // ---- outbox (results waiting to reach the squad server) ---------------------------------------------------

    /// <summary>Queues (or re-queues) the upload of one result. One row per (match, player): the latest wins.</summary>
    public void Enqueue(ContributionDto c, DateTime nowUtc) => Exec("""
        INSERT INTO outbox(match_id,player_id,payload,queued_utc,attempts,last_error) VALUES($m,$p,$j,$q,0,NULL)
        ON CONFLICT(match_id,player_id) DO UPDATE SET payload=excluded.payload, queued_utc=excluded.queued_utc, attempts=0, last_error=NULL
        """, ("$m", c.MatchId), ("$p", c.PlayerId), ("$j", Wire.Serialize(c)), ("$q", Iso(nowUtc)));

    /// <summary>Queues every stored result that has a player id (first connection to a server, or a full resync).</summary>
    public int EnqueueAll(DateTime nowUtc)
    {
        var count = 0;
        Transaction(() =>
        {
            foreach (var m in Matches(new MatchFilter(Counted: true, Limit: int.MaxValue)).Where(m => m.PlayerId != null))
            {
                var detail = GetMatchDetail(m.MatchId);
                Enqueue(Contribution(m, detail?.Roster ?? new(), detail?.Treasures), nowUtc);
                count++;
            }
        });
        return count;
    }

    /// <summary>The player's own treasures as sent to the squad. Team finds are estimates and are never sent.</summary>
    public static List<TreasureDto> TreasuresForUpload(IEnumerable<TreasureInfo>? own) =>
        (own ?? Enumerable.Empty<TreasureInfo>()).Select(t => new TreasureDto(t.PrizeId, t.RawItem, t.TimeSeconds, t.FromGoldenChest)).ToList();

    public static ContributionDto Contribution(MatchRow m, List<RosterEntry> roster, List<TreasureInfo>? treasures = null) => new()
    {
        Treasures = treasures == null ? null : TreasuresForUpload(treasures),
        MatchId = m.MatchId,
        PlayerId = m.PlayerId!,
        PlayerName = m.PlayerName ?? "",
        ChestsOpened = m.Chests,
        ChestsDestroyed = m.ChestsDestroyed,
        AmmoBoxesOpened = m.AmmoBoxes,
        OtherContainersOpened = m.OtherContainers,
        MatchStartUtc = m.StartUtc,
        DurationSeconds = m.DurationSeconds,
        Playlist = m.Playlist,
        ParserVersion = m.ParserVersion,
        Counted = m.Counted,
        PossiblyIncomplete = m.PossiblyIncomplete,
        IdentityReliable = m.IdentityReliable,
        Roster = roster,
    };

    /// <summary>Uploads to send, oldest first. Rows the server refused (attempts &lt; 0) wait for a new result.</summary>
    public List<OutboxRow> PendingUploads(int limit) => Query(
        "SELECT match_id,player_id,payload,queued_utc,attempts,last_error FROM outbox WHERE attempts >= 0 ORDER BY queued_utc LIMIT $l",
        r => new OutboxRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5)), ("$l", limit));

    public List<OutboxRow> RefusedUploads() => Query(
        "SELECT match_id,player_id,payload,queued_utc,attempts,last_error FROM outbox WHERE attempts < 0 ORDER BY queued_utc DESC LIMIT 100",
        r => new OutboxRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5)));

    public (int Pending, int Refused) OutboxCounts() => (
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM outbox WHERE attempts >= 0")),
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM outbox WHERE attempts < 0")));

    /// <summary>Removes an upload the server took, unless a newer result was queued for it meanwhile.</summary>
    public void UploadDone(OutboxRow row) =>
        Exec("DELETE FROM outbox WHERE match_id=$m AND player_id=$p AND queued_utc=$q", ("$m", row.MatchId), ("$p", row.PlayerId), ("$q", row.QueuedUtc));

    public void UploadRefused(OutboxRow row, string reason) =>
        Exec("UPDATE outbox SET attempts=-1, last_error=$e WHERE match_id=$m AND player_id=$p AND queued_utc=$q",
            ("$e", reason), ("$m", row.MatchId), ("$p", row.PlayerId), ("$q", row.QueuedUtc));

    public void UploadFailed(IEnumerable<OutboxRow> rows, string error)
    {
        Transaction(() =>
        {
            foreach (var row in rows)
            {
                Exec("UPDATE outbox SET attempts=attempts+1, last_error=$e WHERE match_id=$m AND player_id=$p AND attempts >= 0",
                    ("$e", error), ("$m", row.MatchId), ("$p", row.PlayerId));
            }
        });
    }

    public void ClearOutbox() => Exec("DELETE FROM outbox");

    // ---- log --------------------------------------------------------------------------------------------------

    public void Log(string level, string source, string message, DateTime utc)
    {
        Exec("INSERT INTO log(at_utc,level,source,message) VALUES($a,$l,$s,$m)", ("$a", Iso(utc)), ("$l", level), ("$s", source), ("$m", message));
        Exec("DELETE FROM log WHERE id <= (SELECT MAX(id) FROM log) - 2000");
    }

    public List<LogRow> Logs(int limit = 200) => Query("SELECT id,at_utc,level,source,message FROM log ORDER BY id DESC LIMIT $l",
        r => new LogRow(r.GetInt64(0), ParseDate(r.GetValue(1))!.Value, r.GetString(2), r.GetString(3), r.GetString(4)), ("$l", limit));

    // ---- maintenance ------------------------------------------------------------------------------------------

    /// <summary>Writes a consistent copy of the whole history to <paramref name="destination"/> (SQLite online backup).</summary>
    public void BackupTo(string destination)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        lock (_gate)
        {
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
            target.Open();
            _c.BackupDatabase(target);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { /* closing anyway */ }
            _c.Dispose();
        }
    }
}
