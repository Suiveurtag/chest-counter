using ChestCounter.Engine;

namespace ChestCounter.Tests;

/// <summary>Local counting: import, restart, readiness, deduplication, re-analysis, bad replays.</summary>
public sealed class TrackingTests : IDisposable
{
    private readonly Sandbox _s = new();
    public void Dispose() => _s.Dispose();

    [Fact] // requirement 1
    public void Initial_import_processes_every_historical_replay()
    {
        new FakeReplay { Chests = 12, Ammo = 3 }.Write(_s.Demos, "a");
        new FakeReplay { Chests = 30, Ammo = 5 }.Write(_s.Demos, "b");
        new FakeReplay { Chests = 8 }.Write(_s.Demos, "c");
        using var db = _s.OpenDb();

        var summary = _s.Tracker(db).ScanOnce(ignoreQuietPeriod: true);

        Assert.Equal(3, summary.Counted);
        Assert.Equal(50, Sandbox.Total(db));
        Assert.Equal(8, db.Matches(new LocalDb.MatchFilter(Limit: 10)).Sum(m => m.AmmoBoxes));
        Assert.Equal(3, db.FileStatusCounts()[FileStatus.Counted]);
    }

    [Fact] // requirement 1: progress survives leaving in the middle
    public void Import_interrupted_midway_keeps_what_was_done_and_resumes()
    {
        for (var i = 0; i < 6; i++) new FakeReplay { Chests = 10 }.Write(_s.Demos, "r" + i, TimeSpan.FromHours(10 - i));
        using (var db = _s.OpenDb())
        {
            using var stop = new CancellationTokenSource();
            var tracker = _s.Tracker(db);
            var seen = 0;
            tracker.MatchSaved += _ => { if (++seen == 2) stop.Cancel(); };
            tracker.ScanOnce(ignoreQuietPeriod: true, ct: stop.Token);
            Assert.Equal(20, Sandbox.Total(db));
        }

        using var reopened = _s.OpenDb();
        var summary = _s.Tracker(reopened).ScanOnce(ignoreQuietPeriod: true);
        Assert.Equal(4, summary.Counted);
        Assert.Equal(60, Sandbox.Total(reopened));
        Assert.Equal(6, _s.Analyzer.Calls); // each replay decoded exactly once overall
    }

    [Fact] // requirement 2
    public void Restart_does_not_duplicate_totals_and_does_not_reread_files()
    {
        new FakeReplay { Chests = 41 }.Write(_s.Demos, "a");
        new FakeReplay { Chests = 9 }.Write(_s.Demos, "b");
        using (var db = _s.OpenDb())
        {
            _s.Tracker(db).ScanOnce(ignoreQuietPeriod: true);
        }
        Assert.Equal(2, _s.Analyzer.Calls);

        for (var restart = 0; restart < 3; restart++)
        {
            using var db = _s.OpenDb();
            var summary = _s.Tracker(db).ScanOnce(ignoreQuietPeriod: true);
            Assert.Equal(0, summary.Total);
            Assert.Equal(50, Sandbox.Total(db));
        }
        Assert.Equal(2, _s.Analyzer.Calls);
    }

    [Fact] // requirement 4
    public void Unfinished_replay_is_not_counted_until_fortnite_finalizes_it()
    {
        using var db = _s.OpenDb();
        var tracker = _s.Tracker(db);
        var match = new FakeReplay { Chests = 33, Live = true, KeyMissing = true };
        match.Write(_s.Demos, "live", TimeSpan.FromSeconds(5));

        Assert.Equal(0, tracker.ScanOnce().Total);
        Assert.Equal(PendingReason.Recording, Assert.Single(tracker.Status.Pending).Reason);
        Assert.Equal(0, Sandbox.Total(db));
        Assert.Equal(0, _s.Analyzer.Calls);

        // Fortnite stops recording: live flag cleared, key written. Still settling on the first look.
        (match with { Live = false, KeyMissing = false }).Write(_s.Demos, "live", TimeSpan.FromSeconds(6));
        Assert.Equal(0, tracker.ScanOnce().Total);
        Assert.Equal(PendingReason.Settling, Assert.Single(tracker.Status.Pending).Reason);

        // Same size on the next look and quiet for a few seconds: read.
        Assert.Equal(1, tracker.ScanOnce().Counted);
        Assert.Equal(33, Sandbox.Total(db));
        Assert.Empty(tracker.Status.Pending);
    }

