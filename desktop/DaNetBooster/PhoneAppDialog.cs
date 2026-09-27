using QRCoder;

namespace DaNetBooster;

/// <summary>First-run helper: scan with the phone to download the phone app from the latest GitHub release.</summary>
sealed class PhoneAppDialog : Form
{
    static readonly Font Title = new("Segoe UI Semibold", 11f), Body = new("Segoe UI", 9f), Small = new("Segoe UI", 8.5f);
    readonly bool[,] modules;

    public PhoneAppDialog(string url)
    {
        Text = "Get the phone app";
        BackColor = Theme.Bg;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(S(340), S(430));

        var m = new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.M).ModuleMatrix;
        modules = new bool[m.Count, m.Count];
        for (var y = 0; y < m.Count; y++)
            for (var x = 0; x < m.Count; x++) modules[x, y] = m[y][x];

        var link = new TextBox
        {
            Text = url, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Text2,
            Font = new Font("Segoe UI", 8.5f), TextAlign = HorizontalAlignment.Center,
            Bounds = new Rectangle(S(20), S(392), S(300), S(20)),
        };
        Controls.Add(link);
    }

    int S(int v) => v * DeviceDpi / 96;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkChrome(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TextRenderer.DrawText(g, "Scan with your phone's camera", Title, new Point(S(20), S(18)), Theme.Text);
        TextRenderer.DrawText(g, "Downloads the app. Open it, allow the install,\nthen turn on USB tethering and tap Start sharing.",
            Body, new Point(S(20), S(44)), Theme.Text2);

        // Dark-on-light QR: phone cameras read it far more reliably than an inverted code.
        var box = new Rectangle(S(45), S(92), S(250), S(250));
        g.FillRectangle(Brushes.White, box);
        var n = modules.GetLength(0);
        var cell = (float)box.Width / n;
        using var ink = new SolidBrush(Theme.Bg);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (modules[x, y]) g.FillRectangle(ink, box.X + x * cell, box.Y + y * cell, cell + 0.5f, cell + 0.5f);
        TextRenderer.DrawText(g, "or type the link:", Small, new Point(S(20), S(362)), Theme.Text3);
    }
}
