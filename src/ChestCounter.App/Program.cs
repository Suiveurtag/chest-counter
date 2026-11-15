using System.Security.Cryptography;
using System.Text;
using ChestCounter.Engine;
using ChestCounter.LocalApi;

namespace ChestCounter.App;

/// <summary>
/// Desktop entry point.
///   ChestCounterSquad.exe [--minimized] [--data &lt;dir&gt;]   the application (one per data folder)
///   ChestCounterSquad.exe --standby [--data &lt;dir&gt;]       the invisible "start with Fortnite" watcher
/// </summary>
internal static class Program
{
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounterSquad");

    /// <summary>Names of the per-data-folder kernel objects (mutex, "show yourself" event, standby).</summary>
    public static string InstanceKey(string dataDir)
    {
        var full = Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToUpperInvariant();
        return "ChestCounterSquad-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        var dataDir = Path.GetFullPath(Arg("--data") ?? DefaultDataDir);
        var key = InstanceKey(dataDir);

        if (Has("--standby"))
        {
            return Standby.Run(dataDir, key);
        }

        // Started by the standby, this process inherits its Idle priority class: put it back to normal.
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; } catch { /* best effort */ }

        using var single = new Mutex(true, @"Local\" + key, out var first);
        if (!first)
        {
            // Already open for this data folder: bring that window forward instead of starting a second engine.
            if (EventWaitHandle.TryOpenExisting(@"Local\" + key + "-show", out var show))
            {
                show.Set();
                show.Dispose();
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);

        ChestEngine engine;
        try
        {
            engine = new ChestEngine(new EngineOptions { DataDir = dataDir });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Chest Counter Squad could not open its data folder:\n{dataDir}\n\n{ex.Message}", "Chest Counter Squad",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var shell = new DesktopShell(engine, dataDir, key);
        var server = new LocalServer(engine, shell);
        try
        {
            server.StartAsync(webRoot).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The local interface server could not start.\n\n{ex.Message}", "Chest Counter Squad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            engine.Dispose();
            return 1;
        }

        engine.Start();
        var minimized = Has("--minimized") || engine.Settings.StartMinimized;
        using (var context = new TrayContext(engine, server, shell, minimized))
        {
            Application.Run(context);
        }

        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        engine.Dispose();
        return 0;
    }
}
