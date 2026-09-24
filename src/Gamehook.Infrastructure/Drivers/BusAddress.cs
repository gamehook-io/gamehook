using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Infrastructure.Drivers;

/// Maps a region-relative segment onto the emulator's CPU bus address, as network drivers address it.
internal static class BusAddress
{
    /// Null when the system has no known base address for the region.
    public static uint? Resolve(GameSystem system, string regionId, ulong offset, int length)
    {
        if (system.FindRegion(regionId)?.BusAddress is not { } baseAddress)
            return null;
        if (offset > uint.MaxValue - baseAddress || (ulong)length > (ulong)uint.MaxValue - baseAddress - offset + 1)
            throw new ArgumentOutOfRangeException(nameof(offset), "Requested segment exceeds the address space.");
        return baseAddress + (uint)offset;
    }

    public static uint Require(GameSystem system, IDriver.MemorySegmentWrite segment) =>
        Resolve(system, segment.RegionId, segment.StartingAddress, segment.Bytes.Length)
        ?? throw new NotSupportedException($"No known base address for region '{segment.RegionId}' on {system.Id}.");

    // Regions the driver can't address at all (no known base address for this system) are simply
    // left out - Property.Refresh reports those as a null value rather than failing the whole read.
    public static List<(IDriver.MemorySegmentRequest Segment, uint Address)> ResolveReadable(IDriver.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var addressable = new List<(IDriver.MemorySegmentRequest, uint)>(request.Segments.Count);
        foreach (var segment in request.Segments)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(segment.Length, nameof(request));
            if (Resolve(request.System, segment.RegionId, segment.StartingAddress, segment.Length) is { } address)
                addressable.Add((segment, address));
        }
        return addressable;
    }
}
