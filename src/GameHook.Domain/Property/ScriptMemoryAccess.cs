using System.Buffers.Binary;
using GameHook.Domain.Interface;

namespace GameHook.Domain.Property;

// Bound as mapper.memory.defaultNamespace for mapper scripts (memory.defaultNamespace.get_uint32_le(addr)
// etc.), backed by the current read's driver snapshot - not a live device round-trip. Method names
// are deliberately snake_case, matching the JS call sites verbatim across the existing mapper .js
// files; they're data this app doesn't own, not code we get to rename.
public sealed class ScriptMemoryAccess(GameSystem? system)
{
    private IReadOnlyList<IDriver.MemorySegmentSnapshot> segments = [];

    public void UpdateSnapshot(IReadOnlyList<IDriver.MemorySegmentSnapshot> newSegments) => segments = newSegments;

    // The scalar readers decode straight out of the snapshot. A preprocessor calls these hundreds
    // of times per frame while walking party structures, so copying the bytes out first - as
    // get_bytes still has to, since it hands the array to script - would be pure waste.
    public double get_byte(double address) => Read((ulong)address, 1).Span[0];
    public double get_uint16_le(double address) => BinaryPrimitives.ReadUInt16LittleEndian(Read((ulong)address, 2).Span);
    public double get_uint32_le(double address) => BinaryPrimitives.ReadUInt32LittleEndian(Read((ulong)address, 4).Span);
    public ScriptByteWindow get_bytes(double address, double length) =>
        new(Read((ulong)address, checked((int)length)).ToArray());

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

// Returned by ScriptMemoryAccess.get_bytes - mirrors the JS call sites' "const pid = pokemonData.get_uint32_le();"
// pattern (a decoded window with its own little-endian readers, plus raw indexed byte access).
public sealed class ScriptByteWindow(byte[] data)
{
    public byte[] data = data;
    public double get_uint32_le(double offset = 0) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)offset), 4));
    public double get_uint16_le(double offset = 0) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(checked((int)offset), 2));
}
