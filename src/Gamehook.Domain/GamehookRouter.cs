using Gamehook.Domain.Interface;

namespace Gamehook.Domain;

/// Host-agnostic operations layered on top of GamehookSession: load-a-mapper-by-key, select a
/// driver by name, read/write a property by dotted path, and raw region read/write with the
/// region-relative-offset math BuildRequest/WriteRawBytesAsync already use. Exists so the REST API
/// and any other host (Avalonia UI included) call the same code instead of each re-deriving it.
public sealed class GamehookRouter
{
    private readonly IDriverReservations? reservations;

    public GamehookRouter(GamehookSession session, IDriverReservations? reservations = null)
    {
        Session = session;
        this.reservations = reservations;
    }

    public GamehookSession Session { get; }

    // Match display updates to a 60 Hz frame budget. RefreshAsync remains non-overlapping, so a
    // slower driver naturally backs this off instead of queuing reads.
    public static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(16);

    public string? DriverName { get; private set; }
    public string? DriverSourcePath { get; private set; }

    /// Path of the mapper last loaded successfully; null after a failed load or Unload.
    public string? MapperPath => mapperPath;
    private string? mapperPath;

    public IMapper? Mapper => Session.Mapper;

    /// The one real load path - every host (UI's Load button, the API's POST /mapper, the API's
    /// POST /driver reload-in-place) funnels through this. Remembers the driver/mapper it was
    /// given so a later single-argument call (SetDriverAsync, LoadMapperAsync) can reuse them.
    /// Starts polling on success regardless of host; the session holds off the loop itself while
    /// continuous read mode is disabled. Refuses, leaving the current load untouched, when another
    /// instance is already using the same driver endpoint.
    public async Task<(bool Success, string? Error)> LoadAsync(string mapperPath, string driverName, string? driverSourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);

        var alreadyHeld = false;
        if (reservations?.TryReserve(this, driverName, driverSourcePath, out alreadyHeld) is { } conflict)
            return (false, conflict);

        // A failed first connection gives the endpoint back; a failed reload (e.g. the emulator was
        // closed) keeps it, so another instance can't take it while this one retries.
        bool success;
        try
        {
            success = await Session.LoadAsync(mapperPath, driverName, driverSourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!alreadyHeld) reservations?.Release(this);
            throw;
        }

