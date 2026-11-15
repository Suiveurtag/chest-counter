using System.Text.Json;
using ChestCounter.Engine;
using ChestCounter.Shared;

namespace ChestCounter.Tests;

/// <summary>
/// A stand-in for a Fortnite replay: a small JSON file that says what the real analyzer would have found in it.
/// No real replay is available in this repository, so these tests cover everything AROUND the parser (tracking,
/// deduplication, storage, synchronization), not the parser itself.
/// </summary>
public sealed record FakeReplay
{
    /// <summary>True while "Fortnite is recording", false once finalized.</summary>
    public bool Live { get; init; }
    public bool KeyMissing { get; init; }
    /// <summary>The analyzer throws on this one.</summary>
    public bool Corrupt { get; init; }
    /// <summary>Null = the replay holds no game session id.</summary>
    public string? SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public string? PlayerId { get; init; } = "aaaa0000aaaa0000aaaa0000aaaa0000";
    public string PlayerName { get; init; } = "PlayerA";
    public DateTime? StartUtc { get; init; } = new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);
    public int Duration { get; init; } = 1200;
    public string Playlist { get; init; } = "Playlist_DefaultSquad";
    public bool Custom { get; init; }
    public int Chests { get; init; }
    public int Destroyed { get; init; }
    public int Ammo { get; init; }
    public int Other { get; init; }
    public int VehicleLoot { get; init; }
    public List<RosterEntry> Roster { get; init; } = new();
    /// <summary>Changes the bytes without changing the match (a second recording of the same match).</summary>
    public string Padding { get; init; } = "";

    public string Write(string folder, string name, TimeSpan? age = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name.EndsWith(".replay") ? name : name + ".replay");
        File.WriteAllText(path, JsonSerializer.Serialize(this));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.FromHours(1)));
        return path;
    }

    public static FakeReplay? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<FakeReplay>(fs);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class FakeAnalyzer : IReplayAnalyzer
{
    public int Version { get; set; } = 1;
    public int Calls;
    /// <summary>Lets a test change what a "newer parser" finds: (replay, version) -> chests.</summary>
    public Func<FakeReplay, int, int>? Recount { get; set; }

    public AnalyzedMatch Analyze(string path, string fingerprint)
    {
        Interlocked.Increment(ref Calls);
        var r = FakeReplay.Read(path);
        if (r == null || r.Corrupt)
        {
            throw new InvalidDataException("not a replay");
        }
        var chests = Recount?.Invoke(r, Version) ?? r.Chests;
        return new AnalyzedMatch
        {
            MatchId = r.SessionId ?? "local:" + fingerprint,
            IdentityReliable = r.SessionId != null,
            PlayerId = r.PlayerId,
            PlayerName = r.PlayerName,
            StartUtc = r.StartUtc,
            DurationSeconds = r.Duration,
            Playlist = r.Playlist,
            IsCustom = r.Custom,
            ChestsOpened = chests,
            ChestsDestroyed = Math.Min(r.Destroyed, chests),
            AmmoBoxesOpened = r.Ammo,
            OtherContainersOpened = r.Other,
            ByKind = new() { [Kinds.Chest] = chests, [Kinds.AmmoBox] = r.Ammo },
            Roster = r.Roster,
            UnrecordedVehicleLoot = r.VehicleLoot,
            ParserVersion = Version,
        };
    }
}

public sealed class FakeProbe : IReplayProbe
{
    public ProbeResult Probe(string path) => FakeReplay.Read(path) is { } r ? new ProbeResult(r.Live, r.KeyMissing) : new ProbeResult(null, true);
}

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}

