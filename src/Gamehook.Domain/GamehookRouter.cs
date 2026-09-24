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

    /// A driver and mapper are loaded, the last read succeeded, and nothing is being warned about.
    public bool IsHealthy =>
        DriverName is not null
        && Session.IsConnected
        && !Session.IsConnecting
        && Session.ConnectionWarning is null
        && Session.DataWarning is null;

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
                return Fail(conflict);
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
            ? Fail("No driver selected.")
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
    private bool WritesDisabled => !Session.IsContinuousReadEnabled;

    private static Task<(bool Success, string? Error)> Fail(string error) => Task.FromResult<(bool, string?)>((false, error));

    public Task<(bool Success, string? Error)> WritePropertyValueAsync(string path, object? value, CancellationToken cancellationToken = default)
    {
        if (WritesDisabled) return Fail(ContinuousReadDisabledWriteError);
        return Session.Mapper is { } mapper
            ? mapper.WriteAsync(path, value, cancellationToken)
            : Fail("No mapper loaded.");
    }

    /// Raw byte poke scoped to one property's own address/region - reuses the same region-relative
    /// offset convention as WriteRawBytesAsync (see BuildRequest).
    public Task<(bool Success, string? Error)> WritePropertyBytesAsync(IProperty property, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (WritesDisabled) return Fail(ContinuousReadDisabledWriteError);
        if (Session.Mapper is not { } mapper) return Fail("No mapper loaded.");
        if (!property.IsWritable || property.BuildRequest() is not { } request)
            return Fail("Property has no fixed address; write 'value' instead.");

        return mapper.WriteRawBytesAsync(request.RegionId, request.StartingAddress, bytes, cancellationToken);
    }

    /// Prefix of a native processor's virtual regions: read-only buffers that never exist on the device.
    public const string VirtualRegionPrefix = "virtual:";

    /// Regions a memory viewer can show: every system region with a known size, plus the loaded
    /// mapper's native-processor regions as "virtual:{id}".
    public IReadOnlyList<string> RegionIds => Session.Mapper is not { } mapper
        ? []
        : mapper.System.RegionDefinitions.Where(region => region.Length is > 0).Select(region => region.Id)
            .Concat(mapper.VirtualMemoryRegions.Select(region => VirtualRegionPrefix + region.Id))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// Resolves a device region id case-insensitively against the loaded mapper's system (e.g. "wram" -> "WRAM").
    public string? ResolveRegionId(string region) => Session.Mapper?.System.FindRegion(region)?.Id;

    /// Reads a whole region (offset and length both omitted) or a slice of it. Virtual regions come
    /// from the mapper's last native-processor output; device regions from the mapper's driver.
    public async Task<(ReadOnlyMemory<byte>? Bytes, string? Error)> ReadRegionAsync(string region, ulong? offset = null, int? length = null)
    {
        if (Session.Mapper is not { } mapper) return (null, "No mapper loaded.");
        if (Session.IsConnecting) return (null, "The mapper is still connecting.");
        if (offset.HasValue != length.HasValue) return (null, "address and length must be supplied together.");

        if (region.StartsWith(VirtualRegionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = region[VirtualRegionPrefix.Length..];
            if (mapper.VirtualMemoryRegions.FirstOrDefault(r => r.Id == id) is not { } virtualRegion)
                return (null, $"Unknown region '{region}'.");
            if (offset is not { } start || length is not { } count) return (virtualRegion.Bytes, null);
            return start <= (ulong)virtualRegion.Bytes.Length && (ulong)count <= (ulong)virtualRegion.Bytes.Length - start
                ? (virtualRegion.Bytes.Slice((int)start, count), null)
                : (null, $"The requested range is outside '{region}'.");
        }

        if (mapper.System.FindRegion(region) is not { } definition) return (null, $"Unknown region '{region}'.");
        if (mapper.MemoryDriver is not { } driver) return (null, "No driver connected.");
        if ((length ?? definition.Length) is not { } resolvedLength)
            return (null, $"Region '{region}' has no known size; supply address & length.");

        var request = new IDriver.MemorySegmentRequest(definition.Id, offset ?? 0, resolvedLength);
        var response = await driver.Read(new IDriver.Request(mapper.System, [request])).ConfigureAwait(false);
        var segment = response.Segments.FirstOrDefault(s => s.RegionId == definition.Id);
        return segment is null ? (null, $"Driver returned no data for '{region}'.") : (segment.Bytes, null);
    }

    public Task<(bool Success, string? Error)> WriteDriverRegionAsync(string region, ulong offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (WritesDisabled) return Fail(ContinuousReadDisabledWriteError);
        if (Session.Mapper is not { } mapper) return Fail("No mapper loaded.");
        if (region.StartsWith(VirtualRegionPrefix, StringComparison.OrdinalIgnoreCase)) return Fail($"Region '{region}' is read-only.");
        var regionId = ResolveRegionId(region);
        if (regionId is null) return Fail($"Unknown region '{region}'.");

        return mapper.WriteRawBytesAsync(regionId, offset, bytes, cancellationToken);
    }
}
