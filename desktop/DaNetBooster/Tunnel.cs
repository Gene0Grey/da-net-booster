using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaNetBooster;

/// <summary>
/// App side of the tether. The phone app runs a SOCKS5 proxy; every connection leaves the phone from the app's own
/// sockets. The link to it is, in order of preference:
///   Tether: Android "USB tethering" used purely as a cable. The phone is the gateway of the RNDIS/NCM adapter and the
///           proxy listens there. A real network link, so one slow stream cannot stall the others. No USB debugging.
///   Adb:    developer fallback (adb forward PC :1080 -> phone :8000). Needs the app run as admin, see Connect.
/// The admin work (adapter, routes) is done by the helper service, or in-process when this app is itself elevated.
/// </summary>
sealed class Tunnel
{
    public enum Link { None, Tether, Adb }
    public enum Health { Ok, Gone, Moved, NotSharing, Down }

    const string Pkg = "com.danet.booster";
    const int PhoneStatsPort = 8001, AdbLocalPort = 1080, AdbStatsPort = 1081;

    public static bool Elevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    readonly Action<string> log;
    readonly ITunnelBackend backend;
    string? serial, tetherNic;
    string socksHost = "", statsHost = "";
    int socksPort, statsPort;

    /// <summary>Raised when the tunnel dies on its own (not via Disconnect).</summary>
    public event Action? Exited;
    public bool Running => backend.Running;
    public Link Mode { get; private set; }

    public Tunnel(Action<string> log)
    {
        this.log = log;
        backend = Elevated ? new TunnelCore(log, Path.GetTempPath()) : new Helper.HelperClient(log);
        backend.Exited += () => Exited?.Invoke();
    }

    public void Connect()
    {
        var t = TunnelCore.FindTether();
        if (t is { } tether && TunnelCore.Greet(tether.phone, TunnelCore.PhonePort))
        {
            Mode = Link.Tether;
            tetherNic = tether.name;
            (socksHost, socksPort, statsHost, statsPort) = (tether.phone, TunnelCore.PhonePort, tether.phone, PhoneStatsPort);
            log($"USB tethering link: phone at {tether.phone} on '{tether.name}'.");
            backend.Start(socksHost, socksPort, tetherNic);
        }
        else if (Device() is { state: "device" })
        {
            // The helper only ever tunnels to the phone on a USB-tethering adapter. Allowing a loopback target would let
            // any local program route all of the PC's traffic through itself, so adb mode stays an admin-only dev path.
            if (!Elevated) throw new InvalidOperationException("USB debugging mode needs the app run as administrator. Use USB tethering instead");
            ConnectAdb();
            backend.Start(socksHost, socksPort, null);
        }
        else if (t != null)
            throw new InvalidOperationException("Phone app isn't sharing. Open Da Net Booster on the phone and tap Start sharing");
        else
            throw new InvalidOperationException("No phone found. Plug in USB and turn on USB tethering");
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
        r = Adb($"forward tcp:{AdbLocalPort} tcp:{TunnelCore.PhonePort}");
        if (r.StartsWith("error")) throw new InvalidOperationException("adb forward failed: " + r);
        Adb($"forward tcp:{AdbStatsPort} tcp:{PhoneStatsPort}");
        (socksHost, socksPort, statsHost, statsPort) = ("127.0.0.1", AdbLocalPort, "127.0.0.1", AdbStatsPort);
        for (var i = 0; i < 25 && !TunnelCore.Greet(socksHost, socksPort); i++) Thread.Sleep(200);
        if (!TunnelCore.Greet(socksHost, socksPort)) throw new InvalidOperationException("Phone proxy not answering. Open Da Net Booster on the phone and tap Start sharing");
        log("adb link (developer mode): phone proxy reachable.");
    }

    public void Disconnect()
    {
        backend.Stop();
        if (Mode == Link.Adb && serial != null)
        {
            Adb($"forward --remove tcp:{AdbLocalPort}");
            Adb($"forward --remove tcp:{AdbStatsPort}");
            Adb($"shell am stopservice -n {Pkg}/.ProxyService");
        }
        serial = null;
        Mode = Link.None;
    }

    /// <summary>Kill tunnels orphaned by older versions. Only possible (and only needed) with admin rights.</summary>
    public static void CleanupOrphans(Action<string> log)
    {
        if (Elevated) TunnelCore.CleanupOrphans(log);
    }

    /// <summary>For the idle UI: which link is available and its state (ready / noapp / device / unauthorized / offline).</summary>
    public static (Link link, string name, string state) Detect()
    {
        if (TunnelCore.FindTether() is { } t) return (Link.Tether, t.name, TunnelCore.Greet(t.phone, TunnelCore.PhonePort) ? "ready" : "noapp");
        if (Device() is { } d) return (Link.Adb, d.model, d.state);
        return (Link.None, "", "");
    }

    /// <summary>While connected: tunnel alive, link still there at the same phone address, phone app answering?</summary>
    public Health Check()
    {
        if (!backend.Alive()) return Health.Down;
        return Mode switch
        {
            Link.Tether => TunnelCore.FindTether() is not { } t || t.name != tetherNic ? Health.Gone
                : t.phone != socksHost ? Health.Moved
                : TunnelCore.Greet(socksHost, socksPort) ? Health.Ok : Health.NotSharing,
            Link.Adb => Device() is not { state: "device" } ? Health.Gone
                : TunnelCore.Greet(socksHost, socksPort) ? Health.Ok : Health.NotSharing,
            _ => Health.Gone,
        };
    }

    /// <summary>First phone visible to adb, or null (also null when adb is missing: release builds need no debugging).</summary>
    public static (string serial, string state, string model)? Device()
    {
        try
        {
            foreach (var line in TunnelCore.Run(TunnelCore.Tool("adb.exe"), "devices -l").output.Split('\n').Skip(1))
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

    /// <summary>Exempt: phone app is exempt from battery optimisation (null = older phone app that doesn't say).</summary>
    public sealed record PhoneStats(long Up, long Down, int Tcp, int Udp, string Net, int? Dbm, int Level, bool Wifi, bool? Exempt);
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
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == TunnelCore.Adapter && n.OperationalStatus == OperationalStatus.Up);
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
        var apk = Path.Combine(TunnelCore.Tools, "DaNetBooster.apk");
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

    string Adb(string args)
    {
        var r = TunnelCore.Run(TunnelCore.Tool("adb.exe"), serial == null ? args : $"-s {serial} {args}").output;
        if (r.Length > 0) log("adb: " + r);
        return r;
    }
}
