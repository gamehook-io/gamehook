using GameHook.Domain.Interface;

namespace GameHook.Domain;

/// Host-agnostic operations layered on top of GameHookSession: load-a-mapper-by-key, select a
/// driver by name, read/write a property by dotted path, and raw region read/write with the
/// region-relative-offset math BuildRequest/WriteRawBytesAsync already use. Exists so the REST API
/// and any other host (Avalonia UI included) call the same code instead of each re-deriving it.
public sealed class GameHookRouter
{
    public GameHookRouter(GameHookSession session)
    {
        Session = session;
    }

    public GameHookSession Session { get; }

    public string? DriverName { get; private set; }
    public string? DriverSourcePath { get; private set; }
    private string? mapperPath;

    public IMapper? Mapper => Session.Mapper;

    /// The one real load path - every host (UI's Load button, the API's POST /mapper, the API's
    /// POST /driver reload-in-place) funnels through this. Remembers the driver/mapper it was
    /// given so a later single-argument call (SetDriverAsync, LoadMapperAsync) can reuse them.
    public async Task<(bool Success, string? Error)> LoadAsync(string mapperPath, string driverName, string? driverSourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);

        var success = await Session.LoadAsync(mapperPath, driverName, driverSourcePath, cancellationToken).ConfigureAwait(false);
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
            DriverName = driverName;
            DriverSourcePath = sourcePath;
            return Task.FromResult<(bool, string?)>((true, null));
        }

        return LoadAsync(mapperPath, driverName, sourcePath, cancellationToken);
    }

    public Task<(bool Success, string? Error)> LoadMapperAsync(string mapperPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);
        return DriverName is null
            ? Task.FromResult<(bool, string?)>((false, "No driver selected."))
            : LoadAsync(mapperPath, DriverName, DriverSourcePath, cancellationToken);
    }

    /// Unloads the active mapper. Keeps the selected driver (mirrors the UI: going back to the
    /// load screen doesn't clear the driver dropdown) but forgets the mapper path, so a later
    /// SetDriverAsync no longer tries to reload a mapper that is no longer active.
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

    public Task<(bool Success, string? Error)> WritePropertyValueAsync(string path, object? value, CancellationToken cancellationToken = default) =>
        Session.Mapper is { } mapper
            ? mapper.WriteAsync(path, value, cancellationToken)
            : Task.FromResult<(bool, string?)>((false, "No mapper loaded."));

    /// Raw byte poke scoped to one property's own address/region - reuses the same region-relative
    /// offset convention as WriteRawBytesAsync (see BuildRequest).
    public Task<(bool Success, string? Error)> WritePropertyBytesAsync(IProperty property, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
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