    [Fact] // requirement 4: a recording cut by a crash never gets a key
    public void Abandoned_recording_is_marked_incomplete_not_counted()
    {
        using var db = _s.OpenDb();
        new FakeReplay { Chests = 20, Live = true, KeyMissing = true }.Write(_s.Demos, "crashed", TimeSpan.FromHours(2));

        var summary = _s.Tracker(db).ScanOnce();

        Assert.Equal(1, summary.Incomplete);
        Assert.Equal(0, Sandbox.Total(db));
        Assert.Equal(FileStatus.Incomplete, Assert.Single(db.Files()).Status);
    }

    [Fact] // requirement 5
    public void Renamed_or_copied_replay_is_not_counted_twice()
    {
        using var db = _s.OpenDb();
        var tracker = _s.Tracker(db);
        var original = new FakeReplay { Chests = 27 }.Write(_s.Demos, "UnsavedReplay-2026.11.01-20.00.00");
        tracker.ScanOnce(ignoreQuietPeriod: true);

        File.Copy(original, Path.Combine(_s.Demos, "copy of my best game.replay"));
        File.Move(original, Path.Combine(_s.Demos, "renamed.replay"));
        var summary = tracker.ScanOnce(ignoreQuietPeriod: true);

        Assert.Equal(2, summary.Duplicates);
        Assert.Equal(27, Sandbox.Total(db));
        Assert.Single(db.Matches(new LocalDb.MatchFilter()));
        Assert.Equal(1, _s.Analyzer.Calls); // same bytes: recognised without decoding again
    }

    [Fact] // requirement 5: different bytes, same game session
    public void Second_recording_of_the_same_match_does_not_add_chests()
    {
        using var db = _s.OpenDb();
        var tracker = _s.Tracker(db);
        var match = new FakeReplay { SessionId = "session-1", Chests = 27 };
        match.Write(_s.Demos, "first");
        tracker.ScanOnce(ignoreQuietPeriod: true);

        (match with { Chests = 27, Padding = "x" }).Write(_s.Demos, "a");
        tracker.ScanOnce(ignoreQuietPeriod: true);

        Assert.Equal(27, Sandbox.Total(db));
        Assert.Single(db.Matches(new LocalDb.MatchFilter()));
    }

    [Fact] // requirement 5: a replay without a session id is identified by its content, not its name
    public void Replay_without_session_id_survives_a_rename()
    {
        using var db = _s.OpenDb();
        var tracker = _s.Tracker(db);
        var path = new FakeReplay { SessionId = null, Chests = 14 }.Write(_s.Demos, "nameless");
        tracker.ScanOnce(ignoreQuietPeriod: true);
        File.Move(path, Path.Combine(_s.Demos, "moved.replay"));
        db.ForgetFiles(); // even when forced to decode it again
        tracker.ScanOnce(ignoreQuietPeriod: true);

        var match = Assert.Single(db.Matches(new LocalDb.MatchFilter()));
        Assert.False(match.IdentityReliable);
        Assert.Equal(14, Sandbox.Total(db));
    }

    [Fact] // requirement 10
    public void Reanalysis_with_a_newer_parser_replaces_the_match()
    {
        new FakeReplay { SessionId = "m1", Chests = 40 }.Write(_s.Demos, "a");
        new FakeReplay { SessionId = "m2", Chests = 10 }.Write(_s.Demos, "b");
        using var db = _s.OpenDb();
        var tracker = _s.Tracker(db);
        tracker.ScanOnce(ignoreQuietPeriod: true);
        var firstAnalyzed = db.GetMatch("m1")!.FirstAnalyzedUtc;

        // The new parser finds two more chests in every match.
        _s.Analyzer.Version = 2;
        _s.Analyzer.Recount = (r, v) => r.Chests + 2;
        _s.Clock.UtcNow = _s.Clock.UtcNow.AddDays(1);
        Assert.Equal(2, tracker.RequeueOutdated());
        var summary = tracker.ScanOnce(ignoreQuietPeriod: true);

        Assert.Equal(2, summary.Replaced);
        Assert.Equal(54, Sandbox.Total(db)); // replaced, not added: 42 + 12
        Assert.Equal(2, db.GetMatch("m1")!.ParserVersion);
        Assert.Equal(firstAnalyzed, db.GetMatch("m1")!.FirstAnalyzedUtc);
        Assert.Equal(0, tracker.RequeueOutdated());
    }

