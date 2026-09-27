using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using TimeoutException = System.TimeoutException;

namespace DaNetBooster;

/// <summary>
/// The admin helper: a LocalSystem Windows service installed once into Program Files, so the app itself never needs
/// admin. It speaks a tiny line protocol over a local named pipe and does only what TunnelCore does.
///
/// Security model:
/// - Its files live in Program Files (admin-only writes). Running helper code from the user-writable install folder
///   would let any non-admin program take over the service.
/// - CONNECT only accepts the address that the helper itself sees as the phone on a USB-tethering adapter, so no other
///   program can use it to point the PC's traffic somewhere else.
/// - The tunnel lives exactly as long as the app's pipe connection: app closed or crashed means tunnel down.
/// </summary>
static class Helper
{
    public const string ServiceName = "DaNetBooster";
    const string PipeName = "DaNetBooster.Helper";
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DaNetBooster");

    // ---------------------------------------------------------------- service side

    /// <summary>Entry for `--service`. Started by the SCM normally; run interactively it serves in the foreground (for testing).</summary>
    public static void RunService()
    {
        if (Environment.UserInteractive) { Serve(CancellationToken.None).GetAwaiter().GetResult(); return; }
        ServiceBase.Run(new Svc());
    }

    sealed class Svc : ServiceBase
    {
        readonly CancellationTokenSource cts = new();
        public Svc() { ServiceName = Helper.ServiceName; CanStop = true; }
        protected override void OnStart(string[] args) => Task.Run(() => Serve(cts.Token));
        protected override void OnStop() { cts.Cancel(); core?.Stop(); }
    }

    static TunnelCore? core;

