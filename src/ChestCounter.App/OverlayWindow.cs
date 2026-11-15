using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ChestCounter.Engine;

namespace ChestCounter.App;

/// <summary>
/// The in-game counter: a separate always-on-top, per-pixel-alpha window (UpdateLayeredWindow, drawn with GDI+).
/// It never touches the game process: it is an ordinary desktop window, so it shows over Fortnite in windowed and
/// borderless modes only. Unlocked: drag to move, wheel to resize, right-click for a menu. Locked: the mouse goes
/// through it. Same look as the OBS page (gold = personal, cyan = squad, pink = session).
/// </summary>
internal sealed class OverlayWindow : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const int GWL_EXSTYLE = -20;
    private const int WM_NCHITTEST = 0x84, WM_NCRBUTTONUP = 0xA5, WM_MOUSEACTIVATE = 0x21, WM_EXITSIZEMOVE = 0x232, WM_MOUSEWHEEL = 0x20A,
        WM_DPICHANGED = 0x2E0, WM_DISPLAYCHANGE = 0x7E;
    private const int HTCAPTION = 2, MA_NOACTIVATE = 3;

    private static readonly Color Gold = Color.FromArgb(0xF3, 0xC9, 0x6B), Cyan = Color.FromArgb(0x6A, 0xD9, 0xEA), Pink = Color.FromArgb(0xF5, 0x93, 0xC9),
        TextColor = Color.FromArgb(0xF1, 0xEE, 0xFC), Label = Color.FromArgb(0xA9, 0xA3, 0xCB), MemberText = Color.FromArgb(0xCF, 0xC9, 0xEA),
        Violet = Color.FromArgb(0x9D, 0x8C, 0xFF);

    private OverlayData? _data;
    private string _language = "en";
    private bool _locked;
    private bool _visible;
    private Point? _position;

    /// <summary>The player moved it (screen coordinates of its top-left corner).</summary>
    public event Action<int, int>? Moved;
    /// <summary>Wheel on it: +1 bigger, -1 smaller.</summary>
    public event Action<int>? Resize;
    /// <summary>Right-click on it, at this screen position.</summary>
    public event Action<Point>? MenuRequested;

    public OverlayWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Chest Counter overlay",
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = 0, Y = 0, Width = 10, Height = 10,
        });
    }

    /// <summary>Shows what <paramref name="data"/> says (its settings included: on/off, lock, position, size, opacity).</summary>
    public void Show(OverlayData data, string language)
    {
        _data = data;
        _language = language;
        var s = data.Settings;
        if (s.Locked != _locked || !_visible)
        {
            _locked = s.Locked;
            var ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, _locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
        }
        _position = s.X is { } x && s.Y is { } y ? new Point(x, y) : null;
        var wanted = s.Enabled && HasContent(data);
        Render();
        if (wanted != _visible)
        {
            _visible = wanted;
            ShowWindow(Handle, wanted ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);
        }
    }

    private static bool HasContent(OverlayData d) =>
        d.Settings.ShowPersonal || d.Settings.ShowSession || d.Settings.ShowLastMatch || (d.Settings.ShowSquad && d.Squad != null) || (d.Settings.ShowMembers && d.Members.Count > 0);

    private string T(string en, string fr) => Lang.Of(_language, en, fr);

    private sealed record Cell(string Label, string Value, Color Color);

    private List<Cell> Cells(OverlayData d)
    {
        static string N(int? v) => v?.ToString("N0") ?? "–";
        var s = d.Settings;
        var cells = new List<Cell>();
        if (s.ShowPersonal) cells.Add(new(T("Chests", "Coffres"), N(d.Personal), Gold));
        if (s.ShowSquad && d.Squad != null) cells.Add(new(d.SquadScope == "shared" ? T("Squad together", "Escouade ensemble") : T("Squad", "Escouade"), N(d.Squad), Cyan));
        if (s.ShowSession) cells.Add(new(T("Session", "Session"), N(d.Session), Pink));
        // "+" = some teammates have not uploaded this match yet: the figure can still grow.
        if (s.ShowLastMatch) cells.Add(new(T("Last match", "Dernière partie"), N(d.LastMatch) + (d.LastMatchPartial ? "+" : ""), TextColor));
        return cells;
    }

    private static FontFamily Family { get; } = PickFamily();

    private static FontFamily PickFamily()
    {
        foreach (var name in new[] { "Segoe UI Variable Display", "Segoe UI" })
        {
            try { return new FontFamily(name); } catch (ArgumentException) { /* not installed */ }
        }
        return FontFamily.GenericSansSerif;
    }

    private float Scale()
    {
        var dpi = 96;
        try { dpi = (int)GetDpiForWindow(Handle); } catch { /* old Windows */ }
        return dpi / 96f * Math.Clamp(_data?.Settings.Scale ?? 100, 60, 250) / 100f;
    }

    /// <summary>Draws the whole overlay into a bitmap and hands it to Windows (no WM_PAINT for layered windows).</summary>
    private void Render()
    {
        if (_data == null) return;
        var k = Scale();
        float S(float v) => v * k;
        var d = _data;
        var cells = Cells(d);
        var members = d.Settings.ShowMembers ? d.Members : new List<OverlayMember>();

        using var labelFont = new Font(Family, S(11), FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font(Family, S(26), FontStyle.Bold, GraphicsUnit.Pixel);
        using var memberFont = new Font(Family, S(13), FontStyle.Regular, GraphicsUnit.Pixel);
        using var memberValueFont = new Font(Family, S(13), FontStyle.Bold, GraphicsUnit.Pixel);
        var format = StringFormat.GenericTypographic;

        // Measure.
        using var probe = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(probe);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        SizeF M(string s, Font f) => mg.MeasureString(s, f, int.MaxValue, format);

        float padX = S(16), padY = S(10), gap = S(18), rowGap = S(8);
        var widths = cells.Select(c => Math.Max(S(54), Math.Max(M(c.Label, labelFont).Width, M(c.Value, valueFont).Width))).ToList();
        var labelH = labelFont.GetHeight(mg);
        var valueH = valueFont.GetHeight(mg);
        var rowW = cells.Count == 0 ? 0 : padX * 2 + widths.Sum() + gap * (cells.Count - 1);
        var rowH = cells.Count == 0 ? 0 : padY * 2 + labelH + S(2) + valueH;

        var memberParts = members.Select(m => (Name: m.Name, Value: m.Chests.ToString("N0"), Online: m.Online)).ToList();
        var memberW = memberParts.Count == 0 ? 0 : padX * 2 + memberParts.Sum(p => M(p.Name, memberFont).Width + S(5) + M(p.Value, memberValueFont).Width) + S(14) * (memberParts.Count - 1);
        var memberH = memberParts.Count == 0 ? 0 : S(6) * 2 + memberFont.GetHeight(mg);

        var margin = S(2); // room for the unlocked outline
        var width = (int)Math.Ceiling(Math.Max(Math.Max(rowW, memberW), S(60)) + margin * 2);
        var height = (int)Math.Ceiling(rowH + (rowH > 0 && memberH > 0 ? rowGap : 0) + memberH + margin * 2);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType needs an opaque background
            var y = margin;
            if (cells.Count > 0)
            {
                var box = new RectangleF(margin, y, rowW, rowH);
                Card(g, box, S(14), Color.FromArgb(184, 16, 13, 34));
                var x = margin + padX;
                using var labelBrush = new SolidBrush(Label);
                for (var i = 0; i < cells.Count; i++)
                {
                    g.DrawString(cells[i].Label, labelFont, labelBrush, x, y + padY, format);
                    using var valueBrush = new SolidBrush(cells[i].Color);
                    g.DrawString(cells[i].Value, valueFont, valueBrush, x - S(1), y + padY + labelH + S(2), format);
                    x += widths[i] + gap;
                }
                y += rowH + rowGap;
            }
            if (memberParts.Count > 0)
            {
                var box = new RectangleF(margin, y, memberW, memberH);
                Card(g, box, S(12), Color.FromArgb(153, 16, 13, 34));
                var x = margin + padX;
                using var nameBrush = new SolidBrush(MemberText);
                using var offlineBrush = new SolidBrush(Label);
                using var valueBrush = new SolidBrush(Cyan);
                foreach (var p in memberParts)
                {
                    g.DrawString(p.Name, memberFont, p.Online ? nameBrush : offlineBrush, x, y + S(6), format);
                    x += M(p.Name, memberFont).Width + S(5);
                    g.DrawString(p.Value, memberValueFont, valueBrush, x, y + S(6), format);
                    x += M(p.Value, memberValueFont).Width + S(14);
                }
            }
            if (!_locked)
            {
                // Unlocked: a violet outline says "you can grab me" (violet = interactive, as in the app).
                using var pen = new Pen(Color.FromArgb(220, Violet), Math.Max(1f, S(1.5f)));
                using var path = Rounded(new RectangleF(margin / 2, margin / 2, width - margin, height - margin), S(15));
                g.DrawPath(pen, path);
            }
        }

        var pos = Place(width, height);
        Push(bmp, pos, (byte)Math.Round(Math.Clamp(d.Settings.Opacity, 20, 100) * 255 / 100.0));
    }

    private static void Card(Graphics g, RectangleF box, float radius, Color fill)
    {
        using var path = Rounded(box, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        using var edge = new Pen(Color.FromArgb(20, 255, 255, 255), 1f);
        g.DrawPath(edge, path);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var dia = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, dia, dia, 180, 90);
        p.AddArc(r.Right - dia, r.Y, dia, dia, 270, 90);
        p.AddArc(r.Right - dia, r.Bottom - dia, dia, dia, 0, 90);
        p.AddArc(r.X, r.Bottom - dia, dia, dia, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The saved position when it is on a screen, else the top-left corner of the main screen.</summary>
    private Point Place(int width, int height)
    {
        if (_position is { } p)
        {
            var rect = new Rectangle(p, new Size(width, height));
            var screen = Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.IntersectsWith(rect));
            if (screen != null)
            {
                var w = screen.WorkingArea;
                return new Point(Math.Clamp(p.X, w.Left, Math.Max(w.Left, w.Right - width)), Math.Clamp(p.Y, w.Top, Math.Max(w.Top, w.Bottom - height)));
            }
        }
        var main = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Point(main.Left + 24, main.Top + 24);
    }

    private void Push(Bitmap bmp, Point pos, byte opacity)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(pos.X, pos.Y);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /* NOSIZE|NOMOVE|NOACTIVATE */);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                m.Result = (IntPtr)HTCAPTION; // the whole overlay is a handle to drag it (never reached when locked)
                return;
            case WM_MOUSEACTIVATE:
                m.Result = (IntPtr)MA_NOACTIVATE; // never take the focus from the game
                return;
            case WM_EXITSIZEMOVE:
                if (GetWindowRect(Handle, out var r))
                {
                    _position = new Point(r.Left, r.Top);
                    Moved?.Invoke(r.Left, r.Top);
                }
                return;
            case WM_MOUSEWHEEL:
                var delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                if (!_locked && delta != 0) Resize?.Invoke(delta > 0 ? 1 : -1);
                return;
            case WM_NCRBUTTONUP:
                MenuRequested?.Invoke(Cursor.Position);
                return;
            case WM_DPICHANGED or WM_DISPLAYCHANGE:
                base.WndProc(ref m);
                Render();
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    // ---- Win32 ----
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Cx, Cy; public SIZE(int cx, int cy) { Cx = cx; Cy = cy; } }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
