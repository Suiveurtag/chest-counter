using System.Text.Json;
using ChestCounter.Engine;
using ChestCounter.LocalApi;
using ChestCounter.Shared;

namespace ChestCounter.App;

/// <summary>
/// Owns everything the desktop shows: main window, tray icon, overlay, shortcuts, startup entries. Everything here
/// runs on the UI thread; engine events (raised on worker threads) are marshalled onto it and coalesced.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int ToggleHotkeyId = 1, LockHotkeyId = 2;

    private readonly ChestEngine _engine;
    private readonly DesktopShell _shell;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly OverlayWindow _overlay;
    private readonly Hotkeys _hotkeys;
    private readonly EventWaitHandle _showSignal;
    private readonly RegisteredWaitHandle _showWait;
    private readonly Icon _icon;
    private int _refreshQueued;
    private volatile bool _settingsDirty = true;
    private (bool Windows, bool Fortnite)? _startupApplied;
    private string? _hotkeyProblem;

    public TrayContext(ChestEngine engine, LocalServer server, DesktopShell shell, bool startMinimized)
    {
        _engine = engine;
        _shell = shell;
        using (var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("app.ico"))
        {
            _icon = stream != null ? new Icon(stream) : SystemIcons.Application;
        }

        _form = new MainForm(engine, server, shell.DataDir, _icon);
        _ = _form.Handle; // created now so that other threads can marshal onto it even while hidden
        _form.QuitRequested += Quit;
        // Closed by Windows (logoff, shutdown, task manager): leave the message loop so the engine shuts down cleanly.
        _form.FormClosed += (_, _) => ExitThread();
        _form.HiddenToTray += HintTrayOnce;
        shell.Attach(_form);

        _overlay = new OverlayWindow();
        _overlay.Moved += (x, y) => SetOverlay(o => o with { X = x, Y = y });
        _overlay.Resize += step => SetOverlay(o => o with { Scale = Math.Clamp(o.Scale + step * 10, 60, 250) });
        _overlay.MenuRequested += at => _tray!.ContextMenuStrip!.Show(at);

        _hotkeys = new Hotkeys();

        _tray = new NotifyIcon { Icon = _icon, Text = "Chest Counter Squad", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _form.ShowAndActivate(); };

        // A second launch (desktop shortcut, the standby) only asks this one to show itself.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + shell.InstanceKey + "-show");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Post(() => _form.ShowAndActivate()), null, Timeout.Infinite, false);

        engine.Changed += OnEngineChanged;
        Refresh();

        if (!startMinimized)
        {
            _form.Show();
        }
    }

    private void Post(Action action)
    {
        if (_form.IsDisposed || !_form.IsHandleCreated) return;
        try { _form.BeginInvoke(action); } catch (InvalidOperationException) { /* closing */ }
    }

    private void OnEngineChanged(string topic)
    {
        if (topic == "settings") _settingsDirty = true;
        // Coalesce bursts (an import of many replays raises one event per file).
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            Post(Refresh);
        }
    }

    private void Refresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        try
        {
            var settings = _engine.Settings;
            if (_settingsDirty)
            {
                _settingsDirty = false;
                ApplySettings(settings);
            }
            _overlay.Show(_engine.GetOverlay(), settings.Language);
        }
        catch (Exception ex)
        {
            _engine.Db.Log("error", "window", "overlay refresh: " + ex.Message, DateTime.UtcNow);
        }
    }

    /// <summary>What lives outside the engine: shortcuts and Windows startup entries.</summary>
    private void ApplySettings(AppSettings s)
    {
        var o = s.Overlay;
        var problems = new List<string>();
        if (!_hotkeys.Set(ToggleHotkeyId, o.ToggleHotkey, () => SetOverlay(x => x with { Enabled = !x.Enabled }))) problems.Add(o.ToggleHotkey);
        if (!_hotkeys.Set(LockHotkeyId, o.LockHotkey, () => SetOverlay(x => x with { Locked = !x.Locked }))) problems.Add(o.LockHotkey);
        var problem = problems.Count == 0 ? null : string.Join(", ", problems);
        if (problem != null && problem != _hotkeyProblem)
        {
            _engine.Db.Log("warn", "overlay", $"shortcut not available (used by another program, or not a valid combination): {problem}", DateTime.UtcNow);
        }
        _hotkeyProblem = problem;

        // Only when these two changed: the overlay sliders write settings many times a second.
        if (_startupApplied != (s.LaunchWithWindows, s.LaunchWithFortnite))
        {
            _startupApplied = (s.LaunchWithWindows, s.LaunchWithFortnite);
            if (Startup.Apply(s.LaunchWithWindows, s.LaunchWithFortnite, _shell.DataDir, _shell.InstanceKey) is { } error)
            {
                _engine.Db.Log("warn", "startup", error, DateTime.UtcNow);
            }
        }
    }

    private void SetOverlay(Func<OverlaySettings, OverlaySettings> change)
    {
        var next = change(_engine.Overlay);
        _engine.UpdateSettings(new() { ["overlay"] = JsonSerializer.SerializeToElement(next, Wire.Json) });
    }

    private void BuildMenu()
    {
        var lang = _engine.Settings.Language;
        var o = _engine.Overlay;
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        var open = new ToolStripMenuItem(Lang.Of(lang, "Open Chest Counter", "Ouvrir Chest Counter"), null, (_, _) => _form.ShowAndActivate()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Lang.Of(lang, "Show the overlay", "Afficher l'overlay"), null, (_, _) => SetOverlay(x => x with { Enabled = !x.Enabled }))
            { Checked = o.Enabled, ShortcutKeyDisplayString = o.ToggleHotkey });
        menu.Items.Add(new ToolStripMenuItem(Lang.Of(lang, "Lock the overlay (click-through)", "Verrouiller l'overlay (clic au travers)"), null, (_, _) => SetOverlay(x => x with { Locked = !x.Locked }))
            { Checked = o.Locked, Enabled = o.Enabled, ShortcutKeyDisplayString = o.LockHotkey });
        menu.Items.Add(new ToolStripMenuItem(Lang.Of(lang, "Scan for new replays", "Chercher de nouveaux replays"), null, (_, _) => _engine.ScanNow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Lang.Of(lang, "Quit", "Quitter"), null, (_, _) => Quit()));
    }

    private void HintTrayOnce()
    {
        if (_engine.Db.GetSetting("trayHintShown") == "1") return;
        _engine.Db.SetSetting("trayHintShown", "1");
        var lang = _engine.Settings.Language;
        _tray.ShowBalloonTip(5000, "Chest Counter Squad",
            Lang.Of(lang, "Still counting in the background. Right-click the tray icon to quit.", "Le comptage continue en arrière-plan. Clic droit sur l'icône pour quitter."),
            ToolTipIcon.Info);
    }

    private void Quit()
    {
        _form.Quitting = true;
        _form.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Changed -= OnEngineChanged;
            _showWait.Unregister(null);
            _showSignal.Dispose();
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _hotkeys.Dispose();
            _overlay.Dispose();
            _form.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
