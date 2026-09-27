using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DaNetBooster;

/// <summary>What the app needs from whoever holds admin rights: start/stop the tunnel, and say whether it's alive.</summary>
interface ITunnelBackend
{
    bool Running { get; }
    event Action? Exited;
    void Start(string socksHost, int socksPort, string? tetherNic);
    void Stop();
    bool Alive();
}

/// <summary>
/// The privileged half: hev-socks5-tunnel, the wintun "DaNet" adapter, split routes, DNS, and the tethering adapter's
/// IPv6 binding. Runs inside the helper service for normal installs, or in-process when the app itself is elevated.
/// </summary>
sealed class TunnelCore(Action<string> log, string workDir) : ITunnelBackend
{
    public const string Adapter = "DaNet";
    public const int PhonePort = 8000;
    const string TunIp = "198.18.0.1", Dns = "1.1.1.1";
    public static readonly string Tools = Path.Combine(AppContext.BaseDirectory, "tools");
    static readonly string[] SplitRoutes = ["0.0.0.0/1", "128.0.0.0/1"];
    static readonly Regex TetherNic = new("Remote NDIS|RNDIS|NCM|Android", RegexOptions.IgnoreCase);

    Process? hev;
    int ifIndex = -1;
    string? ipv6Nic;
    volatile bool stopping;
    readonly object gate = new();

    /// <summary>Raised when hev dies on its own (not via Stop).</summary>
    public event Action? Exited;
    public bool Running => hev is { HasExited: false };
    public bool Alive() => Running;

    public void Start(string socksHost, int socksPort, string? tetherNic)
    {
        lock (gate)
        {
            Stop();
            // IPv6 on the tethering adapter would bypass the tunnel through the phone's NAT: off while connected.
            if (tetherNic != null && Run("powershell", $"-NoProfile -Command \"(Get-NetAdapterBinding -Name '{Ps(tetherNic)}' -ComponentID ms_tcpip6).Enabled\"").output == "True")
            {
                Run("powershell", $"-NoProfile -Command \"Disable-NetAdapterBinding -Name '{Ps(tetherNic)}' -ComponentID ms_tcpip6\"");
                ipv6Nic = tetherNic;
            }
            StartHev(socksHost, socksPort);
            ConfigureAdapter();
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            stopping = true;
            // Delete routes first: if the adapter outlives hev, they would blackhole all traffic.
            if (ifIndex >= 0)
                foreach (var p in SplitRoutes)
                    Run("netsh", $"interface ipv4 delete route prefix={p} interface={ifIndex} store=active");
            ifIndex = -1;
            // Detach before killing: the old process's Exited event can fire later and must not hit a new session.
            var old = hev;
            hev = null;
            if (old is { HasExited: false }) { old.Kill(); old.WaitForExit(3000); }
            if (ipv6Nic != null)
                Run("powershell", $"-NoProfile -Command \"Enable-NetAdapterBinding -Name '{Ps(ipv6Nic)}' -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue\"");
            ipv6Nic = null;
            stopping = false;
        }
    }

    void StartHev(string socksHost, int socksPort)
    {
        // A previous tunnel's adapter can linger a moment after its process is killed; reusing the name too early breaks setup.
        for (var i = 0; i < 25 && NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == Adapter); i++) Thread.Sleep(200);

        // workDir is admin-only for the helper (Program Files): a config anyone could edit would let them redirect traffic.
        Directory.CreateDirectory(workDir);
        var cfg = Path.Combine(workDir, "danet-hev.yml");
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
                WorkingDirectory = Tools,
            },
            EnableRaisingEvents = true,
        };
        p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log("hev: " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log("hev: " + e.Data); };
        p.Exited += (s, _) => { if (!stopping && ReferenceEquals(s, hev)) Exited?.Invoke(); };
        hev = p;
        p.Start();
        KillWithApp.Add(p); // if this process dies for any reason, Windows kills the tunnel too (no orphaned routes)
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

    /// <summary>
    /// A tunnel left running by a crashed or force-closed older version keeps the PC's traffic pointed at a phone that
    /// may be gone. Kill ours (same tools folder) before anything else; its adapter and routes go with it.
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

    /// <summary>Android's USB-tethering adapter (RNDIS or NCM) and the phone's address on it (its DHCP gateway).</summary>
    public static (string name, string phone)? FindTether()
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
    public static bool Greet(string host, int port)
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

    /// <summary>
    /// Only ever the bundled copy: this code runs with admin rights, so falling back to whatever adb.exe / tunnel happens
    /// to be on PATH would run an arbitrary program elevated.
    /// </summary>
    public static string Tool(string exe)
    {
        var p = Path.Combine(Tools, exe);
        return File.Exists(p) ? p : throw new FileNotFoundException($"{exe} is missing from the app's tools folder. Reinstall Da Net Booster.");
    }

    /// <summary>Adapter names come from Windows/drivers; escape them for a single-quoted PowerShell string.</summary>
    static string Ps(string s) => s.Replace("'", "''");

    static void Check((int code, string output) r)
    {
        if (r.code != 0) throw new InvalidOperationException(r.output);
    }

    public static (int code, string output) Run(string exe, string args)
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
/// Windows Job Object with KILL_ON_JOB_CLOSE: processes added here die when this process ends for any reason
/// (normal exit, crash, Task Manager, service stop). Otherwise a stranded tunnel routes all traffic to nowhere.
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