    static async Task Serve(CancellationToken ct)
    {
        AppLog.Target = Path.Combine(AppContext.BaseDirectory, "logs", "helper.log");
        AppLog.Write($"--- helper start v{Updater.Version} ---");
        core = new TunnelCore(AppLog.Write, Path.Combine(AppContext.BaseDirectory, "run"));
        TunnelCore.CleanupOrphans(AppLog.Write);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // One client at a time: the tunnel belongs to whoever holds this connection.
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, PipeAcl());
                await pipe.WaitForConnectionAsync(ct);
                try { await Session(pipe, ct); }
                finally { core.Stop(); } // the app went away (closed, crashed): never leave the tunnel up without it
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { AppLog.Write("helper: " + e.Message); await Task.Delay(500, CancellationToken.None); }
        }
        core.Stop();
    }

    static async Task Session(Stream pipe, CancellationToken ct)
    {
        using var w = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = new StringBuilder();
        var buf = new byte[1];
        while (await pipe.ReadAsync(buf, ct) == 1)
        {
            if (buf[0] != '\n')
            {
                if (line.Length >= 128) return; // commands are tiny; anything longer is garbage, drop the client
                line.Append((char)buf[0]);
                continue;
            }
            await w.WriteLineAsync(Handle(line.ToString().TrimEnd('\r')));
            line.Clear();
        }
    }

    static string Handle(string line)
    {
        var parts = line.Split(' ', 2);
        switch (parts[0])
        {
            case "HELLO":
                return "OK " + Updater.Version;
            case "STATUS":
                return core!.Running ? "OK running" : "OK stopped";
            case "DISCONNECT":
                core!.Stop();
                return "OK";
            case "CONNECT":
                if (parts.Length < 2 || !IPAddress.TryParse(parts[1], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                    return "ERR bad address";
                // Only the phone on a USB-tethering adapter, as seen by the helper itself.
                if (TunnelCore.FindTether() is not { } t || t.phone != ip.ToString())
                    return "ERR That address isn't a phone on USB tethering";
                try
                {
                    core!.Start(t.phone, TunnelCore.PhonePort, t.name);
                    return "OK";
                }
                catch (Exception e)
                {
                    core!.Stop();
                    return "ERR " + e.Message.ReplaceLineEndings(" ");
                }
            default:
                return "ERR unknown command";
        }
    }

    /// <summary>Signed-in users may talk to the helper; nothing from the network; SYSTEM/admins full control.</summary>
    static PipeSecurity PipeAcl()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return s;
    }

    // ---------------------------------------------------------------- install / uninstall (run elevated)

    /// <summary>`--install-service`: copy this build into Program Files and (re)register the service. Also used to update it.</summary>
    public static int Install()
    {
        if (!Tunnel.Elevated) return 5; // ERROR_ACCESS_DENIED
        try
        {
            StopService();
            var src = AppContext.BaseDirectory.TrimEnd('\\');
            var dst = InstallDir;
            if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) CopyDir(src, dst);
            var bin = $"\"\\\"{Path.Combine(dst, "DaNetBooster.exe")}\\\" --service\"";
            var exists = ServiceController.GetServices().Any(s => s.ServiceName == ServiceName);
            Sc(exists ? $"config {ServiceName} binPath= {bin} start= auto"
                      : $"create {ServiceName} binPath= {bin} start= auto DisplayName= \"Da Net Booster helper\"");
            Sc($"description {ServiceName} \"Creates the Da Net Booster network adapter and routes, so the app itself doesn't need admin.\"");
            Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
            using var sc = new ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            return 0;
        }
        catch (Exception e)
        {
            AppLog.Write("helper install failed: " + e);
            return 1;
        }
    }

    /// <summary>`--uninstall-service`: stop and remove the service and its Program Files copy.</summary>
    public static int Uninstall()
    {
        if (!Tunnel.Elevated) return 5;
        try
        {
            StopService();
            if (ServiceController.GetServices().Any(s => s.ServiceName == ServiceName)) Sc($"delete {ServiceName}");
            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true);
            return 0;
        }
        catch (Exception e)
        {
            AppLog.Write("helper uninstall failed: " + e);
            return 1;
        }
    }

    /// <summary>Velopack uninstall hook (runs as the user): ask Windows for admin once to remove the helper too.</summary>
    public static void RequestUninstall()
    {
        if (!ServiceController.GetServices().Any(s => s.ServiceName == ServiceName)) return;
        RunElevated("--uninstall-service", TimeSpan.FromSeconds(60));
    }

    /// <summary>Runs this exe elevated (UAC prompt) and waits. False if the user declined or it failed.</summary>
    public static bool RunElevated(string args, TimeSpan wait)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return p != null && p.WaitForExit(wait) && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // "No" on the UAC prompt
    }

    static void StopService()
    {
        if (!ServiceController.GetServices().Any(s => s.ServiceName == ServiceName)) return;
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
    }

    static void Sc(string args)
    {
        var r = TunnelCore.Run("sc.exe", args);
        if (r.code != 0) throw new InvalidOperationException($"sc {args}: {r.output}");
    }

    static void CopyDir(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: true);
    }

    // ---------------------------------------------------------------- app side

    /// <summary>The helper's version, or null when it isn't installed/running. Only call while not connected.</summary>
    public static string? Version()
    {
        try
        {
            using var c = new HelperClient(_ => { });
            return c.Hello();
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>App-side connection to the helper. The tunnel stays up only while this connection is open.</summary>
    public sealed class HelperClient(Action<string> log) : ITunnelBackend, IDisposable
    {
        readonly object gate = new();
        NamedPipeClientStream? pipe;
        StreamReader? reader;
        StreamWriter? writer;
        bool running;

        public event Action? Exited { add { } remove { } } // helper-side failures surface through Alive()
        public bool Running => running;

        public string Hello()
        {
            var reply = Call("HELLO", 3000);
            return reply.StartsWith("OK ") ? reply[3..] : throw new IOException("helper said: " + reply);
        }

        public void Start(string socksHost, int socksPort, string? tetherNic)
        {
            var reply = Call($"CONNECT {socksHost}", 60000); // creating the adapter and routes takes a few seconds
            if (reply != "OK") throw new InvalidOperationException(reply.StartsWith("ERR ") ? reply[4..] : "Helper: " + reply);
            running = true;
            log("Helper started the tunnel.");
        }

        public void Stop()
        {
            if (pipe == null) return;
            try { Call("DISCONNECT", 15000); } catch (Exception e) when (e is IOException or TimeoutException) { }
            Close();
        }

        public bool Alive()
        {
            if (!running) return false;
            try { return Call("STATUS", 5000) == "OK running"; }
            catch (Exception e) when (e is IOException or TimeoutException) { Close(); return false; }
        }

        string Call(string line, int timeoutMs)
        {
            lock (gate)
            {
                if (pipe is not { IsConnected: true })
                {
                    Close();
                    pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                    pipe.Connect(2000);
                    reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                    writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                }
                writer!.WriteLine(line);
                var read = reader!.ReadLineAsync();
                if (!read.Wait(timeoutMs)) { Close(); throw new TimeoutException("the helper didn't answer"); }
                return read.Result ?? throw new IOException("the helper closed the connection");
            }
        }

        void Close()
        {
            running = false;
            reader?.Dispose(); writer?.Dispose(); pipe?.Dispose();
            reader = null; writer = null; pipe = null;
        }

        public void Dispose() => Close();
    }
}
