using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaNetBooster;

/// <summary>
/// PC side of the tether. The phone app runs a SOCKS5 proxy; every connection leaves the phone from the app's own
/// sockets. The link to it is, in order of preference:
///   Tether: Android "USB tethering" used purely as a cable. The phone is the gateway of the RNDIS/NCM adapter and the
///           proxy listens there. A real network link, so one slow stream cannot stall the others. No USB debugging.
///   Adb:    developer fallback. adb forward (PC :1080 -> phone :8000). Multiplexes everything over one adb pipe, which
///           can stall for ~20 s when a single stream backs up.
/// hev-socks5-tunnel turns the PC's traffic into SOCKS5 through a wintun adapter; split /1 routes send everything there.
/// </summary>
sealed class Tunnel(Action<string> log)
{
    public enum Link { None, Tether, Adb }

    const string Adapter = "DaNet", TunIp = "198.18.0.1", Dns = "1.1.1.1", Pkg = "com.danet.booster";
    const int PhonePort = 8000, PhoneStatsPort = 8001, AdbLocalPort = 1080, AdbStatsPort = 1081;
    static readonly string Tools = Path.Combine(AppContext.BaseDirectory, "tools");
    static readonly string[] SplitRoutes = ["0.0.0.0/1", "128.0.0.0/1"];
    static readonly Regex TetherNic = new("Remote NDIS|RNDIS|NCM|Android", RegexOptions.IgnoreCase);

    Process? hev;
    string? serial, tetherNic;
    int ifIndex = -1;
    string socksHost = "", statsHost = "";
    int socksPort, statsPort;
    volatile bool stopping;
    bool ipv6Restore;

    /// <summary>Raised when hev dies on its own (not via Disconnect).</summary>
    public event Action? Exited;
    public bool Running => hev is { HasExited: false };
    public Link Mode { get; private set; }

    public void Connect()
    {
        var t = FindTether();
        if (t is { } tether && Greet(tether.phone, PhonePort))
        {
            Mode = Link.Tether;
            tetherNic = tether.name;
            (socksHost, socksPort, statsHost, statsPort) = (tether.phone, PhonePort, tether.phone, PhoneStatsPort);
            log($"USB tethering link: phone at {tether.phone} on '{tether.name}'.");
            // IPv6 on the tethering adapter would bypass the tunnel through the phone's NAT: off while connected,
            // restored on disconnect so plain USB tethering behaves normally afterwards.
            ipv6Restore = Run("powershell", $"-NoProfile -Command \"(Get-NetAdapterBinding -Name '{Ps(tether.name)}' -ComponentID ms_tcpip6).Enabled\"").output == "True";
            if (ipv6Restore) Run("powershell", $"-NoProfile -Command \"Disable-NetAdapterBinding -Name '{Ps(tether.name)}' -ComponentID ms_tcpip6\"");
        }
        else if (Device() is { state: "device" })
            ConnectAdb();
        else if (t != null)
            throw new InvalidOperationException("Phone app isn't sharing. Open Da Net Booster on the phone and tap Start sharing");
        else
            throw new InvalidOperationException("No phone found. Plug in USB and turn on USB tethering");

        StartHev();
        ConfigureAdapter();
    }

    void ConnectAdb()
    {
        var d = Device()!.Value;
        serial = d.serial;
        Mode = Link.Adb;
        EnsurePhoneApp();
        var r = Adb($"shell am start-foreground-service -n {Pkg}/.ProxyService");
        if (r.Contains("Error") || r.Contains("Exception"))
        {
            // Some ROMs block background FGS starts from shell; launching the activity always works.
            log("Service start refused, launching app instead.");
            Adb($"shell am start -a {Pkg}.START -n {Pkg}/.MainActivity");
        }
        r = Adb($"forward tcp:{AdbLocalPort} tcp:{PhonePort}");
        if (r.StartsWith("error")) throw new InvalidOperationException("adb forward failed: " + r);
        Adb($"forward tcp:{AdbStatsPort} tcp:{PhoneStatsPort}");
        (socksHost, socksPort, statsHost, statsPort) = ("127.0.0.1", AdbLocalPort, "127.0.0.1", AdbStatsPort);
        for (var i = 0; i < 25 && !Greet(socksHost, socksPort); i++) Thread.Sleep(200);
        if (!Greet(socksHost, socksPort)) throw new InvalidOperationException("Phone proxy not answering. Open Da Net Booster on the phone and tap Start sharing");
        log("adb link (developer mode): phone proxy reachable.");
    }

