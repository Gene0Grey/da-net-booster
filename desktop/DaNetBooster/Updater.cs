using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace DaNetBooster;

/// <summary>
/// Velopack updates from this repo's GitHub Releases. Downloads in the background, then applies only when the
/// tunnel is down (on close, or when the user clicks "Restart to update") so a live match is never dropped.
/// </summary>
sealed class Updater(Action<string> log)
{
    /// <summary>https://github.com/owner/repo, stamped in by CI (-p:UpdateRepo). Empty in local builds = no updates.</summary>
    public static readonly string Repo = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value ?? "";

    /// <summary>Stable download link for the phone app, used by the first-run QR code.</summary>
    public static string PhoneApkUrl => Repo.Length == 0 ? "" : $"{Repo}/releases/latest/download/DaNetBooster.apk";

    public static string Version => typeof(Updater).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    UpdateManager? mgr;
    UpdateInfo? ready;

    /// <summary>Version downloaded and waiting to be applied, or null.</summary>
    public string? Ready => ready?.TargetFullRelease.Version.ToString();

    public async Task CheckAsync()
    {
        if (Repo.Length == 0 || ready != null) return;
        try
        {
            mgr ??= new UpdateManager(new GithubSource(Repo, null, false));
            if (!mgr.IsInstalled) return; // running from a dev build folder, not a Setup.exe install
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return;
            log($"Downloading update {info.TargetFullRelease.Version}...");
            await mgr.DownloadUpdatesAsync(info);
            ready = info;
            log($"Update {Ready} ready. It installs when you close the app or click Restart to update.");
        }
        catch (Exception e) { log("Update check failed: " + e.Message); } // offline or rate-limited: try again later
    }

    /// <summary>Install now and relaunch. Caller must have disconnected the tunnel first.</summary>
    public void ApplyAndRestart()
    {
        if (ready != null) mgr!.ApplyUpdatesAndRestart(ready);
    }

    /// <summary>Install after this process exits (app closing), without relaunching.</summary>
    public void ApplyOnExit()
    {
        if (ready != null) mgr!.WaitExitThenApplyUpdates(ready, silent: true, restart: false);
    }
}
