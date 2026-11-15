using System.Diagnostics;
using Microsoft.Win32;

namespace ChestCounter.App;

/// <summary>
/// "Start with Windows" and "start with Fortnite", through the per-user Run key (no admin rights, no scheduled task).
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The installed executable, or null when running through `dotnet` (development): nothing is registered then.</summary>
    public static string? Exe => Environment.ProcessPath is { } p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileName(p).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? p : null;

    private static bool IsDefault(string dataDir) =>
        string.Equals(Path.GetFullPath(dataDir).TrimEnd('\\'), Path.GetFullPath(Program.DefaultDataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string DataArg(string dataDir) => IsDefault(dataDir) ? "" : $" --data \"{Path.GetFullPath(dataDir).TrimEnd('\\')}\"";

    /// <summary>Run value names: one pair per data folder, so a second (test) profile does not overwrite the first.</summary>
    private static string ValueName(string dataDir, string key, string suffix) =>
        "ChestCounterSquad" + suffix + (IsDefault(dataDir) ? "" : "-" + key[^8..]);

    /// <summary>Writes or removes the Run entries and starts or stops the standby. Returns an error message, or null.</summary>
    public static string? Apply(bool withWindows, bool withFortnite, string dataDir, string key)
    {
        var exe = Exe;
        if (exe == null)
        {
            return (withWindows || withFortnite) ? "startup options need the published ChestCounterSquad.exe" : null;
        }
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            Set(run, ValueName(dataDir, key, ""), withWindows ? $"\"{exe}\" --minimized{DataArg(dataDir)}" : null);
            Set(run, ValueName(dataDir, key, "-Standby"), withFortnite ? $"\"{exe}\" --standby{DataArg(dataDir)}" : null);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        if (withFortnite)
        {
            Standby.EnsureRunning(exe, dataDir, key);
        }
        else
        {
            Standby.Stop(key);
        }
        return null;
    }

    private static void Set(RegistryKey run, string name, string? value)
    {
        if (value == null)
        {
            run.DeleteValue(name, throwOnMissingValue: false);
        }
        else if (run.GetValue(name) as string != value)
        {
            run.SetValue(name, value); // refreshed on every start: follows the exe when it is moved
        }
    }
}

/// <summary>
/// The "start with Fortnite" watcher. Windows cannot start a program when another one starts without admin rights,
/// so a tiny windowless process started at logon checks every few seconds whether Fortnite is running, and opens the
/// application when it is and the application is not open. It reads nothing from the game: it only looks at the
/// list of running programs. Idea taken from LED-N/Fortnite-ChestCounter (FortniteWatch.cs), rewritten.
/// </summary>
internal static class Standby
{
    // CC_GAME_PROCESS: another process name, to test the standby without Fortnite.
    private static string GameProcess => Environment.GetEnvironmentVariable("CC_GAME_PROCESS") is { Length: > 0 } test ? test : "FortniteClient-Win64-Shipping";

    private static string MutexName(string key) => @"Local\" + key + "-standby";
    private static string StopName(string key) => @"Local\" + key + "-standby-stop";

    private static bool GameRunning()
    {
        var found = Process.GetProcessesByName(GameProcess);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    public static void EnsureRunning(string exe, string dataDir, string key)
    {
        if (Mutex.TryOpenExisting(MutexName(key), out var running))
        {
            running.Dispose();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--standby{Startup.DataArg(dataDir)}") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch { /* started again at next logon */ }
    }

    public static void Stop(string key)
    {
        if (EventWaitHandle.TryOpenExisting(StopName(key), out var stop))
        {
            stop.Set();
            stop.Dispose();
        }
    }

    public static int Run(string dataDir, string key)
    {
        using var single = new Mutex(false, MutexName(key));
        try
        {
            if (!single.WaitOne(0)) return 0; // already waiting
        }
        catch (AbandonedMutexException)
        {
            // the previous standby was killed: take over
        }
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopName(key));
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { /* best effort */ }

        var exe = Startup.Exe;
        if (exe == null) return 1;

        while (true)
        {
            if (stop.WaitOne(TimeSpan.FromSeconds(5))) return 0;
            if (!GameRunning()) continue;

            if (Mutex.TryOpenExisting(@"Local\" + key, out var open))
            {
                open.Dispose(); // already open
            }
            else
            {
                try { Process.Start(new ProcessStartInfo(exe, $"--minimized{Startup.DataArg(dataDir)}") { UseShellExecute = false })?.Dispose(); } catch { }
            }

            // Arm again only for the next Fortnite launch: the player may have closed the application on purpose.
            while (GameRunning())
            {
                if (stop.WaitOne(TimeSpan.FromSeconds(10))) return 0;
            }
        }
    }
}
