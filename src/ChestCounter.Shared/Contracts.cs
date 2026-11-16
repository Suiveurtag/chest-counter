using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter.Shared;

/// <summary>JSON settings shared by the desktop client and the sync server.</summary>
public static class Wire
{
    public const int ProtocolVersion = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}

/// <summary>
/// A teammate as seen in the uploader's own replay. <see cref="ObservedChests"/> is what that replay happened to
/// record of the teammate (only what was in range of the recorder): secondary information, never added to a total.
/// </summary>
public sealed record RosterEntry(string? PlayerId, string Name, bool IsBot, int ObservedChests);

/// <summary>
/// A treasure the uploader's own replay shows they obtained. Raw ids only: the clients name them with their own
/// catalogue. Treasures deduced for teammates are estimates and are never sent.
/// </summary>
public sealed record TreasureDto(string? PrizeId, string RawItem, float? TimeSeconds, bool FromGoldenChest);

/// <summary>
/// One player's own result for one match, derived from that player's own replay. The authoritative unit of the
/// squad synchronization: the server keeps exactly one per (MatchId, PlayerId).
/// </summary>
public sealed record ContributionDto
{
    public required string MatchId { get; init; }
    public required string PlayerId { get; init; }
    public string PlayerName { get; init; } = "";
    public int ChestsOpened { get; init; }
    /// <summary>Part of <see cref="ChestsOpened"/> that was destroyed rather than opened.</summary>
    public int ChestsDestroyed { get; init; }
    public int AmmoBoxesOpened { get; init; }
    public int OtherContainersOpened { get; init; }
    /// <summary>Match start as replicated by the game server (not the uploader's clock).</summary>
    public DateTime? MatchStartUtc { get; init; }
    public int? DurationSeconds { get; init; }
    public string? Playlist { get; init; }
    public int ParserVersion { get; init; }
    /// <summary>False withdraws the contribution (a re-analysis decided the match does not count).</summary>
    public bool Counted { get; init; } = true;
    /// <summary>The replay shows signs that some chests could not be recorded (see docs: vehicle loot).</summary>
    public bool PossiblyIncomplete { get; init; }
    /// <summary>False when the replay held no game session id: the match id is local and can never be matched across devices.</summary>
    public bool IdentityReliable { get; init; } = true;
    public List<RosterEntry> Roster { get; init; } = new();
    /// <summary>
    /// The uploader's own treasures in this match. Null: a client from before treasures were synced (unknown, not
    /// zero). Added in protocol 1 as an optional field: older servers ignore it, older clients omit it.
    /// </summary>
    public List<TreasureDto>? Treasures { get; init; }
}

public sealed record RegisterRequest(string DisplayName, string? RegistrationKey = null);
public sealed record RegisterResponse(string UserId, string Token, int ProtocolVersion);
public sealed record AccountDto(string PlayerId, string PlayerName);
public sealed record HelloRequest(string? DisplayName, List<AccountDto> Accounts);
public sealed record CreateSquadRequest(string Name);
public sealed record RenameSquadRequest(string Name);
public sealed record JoinSquadRequest(string Code);
public sealed record CreateInviteRequest(int? TtlHours = null, int? MaxUses = null);
public sealed record InviteDto(string Code, string Link, DateTime CreatedUtc, DateTime ExpiresUtc, int MaxUses, int Uses);
public sealed record UploadRequest(List<ContributionDto> Contributions);

public static class UploadStatus
{
    public const string Accepted = "accepted";
    /// <summary>The server already holds a result from a newer parser version for this (match, player).</summary>
    public const string Stale = "stale";
    /// <summary>This player id was first uploaded by another member: refused.</summary>
    public const string PlayerClaimed = "player_claimed";
    public const string Invalid = "invalid";
}

public sealed record UploadResult(string MatchId, string PlayerId, string Status);
public sealed record UploadResponse(List<UploadResult> Results, DateTime ServerTimeUtc);
public sealed record MeResponse(string UserId, string DisplayName, List<AccountDto> Accounts, List<SquadSummaryDto> Squads);
public sealed record SquadSummaryDto(string SquadId, string Name, string Role);
public sealed record ErrorResponse(string Error, string? Message = null);

// ---- Squad state (computed by SquadAggregator, sent over REST and WebSocket) ----------------------------------

public static class SharedMatchStatus
{
    /// <summary>Every member who played this match together has uploaded their own result.</summary>
    public const string Complete = "complete";
    /// <summary>At least one member who played has not uploaded yet: the total is a lower bound.</summary>
    public const string Partial = "partial";
    /// <summary>The uploads disagree about the match (start time, mode or teammates): kept out of the totals.</summary>
    public const string Ambiguous = "ambiguous";
}

public sealed record MatchRefDto(string MatchId, DateTime? StartUtc, int Chests);

