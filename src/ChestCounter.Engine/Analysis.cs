using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ChestCounter.Shared;

namespace ChestCounter.Engine;

/// <summary>Stable, language independent codes for what the analyzer classifies.</summary>
public static class Kinds
{
    public const string Chest = "chest", RareChest = "rare_chest", BuriedChest = "buried_chest", SpriteChest = "sprite_chest",
        PosterChest = "poster_chest", OtherChest = "other_chest", LootCannon = "loot_cannon", AmmoBox = "ammo_box",
        FoodBox = "food_box", OtherContainer = "other_container";

    /// <summary>The reference analyzer stores kinds under their French names: map them once, here.</summary>
    public static string FromReference(string kind) => global::ChestCounter.ChestKinds.Canonical(kind) switch
    {
        global::ChestCounter.ChestKinds.Normal => Chest,
        global::ChestCounter.ChestKinds.Rare => RareChest,
        global::ChestCounter.ChestKinds.Buried => BuriedChest,
        global::ChestCounter.ChestKinds.Extraction => SpriteChest,
        global::ChestCounter.ChestKinds.PosterChest => PosterChest,
        global::ChestCounter.ChestKinds.Other => OtherChest,
        global::ChestCounter.ChestKinds.Cannon => LootCannon,
        global::ChestCounter.ChestKinds.AmmoBox => AmmoBox,
        global::ChestCounter.ChestKinds.FoodBox => FoodBox,
        _ => OtherContainer,
    };
}

public sealed record ChestEvent(float TimeSeconds, string Kind, bool Destroyed);

/// <summary>A treasure the replay shows the player obtained. Raw ids are kept: naming them is a display concern.</summary>
public sealed record TreasureInfo(string? PrizeId, string RawItem, float? TimeSeconds, string SourceKind, bool FromGoldenChest, bool SourceCertain);

/// <summary>Everything the engine keeps from the analysis of one replay.</summary>
public sealed record AnalyzedMatch
{
    public required string MatchId { get; init; }
    /// <summary>True when the id is the game server's session id (same for every player of the match).</summary>
    public bool IdentityReliable { get; init; } = true;
    public string? PlayerId { get; init; }
    public string? PlayerName { get; init; }
    public DateTime? StartUtc { get; init; }
    public int? DurationSeconds { get; init; }
    public string? Playlist { get; init; }
    public string? Map { get; init; }
    public string? GameVersion { get; init; }
    public bool IsCustom { get; init; }
    public int? Placement { get; init; }
    public int? TotalPlayers { get; init; }
    public int? Eliminations { get; init; }
    public int ChestsOpened { get; init; }
    public int ChestsDestroyed { get; init; }
    public int AmmoBoxesOpened { get; init; }
    public int OtherContainersOpened { get; init; }
    /// <summary>Chests by kind code, then every other interaction by kind code.</summary>
    public Dictionary<string, int> ByKind { get; init; } = new();
    public List<ChestEvent> Chests { get; init; } = new();
    public List<RosterEntry> Roster { get; init; } = new();
    public List<TreasureInfo> Treasures { get; init; } = new();
    public int TeamTreasures { get; init; }
    /// <summary>Loot piles along the player's drives that no recorded chest explains (3 or more: chests are missing).</summary>
    public int UnrecordedVehicleLoot { get; init; }
    public int ParserVersion { get; init; }

    public string ProfileKey => !string.IsNullOrEmpty(PlayerId) ? PlayerId! : PlayerName ?? "";
    public bool PossiblyIncomplete => UnrecordedVehicleLoot >= 3;
}

public interface IReplayAnalyzer
{
    /// <summary>Bump when the analysis changes: replays still on disk are analyzed again and their results replaced.</summary>
    int Version { get; }
    /// <summary>Reads one finished replay. Throws when it cannot be read.</summary>
    AnalyzedMatch Analyze(string path, string fingerprint);
}

/// <summary>What the first bytes of a replay file say, without decoding the match.</summary>
/// <param name="IsLive">True while Fortnite records it, false once finalized, null when the header cannot be read.</param>
/// <param name="KeyMissing">True when the replay is encrypted and Fortnite has not written the key yet.</param>
public readonly record struct ProbeResult(bool? IsLive, bool KeyMissing);

public interface IReplayProbe
{
    ProbeResult Probe(string path);
}

public sealed class FortniteReplayProbe : IReplayProbe
{
    public ProbeResult Probe(string path) =>
        new(global::ChestCounter.ReplayHeaderProbe.IsLive(path), global::ChestCounter.ReplayHeaderProbe.KeyMissing(path));
}

/// <summary>
/// Runs the reference analyzer (unchanged) and converts its record. Counting rules, chest destruction, refills,
/// player attribution and replay-version handling are all the reference's.
/// </summary>
public sealed class FortniteReplayAnalyzer : IReplayAnalyzer
{
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    public int Version => global::ChestCounter.ReplayAnalyzer.CurrentVersion;

