using System.Buffers.Binary;
using Gamehook.Domain.Interface;

namespace Gamehook.Domain.Property;

// Bound under a memory-region namespace for mapper scripts (currently memory.wram.get_uint32_le(addr)
// for Game Boy mappers), backed by the current read's driver snapshot - not a live device round-trip. Method names
// are deliberately snake_case, matching the JS call sites verbatim across the existing mapper .js
// files; they're data this app doesn't own, not code we get to rename.
public sealed class ScriptMemoryAccess(GameSystem? system)
{
    private IReadOnlyList<IDriver.MemorySegmentSnapshot> segments = [];

    public void UpdateSnapshot(IReadOnlyList<IDriver.MemorySegmentSnapshot> newSegments) => segments = newSegments;

    // Scalar readers decode straight out of snapshot. Preprocessors can call these hundreds of
    // times per frame, so avoid allocating an intermediate byte array.
    public double get_byte(double address) => Read((ulong)address, 1).Span[0];
    public double get_uint16_le(double address) => BinaryPrimitives.ReadUInt16LittleEndian(Read((ulong)address, 2).Span);
    public double get_uint32_le(double address) => BinaryPrimitives.ReadUInt32LittleEndian(Read((ulong)address, 4).Span);
    /// <summary>CLR counterpart to script readers, backed by current read snapshot.</summary>
    public ReadOnlyMemory<byte> ReadBytes(ulong address, int length) => Read(address, length);

    private ReadOnlyMemory<byte> Read(ulong address, int length)
    {
        if (!MemoryRegion.TryResolve(address, system, out var region))
        {
            throw new InvalidOperationException($"No mapped memory region for address 0x{address:X}.");
        }
        if (!MemoryRegion.TryReadBytes(segments, region.Id, address - region.BusAddress!.Value, length, out var bytes))
        {
            throw new InvalidOperationException($"No data available at 0x{address:X} ({length} byte(s)) - is it covered by a <memory> block?");
        }
        return bytes;
    }
}
