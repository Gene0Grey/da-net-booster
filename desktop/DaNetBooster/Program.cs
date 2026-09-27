namespace DaNetBooster;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Must run first: handles Velopack's install/update/uninstall hooks, then returns for a normal launch.
        Velopack.VelopackApp.Build().Run();
        ApplicationConfiguration.Initialize();
        // One copy only: a second one would fight the first over the tunnel adapter and routes.
        using var single = new Mutex(true, @"Local\DaNetBooster.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Da Net Booster is already running. Look for it in the taskbar.", "Da Net Booster");
            return;
        }
        // Never show the "Unhandled exception" dialog mid-game: record it and keep the tunnel running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Write("UNHANDLED: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("FATAL: " + e.ExceptionObject);
        AppLog.Write($"--- app start v{Updater.Version} ---");
        Application.Run(new MainForm());
    }
}

/// <summary>Plain text log at %LOCALAPPDATA%\DaNetBooster\danet.log, so a problem can be read after the fact.</summary>
static class AppLog
{
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaNetBooster", "danet.log");
    static readonly object Gate = new();

    public static void Write(string s)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 2_000_000) File.Delete(Path); // ponytail: crude cap, rotate if history matters
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}");
            }
        }
        catch (Exception) { } // logging must never throw: the crash handlers call this too
    }
}