    [Fact] // requirement 10 + "never silently discard"
    public void Reanalysis_keeps_matches_whose_replay_was_deleted_by_fortnite()
    {
        var gone = new FakeReplay { SessionId = "old", Chests = 25 }.Write(_s.Demos, "old");
        new FakeReplay { SessionId = "kept", Chests = 5 }.Write(_s.Demos, "kept");
        using var db = _s.OpenDb();
        var tracker = _s.Tracker(db);
        tracker.ScanOnce(ignoreQuietPeriod: true);

        File.Delete(gone);
        _s.Analyzer.Version = 2;
        tracker.RequeueOutdated();
        tracker.ScanOnce(ignoreQuietPeriod: true);

        Assert.Equal(30, Sandbox.Total(db));
        Assert.Equal(1, db.GetMatch("old")!.ParserVersion); // still the old analysis, still counted
        Assert.Equal(2, db.GetMatch("kept")!.ParserVersion);
    }

    [Fact] // requirement 11
    public void Invalid_and_unsupported_replays_do_not_corrupt_history()
    {
        new FakeReplay { Chests = 20 }.Write(_s.Demos, "good");
        new FakeReplay { Chests = 99, Corrupt = true }.Write(_s.Demos, "corrupt");
        new FakeReplay { Chests = 50, Playlist = "Playlist_Figment_Squad" }.Write(_s.Demos, "og-mode");
        new FakeReplay { Chests = 50, Custom = true }.Write(_s.Demos, "custom");
        File.WriteAllBytes(Path.Combine(_s.Demos, "garbage.replay"), new byte[] { 1, 2, 3, 4, 5 });
        File.SetLastWriteTimeUtc(Path.Combine(_s.Demos, "garbage.replay"), DateTime.UtcNow.AddHours(-1));
        using var db = _s.OpenDb();
        var tracker = _s.Tracker(db);

        tracker.ScanOnce(ignoreQuietPeriod: true);
        tracker.ScanOnce(ignoreQuietPeriod: true); // the unreadable header needs a second look before it is given up

        Assert.Equal(20, Sandbox.Total(db));
        var counts = db.FileStatusCounts();
        Assert.Equal(1, counts[FileStatus.Counted]);
        Assert.Equal(2, counts[FileStatus.NotCounted]);
        Assert.Equal(2, counts[FileStatus.Error]);
        // The unsupported matches are kept, visibly not counted, with the reason.
        var notCounted = db.Matches(new LocalDb.MatchFilter(Counted: false));
        Assert.Equal(new[] { ModeRules.ReasonCustom, ModeRules.ReasonMode }, notCounted.Select(m => m.NotCountedReason).Order().ToArray());
        Assert.Contains(db.Logs(), l => l.Level == "error" && l.Message.Contains("corrupt.replay"));

        // And nothing changes on the next scans.
        Assert.Equal(0, tracker.ScanOnce(ignoreQuietPeriod: true).Total);
        Assert.Equal(20, Sandbox.Total(db));
    }

    [Fact] // requirement 11, with the real parser: honest about what is tested
    public void Real_analyzer_rejects_a_file_that_is_not_a_replay()
    {
        var path = Path.Combine(_s.Demos, "not-a-replay.replay");
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        using var db = _s.OpenDb();
        var tracker = new ReplayTracker(db, new FortniteReplayAnalyzer(), new FortniteReplayProbe(), _s.Clock, () => _s.Demos);

        tracker.ScanOnce(ignoreQuietPeriod: true);
        tracker.ScanOnce(ignoreQuietPeriod: true);

        Assert.Equal(FileStatus.Error, Assert.Single(db.Files()).Status);
        Assert.Empty(db.Matches(new LocalDb.MatchFilter(Counted: null)));
    }