public sealed record MemberDto
{
    public required string UserId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "member";
    public bool Online { get; init; }
    public DateTime JoinedUtc { get; init; }
    public DateTime? LastSyncUtc { get; init; }
    public DateTime? LastSeenUtc { get; init; }
    public List<AccountDto> Accounts { get; init; } = new();
    /// <summary>All confirmed chests of this member's own replays, every account, played with the squad or not.</summary>
    public int LifetimeChests { get; init; }
    public int LifetimeMatches { get; init; }
    /// <summary>This member's confirmed chests in shared squad matches only.</summary>
    public int SharedChests { get; init; }
    public MatchRefDto? LastMatch { get; init; }
    /// <summary>Treasures of this member's own replays (matches whose upload carries treasures).</summary>
    public int LifetimeTreasures { get; init; }
    /// <summary>This member's treasures in shared squad matches only.</summary>
    public int SharedTreasures { get; init; }
    /// <summary>Counted matches uploaded by a client that did not send treasures: the treasure totals are a lower bound.</summary>
    public int TreasuresUnknownMatches { get; init; }
    /// <summary>This member's best match (most chests).</summary>
    public MatchRefDto? BestMatch { get; init; }
}

public sealed record SharedContributionDto(string UserId, string PlayerId, string PlayerName, int Chests, int ChestsDestroyed,
    int AmmoBoxes, int ParserVersion, DateTime ReceivedUtc, bool PossiblyIncomplete, int? Treasures = null);

/// <summary>One treasure a member found, for the squad's recent finds.</summary>
/// <param name="Shared">Found in a match played with the squad (not ambiguous).</param>
public sealed record SquadTreasureDto(string UserId, string MatchId, DateTime? MatchStartUtc, string? PrizeId, string RawItem,
    bool FromGoldenChest, bool Shared);

public sealed record SessionMemberDto(string UserId, int Chests, int Matches, int Treasures);

/// <summary>
/// The squad's latest play session: the members' matches in a row with no break longer than
/// <see cref="SquadAggregator.SessionGap"/>. Sums of each member's own results, like every other figure.
/// </summary>
public sealed record SquadSessionDto
{
    public DateTime StartUtc { get; init; }
    /// <summary>End of the session's last match.</summary>
    public DateTime EndUtc { get; init; }
    /// <summary>The last match ended less than <see cref="SquadAggregator.SessionGap"/> ago: the session may go on.</summary>
    public bool Active { get; init; }
    public int Chests { get; init; }
    public int Matches { get; init; }
    public int Treasures { get; init; }
    /// <summary>Shared squad matches of the session (ambiguous ones excluded) and their confirmed chests.</summary>
    public int SharedMatches { get; init; }
    public int SharedChests { get; init; }
    public List<SessionMemberDto> Members { get; init; } = new();
}

/// <summary>A member who played the match but whose own result is not on the server (yet).</summary>
/// <param name="ObservedEstimate">What teammates' replays happened to record of them: shown as a hint, never summed.</param>
public sealed record MissingContributionDto(string UserId, string PlayerId, string Name, int? ObservedEstimate);

public sealed record SharedMatchDto
{
    public required string MatchId { get; init; }
    public DateTime? StartUtc { get; init; }
    public string? Playlist { get; init; }
    public int? DurationSeconds { get; init; }
    public string Status { get; init; } = SharedMatchStatus.Complete;
    /// <summary>Sum of the uploaded own results only. A lower bound while <see cref="Status"/> is partial.</summary>
    public int ConfirmedChests { get; init; }
    public List<SharedContributionDto> Contributions { get; init; } = new();
    public List<MissingContributionDto> Missing { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed record SquadStateDto
{
    public required string SquadId { get; init; }
    public string Name { get; init; } = "";
    public long Revision { get; init; }
    public DateTime ServerTimeUtc { get; init; }
    public int MaxMembers { get; init; } = 4;
    public List<MemberDto> Members { get; init; } = new();
    /// <summary>Sum of the members' lifetime totals, whoever they played with.</summary>
    public int CombinedMemberChests { get; init; }
    /// <summary>Confirmed chests of matches at least two members played together (ambiguous matches excluded).</summary>
    public int SharedChests { get; init; }
    public int SharedMatchCount { get; init; }
    public int PartialMatchCount { get; init; }
    public int AmbiguousMatchCount { get; init; }
    /// <summary>Sum of the members' own treasures, whoever they played with.</summary>
    public int CombinedTreasures { get; init; }
    /// <summary>Own treasures of the members in shared matches (ambiguous matches excluded).</summary>
    public int SharedTreasures { get; init; }
    /// <summary>Null when no member has a counted match with a start time.</summary>
    public SquadSessionDto? Session { get; init; }
    /// <summary>Most recent first, capped by the server.</summary>
    public List<SquadTreasureDto> RecentTreasures { get; init; } = new();
    /// <summary>Most recent first, capped by the server.</summary>
    public List<SharedMatchDto> SharedMatches { get; init; } = new();
    public List<InviteDto> Invites { get; init; } = new();
}

/// <summary>Server to client WebSocket message.</summary>
public sealed record ServerMessage(string Type, SquadStateDto? State = null, string? SquadId = null, string? Reason = null)
{
    public const string SquadState = "squad_state";
    public const string SquadRemoved = "squad_removed";
}