    public AnalyzedMatch Analyze(string path, string fingerprint)
    {
        // Fortnite may still hold the file open: share everything, change nothing.
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        global::ChestCounter.MatchRecord? record = null;
        Exception? failure = null;
        var name = Path.GetFileName(path);
        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                // "" = track the replay owner: the replays saved on a PC are those of its player.
                record = new global::ChestCounter.ReplayAnalyzer().Analyze(ms, name, "");
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "ReplayAnalysis" };
        worker.Start();
        if (!worker.Join(ParseTimeout))
        {
            throw new TimeoutException("analysis took too long");
        }
        bytes = Array.Empty<byte>();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return Convert(record!, name, fingerprint);
    }

    internal static AnalyzedMatch Convert(global::ChestCounter.MatchRecord r, string fileName, string fingerprint)
    {
        // Without a game session id the reference falls back to "<timestamp>-<file name>", which changes when the file
        // is renamed. Use the content fingerprint instead, so a renamed or copied replay is still the same match.
        var reliable = !string.IsNullOrWhiteSpace(r.SessionId) && !r.SessionId.EndsWith("-" + fileName, StringComparison.Ordinal);
        var byKind = new Dictionary<string, int>();
        foreach (var chest in r.Chests)
        {
            var kind = Kinds.FromReference(chest.Kind);
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
        }
        foreach (var (kind, count) in r.OtherInteractions)
        {
            var code = Kinds.FromReference(kind);
            byKind[code] = byKind.GetValueOrDefault(code) + count;
        }

        return new AnalyzedMatch
        {
            MatchId = reliable ? r.SessionId.Trim() : "local:" + fingerprint,
            IdentityReliable = reliable,
            PlayerId = string.IsNullOrWhiteSpace(r.EpicId) ? null : r.EpicId.Trim().ToLowerInvariant(),
            PlayerName = r.PlayerName,
            StartUtc = r.MatchStartUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
            DurationSeconds = r.DurationSeconds,
            Playlist = r.Playlist,
            Map = r.Map,
            GameVersion = r.GameVersion,
            IsCustom = r.IsCustomMatch,
            Placement = r.Placement,
            TotalPlayers = r.TotalPlayers,
            Eliminations = r.Eliminations,
            ChestsOpened = r.ChestsOpened,
            ChestsDestroyed = r.Chests.Count(c => c.Destroyed),
            AmmoBoxesOpened = r.AmmoBoxesOpened,
            OtherContainersOpened = r.OtherContainersOpened,
            ByKind = byKind,
            Chests = r.Chests.Select(c => new ChestEvent(c.TimeSeconds, Kinds.FromReference(c.Kind), c.Destroyed)).ToList(),
            Roster = r.TeamChests.Select(t => new RosterEntry(string.IsNullOrWhiteSpace(t.EpicId) ? null : t.EpicId.Trim().ToLowerInvariant(), t.Name, t.IsBot, t.Chests)).ToList(),
            Treasures = r.TreasureFinds.Select(t => new TreasureInfo(t.PrizeId, t.RawItem, t.ObtainedAtSeconds, Kinds.FromReference(t.SourceKind), t.FromGoldenChest, t.SourceCertain)).ToList(),
            TeamTreasures = r.TeamTreasureFinds.Count,
            UnrecordedVehicleLoot = r.UnrecordedVehicleLoot,
            ParserVersion = r.AnalyzerVersion,
        };
    }
}

/// <summary>Which matches count toward the chest totals.</summary>
public static class ModeRules
{
    public const string ReasonCustom = "custom_match", ReasonMode = "unsupported_mode", ReasonNoPlayer = "no_player";

    // Same playlists as the reference: regular Battle Royale, build and zero build. Ranked, OG, Reload, limited-time
    // modes, Creative and the other games have other playlists and are left out: the analyzer was only ever checked
    // against Battle Royale chests.
    private static readonly Regex Supported = new(
        @"^Playlist_(Default(Solo|Duo|Duos|Trio|Trios|Squad|Squads)|Trios|NoBuildBR_(Solo|Duo|Duos|Trio|Trios|Squad|Squads))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsSupportedPlaylist(string? playlist) => playlist != null && Supported.IsMatch(playlist);

    /// <summary>Null when the match counts, else a reason code.</summary>
    public static string? WhyNot(AnalyzedMatch m)
    {
        if (m.IsCustom) return ReasonCustom;
        if (!IsSupportedPlaylist(m.Playlist)) return ReasonMode;
        if (string.IsNullOrEmpty(m.ProfileKey)) return ReasonNoPlayer;
        return null;
    }
}

public static class Fingerprint
{
    private const int Head = 256 * 1024, Tail = 64 * 1024;

    /// <summary>
    /// Identity of a replay's content, cheap enough for every file: length + first 256 KB + last 64 KB. A renamed or
    /// copied replay has the same fingerprint; a replay Fortnite appended to has another one.
    /// </summary>
    public static string Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(fs.Length));
        var buffer = new byte[Head];
        var n = fs.ReadAtLeast(buffer, Head, throwOnEndOfStream: false);
        sha.AppendData(buffer, 0, n);
        if (fs.Length > Head)
        {
            var tail = (int)Math.Min(Tail, fs.Length - Head);
            fs.Seek(-tail, SeekOrigin.End);
            n = fs.ReadAtLeast(buffer.AsSpan(0, tail), tail, throwOnEndOfStream: false);
            sha.AppendData(buffer, 0, n);
        }
        return System.Convert.ToHexString(sha.GetHashAndReset(), 0, 16).ToLowerInvariant();
    }
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
