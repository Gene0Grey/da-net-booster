namespace DaNetBooster;

sealed class MainForm : Form
{
    const int M = Dashboard.M;

    readonly Dashboard dash = new() { Dock = DockStyle.Fill };
    readonly PillButton connect = new() { Text = "Connect", AccessibleName = "Connect", Enabled = false };
    readonly TextButton logToggle = new() { Text = "Show log", AccessibleName = "Show log" };
    readonly TextButton phoneApp = new() { Text = "Get phone app", AccessibleName = "Get phone app" };
    readonly TextButton update = new() { Text = "", Visible = false, Normal = Theme.Accent };
    readonly TextBox logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false,
        BackColor = Theme.Surface, ForeColor = Theme.Text2, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 8.5f),
    };

    readonly System.Windows.Forms.Timer poll = new() { Interval = 3000 }, tick = new() { Interval = 1000 }, busyAnim = new() { Interval = 16 };
    readonly System.Windows.Forms.Timer updateCheck = new() { Interval = 6 * 3600 * 1000 };
    readonly Updater updater;
    readonly Queue<double?> window = new(); // last 60 s of probe results
    readonly Tunnel tunnel;
    Probe? probe;
    bool busy;
    string deviceState = "";
    int misses; // consecutive polls without the phone in 'device' state
    string? lastError;
    string? phoneWarning;        // shown instead of the verdict while the phone app isn't answering
    int notSharing;              // consecutive polls where the phone app didn't answer
    DateTime autoReconnectUntil; // after an unplanned drop, reconnect by itself when the phone is back
    DateTime since;
    (long rx, long tx)? baseBytes, lastBytes;
    Color chrome = Color.Empty;

    public MainForm()
    {
        Text = "Da Net Booster";
        BackColor = Theme.Bg;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None; // geometry is scaled explicitly per DPI
        Icon = Theme.Dot(Theme.Text3);
        tunnel = new Tunnel(Log);
        updater = new Updater(Log);

        Controls.AddRange([logBox, connect, logToggle, phoneApp, update, dash]); // first = topmost
        // No AcceptButton: a stray Enter while alt-tabbing must never drop the tunnel mid-match.
        LayoutUi();
        DpiChanged += (_, _) => LayoutUi();

        connect.Click += async (_, _) => await Toggle();
        logToggle.Click += (_, _) =>
        {
            logBox.Visible = !logBox.Visible;
            logToggle.Text = logToggle.AccessibleName = logBox.Visible ? "Hide log" : "Show log";
        };
        phoneApp.Click += (_, _) =>
        {
            if (Updater.PhoneApkUrl.Length == 0)
                MessageBox.Show(this, "This is a development build. Use USB debugging: Connect installs the phone app automatically.", "Get the phone app");
            else using (var d = new PhoneAppDialog(Updater.PhoneApkUrl)) d.ShowDialog(this);
        };
        update.Click += async (_, _) => await ApplyUpdate();
        updateCheck.Tick += async (_, _) => await CheckForUpdate();
        tunnel.Exited += () => BeginInvoke(async () =>
        {
            Log("Tunnel process exited unexpectedly.");
            await Task.Run(tunnel.Disconnect);
            SetConnected(false);
            lastError = "The tunnel stopped unexpectedly. Reconnecting…";
            autoReconnectUntil = DateTime.Now.AddMinutes(2);
            await RefreshDevice();
        });
        busyAnim.Tick += (_, _) => { dash.BusyPhase = (dash.BusyPhase + 0.015f) % 1f; dash.Invalidate(); };
        poll.Tick += async (_, _) => await RefreshDevice();
        tick.Tick += async (_, _) => await Tick();
        Shown += async (_, _) => { poll.Start(); updateCheck.Start(); await RefreshDevice(); await CheckForUpdate(); };
        FormClosing += (_, _) =>
        {
            poll.Stop(); tick.Stop(); updateCheck.Stop(); probe?.Dispose();
            tunnel.Disconnect();
            updater.ApplyOnExit(); // a downloaded update installs after the window closes
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkChrome(Handle);
        Chrome(Theme.Stroke);
    }

    float S(float v) => v * DeviceDpi / 96f;

    void LayoutUi()
    {
        ClientSize = new Size((int)S(Dashboard.W), (int)S(Dashboard.H));
        // Pill is 112x36 visible; the control is 4 px larger on each side for its focus ring.
        connect.Bounds = Rectangle.Round(new RectangleF(S(Dashboard.W - M - 116), S(14), S(120), S(44)));
        logToggle.Bounds = Rectangle.Round(new RectangleF(S(M - 2), S(454), S(80), S(28)));
        phoneApp.Bounds = Rectangle.Round(new RectangleF(S(M + 86), S(454), S(100), S(28)));
        update.Bounds = Rectangle.Round(new RectangleF(S(Dashboard.W - M - 170), S(454), S(170), S(28)));
        logBox.Bounds = dash.LogArea;
    }

    async Task CheckForUpdate()
    {
        // Updates are optional: even a missing/broken Velopack.dll (throws before CheckAsync's own try) must only be logged.
        try { await updater.CheckAsync(); }
        catch (Exception e) { Log("Update check unavailable: " + e.Message); return; }
        if (updater.Ready is not { } v) return;
        update.Text = update.AccessibleName = $"Restart to update to {v}";
        update.Visible = true;
    }

    /// <summary>Never mid-match: installing restarts the app, so the tunnel goes down first (with the user's OK).</summary>
    async Task ApplyUpdate()
    {
        if (busy) return;
        if (tunnel.Running)
        {
            if (MessageBox.Show(this, "Updating restarts the app, so games and downloads using this connection will drop.",
                    $"Disconnect and update to {updater.Ready}?", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            SetConnected(false);
            await Task.Run(tunnel.Disconnect);
        }
        updater.ApplyAndRestart();
    }

    async Task Toggle()
    {
        if (busy) return;
        busy = true;
        connect.Enabled = false;
        try
        {
            if (tunnel.Running)
            {
                if (MessageBox.Show(this, "Games and downloads using this connection will drop.", "Disconnect from phone?",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                autoReconnectUntil = DateTime.MinValue; // you asked for it: don't come back by itself
                SetConnected(false);
                await Task.Run(tunnel.Disconnect);
                Log("Disconnected.");
            }
            else
            {
                lastError = null;
                dash.State = Dashboard.Phase.Busy;
                (dash.Message, dash.MessageColor) = ("Connecting to your phone…", Theme.Text2);
                connect.Text = "Connecting…";
                if (SystemInformation.UIEffectsEnabled) busyAnim.Start();
                dash.Invalidate();
                Log("Connecting...");
                await Task.Run(tunnel.Connect);
                Log("Connected. All IPv4 traffic now goes through the phone.");
                SetConnected(true);
            }
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            await Task.Run(tunnel.Disconnect);
            SetConnected(false);
            lastError = ex.Message.Length <= 70 ? ex.Message.TrimEnd('.') : "Couldn't connect. Open the log for details";
        }
        finally
        {
            busy = false;
            busyAnim.Stop();
            await RefreshDevice();
        }
    }

    void SetConnected(bool on)
    {
        dash.State = on ? Dashboard.Phase.On : Dashboard.Phase.Idle;
        connect.Primary = !on;
        connect.Text = connect.AccessibleName = on ? "Disconnect" : "Connect";
        probe?.Dispose();
        probe = null;
        window.Clear();
        dash.Clear();
        (dash.Ping, dash.PingNow, dash.P95, dash.Jitter, dash.Loss) = (null, null, null, null, null);
        (dash.Down, dash.Up, dash.Used, dash.Network, dash.Conns, dash.Uptime, dash.Level) = ("—", "—", "—", "—", "—", "—", -1);
        if (on)
        {
            since = DateTime.Now;
            baseBytes = lastBytes = Tunnel.AdapterBytes();
            (dash.Message, dash.MessageColor) = ("Measuring your connection…", Theme.Text2);
            probe = new Probe();
            probe.Sample += rtt => BeginInvoke(() => OnSample(rtt));
            tick.Start();
            Chrome(Theme.Accent);
        }
        else
        {
            tick.Stop();
            Chrome(Theme.Stroke);
        }
        dash.Invalidate();
    }

    void OnSample(double? rtt)
    {
        if (probe == null) return;
        dash.Push(rtt);
        window.Enqueue(rtt);
        while (window.Count > 120) window.Dequeue();

        var ok = window.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var recent = window.Skip(Math.Max(0, window.Count - 10)).Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
        var sorted = ok.Order().ToArray();
        dash.PingNow = rtt;
        dash.Ping = recent.Length > 0 ? recent[recent.Length / 2] : null; // median of the last 5 s: steady at a glance
        dash.P95 = sorted.Length > 0 ? sorted[(int)Math.Min(sorted.Length - 1, sorted.Length * 0.95)] : null;
        dash.Jitter = ok.Length > 1 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(a - b)).Average() : null;
        dash.Loss = 100.0 * (window.Count - ok.Length) / window.Count;

        (dash.Message, dash.MessageColor) = Verdict();
        Chrome(dash.MessageColor == Theme.Text2 ? Theme.Accent : dash.MessageColor);
        dash.AccessibleName = $"Ping {dash.Ping:0} milliseconds, jitter {dash.Jitter:0} milliseconds, loss {dash.Loss:0.#} percent. {dash.Message}";
        dash.Invalidate();
    }

    /// <summary>Plain-language read of the last minute for competitive play, naming the worst offender.</summary>
    (string, Color) Verdict()
    {
        if (phoneWarning != null) return (phoneWarning, Theme.Bad);
        if (window.Count < 10 || dash.Ping is not { } ping) return ("Measuring your connection…", Theme.Text2);
        double loss = dash.Loss ?? 0, jit = dash.Jitter ?? 0;
        var worst = loss > 0.5 ? $"loss {loss:0.#}% in the last minute"
            : jit > 8 ? $"jitter {jit:0} ms"
            : $"ping {ping:0} ms";
        if (loss > 2 || jit > 20 || ping > 100) return ($"Unstable · {worst}", Theme.Bad);
        if (loss > 0.5 || jit > 8 || ping > 60) return ($"Some spikes · {worst}", Theme.Warn);
        return ("Stable · good for competitive play", Theme.Good);
    }

    async Task Tick()
    {
        if (!tunnel.Running) return;
        try
        {
            var (bytes, phone) = await Task.Run(() => (Tunnel.AdapterBytes(), tunnel.ReadPhone()));

            if (bytes is { } b && lastBytes is { } l && baseBytes is { } b0)
            {
                dash.Down = Rate(b.rx - l.rx);
                dash.Up = Rate(b.tx - l.tx);
                dash.Used = Bytes(b.rx - b0.rx + b.tx - b0.tx);
            }
            lastBytes = bytes;

            if (phone != null)
            {
                dash.Network = phone.Wifi ? "Phone on Wi-Fi" : phone.Dbm is { } d ? $"{phone.Net}  −{-d} dBm" : phone.Net;
                dash.Level = phone.Wifi ? 0 : phone.Level;
                var n = phone.Tcp + phone.Udp;
                dash.Conns = n == 1 ? "1 connection" : $"{n} connections";
            }
            dash.Uptime = (DateTime.Now - since).ToString(@"hh\:mm\:ss");
            dash.Invalidate();
        }
        catch (Exception ex) { Log("stats: " + ex.Message); } // a stats hiccup must never take the window down
    }

    async Task RefreshDevice()
    {
        if (busy) return;
        try
        {
            if (tunnel.Running)
            {
                var health = await Task.Run(tunnel.Check);
                if (!tunnel.Running) return; // disconnected while we were checking
                misses = health == Tunnel.Health.Gone ? misses + 1 : 0;
                notSharing = health == Tunnel.Health.NotSharing ? notSharing + 1 : 0;
                // The tunnel recovers by itself once the phone app answers again; meanwhile say what's wrong.
                phoneWarning = notSharing >= 3 ? "Phone app isn't answering. Open it and tap Start sharing" : null;
                if (health == Tunnel.Health.Moved)
                {
                    // Re-plugging the cable gives the phone a new address; the old tunnel would be connected to nothing.
                    Log("Phone's USB address changed. Reconnecting.");
                    SetConnected(false);
                    await Task.Run(tunnel.Disconnect);
                    await Toggle();
                    return;
                }
                if (misses < 3) return; // debounce: a link can blip for one poll when busy; give up after ~9 s
                Log("Phone disconnected.");
                SetConnected(false);
                await Task.Run(tunnel.Disconnect);
                lastError = "Phone disconnected. Reconnecting when it's back…";
                autoReconnectUntil = DateTime.Now.AddMinutes(2);
            }
            misses = notSharing = 0;
            phoneWarning = null;

            var (link, name, state) = await Task.Run(Tunnel.Detect);
            var key = $"{link}:{state}";
            if (key != deviceState && DateTime.Now >= autoReconnectUntil) lastError = null; // a plug/unplug supersedes the old error
            deviceState = key;

            (dash.DeviceLine, dash.DeviceColor) = (link, state) switch
            {
                (Tunnel.Link.Tether, "ready") => ("USB tethering · Ready", Theme.Text2),
                (Tunnel.Link.Tether, _) => ("USB tethering · Phone app not sharing", Theme.Warn),
                (Tunnel.Link.Adb, "device") => ($"{name} · USB debugging", Theme.Text2),
                (Tunnel.Link.Adb, "unauthorized") => ($"{name} · Waiting for permission", Theme.Warn),
                (Tunnel.Link.Adb, _) => ($"{name} · Offline", Theme.Warn),
                _ => ("No phone detected", Theme.Text2),
            };
            (dash.Message, dash.MessageColor) = lastError != null ? (lastError, Theme.Bad) : ((link, state) switch
            {
                (Tunnel.Link.Tether, "ready") or (Tunnel.Link.Adb, "device") => "Ready. Press Connect to start",
                (Tunnel.Link.Tether, _) => "Open Da Net Booster on the phone and tap Start sharing",
                (Tunnel.Link.Adb, "unauthorized") => "Tap Allow on the phone's USB debugging prompt",
                (Tunnel.Link.Adb, _) => "Reconnect the USB cable",
                _ => "Plug in your phone and turn on USB tethering",
            }, Theme.Text2);
            connect.Enabled = state is "ready" or "device";

            if (connect.Enabled && DateTime.Now < autoReconnectUntil)
            {
                Log("Phone is back. Reconnecting automatically.");
                autoReconnectUntil = DateTime.MinValue;
                await Toggle();
            }
        }
        catch (Exception ex)
        {
            Log("device check: " + ex.Message);
        }
        dash.Invalidate();
    }

    /// <summary>Window border + taskbar icon in the status colour.</summary>
    void Chrome(Color c)
    {
        if (c == chrome || !IsHandleCreated) return;
        chrome = c;
        Theme.Border(Handle, c);
        Icon = Theme.Dot(c);
    }

    static string Rate(long bps) => bps >= 1_000_000 ? $"{bps / 1e6:0.0} MB/s" : bps >= 1000 ? $"{bps / 1e3:0} KB/s" : $"{bps} B/s";
    static string Bytes(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : $"{b / 1e3:0} KB";

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        AppLog.Write(s);
        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
    }
}
