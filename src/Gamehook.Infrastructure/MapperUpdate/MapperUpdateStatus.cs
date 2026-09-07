namespace Gamehook.Infrastructure.MapperUpdate;

public enum MapperUpdateState
{
    Checking,
    UpToDate,
    Updated,
    Failed,
    Skipped,
}

public sealed record MapperUpdateStatus(
    MapperUpdateState State,
    MapperUpdateSource? Source,
    string? Reference,
    string? CommitSha,
    string? PreviousCommitSha,
    string? Message,
    DateTimeOffset CheckedAtUtc);

// Singleton so other components (logging aside, e.g. a future diagnostics view) can read the
// outcome of the startup update check without depending on the update service itself.
public sealed class MapperUpdateStatusProvider
{
    private MapperUpdateStatus current = new(
        MapperUpdateState.Checking, null, null, null, null, "Checking for mapper updates...", DateTimeOffset.UtcNow);

    public MapperUpdateStatus Current => current;

    public event Action? Changed;

    public void SetSkipped(string message) =>
        Set(new MapperUpdateStatus(MapperUpdateState.Skipped, null, null, null, null, message, DateTimeOffset.UtcNow));

    public void SetUpToDate(MapperUpdateSource source, string reference, string commitSha) =>
        Set(new MapperUpdateStatus(MapperUpdateState.UpToDate, source, reference, commitSha, commitSha, "Mappers are up to date.", DateTimeOffset.UtcNow));

    public void SetUpdated(MapperUpdateSource source, string reference, string commitSha, string? previousCommitSha) =>
        Set(new MapperUpdateStatus(MapperUpdateState.Updated, source, reference, commitSha, previousCommitSha, "Mappers were updated.", DateTimeOffset.UtcNow));

    public void SetFailed(MapperUpdateSource? source, string message) =>
        Set(new MapperUpdateStatus(MapperUpdateState.Failed, source, null, null, null, message, DateTimeOffset.UtcNow));

    private void Set(MapperUpdateStatus status)
    {
        current = status;
        Changed?.Invoke();
    }
}
