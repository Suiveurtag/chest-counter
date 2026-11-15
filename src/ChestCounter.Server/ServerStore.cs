using System.Globalization;
using System.Security.Cryptography;
using ChestCounter.Shared;
using Microsoft.Data.Sqlite;

namespace ChestCounter.Server;

public sealed record UserRow(string Id, string DisplayName, DateTime? LastSeenUtc, DateTime? LastSyncUtc);
public sealed record SquadRow(string Id, string Name, string OwnerId, long Revision);

public sealed class StoreException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>
/// Persistent state of the server (SQLite, one file). The table that matters is <c>contributions</c>: its primary
/// key (match_id, player_id) is what makes every upload an idempotent upsert and double counting impossible.
/// </summary>
public sealed class ServerStore : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly object _gate = new();
    private readonly ServerOptions _o;

    public ServerStore(ServerOptions options)
    {
        _o = options;
        if (options.DatabaseFile is { Length: > 0 } file && file != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        }
        _c = new SqliteConnection(new SqliteConnectionStringBuilder(options.ConnectionString) { Pooling = false }.ToString());
        _c.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        if (Convert.ToInt32(Scalar("PRAGMA user_version")) < 1)
        {
            Exec("""
                BEGIN;
                CREATE TABLE users(id TEXT PRIMARY KEY, display_name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE,
                    created_utc TEXT NOT NULL, last_seen_utc TEXT, last_sync_utc TEXT);
                CREATE TABLE accounts(player_id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    player_name TEXT NOT NULL, first_seen_utc TEXT NOT NULL);
                CREATE INDEX accounts_user ON accounts(user_id);
                CREATE TABLE squads(id TEXT PRIMARY KEY, name TEXT NOT NULL, owner_id TEXT NOT NULL, created_utc TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE members(squad_id TEXT NOT NULL REFERENCES squads(id) ON DELETE CASCADE, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    role TEXT NOT NULL, joined_utc TEXT NOT NULL, PRIMARY KEY(squad_id, user_id));
                CREATE INDEX members_user ON members(user_id);
                CREATE TABLE invites(code TEXT PRIMARY KEY, squad_id TEXT NOT NULL REFERENCES squads(id) ON DELETE CASCADE, created_by TEXT NOT NULL,
                    created_utc TEXT NOT NULL, expires_utc TEXT NOT NULL, max_uses INTEGER NOT NULL, uses INTEGER NOT NULL DEFAULT 0, revoked INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE contributions(
                    match_id TEXT NOT NULL, player_id TEXT NOT NULL, user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    parser_version INTEGER NOT NULL, counted INTEGER NOT NULL, payload TEXT NOT NULL,
                    received_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                    PRIMARY KEY(match_id, player_id));
                CREATE INDEX contributions_user ON contributions(user_id);
                PRAGMA user_version = 1;
                COMMIT;
                """);
        }
    }

    // ---- plumbing ---------------------------------------------------------------------------------------------

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static DateTime? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null
        : DateTime.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private SqliteCommand Command(string sql, (string, object?)[] args)
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
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private T Transaction<T>(Func<T> work)
    {
        lock (_gate)
        {
            Exec("BEGIN IMMEDIATE");
            try
            {
                var result = work();
                Exec("COMMIT");
                return result;
            }
            catch
            {
                try { Exec("ROLLBACK"); } catch { /* the original error matters */ }
                throw;
            }
        }
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // ---- users ------------------------------------------------------------------------------------------------

    public string CreateUser(string displayName, string tokenHash, DateTime now)
    {
        var id = NewId();
        Exec("INSERT INTO users(id,display_name,token_hash,created_utc,last_seen_utc) VALUES($i,$n,$t,$c,$c)",
            ("$i", id), ("$n", displayName), ("$t", tokenHash), ("$c", Iso(now)));
        return id;
    }

    public UserRow? FindUserByTokenHash(string tokenHash) => Query(
        "SELECT id,display_name,last_seen_utc,last_sync_utc FROM users WHERE token_hash=$t",
        r => new UserRow(r.GetString(0), r.GetString(1), Date(r, 2), Date(r, 3)), ("$t", tokenHash)).FirstOrDefault();

    public void Touch(string userId, DateTime now) => Exec("UPDATE users SET last_seen_utc=$n WHERE id=$i", ("$n", Iso(now)), ("$i", userId));

    public void SetDisplayName(string userId, string name) => Exec("UPDATE users SET display_name=$n WHERE id=$i", ("$n", name), ("$i", userId));

    public List<AccountDto> AccountsOf(string userId) => Query("SELECT player_id, player_name FROM accounts WHERE user_id=$u ORDER BY first_seen_utc",
        r => new AccountDto(r.GetString(0), r.GetString(1)), ("$u", userId));

    /// <summary>
    /// Links a player id to a member. The first member to present a player id keeps it: nobody else can then upload
    /// results in that player's name. Returns false when it belongs to someone else.
    /// </summary>
    public bool ClaimAccount(string userId, string playerId, string playerName, DateTime now)
    {
        lock (_gate)
        {
            var owner = Scalar("SELECT user_id FROM accounts WHERE player_id=$p", ("$p", playerId)) as string;
            if (owner == null)
            {
                Exec("INSERT INTO accounts(player_id,user_id,player_name,first_seen_utc) VALUES($p,$u,$n,$t)",
                    ("$p", playerId), ("$u", userId), ("$n", playerName), ("$t", Iso(now)));
                return true;
            }
            if (owner != userId)
            {
                return false;
            }
            if (playerName.Length > 0)
            {
                Exec("UPDATE accounts SET player_name=$n WHERE player_id=$p", ("$n", playerName), ("$p", playerId));
            }
            return true;
        }
    }

    public bool ReleaseAccount(string playerId) => Exec("DELETE FROM accounts WHERE player_id=$p", ("$p", playerId)) > 0;

    // ---- squads -----------------------------------------------------------------------------------------------

    public SquadRow? GetSquad(string squadId) => Query("SELECT id,name,owner_id,revision FROM squads WHERE id=$i",
        r => new SquadRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ("$i", squadId)).FirstOrDefault();

    public List<SquadSummaryDto> SquadsOf(string userId) => Query(
        "SELECT s.id, s.name, m.role FROM members m JOIN squads s ON s.id = m.squad_id WHERE m.user_id=$u ORDER BY m.joined_utc",
        r => new SquadSummaryDto(r.GetString(0), r.GetString(1), r.GetString(2)), ("$u", userId));

    public bool IsMember(string squadId, string userId) =>
        Scalar("SELECT 1 FROM members WHERE squad_id=$s AND user_id=$u", ("$s", squadId), ("$u", userId)) != null;

    public string? RoleOf(string squadId, string userId) =>
        Scalar("SELECT role FROM members WHERE squad_id=$s AND user_id=$u", ("$s", squadId), ("$u", userId)) as string;

    public List<string> MemberIds(string squadId) => Query("SELECT user_id FROM members WHERE squad_id=$s", r => r.GetString(0), ("$s", squadId));

    public string CreateSquad(string name, string ownerId, DateTime now) => Transaction(() =>
    {
        var id = NewId();
        Exec("INSERT INTO squads(id,name,owner_id,created_utc) VALUES($i,$n,$o,$c)", ("$i", id), ("$n", name), ("$o", ownerId), ("$c", Iso(now)));
        Exec("INSERT INTO members(squad_id,user_id,role,joined_utc) VALUES($s,$u,'owner',$j)", ("$s", id), ("$u", ownerId), ("$j", Iso(now)));
        return id;
    });

    public void RenameSquad(string squadId, string name) => Exec("UPDATE squads SET name=$n, revision=revision+1 WHERE id=$i", ("$n", name), ("$i", squadId));

    public void Bump(string squadId) => Exec("UPDATE squads SET revision=revision+1 WHERE id=$i", ("$i", squadId));

    /// <summary>Removes a member. The oldest remaining member becomes owner; an empty squad is deleted. Returns true when the squad is gone.</summary>
    public bool RemoveMember(string squadId, string userId) => Transaction(() =>
    {
        Exec("DELETE FROM members WHERE squad_id=$s AND user_id=$u", ("$s", squadId), ("$u", userId));
        var next = Scalar("SELECT user_id FROM members WHERE squad_id=$s ORDER BY joined_utc LIMIT 1", ("$s", squadId)) as string;
        if (next == null)
        {
            Exec("DELETE FROM squads WHERE id=$s", ("$s", squadId));
            return true;
        }
        if (Scalar("SELECT 1 FROM members WHERE squad_id=$s AND role='owner'", ("$s", squadId)) == null)
        {
            Exec("UPDATE members SET role='owner' WHERE squad_id=$s AND user_id=$u", ("$s", squadId), ("$u", next));
            Exec("UPDATE squads SET owner_id=$u WHERE id=$s", ("$s", squadId), ("$u", next));
        }
        Bump(squadId);
        return false;
    });

    // ---- invites ----------------------------------------------------------------------------------------------

    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I/L

    public static string NormalizeCode(string code) => new(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public InviteDto CreateInvite(string squadId, string userId, DateTime now, int ttlHours, int maxUses, string baseUrl)
    {
        var raw = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, 8));
        var expires = now.AddHours(ttlHours);
        Exec("INSERT INTO invites(code,squad_id,created_by,created_utc,expires_utc,max_uses) VALUES($c,$s,$u,$n,$e,$m)",
            ("$c", raw), ("$s", squadId), ("$u", userId), ("$n", Iso(now)), ("$e", Iso(expires)), ("$m", maxUses));
        Bump(squadId);
        return Invite(raw, now, expires, maxUses, 0, baseUrl);
    }

    private static InviteDto Invite(string raw, DateTime created, DateTime expires, int maxUses, int uses, string baseUrl)
    {
        var shown = raw[..4] + "-" + raw[4..];
        return new InviteDto(shown, $"{baseUrl}/join/{shown}", created, expires, maxUses, uses);
    }

    public List<InviteDto> ActiveInvites(string squadId, DateTime now, string baseUrl) => Query(
        "SELECT code,created_utc,expires_utc,max_uses,uses FROM invites WHERE squad_id=$s AND revoked=0 AND expires_utc > $n AND uses < max_uses ORDER BY created_utc DESC",
        r => Invite(r.GetString(0), Date(r, 1)!.Value, Date(r, 2)!.Value, r.GetInt32(3), r.GetInt32(4), baseUrl), ("$s", squadId), ("$n", Iso(now)));

    public bool RevokeInvite(string squadId, string code)
    {
        var done = Exec("UPDATE invites SET revoked=1 WHERE squad_id=$s AND code=$c", ("$s", squadId), ("$c", NormalizeCode(code))) > 0;
        if (done) Bump(squadId);
        return done;
    }

    public string? SquadOfInvite(string code, DateTime now) => Scalar(
        "SELECT squad_id FROM invites WHERE code=$c AND revoked=0 AND expires_utc > $n AND uses < max_uses", ("$c", NormalizeCode(code)), ("$n", Iso(now))) as string;

    /// <summary>Joins with an invite code. Joining a squad one is already in changes nothing and uses no invite.</summary>
    public string Join(string code, string userId, DateTime now) => Transaction(() =>
    {
        var normalized = NormalizeCode(code);
        var invite = Query("SELECT squad_id, expires_utc, max_uses, uses, revoked FROM invites WHERE code=$c",
            r => (Squad: r.GetString(0), Expires: Date(r, 1)!.Value, Max: r.GetInt32(2), Uses: r.GetInt32(3), Revoked: r.GetInt32(4) != 0), ("$c", normalized)).FirstOrDefault();
        if (invite.Squad == null)
        {
            throw new StoreException("invite_not_found", "This invite code does not exist.", 404);
        }
        if (IsMember(invite.Squad, userId))
        {
            return invite.Squad;
        }
        if (invite.Revoked || invite.Expires <= now || invite.Uses >= invite.Max)
        {
            throw new StoreException("invite_expired", "This invite has expired or was revoked. Ask for a new one.", 410);
        }
        if (MemberIds(invite.Squad).Count >= _o.MaxSquadSize)
        {
            throw new StoreException("squad_full", $"This squad already has {_o.MaxSquadSize} members.", 409);
        }
        Exec("INSERT INTO members(squad_id,user_id,role,joined_utc) VALUES($s,$u,'member',$j)", ("$s", invite.Squad), ("$u", userId), ("$j", Iso(now)));
        Exec("UPDATE invites SET uses=uses+1 WHERE code=$c", ("$c", normalized));
        Bump(invite.Squad);
        return invite.Squad;
    });

    // ---- contributions ----------------------------------------------------------------------------------------

    /// <summary>
    /// Stores one player's result for one match. Idempotent: the same upload twice leaves one row. A result from an
    /// older parser never replaces one from a newer parser.
    /// </summary>
    public string Upsert(string userId, ContributionDto c, DateTime now) => Transaction(() =>
    {
        if (!ClaimAccount(userId, c.PlayerId, c.PlayerName, now))
        {
            return UploadStatus.PlayerClaimed;
        }
        var existing = Query("SELECT user_id, parser_version FROM contributions WHERE match_id=$m AND player_id=$p",
            r => (User: r.GetString(0), Parser: r.GetInt32(1)), ("$m", c.MatchId), ("$p", c.PlayerId)).FirstOrDefault();
        if (existing.User != null && existing.User != userId)
        {
            return UploadStatus.PlayerClaimed;
        }
        if (existing.User != null && existing.Parser > c.ParserVersion)
        {
            return UploadStatus.Stale;
        }
        Exec("""
            INSERT INTO contributions(match_id,player_id,user_id,parser_version,counted,payload,received_utc,updated_utc)
            VALUES($m,$p,$u,$v,$c,$j,$n,$n)
            ON CONFLICT(match_id,player_id) DO UPDATE SET parser_version=excluded.parser_version, counted=excluded.counted,
                payload=excluded.payload, updated_utc=excluded.updated_utc
            """, ("$m", c.MatchId), ("$p", c.PlayerId), ("$u", userId), ("$v", c.ParserVersion), ("$c", c.Counted ? 1 : 0),
            ("$j", Wire.Serialize(c)), ("$n", Iso(now)));
        return UploadStatus.Accepted;
    });

    public void MarkSynced(string userId, DateTime now)
    {
        Exec("UPDATE users SET last_sync_utc=$n, last_seen_utc=$n WHERE id=$i", ("$n", Iso(now)), ("$i", userId));
        Exec("UPDATE squads SET revision=revision+1 WHERE id IN (SELECT squad_id FROM members WHERE user_id=$u)", ("$u", userId));
    }

    // ---- squad state ------------------------------------------------------------------------------------------

    /// <summary>The squad as the clients see it, computed from the stored rows (nothing derived is ever stored).</summary>
    public SquadStateDto? State(string squadId, Func<string, bool> isOnline, DateTime now, string baseUrl)
    {
        lock (_gate)
        {
            var squad = GetSquad(squadId);
            if (squad == null) return null;
            var members = Query("""
                SELECT u.id, u.display_name, m.role, m.joined_utc, u.last_sync_utc, u.last_seen_utc
                FROM members m JOIN users u ON u.id = m.user_id WHERE m.squad_id=$s ORDER BY m.joined_utc
                """, r => new MemberInfo(r.GetString(0), r.GetString(1), r.GetString(2), Date(r, 3)!.Value, Date(r, 4), Date(r, 5), AccountsOf(r.GetString(0))),
                ("$s", squadId));
            var contributions = Query("""
                SELECT c.user_id, c.payload, c.received_utc FROM contributions c
                WHERE c.user_id IN (SELECT user_id FROM members WHERE squad_id=$s)
                """, r => new ContributionRecord(r.GetString(0), Wire.Deserialize<ContributionDto>(r.GetString(1))!, Date(r, 2)!.Value), ("$s", squadId));
            return SquadAggregator.Build(new SquadInfo(squad.Id, squad.Name, squad.Revision, _o.MaxSquadSize), members, contributions, isOnline, now,
                ActiveInvites(squadId, now, baseUrl));
        }
    }

    public object AdminStats() => new
    {
        users = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM users")),
        squads = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM squads")),
        contributions = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM contributions")),
        accounts = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM accounts")),
        activeInvites = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM invites WHERE revoked=0 AND uses < max_uses")),
        squadList = Query("SELECT s.id, s.name, s.revision, (SELECT COUNT(*) FROM members m WHERE m.squad_id = s.id) FROM squads s ORDER BY s.created_utc DESC LIMIT 200",
            r => new { id = r.GetString(0), name = r.GetString(1), revision = r.GetInt64(2), members = r.GetInt32(3) }),
    };

    public bool Healthy()
    {
        try { return Convert.ToInt32(Scalar("SELECT 1")) == 1; } catch { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { /* closing */ }
            _c.Dispose();
        }
    }
}
