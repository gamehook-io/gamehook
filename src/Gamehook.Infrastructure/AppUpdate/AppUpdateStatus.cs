namespace Gamehook.Infrastructure.AppUpdate;

public enum AppUpdateState
{
    Checking,
    UpToDate,
    Downloading,
    ReadyToApply,
    Failed,
    Skipped,
}

public sealed record AppUpdateStatus(
    AppUpdateState State,
    string? Version,
    string? Message,
    DateTimeOffset CheckedAtUtc);

// Singleton so the UI can read the outcome of the startup update check - and offer a "Restart to
// Update" action once a download completes - without depending on AppUpdateService itself.
public sealed class AppUpdateStatusProvider
{
    private AppUpdateStatus current = new(AppUpdateState.Checking, null, "Checking for updates...", DateTimeOffset.UtcNow);

    public AppUpdateStatus Current => current;

    public event Action? Changed;

    public void SetSkipped(string message) => Set(new AppUpdateStatus(AppUpdateState.Skipped, null, message, DateTimeOffset.UtcNow));

    public void SetUpToDate() => Set(new AppUpdateStatus(AppUpdateState.UpToDate, null, "Gamehook is up to date.", DateTimeOffset.UtcNow));

    public void SetDownloading(string version) =>
        Set(new AppUpdateStatus(AppUpdateState.Downloading, version, $"Downloading Gamehook {version}...", DateTimeOffset.UtcNow));

    public void SetReadyToApply(string version) =>
        Set(new AppUpdateStatus(AppUpdateState.ReadyToApply, version, $"Gamehook {version} is ready. Restart to update.", DateTimeOffset.UtcNow));

    public void SetFailed(string message) => Set(new AppUpdateStatus(AppUpdateState.Failed, null, message, DateTimeOffset.UtcNow));

    private void Set(AppUpdateStatus status)
    {
        current = status;
        Changed?.Invoke();
    }
}
