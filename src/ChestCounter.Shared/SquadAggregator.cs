namespace ChestCounter.Shared;

public sealed record SquadInfo(string SquadId, string Name, long Revision, int MaxMembers);

public sealed record MemberInfo(string UserId, string DisplayName, string Role, DateTime JoinedUtc, DateTime? LastSyncUtc,
    DateTime? LastSeenUtc, IReadOnlyList<AccountDto> Accounts);

/// <summary>A stored contribution: the uploaded result plus who uploaded it and when the server received it.</summary>
public sealed record ContributionRecord(string UserId, ContributionDto Data, DateTime ReceivedUtc);

/// <summary>
/// Turns the stored per-player results into the squad figures. Pure: same input, same output.
///
/// The rule that prevents double counting: a total is only ever the sum of each player's OWN result, one per
/// (match, player). What a replay recorded about teammates (<see cref="RosterEntry.ObservedChests"/>) is used to know
/// who played together and as a labelled hint for a missing upload, never as a term of a sum.
/// </summary>
public static class SquadAggregator
{
    /// <summary>Starts replicated by the same game server should be equal; this much slack covers rounding.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(5);

    public const string WarnStartTime = "start_time_mismatch";
    public const string WarnPlaylist = "playlist_mismatch";
    public const string WarnRoster = "roster_mismatch";

    public static SquadStateDto Build(SquadInfo squad, IReadOnlyList<MemberInfo> members, IReadOnlyList<ContributionRecord> contributions,
        Func<string, bool> isOnline, DateTime nowUtc, IReadOnlyList<InviteDto>? invites = null, int maxSharedMatches = 100)
    {
        var memberIds = members.Select(m => m.UserId).ToHashSet();
        // One row per (match, player) is guaranteed by the store; defend against a caller passing duplicates anyway.
        var counted = contributions
            .Where(c => c.Data.Counted && memberIds.Contains(c.UserId))
            .GroupBy(c => (c.Data.MatchId, c.Data.PlayerId))
            .Select(g => g.OrderByDescending(c => c.Data.ParserVersion).ThenByDescending(c => c.ReceivedUtc).First())
            .ToList();

        // Which member owns which player id: declared accounts first, then whatever they uploaded.
        var ownerOfPlayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            foreach (var a in m.Accounts)
            {
                ownerOfPlayer.TryAdd(a.PlayerId, m.UserId);
            }
        }
        foreach (var c in counted)
        {
            ownerOfPlayer.TryAdd(c.Data.PlayerId, c.UserId);
        }
        var nameOfUser = members.ToDictionary(m => m.UserId, m => m.DisplayName);

        // A member whose own replay says the match does not count (withdrawn result) is not "missing" from it.
        var withdrawn = contributions.Where(c => !c.Data.Counted).Select(c => (c.Data.MatchId, c.UserId)).ToHashSet();

        var shared = new List<SharedMatchDto>();
        foreach (var match in counted.Where(c => c.Data.IdentityReliable).GroupBy(c => c.Data.MatchId))
        {
            shared.AddRange(SharedGroups(match.Key, match.ToList(), ownerOfPlayer, nameOfUser, withdrawn));
        }
        shared = shared
            .OrderByDescending(s => s.StartUtc ?? DateTime.MinValue)
            .ThenBy(s => s.MatchId, StringComparer.Ordinal)
            .ToList();

        var inTotals = shared.Where(s => s.Status != SharedMatchStatus.Ambiguous).ToList();
        var sharedByUser = inTotals.SelectMany(s => s.Contributions).GroupBy(c => c.UserId).ToDictionary(g => g.Key, g => g.Sum(c => c.Chests));

        var memberDtos = members.Select(m =>
        {
            var own = counted.Where(c => c.UserId == m.UserId).ToList();
            var last = own.Where(c => c.Data.MatchStartUtc != null).OrderByDescending(c => c.Data.MatchStartUtc).FirstOrDefault()
                       ?? own.OrderByDescending(c => c.ReceivedUtc).FirstOrDefault();
            var accounts = m.Accounts
                .Concat(own.Select(c => new AccountDto(c.Data.PlayerId, c.Data.PlayerName)))
                .GroupBy(a => a.PlayerId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(a => a.PlayerName.Length).First())
                .ToList();
            return new MemberDto
            {
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                Role = m.Role,
                Online = isOnline(m.UserId),
                JoinedUtc = m.JoinedUtc,
                LastSyncUtc = m.LastSyncUtc,
                LastSeenUtc = m.LastSeenUtc,
                Accounts = accounts,
                LifetimeChests = own.Sum(c => c.Data.ChestsOpened),
                LifetimeMatches = own.Count,
                SharedChests = sharedByUser.GetValueOrDefault(m.UserId),
                LastMatch = last == null ? null : new MatchRefDto(last.Data.MatchId, last.Data.MatchStartUtc, last.Data.ChestsOpened),
            };
        }).ToList();

