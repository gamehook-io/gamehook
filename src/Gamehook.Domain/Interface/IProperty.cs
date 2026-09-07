namespace Gamehook.Domain.Interface;

public interface IProperty
{
    string Name { get; }
    string Type { get; }
    ulong? Address { get; }
    int Length { get; }
    string? Bits { get; }
    string? Reference { get; }
    string? StaticValue { get; }
    string? CharacterMap { get; }
    string? Description { get; }

    /// Name of a script-populated virtual buffer this property reads from instead of live device
    /// memory; when set, Address is a relative offset into that buffer.
    string? MemoryContainer { get; }

    /// Memory region (WRAM/VRAM/SRAM/...) derived from Address; null for constants or unsupported addresses.
    string? Region { get; }

    /// Space-separated uppercase hex of the last-read Bytes, e.g. "3F 02 A1"; empty until a value has been read.
    string RawBytesHex { get; }

    ReadOnlyMemory<byte> Bytes { get; }
    object? Value { get; }

    bool TryDecode(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references, out object? value, out string? error);

    /// Reverse of TryDecode: turns `value` into this property's raw bytes. `currentBytes` (exactly
    /// Length bytes) is the basis to merge onto for bit-masked properties (Bits set) - it must
    /// already reflect any other pending edit to the same byte(s), not a stale poll snapshot, or a
    /// concurrent edit to a sibling bit field can be silently clobbered.
    bool TryEncode(
        object? value,
        ReadOnlyMemory<byte> currentBytes,
        IReadOnlyDictionary<string, ReferenceTable> references,
        out ReadOnlyMemory<byte> bytes,
        out string? error);

    /// Commits bytes already written to the device straight into Bytes/Value, without waiting for
    /// the next poll, so the UI reflects an edit immediately.
    void ApplyWrittenBytes(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references);

    /// Address-backed properties report the memory they need; value-only properties return null.
    IDriver.MemorySegmentRequest? BuildRequest();

    void Refresh(
        IReadOnlyList<IDriver.MemorySegmentSnapshot> segments,
        IReadOnlyDictionary<string, ReferenceTable> references,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? containers = null);
}
