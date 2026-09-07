using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Gamehook.Infrastructure.AppUpdate;

// Checks the configured release source at startup, downloads updates in the background,
// and exposes an explicit restart action. Downloads are cancelled and awaited on shutdown.
// A custom feed may publish an older version for recovery; normal releases use GitHub.
public sealed class AppUpdateService(
    IConfiguration configuration,
    AppUpdateStatusProvider statusProvider,
    ILogger<AppUpdateService> logger) : IHostedService, IDisposable
{
    private UpdateManager? manager;
    private UpdateInfo? pendingUpdate;
    private readonly CancellationTokenSource stopping = new();
    private Task downloadTask = Task.CompletedTask;
    private bool disposed;

    public Task StartAsync(CancellationToken cancellationToken) => CheckForUpdatesAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        await downloadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        stopping.Dispose();
    }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!ParseBool(configuration["AppUpdateEnabled"], defaultValue: true))
        {
            statusProvider.SetSkipped("Automatic app updates are disabled (AppUpdateEnabled=false).");
            return;
        }

        try
        {
            var feedUrl = configuration["AppUpdateFeedUrl"];
            // AllowVersionDowngrade is what makes this a recovery path, not just an update check -
            // if a bad release gets pulled from the feed and the last-good version republished as
            // current, every installed copy self-heals on its next check, not only ones caught
            // mid crash-loop by CrashGuard/RecoveryRunner.
            var um = manager ??= new UpdateManager(UpdateSourceFactory.Create(feedUrl), new UpdateOptions { AllowVersionDowngrade = true });

            if (!um.IsInstalled)
            {
                // Running from `dotnet run` / an unpackaged build - there's no installed app to
                // replace, so a check would just fail. Nothing to log as an error here.
                statusProvider.SetSkipped("Not running as an installed build; automatic updates are disabled.");
                return;
            }

            logger.LogInformation("Checking {FeedUrl} for app updates.", feedUrl ?? UpdateSourceFactory.RepositoryUrl);
            var updateInfo = await um.CheckForUpdatesAsync()
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (updateInfo is null)
            {
                logger.LogInformation("Gamehook is up to date.");
                statusProvider.SetUpToDate();
                return;
            }

            var version = updateInfo.TargetFullRelease.Version.ToString();
            logger.LogInformation("Update found: Gamehook {Version}. Downloading in the background.", version);
            statusProvider.SetDownloading(version);

            // Deliberately not awaited - see the type-level comment. StartAsync (and so
            // Host.Start()/MapperUpdateService right after it) only waits for the check above.
            downloadTask = DownloadInBackgroundAsync(um, updateInfo, version, stopping.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "App update check failed.");
            statusProvider.SetFailed(ex.Message);
        }
    }

    private async Task DownloadInBackgroundAsync(UpdateManager um, UpdateInfo updateInfo, string version, CancellationToken cancellationToken)
    {
        try
        {
            await um.DownloadUpdatesAsync(updateInfo, cancelToken: cancellationToken).ConfigureAwait(false);

            pendingUpdate = updateInfo;
            logger.LogInformation("Gamehook {Version} downloaded and verified; ready to apply.", version);
            statusProvider.SetReadyToApply(version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "App update download failed.");
            statusProvider.SetFailed(ex.Message);
        }
    }

    // Exits the process and relaunches on the new version. Only valid to call once
    // AppUpdateStatusProvider has reported ReadyToApply - Velopack itself is what makes this
    // atomic (it swaps the "current" version pointer only after the new files are fully staged).
    public void ApplyUpdateAndRestart()
    {
        if (manager is { } um && pendingUpdate is { } updateInfo)
        {
            um.ApplyUpdatesAndRestart(updateInfo);
        }
    }

    private static bool ParseBool(string? value, bool defaultValue) =>
        bool.TryParse(value, out var result) ? result : defaultValue;
}