        return new SquadStateDto
        {
            SquadId = squad.SquadId,
            Name = squad.Name,
            Revision = squad.Revision,
            ServerTimeUtc = nowUtc,
            MaxMembers = squad.MaxMembers,
            Members = memberDtos,
            CombinedMemberChests = memberDtos.Sum(m => m.LifetimeChests),
            SharedChests = inTotals.Sum(s => s.ConfirmedChests),
            SharedMatchCount = inTotals.Count,
            PartialMatchCount = shared.Count(s => s.Status == SharedMatchStatus.Partial),
            AmbiguousMatchCount = shared.Count(s => s.Status == SharedMatchStatus.Ambiguous),
            SharedMatches = shared.Take(maxSharedMatches).ToList(),
            Invites = invites?.ToList() ?? new List<InviteDto>(),
        };
    }

    /// <summary>
    /// Groups the members of one match by team. A team of at least two members is a shared match. Two members in the
    /// same lobby but on different teams (or both in a solo queue) did not play together: no shared match.
    /// </summary>
    private static IEnumerable<SharedMatchDto> SharedGroups(string matchId, List<ContributionRecord> uploads,
        Dictionary<string, string> ownerOfPlayer, Dictionary<string, string> nameOfUser, HashSet<(string MatchId, string UserId)> withdrawn)
    {
        // Nodes: members. Edge: an uploader's roster names another member's account as a teammate.
        var parent = new Dictionary<string, string>();
        string Find(string x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Add(string x) => parent.TryAdd(x, x);
        void Union(string a, string b) { parent[Find(a)] = Find(b); }

        var uploaderOf = uploads.GroupBy(u => u.UserId).ToDictionary(g => g.Key, g => g.ToList());
        var missingInfo = new Dictionary<string, (string PlayerId, string Name, int? Estimate)>();
        var names = new HashSet<(string From, string To)>();

        foreach (var u in uploads)
        {
            Add(u.UserId);
            foreach (var mate in u.Data.Roster.Where(r => !r.IsBot && !string.IsNullOrEmpty(r.PlayerId)))
            {
                if (!ownerOfPlayer.TryGetValue(mate.PlayerId!, out var mateUser) || mateUser == u.UserId || withdrawn.Contains((matchId, mateUser)))
                {
                    continue; // a random teammate, not a squad member (or a member who withdrew this match)
                }
                Add(mateUser);
                Union(u.UserId, mateUser);
                names.Add((u.UserId, mateUser));
                if (!uploaderOf.ContainsKey(mateUser))
                {
                    var previous = missingInfo.GetValueOrDefault(mateUser);
                    var estimate = previous.Estimate is int e ? Math.Max(e, mate.ObservedChests) : mate.ObservedChests;
                    missingInfo[mateUser] = (mate.PlayerId!, string.IsNullOrEmpty(mate.Name) ? previous.Name ?? "" : mate.Name, estimate);
                }
            }
        }

        foreach (var team in parent.Keys.GroupBy(Find).Where(g => g.Count() >= 2))
        {
            var users = team.ToList();
            var present = uploads.Where(u => users.Contains(u.UserId)).OrderBy(u => u.Data.PlayerId, StringComparer.Ordinal).ToList();
            var warnings = new List<string>();

            var starts = present.Where(p => p.Data.MatchStartUtc != null).Select(p => p.Data.MatchStartUtc!.Value).ToList();
            if (starts.Count >= 2 && starts.Max() - starts.Min() > StartTolerance)
            {
                warnings.Add(WarnStartTime);
            }
            if (present.Select(p => p.Data.Playlist).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                warnings.Add(WarnPlaylist);
            }
            // Two uploaders of the same team must name each other. One-sided = the two replays disagree.
            var presentUsers = present.Select(p => p.UserId).Distinct().ToList();
            if (presentUsers.Any(a => presentUsers.Any(b => a != b && names.Contains((a, b)) != names.Contains((b, a)))))
            {
                warnings.Add(WarnRoster);
            }

            var missing = users.Where(u => !uploaderOf.ContainsKey(u))
                .Select(u => new MissingContributionDto(u, missingInfo[u].PlayerId,
                    string.IsNullOrEmpty(missingInfo[u].Name) ? nameOfUser.GetValueOrDefault(u, "") : missingInfo[u].Name, missingInfo[u].Estimate))
                .OrderBy(m => m.PlayerId, StringComparer.Ordinal)
                .ToList();

            var first = present.OrderBy(p => p.ReceivedUtc).First().Data;
            yield return new SharedMatchDto
            {
                MatchId = matchId,
                StartUtc = starts.Count > 0 ? starts.Min() : null,
                Playlist = first.Playlist,
                DurationSeconds = present.Max(p => p.Data.DurationSeconds),
                Status = warnings.Count > 0 ? SharedMatchStatus.Ambiguous : missing.Count > 0 ? SharedMatchStatus.Partial : SharedMatchStatus.Complete,
                ConfirmedChests = present.Sum(p => p.Data.ChestsOpened),
                Contributions = present.Select(p => new SharedContributionDto(p.UserId, p.Data.PlayerId, p.Data.PlayerName, p.Data.ChestsOpened,
                    p.Data.ChestsDestroyed, p.Data.AmmoBoxesOpened, p.Data.ParserVersion, p.ReceivedUtc, p.Data.PossiblyIncomplete)).ToList(),
                Missing = missing,
                Warnings = warnings,
            };
        }
    }
}
