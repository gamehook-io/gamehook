using GameHook.Domain;
using GameHook.Domain.Interface;

namespace GameHook.Domain.Property;

// Public rather than internal so Mapper (a different assembly) can resolve its own <memory> block
// declarations through the same address table Property uses, instead of duplicating it.
public static class MemoryRegion
{
    public static bool TryToRegion(ulong address, GameSystem? system, out string region)
    {
        if (TryResolve(address, system, out var definition))
        {
            region = definition.Id;
            return true;
        }
        region = "";
        return false;
    }

    public static string ToRegion(ulong address, GameSystem? system) => Resolve(address, system).Id;

    public static ulong ToOffset(ulong address, GameSystem? system) => address - Resolve(address, system).BusAddress!.Value;

    /// Non-throwing region lookup. Callers on the read path resolve an address once and cache the
    /// result - the throwing overloads above are for one-off/diagnostic use.
    public static bool TryResolve(ulong address, GameSystem? system, out MemoryRegionDefinition definition)
    {
        // Preserve the existing GB fallback for systems without property address translation.
        var regions = (system?.Id == "GBA" ? system : GameSystem.GB).RegionDefinitions;
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            if (region.BusAddress is { } start && region.Length is { } length &&
                address >= start && address - start < (ulong)length)
            {
                definition = region;
                return true;
            }
        }
        definition = null!;
        return false;
    }

    private static MemoryRegionDefinition Resolve(ulong address, GameSystem? system)
    {
        if (TryResolve(address, system, out var definition)) return definition;
        throw new NotSupportedException(system?.Id == "GBA"
            ? $"Address 0x{address:X} has no supported GBA memory region."
            : $"Address 0x{address:X} has no supported memory region.");
    }

    public static ReadOnlyMemory<byte> ReadBytes(
        IReadOnlyList<IDriver.MemorySegmentSnapshot> segments,
        string region,
        ulong offset,
        int length)
    {
        if (!TryReadBytes(segments, region, offset, length, out var bytes))
        {
            throw new InvalidDataException($"Driver response does not contain {length} byte(s) at {region}:0x{offset:X}.");
        }
        return bytes;
    }

    // Drivers may omit a region entirely (e.g. RetroArch has no core memory map for it) rather than
    // treat that as fatal - callers use this to fall back to a null property value instead of throwing.
    public static bool TryReadBytes(
        IReadOnlyList<IDriver.MemorySegmentSnapshot> segments,
        string region,
        ulong offset,
        int length,
        out ReadOnlyMemory<byte> bytes)
    {
        var hint = 0;
        return TryReadBytes(segments, region, offset, length, ref hint, out bytes);
    }

    // Thousands of properties re-run this against the same handful of segments every read, so the
    // caller passes a hint holding whichever segment matched last time. Segment order is stable
    // across reads, so the hint hits on the first probe and the scan below almost never runs.
    internal static bool TryReadBytes(
        IReadOnlyList<IDriver.MemorySegmentSnapshot> segments,
        string region,
        ulong offset,
        int length,
        ref int hint,
        out ReadOnlyMemory<byte> bytes)
    {
        var count = segments.Count;
        if ((uint)hint < (uint)count && Covers(segments[hint], region, offset, length))
        {
            bytes = Slice(segments[hint], offset, length);
            return true;
        }

        for (var index = 0; index < count; index++)
        {
            if (!Covers(segments[index], region, offset, length)) continue;
            hint = index;
            bytes = Slice(segments[index], offset, length);
            return true;
        }

        bytes = default;
        return false;
    }

    private static bool Covers(IDriver.MemorySegmentSnapshot segment, string region, ulong offset, int length) =>
        length >= 0 &&
        segment.RegionId == region &&
        segment.StartingAddress <= offset &&
        offset - segment.StartingAddress <= (ulong)segment.Bytes.Length &&
        (ulong)length <= (ulong)segment.Bytes.Length - (offset - segment.StartingAddress);

    private static ReadOnlyMemory<byte> Slice(IDriver.MemorySegmentSnapshot segment, ulong offset, int length) =>
        segment.Bytes.Slice(checked((int)(offset - segment.StartingAddress)), length);
}