        if (success) Session.StartPolling(PollingInterval);
        else if (!alreadyHeld) reservations?.Release(this);
        DriverName = driverName;
        DriverSourcePath = driverSourcePath;
        this.mapperPath = success ? mapperPath : null;
        return success ? (true, null) : (false, Session.Status);
    }

    /// Selects the driver used by the next LoadMapperAsync call. If a mapper is already loaded,
    /// reloads it immediately against the new driver so Session/Mapper never observe a mismatch
    /// between "selected driver" and "driver actually in use".
    public Task<(bool Success, string? Error)> SetDriverAsync(string driverName, string? sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);
        if (mapperPath is null)
        {
            if (FindDriverConflict(driverName, sourcePath) is { } conflict)
                return Task.FromResult<(bool, string?)>((false, conflict));
            DriverName = driverName;
            DriverSourcePath = sourcePath;
            return Task.FromResult<(bool, string?)>((true, null));
        }

        return LoadAsync(mapperPath, driverName, sourcePath, cancellationToken);
    }

    /// Error message when another instance is already using this driver endpoint, else null.
    public string? FindDriverConflict(string driverName, string? sourcePath) =>
        reservations?.FindConflict(this, driverName, sourcePath);

    public Task<(bool Success, string? Error)> LoadMapperAsync(string mapperPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);
        return DriverName is null
            ? Task.FromResult<(bool, string?)>((false, "No driver selected."))
            : LoadAsync(mapperPath, DriverName, DriverSourcePath, cancellationToken);
    }

    /// Unloads the active mapper. Keeps the selected driver - and its endpoint reservation - so the
    /// UI can load another mapper on the same driver without another instance taking it meanwhile,
    /// but forgets the mapper path, so a later SetDriverAsync no longer tries to reload a mapper
    /// that is no longer active.
    public void Unload()
    {
        Session.Unload();
        mapperPath = null;
    }

    public IReadOnlyDictionary<string, IProperty>? Properties => Session.Mapper?.Properties;

    public bool TryGetProperty(string path, out IProperty property)
    {
        if (Session.Mapper is { } mapper && mapper.Properties.TryGetValue(path, out var found))
        {
            property = found;
            return true;
        }
        property = null!;
        return false;
    }

    public const string ContinuousReadDisabledWriteError = "Writing is unavailable while continuous read mode is disabled.";

    // Every host's writes (REST API, property inspector, hex editor, popped-out inspector windows)
    // come through the three Write* methods below, so continuous read mode's "no writes" rule is enforced
    // here once rather than at each call site.
    private bool TryRejectWrite(out Task<(bool Success, string? Error)> rejection)
    {
        if (Session.IsContinuousReadEnabled)
        {
            rejection = null!;
            return false;
        }

        rejection = Task.FromResult<(bool, string?)>((false, ContinuousReadDisabledWriteError));
        return true;
    }

    public Task<(bool Success, string? Error)> WritePropertyValueAsync(string path, object? value, CancellationToken cancellationToken = default)
    {
        if (TryRejectWrite(out var rejection)) return rejection;
        return Session.Mapper is { } mapper
            ? mapper.WriteAsync(path, value, cancellationToken)
            : Task.FromResult<(bool, string?)>((false, "No mapper loaded."));
    }

    /// Raw byte poke scoped to one property's own address/region - reuses the same region-relative
    /// offset convention as WriteRawBytesAsync (see BuildRequest).
    public Task<(bool Success, string? Error)> WritePropertyBytesAsync(IProperty property, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (TryRejectWrite(out var rejection)) return rejection;
        if (Session.Mapper is not { } mapper) return Task.FromResult<(bool, string?)>((false, "No mapper loaded."));
        if (property.Address is not { } address || property.Region is not { } region)
            return Task.FromResult<(bool, string?)>((false, "Property has no fixed address; write 'value' instead."));
        if (!TryResolveOffset(mapper.System, region, address, out var offset, out var error))
            return Task.FromResult<(bool, string?)>((false, error));

        return mapper.WriteRawBytesAsync(region, offset, bytes, cancellationToken);
    }

    /// Resolves a region id case-insensitively against the loaded mapper's system (e.g. "wram" -> "WRAM").
    public string? ResolveRegionId(string region) =>
        Session.Mapper is { } mapper
            ? mapper.System.RegionDefinitions.FirstOrDefault(r => string.Equals(r.Id, region, StringComparison.OrdinalIgnoreCase))?.Id
            : null;

    public async Task<(ReadOnlyMemory<byte>? Bytes, string? Error)> ReadDriverRegionAsync(string region, ulong? offset, int? length)
    {
        if (Session.Mapper is not { } mapper) return (null, "No mapper loaded.");
        if (Session.HexDriver is not { } driver) return (null, "No driver connected.");

        var regionId = ResolveRegionId(region);
        if (regionId is null) return (null, $"Unknown region '{region}'.");

        ulong resolvedOffset;
        int resolvedLength;
        if (offset is null && length is null)
        {
            var definition = mapper.System.RegionDefinitions.First(r => r.Id == regionId);
            if (definition.Length is not { } wholeLength)
                return (null, $"Region '{region}' has no known size; supply address & length.");
            resolvedOffset = 0;
            resolvedLength = wholeLength;
        }
        else if (offset is { } o && length is { } l)
        {
            resolvedOffset = o;
            resolvedLength = l;
        }
        else
        {
            return (null, "address and length must be supplied together.");
        }

        var response = await driver.Read(new IDriver.Request(mapper.System, [new IDriver.MemorySegmentRequest(regionId, resolvedOffset, resolvedLength)])).ConfigureAwait(false);
        var segment = response.Segments.FirstOrDefault(s => s.RegionId == regionId);
        return segment is null ? (null, $"Driver returned no data for '{region}'.") : (segment.Bytes, null);
    }

    public Task<(bool Success, string? Error)> WriteDriverRegionAsync(string region, ulong offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (TryRejectWrite(out var rejection)) return rejection;
        if (Session.Mapper is not { } mapper) return Task.FromResult<(bool, string?)>((false, "No mapper loaded."));
        var regionId = ResolveRegionId(region);
        if (regionId is null) return Task.FromResult<(bool, string?)>((false, $"Unknown region '{region}'."));

        return mapper.WriteRawBytesAsync(regionId, offset, bytes, cancellationToken);
    }

    private static bool TryResolveOffset(GameSystem system, string regionId, ulong absoluteAddress, out ulong offset, out string? error)
    {
        var definition = system.RegionDefinitions.FirstOrDefault(r => r.Id == regionId);
        if (definition?.BusAddress is not { } busAddress)
        {
            offset = 0;
            error = $"Region '{regionId}' has no known base address.";
            return false;
        }
        offset = absoluteAddress - busAddress;
        error = null;
        return true;
    }
}