    public void Disconnect()
    {
        stopping = true;
        // Delete routes first: if the adapter outlives hev, they would blackhole all traffic.
        if (ifIndex >= 0)
            foreach (var p in SplitRoutes)
                Run("netsh", $"interface ipv4 delete route prefix={p} interface={ifIndex} store=active");
        ifIndex = -1;
        // Detach before killing: the old process's Exited event can fire after we return, and must not hit a new session.
        var old = hev;
        hev = null;
        if (old is { HasExited: false }) { old.Kill(); old.WaitForExit(3000); }
        if (Mode == Link.Adb && serial != null)
        {
            Adb($"forward --remove tcp:{AdbLocalPort}");
            Adb($"forward --remove tcp:{AdbStatsPort}");
            Adb($"shell am stopservice -n {Pkg}/.ProxyService");
        }
        if (ipv6Restore && tetherNic != null)
            Run("powershell", $"-NoProfile -Command \"Enable-NetAdapterBinding -Name '{Ps(tetherNic)}' -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue\"");
        ipv6Restore = false;
        serial = null;
        Mode = Link.None;
        stopping = false;
    }

    /// <summary>
    /// A tunnel left running by a crashed or force-closed older version keeps the PC's traffic pointed at a phone that
    /// may be gone (no internet at all). Kill ours before anything else; its adapter and routes go with it.
    /// </summary>
    public static void CleanupOrphans(Action<string> log)
    {
        foreach (var p in Process.GetProcessesByName("hev-socks5-tunnel"))
        {
            try
            {
                if (!string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), Tools, StringComparison.OrdinalIgnoreCase)) continue;
                log($"Stopping a tunnel left behind by an earlier session (pid {p.Id}).");
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>For the idle UI: which link is available and its state (ready / noapp / device / unauthorized / offline).</summary>
    public static (Link link, string name, string state) Detect()
    {
        if (FindTether() is { } t) return (Link.Tether, t.name, Greet(t.phone, PhonePort) ? "ready" : "noapp");
        if (Device() is { } d) return (Link.Adb, d.model, d.state);
        return (Link.None, "", "");
    }

    public enum Health { Ok, Gone, Moved, NotSharing }

    /// <summary>While connected: is the link still there, at the same phone address, with the phone app answering?</summary>
    public Health Check() => Mode switch
    {
        Link.Tether => FindTether() is not { } t || t.name != tetherNic ? Health.Gone
            : t.phone != socksHost ? Health.Moved
            : Greet(socksHost, socksPort) ? Health.Ok : Health.NotSharing,
        Link.Adb => Device() is not { state: "device" } ? Health.Gone
            : Greet(socksHost, socksPort) ? Health.Ok : Health.NotSharing,
        _ => Health.Gone,
    };

    /// <summary>Android's USB-tethering adapter (RNDIS or NCM) and the phone's address on it (its DHCP gateway).</summary>
    static (string name, string phone)? FindTether()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || !TetherNic.IsMatch(n.Description)) continue;
            var ip = n.GetIPProperties();
            var phone = ip.GatewayAddresses.Select(g => g.Address).Concat(ip.DhcpServerAddresses)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (phone != null) return (n.Name, phone.ToString());
        }
        return null;
    }

    /// <summary>A real SOCKS5 greeting: proves the phone app is sharing, not just that something accepts TCP.</summary>
    static bool Greet(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            if (!c.ConnectAsync(host, port).Wait(1500)) return false;
            var s = c.GetStream();
            s.ReadTimeout = 1500;
            s.Write([5, 1, 0]);
            var buf = new byte[2];
            return s.Read(buf) == 2 && buf[0] == 5 && buf[1] == 0;
        }
        catch (Exception e) when (e is IOException or SocketException or AggregateException) { return false; }
    }

    /// <summary>First phone visible to adb, or null (also null when adb is missing: release builds need no debugging).</summary>
    public static (string serial, string state, string model)? Device()
    {
        try
        {
            foreach (var line in Run(Tool("adb.exe"), "devices -l").output.Split('\n').Skip(1))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (p.Length < 2) continue;
                var model = p.FirstOrDefault(x => x.StartsWith("model:"))?[6..].Replace('_', ' ') ?? "Phone";
                return (p[0], p[1], model);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException) { }
        return null;
    }

    public sealed record PhoneStats(long Up, long Down, int Tcp, int Udp, string Net, int? Dbm, int Level, bool Wifi);
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Counters + radio info from the phone app's stats port.</summary>
    public PhoneStats? ReadPhone()
    {
        if (Mode == Link.None) return null;
        try
        {
            using var c = new TcpClient();
            if (!c.ConnectAsync(statsHost, statsPort).Wait(1000)) return null;
            c.ReceiveTimeout = 1500;
            using var r = new StreamReader(c.GetStream());
            return JsonSerializer.Deserialize<PhoneStats>(r.ReadLine() ?? "", Json);
        }
        catch (Exception e) when (e is IOException or SocketException or JsonException or AggregateException) { return null; }
    }

    /// <summary>Bytes received/sent on the TUN adapter = what the PC actually pushed through the phone.</summary>
    public static (long rx, long tx)? AdapterBytes()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == Adapter && n.OperationalStatus == OperationalStatus.Up);
            if (nic == null) return null;
            var st = nic.GetIPStatistics();
            return (st.BytesReceived, st.BytesSent);
        }
        catch (NetworkInformationException) { return null; } // adapter vanished mid-read (disconnect)
    }

    /// <summary>Install or upgrade the phone app over adb when its versionCode is below the bundled APK's (build.gradle.kts).</summary>
    void EnsurePhoneApp()
    {
        // Phone and desktop ship together: the bundled APK has this build's version (major*10000 + minor*100 + patch).
        var v = typeof(Tunnel).Assembly.GetName().Version!;
        var bundled = v.Major * 10000 + v.Minor * 100 + v.Build;
        var m = Regex.Match(Adb($"shell pm list packages --show-versioncode {Pkg}"), @"versionCode:(\d+)");
        if (m.Success && int.Parse(m.Groups[1].Value) >= bundled) return;
        var apk = Path.Combine(Tools, "DaNetBooster.apk");
        if (!File.Exists(apk))
        {
            if (m.Success) return; // older app still works, just without new features
            throw new InvalidOperationException("Phone app not installed and tools\\DaNetBooster.apk missing.");
        }
        log(m.Success ? "Updating phone app..." : "Installing phone app...");
        var r = Adb($"install -r \"{apk}\"");
        if (r.Contains("Success")) return;
        // e.g. a differently-signed build is installed: the existing app still works, so don't block connecting.
        if (m.Success) { log("Couldn't update the phone app (continuing with the installed one): " + r); return; }
        throw new InvalidOperationException("APK install failed: " + r);
    }

    void StartHev()
    {
        // A previous tunnel's adapter can linger a moment after its process is killed; reusing the name too early breaks setup.
        for (var i = 0; i < 25 && NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == Adapter); i++) Thread.Sleep(200);

        var cfg = Path.Combine(Path.GetTempPath(), "danet-hev.yml");
        File.WriteAllText(cfg, $"""
            tunnel:
              name: {Adapter}
              mtu: 8500
              ipv4: {TunIp}
            socks5:
              address: {socksHost}
              port: {socksPort}
              udp: 'tcp'
            misc:
              log-level: warn
            """);
        var exe = Tool("hev-socks5-tunnel.exe");
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, $"\"{cfg}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? Tools,
            },
            EnableRaisingEvents = true,
        };
        p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log("hev: " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log("hev: " + e.Data); };
        p.Exited += (s, _) => { if (!stopping && ReferenceEquals(s, hev)) Exited?.Invoke(); };
        hev = p;
        p.Start();
        KillWithApp.Add(p); // if this app crashes or is killed, Windows kills the tunnel too (no orphaned routes)
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    void ConfigureAdapter()
    {
        NetworkInterface? nic = null;
        for (var i = 0; i < 50 && nic == null; i++)
        {
            if (hev is not { HasExited: false }) throw new InvalidOperationException("hev-socks5-tunnel exited (needs admin + wintun.dll next to it).");
            Thread.Sleep(200);
            nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == Adapter && n.OperationalStatus == OperationalStatus.Up);
        }
        if (nic == null) throw new InvalidOperationException($"TUN adapter '{Adapter}' did not appear.");

        if (!nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == TunIp))
            Check(Run("netsh", $"interface ipv4 set address name=\"{Adapter}\" source=static address={TunIp} mask=255.255.255.0"));
        ifIndex = nic.GetIPProperties().GetIPv4Properties().Index;

        Check(Run("netsh", $"interface ipv4 set interface {ifIndex} metric=1"));
        Check(Run("netsh", $"interface ipv4 set dnsservers name=\"{Adapter}\" source=static address={Dns} register=none validate=no"));
        // /1 routes beat the existing 0.0.0.0/0 without touching it; store=active = gone on reboot.
        foreach (var p in SplitRoutes)
        {
            var r = Run("netsh", $"interface ipv4 add route prefix={p} interface={ifIndex} nexthop=0.0.0.0 metric=1 store=active");
            if (r.code != 0 && !r.output.Contains("exists", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(r.output);
        }
        log($"Routes 0.0.0.0/1 + 128.0.0.0/1 -> {Adapter} (if {ifIndex}), DNS {Dns}.");
    }

    string Adb(string args)
    {
        var r = Run(Tool("adb.exe"), serial == null ? args : $"-s {serial} {args}").output;
        if (r.Length > 0) log("adb: " + r);
        return r;
    }

    static void Check((int code, string output) r)
    {
        if (r.code != 0) throw new InvalidOperationException(r.output);
    }

    /// <summary>
    /// Only ever the bundled copy: this app runs as admin, so falling back to whatever adb.exe / tunnel happens to be on
    /// PATH would run an arbitrary program elevated.
    /// </summary>
    static string Tool(string exe)
    {
        var p = Path.Combine(Tools, exe);
        return File.Exists(p) ? p : throw new FileNotFoundException($"{exe} is missing from the app's tools folder. Reinstall Da Net Booster.");
    }

    /// <summary>Adapter names come from Windows/drivers; escape them for a single-quoted PowerShell string.</summary>
    static string Ps(string s) => s.Replace("'", "''");

    static (int code, string output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd() + err.Result;
        p.WaitForExit();
        return (p.ExitCode, output.Trim());
    }
}

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE: processes added here die when this app's process ends for any reason
/// (normal exit, crash, Task Manager). Otherwise a stranded tunnel keeps all traffic routed to a vanished phone.
/// </summary>
static class KillWithApp
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong R, W, O, RB, WB, OB; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    // Held for the life of the process; the OS closes it on exit, which kills everything in the job.
    static readonly IntPtr Job = Create();

    static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimits { Basic = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimits>());
        return job;
    }

    public static void Add(Process p)
    {
        if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, p.Handle);
    }
}