    [Fact]
    public void Matches_of_two_accounts_are_kept_in_separate_profiles()
    {
        new FakeReplay { PlayerId = "aaaa", PlayerName = "Main", Chests = 30 }.Write(_s.Demos, "a");
        new FakeReplay { PlayerId = "bbbb", PlayerName = "Alt", Chests = 7 }.Write(_s.Demos, "b");
        using var engine = _s.Engine();
        engine.Begin();
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);

        Assert.Equal(2, engine.Db.Profiles().Count);
        Assert.Equal(30, engine.GetTotals("aaaa").Chests);
        Assert.Equal(7, engine.GetTotals("bbbb").Chests);
    }

    [Fact]
    public void Session_total_only_counts_matches_played_during_this_tracking_session()
    {
        using var engine = _s.Engine(clock: new SystemClock());
        new FakeReplay { Chests = 100, StartUtc = DateTime.UtcNow.AddDays(-3) }.Write(_s.Demos, "history");
        engine.Begin();
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);
        Assert.Equal(0, engine.GetState().Totals.Session.Chests);

        new FakeReplay { Chests = 17, StartUtc = DateTime.UtcNow.AddSeconds(30), Duration = 900 }.Write(_s.Demos, "tonight");
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);

        var totals = engine.GetState().Totals;
        Assert.Equal(117, totals.Chests);
        Assert.Equal(17, totals.Session.Chests);
        Assert.Equal(17, totals.LastMatch!.Chests);
        Assert.Equal(100, totals.Best!.Chests);
        Assert.Equal(58.5, totals.Average);
    }

    [Fact] // requirement 3: the real background loop, driven by the file system
    public async Task Newly_completed_replay_is_processed_automatically()
    {
        new FakeReplay { Chests = 5 }.Write(_s.Demos, "history");
        using var engine = _s.Engine(clock: new SystemClock());
        engine.Start();
        await Wait.Until(() => engine.GetState().Totals.Chests == 5, "the initial import");

        // A match ends while the app is running: Fortnite writes the finalized replay.
        new FakeReplay { Chests = 21 }.Write(_s.Demos, "UnsavedReplay-new", TimeSpan.Zero);

        await Wait.Until(() => engine.GetState().Totals.Chests == 26, "the new replay to be counted on its own", 30);
        Assert.Equal(2, engine.GetState().Totals.Matches);
    }

    [Fact]
    public void Timeline_fills_days_without_a_match_and_buckets_by_local_day()
    {
        new FakeReplay { Chests = 10, StartUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc) }.Write(_s.Demos, "a");
        new FakeReplay { Chests = 5, StartUtc = new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc) }.Write(_s.Demos, "b");
        new FakeReplay { Chests = 7, StartUtc = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc) }.Write(_s.Demos, "c");
        using var engine = _s.Engine();
        engine.Begin();
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);

        var days = engine.GetTimeline(null, null, "day", TimeZoneInfo.Utc);

        Assert.Equal(new[] { "2026-11-01", "2026-11-02", "2026-11-03", "2026-11-04" }, days.Select(d => d.Date).ToArray());
        Assert.Equal(new[] { 15, 0, 0, 7 }, days.Select(d => d.Chests).ToArray());
        Assert.Equal(22, engine.GetTimeline(null, null, "week", TimeZoneInfo.Utc).Sum(w => w.Chests));
    }

    [Fact]
    public void Backup_is_a_complete_readable_copy()
    {
        new FakeReplay { Chests = 12 }.Write(_s.Demos, "a");
        using var engine = _s.Engine();
        engine.Begin();
        engine.Tracker.ScanOnce(ignoreQuietPeriod: true);

        var backup = engine.Backup();
        var csv = engine.ExportCsv();

        using var copy = new LocalDb(backup);
        Assert.Equal(12, Sandbox.Total(copy));
        Assert.Equal(2, File.ReadAllLines(csv).Length);
    }
}
