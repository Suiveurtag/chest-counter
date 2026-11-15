using System.Runtime.InteropServices;

namespace ChestCounter.App;

/// <summary>Global keyboard shortcuts (RegisterHotKey on a message-only window). They work while the game has focus.</summary>
internal sealed class Hotkeys : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly Dictionary<int, (string Combo, Action Action)> _registered = new();

    public Hotkeys()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    /// <summary>Parses "Ctrl+Shift+O", "Alt+F9"... Null when it is not a usable combination.</summary>
    public static (uint Mods, uint Key)? Parse(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        uint mods = 0;
        Keys? key = null;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "alt": mods |= MOD_ALT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                default:
                    if (key != null) return null;
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<Keys>(name, ignoreCase: true, out var k) || (k & Keys.Modifiers) != 0) return null;
                    key = k;
                    break;
            }
        }
        // A bare letter would steal it from the game: at least one modifier, except for function keys.
        if (key == null || (mods == 0 && key is not (>= Keys.F1 and <= Keys.F24))) return null;
        return (mods, (uint)key.Value);
    }

    /// <summary>Registers (or re-registers when the combination changed). Returns false when Windows refused it.</summary>
    public bool Set(int id, string combo, Action action)
    {
        if (_registered.TryGetValue(id, out var current) && current.Combo == combo)
        {
            _registered[id] = (combo, action);
            return true;
        }
        if (_registered.Remove(id))
        {
            UnregisterHotKey(Handle, id);
        }
        if (Parse(combo) is not { } parsed || !RegisterHotKey(Handle, id, parsed.Mods | MOD_NOREPEAT, parsed.Key))
        {
            return false;
        }
        _registered[id] = (combo, action);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _registered.TryGetValue((int)m.WParam, out var entry))
        {
            entry.Action();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(Handle, id);
        _registered.Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
