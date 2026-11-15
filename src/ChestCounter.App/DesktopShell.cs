using System.Diagnostics;
using ChestCounter.Engine;
using ChestCounter.LocalApi;

namespace ChestCounter.App;

/// <summary>What the interface asks of Windows: a folder picker, Explorer. Runs dialogs on the window's thread.</summary>
internal sealed class DesktopShell : IShell
{
    private readonly ChestEngine _engine;
    private Form? _owner;

    public DesktopShell(ChestEngine engine, string dataDir, string instanceKey)
    {
        _engine = engine;
        DataDir = dataDir;
        InstanceKey = instanceKey;
    }

    public string DataDir { get; }
    public string InstanceKey { get; }

    /// <summary>The main window: dialogs are owned by it and run on its thread.</summary>
    public void Attach(Form owner) => _owner = owner;

    public Task<string?> PickFolderAsync(string? start)
    {
        var owner = _owner;
        if (owner == null || owner.IsDisposed || !owner.IsHandleCreated)
        {
            return Task.FromResult<string?>(null);
        }
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(() =>
        {
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = Lang.Of(_engine.Settings.Language, "Folder where Fortnite saves its replays", "Dossier où Fortnite enregistre ses replays"),
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                    InitialDirectory = start != null && Directory.Exists(start) ? start : "",
                };
                done.TrySetResult(dialog.ShowDialog(owner.Visible ? owner : null) == DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public void OpenPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(path);
        try { Process.Start(start)?.Dispose(); } catch (Exception ex) { _engine.Db.Log("warn", "shell", $"could not open {path}: {ex.Message}", DateTime.UtcNow); }
    }

    // Settings are re-applied by TrayContext on the engine's "settings" change, which every settings write raises
    // (including the overlay's own drag/resize/hotkeys, which do not go through the HTTP API).
    public void SettingsChanged() { }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
        {
            try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose(); } catch { /* no browser */ }
        }
    }
}

/// <summary>The two languages of the interface, for the few strings the shell draws itself.</summary>
internal static class Lang
{
    public static string Of(string language, string en, string fr) => language == "fr" ? fr : en;
}
