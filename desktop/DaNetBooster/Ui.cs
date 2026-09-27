using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DaNetBooster;

/// <summary>Colour roles shared with the phone app. Status colours are for status only, never data series.</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x0B, 0x0E, 0x14);
    public static readonly Color Surface = Color.FromArgb(0x13, 0x18, 0x21);
    public static readonly Color Surface2 = Color.FromArgb(0x1A, 0x20, 0x2B);
    public static readonly Color Surface3 = Color.FromArgb(0x22, 0x29, 0x36);
    public static readonly Color Stroke = Color.FromArgb(0x23, 0x2A, 0x38);
    public static readonly Color Text = Color.FromArgb(0xE6, 0xEA, 0xF2);
    public static readonly Color Text2 = Color.FromArgb(0x8A, 0x93, 0xA6);
    public static readonly Color Text3 = Color.FromArgb(0x5B, 0x64, 0x77);
    public static readonly Color Accent = Color.FromArgb(0x4D, 0xA3, 0xFF);
    public static readonly Color AccentHover = Color.FromArgb(0x6F, 0xB6, 0xFF);
    public static readonly Color Good = Color.FromArgb(0x3D, 0xDC, 0x97);
    public static readonly Color Warn = Color.FromArgb(0xFF, 0xB5, 0x47);
    public static readonly Color Bad = Color.FromArgb(0xFF, 0x5C, 0x6C);

    /// <summary>Good / warn / bad by thresholds (lower is better).</summary>
    public static Color Grade(double v, double ok, double bad) => v <= ok ? Good : v <= bad ? Warn : Bad;

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Graphics Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return g;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void Dwm(IntPtr hwnd, int attr, int value) => DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));
    static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;

    /// <summary>Dark title bar that melts into the window (caption colour needs Windows 11; ignored elsewhere).</summary>
    public static void DarkChrome(IntPtr hwnd)
    {
        Dwm(hwnd, 20, 1);                   // DWMWA_USE_IMMERSIVE_DARK_MODE
        Dwm(hwnd, 35, ColorRef(Bg));        // DWMWA_CAPTION_COLOR
        Dwm(hwnd, 36, ColorRef(Text2));     // DWMWA_TEXT_COLOR
    }

    /// <summary>Windows 11 window border in the verdict colour: readable even when the window is half covered.</summary>
    public static void Border(IntPtr hwnd, Color c) => Dwm(hwnd, 34, ColorRef(c)); // DWMWA_BORDER_COLOR

    static readonly Dictionary<Color, Icon> Icons = [];

    /// <summary>Taskbar / Alt-Tab icon: a dot in the status colour. Cached, so handles are created once per colour.</summary>
    public static Icon Dot(Color c)
    {
        if (Icons.TryGetValue(c, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Smooth(Graphics.FromImage(bmp)))
        {
            using var ring = new Pen(c, 3);
            using var fill = new SolidBrush(c);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            g.FillEllipse(fill, 10, 10, 12, 12);
        }
        return Icons[c] = Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>Real Button (Tab focus, Space/Enter, AcceptButton, Narrator) painted as a pill.</summary>
sealed class PillButton : Button
{
    const int Pad = 4; // room for the focus ring
    bool hover, down;
    public bool Primary { get; set; } = true;

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 10f);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Theme.Smooth(e.Graphics);
        g.Clear(Theme.Bg);
        var r = new RectangleF(Pad, Pad, Width - 2 * Pad - 1, Height - 2 * Pad - 1);
        using var path = Theme.Round(r, r.Height / 2);

        Color fill, text;
        if (!Enabled) (fill, text) = (Theme.Surface2, Theme.Text3);
        else if (Primary) (fill, text) = (hover && !down ? Theme.AccentHover : Theme.Accent, Theme.Bg);
        else (fill, text) = (hover ? Theme.Surface3 : Theme.Surface2, Theme.Text);

        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!Primary || !Enabled)
            using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, path);

        if (Focused && ShowFocusCues)
        {
            var fr = RectangleF.Inflate(r, 3, 3);
            using var ring = Theme.Round(fr, fr.Height / 2);
            using var p = new Pen(Theme.Accent, 2);
            g.DrawPath(p, ring);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Quiet text-only button ("Show log").</summary>
sealed class TextButton : Button
{
    bool hover;
    public Color Normal { get; set; } = Theme.Text2;

    public TextButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 9f);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, hover ? Theme.Text : Normal,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues)
        {
            using var p = new Pen(Theme.Accent, 2);
            g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
        }
    }
}
