namespace DaNetBooster;

/// <summary>
/// "Net Graph": one owner-drawn surface, read the way players read the in-game netgraph.
/// Order of attention: ping, then jitter and loss, then the verdict, then history, then everything else.
/// Geometry is in 96-dpi logical px through S(); fonts are rebuilt per DPI.
/// </summary>
sealed class Dashboard : Control
{
    public const int W = 400, H = 492, M = 20;
    public enum Phase { Idle, Busy, On }

    // Set by MainForm, then Invalidate().
    public Phase State;
    public string DeviceLine = "Looking for your phone…";
    public Color DeviceColor = Theme.Text2;
    public string Message = "Plug in your phone with USB debugging on";
    public Color MessageColor = Theme.Text2;
    public double? Ping, PingNow, P95, Jitter, Loss;
    public string Down = "—", Up = "—", Used = "—", Network = "—", Conns = "—", Uptime = "—";
    public int Level = -1;
    public float BusyPhase;

    const int Cap = 240; // 2 min at 2 samples/s
    readonly Queue<double?> samples = new();
    Fonts f;

    sealed record Fonts(Font Word, Font Meta, Font Label, Font Hero, Font HeroUnit, Font Metric, Font Verdict, Font Small)
    {
        public static Fonts For(int dpi)
        {
            Font F(string face, float px) => new(face, px * dpi / 96f, GraphicsUnit.Pixel);
            const string reg = "Segoe UI", semi = "Segoe UI Semibold";
            return new(F(semi, 15), F(reg, 12), F(semi, 11), F(semi, 44), F(reg, 14), F(semi, 22), F(semi, 13), F(reg, 11));
        }
    }

