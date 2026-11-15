using System.Globalization;
using System.Runtime.InteropServices;
using ChestCounter.Engine;
using ChestCounter.LocalApi;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChestCounter.App;

/// <summary>
/// The main window: the React interface in WebView2, pointed at the loopback server. The access token is handed to
/// the page by script injection (only on that origin), never put in the address.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Night = Color.FromArgb(0x0E, 0x0B, 0x1D);

    private readonly ChestEngine _engine;
    private readonly LocalServer _server;
    private readonly string _dataDir;
    private WebView2? _web;
    private bool _webStarted;

    /// <summary>True once the application is quitting: closing the window really closes it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Quitting { get; set; }

    /// <summary>Closing the window means quitting (tray mode off).</summary>
    public event Action? QuitRequested;
    /// <summary>The window was hidden to the tray.</summary>
    public event Action? HiddenToTray;

    public MainForm(ChestEngine engine, LocalServer server, string dataDir, Icon icon)
    {
        _engine = engine;
        _server = server;
        _dataDir = dataDir;
        Text = "Chest Counter Squad";
        Icon = icon;
        BackColor = Night;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 820);
        LoadBounds();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var on = 1;
            DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            var caption = Night.R | (Night.G << 8) | (Night.B << 16);
            DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR, Windows 11 */, ref caption, sizeof(int));
        }
        catch { /* older Windows: default title bar */ }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartWeb();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) StartWeb();
    }

    /// <summary>Created on first show only: starting minimized to the tray costs no browser process.</summary>
    private async void StartWeb()
    {
        if (_webStarted) return;
        _webStarted = true;
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Night };
        Controls.Add(_web);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDir, "webview"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            var origin = new Uri(_server.Url).GetLeftPart(UriPartial.Authority);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"if (location.origin === '{origin}') {{ window.__CC_TOKEN = '{_server.Token}'; }}");
            core.NavigationStarting += (_, a) =>
            {
                if (!IsOurs(a.Uri))
                {
                    a.Cancel = true;
                    DesktopShell.OpenUrl(a.Uri);
                }
            };
            core.NewWindowRequested += (_, a) =>
            {
                a.Handled = true;
                DesktopShell.OpenUrl(a.Uri); // links meant for a new tab (server docs, invite pages) go to the browser
            };
            core.Navigate(_server.Url);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFallback(Lang.Of(_engine.Settings.Language,
                "The Microsoft Edge WebView2 Runtime is missing. Install it, or use the interface in your browser.",
                "Le runtime Microsoft Edge WebView2 est absent. Installe-le, ou utilise l'interface dans ton navigateur."));
        }
        catch (Exception ex)
        {
            _engine.Db.Log("error", "window", "WebView2: " + ex.Message, DateTime.UtcNow);
            ShowFallback(ex.Message);
        }
    }

    private bool IsOurs(string uri) => uri.StartsWith(_server.Url, StringComparison.OrdinalIgnoreCase) || uri == "about:blank";

    /// <summary>No WebView2: a plain panel that opens the same interface in the default browser (token in the address).</summary>
    private void ShowFallback(string message)
    {
        if (_web != null)
        {
            Controls.Remove(_web);
            _web.Dispose();
            _web = null;
        }
        var lang = _engine.Settings.Language;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(40), BackColor = Night };
        panel.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Color.FromArgb(0xEE, 0xEA, 0xFB), Font = new Font("Segoe UI", 12f), Margin = new Padding(0, 0, 0, 16) });
        var open = new Button { Text = Lang.Of(lang, "Open in my browser", "Ouvrir dans mon navigateur"), AutoSize = true, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => DesktopShell.OpenUrl($"{_server.Url}?token={_server.Token}");
        var get = new Button { Text = Lang.Of(lang, "Get WebView2", "Obtenir WebView2"), AutoSize = true, FlatStyle = FlatStyle.System };
        get.Click += (_, _) => DesktopShell.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
        panel.Controls.Add(open);
        panel.Controls.Add(get);
        Controls.Add(panel);
    }

    public void ShowAndActivate()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveBounds();
        if (!Quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (_engine.Settings.MinimizeToTray)
            {
                Hide();
                HiddenToTray?.Invoke();
            }
            else
            {
                QuitRequested?.Invoke();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    // ---- remembered size and position ----

    private void LoadBounds()
    {
        var saved = _engine.Db.GetSetting("windowBounds")?.Split(',');
        if (saved is not { Length: 5 }) return;
        var n = saved.Take(4).Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : int.MinValue).ToArray();
        if (n.Any(v => v == int.MinValue)) return;
        var rect = new Rectangle(n[0], n[1], Math.Max(n[2], 900), Math.Max(n[3], 600));
        // Only when the title bar would be on a screen that still exists.
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(rect.X, rect.Y, rect.Width, 40)))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = rect;
        if (saved[4] == "1") WindowState = FormWindowState.Maximized;
    }

    private void SaveBounds()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized || !Visible) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _engine.Db.SetSetting("windowBounds", string.Create(CultureInfo.InvariantCulture,
            $"{b.X},{b.Y},{b.Width},{b.Height},{(WindowState == FormWindowState.Maximized ? 1 : 0)}"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _web?.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
