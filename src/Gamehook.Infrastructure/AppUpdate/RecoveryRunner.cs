using Velopack;
using Velopack.Sources;

namespace Gamehook.Infrastructure.AppUpdate;

// Runs before the application host so repair remains available when normal startup fails.
// Uses the published GitHub Releases by default, or a custom HTTPS feed supplied with --feed.
// If no replacement is available, automatic recovery permits another normal startup attempt.
public static class RecoveryRunner
{
    public static int Run(string[] args)
    {
        try { return RunCore(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Recovery failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunCore(string[] args)
    {
        var feedUrl = GetArgValue(args, "--feed");
        var options = new UpdateOptions { AllowVersionDowngrade = true };
        var manager = new UpdateManager(UpdateSourceFactory.Create(feedUrl), options);

        if (!manager.IsInstalled)
        {
            Console.WriteLine("Gamehook is not running as an installed build; nothing to recover.");
            return 1;
        }

        Console.WriteLine($"Current version: {manager.CurrentVersion}");
        Console.WriteLine($"Checking {feedUrl ?? UpdateSourceFactory.RepositoryUrl} for the published release...");

        UpdateInfo? updateInfo;
        try
        {
            updateInfo = manager.CheckForUpdates();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Recovery check failed: {ex.Message}");
            return 1;
        }

        if (updateInfo is null)
        {
            Console.WriteLine("Already on the version currently published to the feed. Nothing to do.");
            return 0;
        }

        var target = updateInfo.TargetFullRelease;
        Console.WriteLine((updateInfo.IsDowngrade ? "Rolling back to " : "Updating to ") + $"{target.Version}...");

        manager.DownloadUpdates(updateInfo);
        manager.ApplyUpdatesAndRestart(target, restartArgs: []);
        return 0;
    }

    private static string? GetArgValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