    public Dashboard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Bg;
        AccessibleRole = AccessibleRole.StaticText;
        f = Fonts.For(96);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); f = Fonts.For(DeviceDpi); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); f = Fonts.For(DeviceDpi); Invalidate(); }

    public float S(float v) => v * DeviceDpi / 96f;
    public Rectangle LogArea => Rectangle.Round(new RectangleF(S(M), S(248), S(W - 2 * M), S(186)));

    public void Push(double? rtt)
    {
        samples.Enqueue(rtt);
        while (samples.Count > Cap) samples.Dequeue();
    }

    public void Clear() => samples.Clear();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Theme.Smooth(e.Graphics);
        g.Clear(Theme.Bg);
        Header(g);
        Hero(g);
        Verdict(g);
        Graph(g);
        Secondary(g);
    }

    void Draw(Graphics g, string s, Font font, float x, float y, Color c, TextFormatFlags extra = 0) =>
        TextRenderer.DrawText(g, s, font, new Point((int)S(x), (int)S(y)), c, TextFormatFlags.NoPadding | extra);

    int Measure(Graphics g, string s, Font font) => TextRenderer.MeasureText(g, s, font, Size.Empty, TextFormatFlags.NoPadding).Width;

    void Header(Graphics g)
    {
        Draw(g, "Da Net Booster", f.Word, M, 18, Theme.Text);
        Draw(g, DeviceLine, f.Meta, M, 40, DeviceColor);

        using var line = new Pen(Theme.Stroke, S(1));
        g.DrawLine(line, S(M), S(68), S(W - M), S(68));
        if (State == Phase.Busy)
        {
            // Indeterminate progress under the header while connecting.
            var span = S(W - 2 * M);
            var x = S(M) + (span + S(80)) * BusyPhase - S(80);
            using var p = new Pen(Theme.Accent, S(2));
            g.DrawLine(p, Math.Max(S(M), x), S(68), Math.Min(S(W - M), x + S(80)), S(68));
        }
    }

    void Hero(Graphics g)
    {
        const int y = 84, right = M + 206;
        Draw(g, "PING", f.Label, M, y, Theme.Text2);
        var value = Ping is { } p ? $"{p:0}" : "—";
        var color = Ping is { } pc ? Theme.Grade(pc, 60, 100) : Theme.Text3;
        Draw(g, value, f.Hero, M - 2, y + 12, color);
        var vw = Measure(g, value, f.Hero) / (DeviceDpi / 96f);
        if (Ping != null) Draw(g, "ms", f.HeroUnit, M + vw + 4, y + 38, Theme.Text2);
        var sub = Ping == null ? "Median of the last 5 s" : $"now {(PingNow is { } n ? $"{n:0}" : "lost")} · p95 {P95:0}";
        Draw(g, sub, f.Meta, M, y + 72, Theme.Text2);

        Metric(g, "JITTER", Jitter is { } j ? $"{j:0.#} ms" : "—", Jitter is { } jc ? Theme.Grade(jc, 8, 20) : (Color?)null, right, y);
        Metric(g, "LOSS", Loss is { } l ? $"{l:0.#}%" : "—", Loss is { } lc ? Theme.Grade(lc, 0.5, 2) : (Color?)null, right, y + 52);
    }

    void Metric(Graphics g, string label, string value, Color? grade, float x, float y)
    {
        Draw(g, label, f.Label, x, y, Theme.Text2);
        Draw(g, value, f.Metric, x - 1, y + 12, grade ?? Theme.Text3);
        using var bar = new SolidBrush(grade ?? Theme.Stroke);
        g.FillRectangle(bar, S(x), S(y + 42), S(24), S(3));
    }

    void Verdict(Graphics g)
    {
        var r = new RectangleF(S(M), S(196), S(W - 2 * M), S(38));
        var neutral = MessageColor == Theme.Text2;
        using (var path = Theme.Round(r, S(8)))
        using (var fill = new SolidBrush(neutral ? Theme.Surface : Color.FromArgb(34, MessageColor)))
            g.FillPath(fill, path);
        using (var dot = new SolidBrush(neutral ? Theme.Text3 : MessageColor))
            g.FillEllipse(dot, S(M + 14), r.Y + r.Height / 2 - S(4), S(8), S(8));
        var tr = Rectangle.Round(new RectangleF(S(M + 30), r.Y, r.Width - S(40), r.Height));
        TextRenderer.DrawText(g, Message, f.Verdict, tr, neutral ? Theme.Text : MessageColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    void Graph(Graphics g)
    {
        Draw(g, "PING · 2 MIN", f.Label, M, 250, Theme.Text2);
        var plot = new RectangleF(S(M), S(270), S(W - 2 * M - 34), S(96));
        var arr = samples.ToArray();
        double peak = 150;
        foreach (var s in arr) if (s is { } v && v * 1.15 > peak) peak = v * 1.15;
        float Y(double ms) => plot.Bottom - (float)(Math.Min(ms, peak) / peak) * plot.Height;
        float X(int i) => plot.Left + plot.Width * i / (Cap - 1f);

        // Quality bands (60-100 ms warn, >100 ms bad), only once there is data to judge.
        if (arr.Length > 0)
        {
            using var warn = new SolidBrush(Color.FromArgb(12, Theme.Warn));
            using var bad = new SolidBrush(Color.FromArgb(9, Theme.Bad));
            g.FillRectangle(warn, plot.Left, Y(100), plot.Width, Y(60) - Y(100));
            g.FillRectangle(bad, plot.Left, plot.Top, plot.Width, Y(100) - plot.Top);
        }
        using (var grid = new Pen(Theme.Stroke, S(1)))
        {
            g.DrawLine(grid, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            foreach (var ms in new[] { 50, 100, 200, 400 })
            {
                if (ms >= peak) continue;
                TextRenderer.DrawText(g, ms.ToString(), f.Small,
                    new Rectangle((int)plot.Right, (int)(Y(ms) - S(7)), (int)S(34), (int)S(14)), Theme.Text3,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }
        }

        if (arr.Length == 0)
        {
            TextRenderer.DrawText(g, State == Phase.On ? "Measuring…" : "Ping history appears after you connect", f.Meta,
                Rectangle.Round(plot), Theme.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var offset = Cap - arr.Length; // newest sample at the right edge
        using var loss = new SolidBrush(Theme.Bad);
        using var line = new Pen(Theme.Accent, S(1.5f)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        PointF? prev = null;
        for (var i = 0; i < arr.Length; i++)
        {
            var x = X(i + offset);
            if (arr[i] is not { } ms)
            {
                g.FillRectangle(loss, x - S(1.5f), plot.Bottom + S(4), S(3), S(6)); // loss tick under the baseline
                prev = null;
                continue;
            }
            var pt = new PointF(x, Y(ms));
            if (prev is { } p) g.DrawLine(line, p, pt);
            prev = pt;
        }
    }

    void Secondary(Graphics g)
    {
        using (var line = new Pen(Theme.Stroke, S(1)))
            g.DrawLine(line, S(M), S(392), S(W - M), S(392));

        Pairs(g, M, 404, ("Down", Down), ("Up", Up), ("Used", Used));

        // Signal bars, drawn (no glyphs): 4 bars, 4 px wide, 2 px gaps.
        var lit = Level >= 3 ? Theme.Good : Level == 2 ? Theme.Warn : Theme.Bad;
        using (var on = new SolidBrush(lit))
        using (var off = new SolidBrush(Theme.Stroke))
            for (var i = 0; i < 4; i++)
            {
                var h = 5 + 3 * i;
                g.FillRectangle(i < Level ? on : off, S(M + i * 6), S(440 - h), S(4), S(h));
            }
        Pairs(g, M + 30, 426, ("", Network), ("", Conns), ("", Uptime));
    }

    /// <summary>Label (muted) + value (bright) pairs on one line.</summary>
    void Pairs(Graphics g, float x, float y, params (string label, string value)[] items)
    {
        var k = DeviceDpi / 96f;
        foreach (var (label, value) in items)
        {
            if (label.Length > 0)
            {
                Draw(g, label, f.Meta, x, y, Theme.Text2);
                x += Measure(g, label, f.Meta) / k + 5;
            }
            Draw(g, value, f.Meta, x, y, Theme.Text);
            x += Measure(g, value, f.Meta) / k + 18;
        }
    }
}
