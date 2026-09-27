namespace DaNetBooster;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Helper-service modes come first: they must not touch Velopack, the UI, or the single-instance lock
        // (the elevated installer is launched while the app window is open).
        if (args.Contains("--service")) { Helper.RunService(); return 0; }
        if (args.Contains("--install-service")) return Helper.Install();
        if (args.Contains("--uninstall-service")) return Helper.Uninstall();

        // Handles Velopack's install/update/uninstall hooks, then returns for a normal launch.
        // Uninstalling the app also removes the helper service (one admin prompt).
        Velopack.VelopackApp.Build().OnBeforeUninstallFastCallback(_ => Helper.RequestUninstall()).Run();
        ApplicationConfiguration.Initialize();
        // One copy only: a second one would fight the first over the tunnel.
        using var single = new Mutex(true, @"Local\DaNetBooster.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Da Net Booster is already running. Look for it in the taskbar.", "Da Net Booster");
            return 0;
        }
        // Never show the "Unhandled exception" dialog mid-game: record it and keep the tunnel running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Write("UNHANDLED: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("FATAL: " + e.ExceptionObject);
        AppLog.Write($"--- app start v{Updater.Version}{(Tunnel.Elevated ? " (elevated)" : "")} ---");
        Application.Run(new MainForm());
        return 0;
    }
}

/// <summary>
/// Plain text log, so a problem can be read after the fact: %LOCALAPPDATA%\DaNetBooster\danet.log for the app,
/// Program Files\DaNetBooster\logs\helper.log for the helper service.
/// </summary>
static class AppLog
{
    public static string Target { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaNetBooster", "danet.log");
    static readonly object Gate = new();

    public static void Write(string s)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
                if (File.Exists(Target) && new FileInfo(Target).Length > 2_000_000) File.Delete(Target); // ponytail: crude cap, rotate if history matters
                File.AppendAllText(Target, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}");
            }
        }
        catch (Exception) { } // logging must never throw: the crash handlers call this too
    }
}
