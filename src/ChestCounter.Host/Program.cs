using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChestCounter.Engine;
using ChestCounter.LocalApi;

namespace ChestCounter.Host;

/// <summary>
/// The engine without a window, run by the Tauri shell (desktop/) as a child process:
///   ChestCounterEngine.exe --data &lt;dir&gt; [--web &lt;wwwroot&gt;]
///
/// It talks to the shell with one JSON object per line.
///   stdout (to the shell):  {"event":"ready","url":"...","token":"...","obsUrl":"..."}
///                           {"event":"changed","topic":"settings"}            (every engine change)
///                           {"event":"pickFolder","id":1,"start":"C:\\..."}    (the interface asked for a folder)
///                           {"event":"fatal","message":"..."}                 (could not start)
///   stdin (from the shell): {"cmd":"pickedFolder","id":1,"path":"C:\\..."|null}
///                           {"cmd":"quit"}
/// When stdin closes (the shell is gone, even killed), the engine shuts down: it never outlives its window.
/// </summary>
internal static class Program
{
    private static readonly object WriteLock = new();

    public static void Send(object message)
    {
        var line = JsonSerializer.Serialize(message);
        lock (WriteLock)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private static int Main(string[] args)
    {
        // Paths may hold any character: the pipe is UTF-8 both ways, whatever the console code page.
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        string? Arg(string name) => args.SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();

        var dataDir = Path.GetFullPath(Arg("--data")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounterSquad"));
        var webRoot = Arg("--web") ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");

        ChestEngine engine;
        try
        {
            engine = new ChestEngine(new EngineOptions { DataDir = dataDir });
        }
        catch (Exception ex)
        {
            Send(new { @event = "fatal", message = $"Could not open the data folder {dataDir}: {ex.Message}" });
            return 1;
        }

        var shell = new PipeShell(engine);
        var server = new LocalServer(engine, shell);
        try
        {
            server.StartAsync(webRoot).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Send(new { @event = "fatal", message = $"The local interface server could not start: {ex.Message}" });
            engine.Dispose();
            return 1;
        }

        engine.Changed += topic => Send(new { @event = "changed", topic });
        engine.Start();
        Send(new { @event = "ready", url = server.Url, token = server.Token, obsUrl = server.ObsUrl });

        // Commands until "quit" or the end of stdin.
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            JsonNode? cmd;
            try { cmd = JsonNode.Parse(line); } catch (JsonException) { continue; }
            switch ((string?)cmd?["cmd"])
            {
                case "pickedFolder":
                    shell.Picked((int?)cmd!["id"] ?? -1, (string?)cmd["path"]);
                    break;
                case "quit":
                    goto done;
            }
        }
        done:
        shell.CancelAll();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        engine.Dispose();
        return 0;
    }
}

/// <summary>What the interface asks of Windows. Dialogs are drawn by the Tauri shell; Explorer is started here.</summary>
internal sealed class PipeShell(ChestEngine engine) : IShell
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _picks = new();
    private int _nextId;

    public Task<string?> PickFolderAsync(string? start)
    {
        var id = Interlocked.Increment(ref _nextId);
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _picks[id] = done;
        Program.Send(new { @event = "pickFolder", id, start = start != null && Directory.Exists(start) ? start : null });
        return done.Task;
    }

    public void Picked(int id, string? path)
    {
        if (_picks.TryRemove(id, out var done)) done.TrySetResult(string.IsNullOrWhiteSpace(path) ? null : path);
    }

    public void CancelAll()
    {
        foreach (var id in _picks.Keys) Picked(id, null);
    }

    public void OpenPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(path);
        try { Process.Start(start)?.Dispose(); }
        catch (Exception ex) { engine.Db.Log("warn", "shell", $"could not open {path}: {ex.Message}", DateTime.UtcNow); }
    }
}
