using System.Globalization;
using System.Text.RegularExpressions;
using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Domain.Property;

public readonly record struct PropertyConfig(
    string Name,
    string Type,
    ulong? Address,
    int Length,
    string? Bits,
    string? Reference,
    string? StaticValue,
    string? CharacterMap = null,
    string? MemoryContainer = null,
    string? Description = null);

public abstract class Property : IProperty
{
    private static readonly Regex BitRange = new("^(?<first>\\d+)-(?<last>\\d+)$", RegexOptions.Compiled);

    private bool refreshed;

    // Null defaults to the legacy Game Boy address ranges MemoryRegion has always used, so every
    // existing caller (tests included) that doesn't pass a system keeps its exact current behavior.
    // This is the only mapper-level thing a Property holds onto - it's a fixed fact about which
    // console's address space applies, not mapper orchestration state. Everything that changes
    // read-to-read (driver segments, virtual container contents) comes in as a Refresh() parameter
    // instead, and anything script-related (the engine itself, after-read-value-expression) is
    // applied externally by the mapper via SetValueOverride/SetAddress - a Property never touches
    // the scripting engine.
    private readonly GameSystem? system;

    private MemoryRegionDefinition? resolvedRegion;
    private int segmentHint;
    private ulong bitMask;
    private int bitShift;

    // Single place a mapper type string turns into a concrete Property - Mapper's compiled
    // properties and its inspection properties go through this instead of each carrying their own
    // copy of the type switch.
    public static Property Create(PropertyConfig config, Endianness integerEndianness = Endianness.Big, GameSystem? system = null) =>
        config.Type switch
        {
            "string" => new StringProperty(config, integerEndianness, system),
            "bool" => new BooleanProperty(config, integerEndianness, system),
            "bitArray" => new BitArrayProperty(config, integerEndianness, system),
            "int" or "uint" or "binaryCodedDecimal" => new NumberProperty(config, integerEndianness, system),
            _ => throw new NotSupportedException($"Unsupported property type '{config.Type}'."),
        };

    protected Property(PropertyConfig config, Endianness integerEndianness = Endianness.Big, GameSystem? system = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Name);
        ArgumentOutOfRangeException.ThrowIfNegative(config.Length);
        Name = config.Name;
        Type = config.Type;
        Address = config.Address;
        Length = config.Length;
        Bits = config.Bits;
        Reference = config.Reference;
        StaticValue = config.StaticValue;
        CharacterMap = config.CharacterMap;
        MemoryContainer = config.MemoryContainer;
        Description = config.Description;
        IntegerEndianness = integerEndianness;
        this.system = system;
        CompileBits(config.Bits);
    }

    public string Name { get; }
    public string Type { get; }
    public ulong? Address { get; private set; }
    public int Length { get; private set; }
    public string? Bits { get; private set; }
    public string? Reference { get; private set; }
    public string? StaticValue { get; }
    public string? CharacterMap { get; }
    public string? Description { get; }

    /// Name of a native-processor virtual memory region this property reads from
    /// instead of live device memory; when set, Address is a relative offset into that buffer.
    public string? MemoryContainer { get; private set; }
    protected Endianness IntegerEndianness { get; }

    public string? Region => MemoryContainer is null && Address is { } address && MemoryRegion.TryToRegion(address, system, out var region) ? region : null;

    public string RawBytesHex => string.Join(' ', Bytes.ToArray().Select(b => b.ToString("X2")));

    public ReadOnlyMemory<byte> Bytes { get; private set; }
    public object? Value { get; private set; }

    /// The value this property's own bytes decoded to, before any after-read-value-expression,
    /// condition, or script override. Kept apart from <see cref="Value"/> so a transform is always
    /// applied to the raw reading: re-applying it to its own previous output would compound it
    /// every frame the underlying bytes happen not to change.
    public object? DecodedValue { get; private set; }

    // Mutation hooks used only by the mapper's script bridge (Mapper.properties[path].address = ...,
    // Mapper.properties[path].value = ...) - never called from normal decode. Public rather than
    // internal since the bridge lives in a different assembly (Gamehook.Infrastructure) and this is
    // a small desktop app, not a published API surface that needs InternalsVisibleTo ceremony.
    public void SetAddress(ulong? address)
    {
        if (Address == address) return;
        Address = address;
        if (address is null) ClearValue();
        resolvedRegion = null;
        segmentHint = 0;
        refreshed = false;
    }

    public void SetMemoryContainer(string? memoryContainer)
    {
        if (MemoryContainer == memoryContainer) return;
        MemoryContainer = memoryContainer;
        refreshed = false;
    }

    public void SetReference(string? reference)
    {
        if (Reference == reference) return;
        Reference = reference;
        refreshed = false;
    }

    public void SetLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (Length == length) return;
        Length = length;
        refreshed = false;
    }

    public void SetBits(string? bits)
    {
        if (Bits == bits) return;
        Bits = bits;
        CompileBits(bits);
        refreshed = false;
    }

    public void SetValueOverride(object? value)
    {
        Value = value;
        refreshed = true;
    }

    public bool TryDecode(
        ReadOnlyMemory<byte> bytes,
        IReadOnlyDictionary<string, ReferenceTable> references,
        out object? value,
        out string? error)
    {
        try
        {
            value = DecodeWithReference(bytes, references);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    public bool TryEncode(
        object? value,
        ReadOnlyMemory<byte> currentBytes,
        IReadOnlyDictionary<string, ReferenceTable> references,
        out ReadOnlyMemory<byte> bytes,
        out string? error)
    {
        if (currentBytes.Length != Length)
        {
            bytes = default;
            error = $"Property '{Name}': expected {Length} current byte(s) to merge onto, got {currentBytes.Length}.";
            return false;
        }

        try
        {
            bytes = EncodeWithReference(value, currentBytes, references);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException or FormatException)
        {
            bytes = default;
            error = ex.Message;
            return false;
        }
    }

    public void ApplyWrittenBytes(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        var value = DecodeWithReference(bytes, references);
        Bytes = bytes;
        DecodedValue = value;
        Value = value;
        refreshed = true;
    }

    private ReadOnlyMemory<byte> EncodeWithReference(
        object? value,
        ReadOnlyMemory<byte> currentBytes,
        IReadOnlyDictionary<string, ReferenceTable> references)
    {
        if (Reference is { } reference && references.TryGetValue(reference, out var table)
            && TryReverseResolveReference(table, value, out var raw))
        {
            return MergeBits(currentBytes, raw);
        }

        return Encode(value, currentBytes, references);
    }

    // Reverse of ResolveReference: given the display value shown to the user, find the raw key the
    // table maps it from. Tables are small (character maps aside, which never carry a Reference),
    // so a linear scan is fine and avoids keeping a second reverse dictionary in sync.
    private static bool TryReverseResolveReference(ReferenceTable table, object? value, out ulong raw)
    {
        var text = value?.ToString();
        if (text is null)
        {
            raw = 0;
            return false;
        }

        foreach (var (key, mapped) in table.Values)
        {
            if (string.Equals(mapped, text, StringComparison.Ordinal))
            {
                raw = key;
                return true;
            }
        }

        raw = 0;
        return false;
    }

    public IDriver.MemorySegmentRequest? BuildRequest()
    {
        // Virtual regions are populated by native processors, never by the driver.
        if (MemoryContainer is not null) return null;
        if (Address is not { } address) return null;
        var region = ResolveRegion(address);
        return new IDriver.MemorySegmentRequest(region.Id, address - region.BusAddress!.Value, Length);
    }

    private MemoryRegionDefinition ResolveRegion(ulong address)
    {
        if (MemoryRegion.TryResolve(address, system, out var region)) return region;
        throw new InvalidDataException(
            $"Property '{Name}' (type {Type}, length {Length}, address 0x{address:X}): " +
            (system?.Id == "GBA"
                ? $"Address 0x{address:X} has no supported GBA memory region."
                : $"Address 0x{address:X} has no supported memory region."));
    }

    public void Refresh(
        IReadOnlyList<IDriver.MemorySegmentSnapshot> segments,
        IReadOnlyDictionary<string, ReferenceTable> references,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? containers = null)
    {
        if (StaticValue is { } staticValue)
        {
            // constant, never changes once parsed
            if (refreshed) return;
            DecodedValue = DecodeStaticValue(staticValue);
            Value = DecodedValue;
            refreshed = true;
            return;
        }

        if (Address is not { } address)
        {
            return;
        }

        ReadOnlyMemory<byte> bytes;
        if (MemoryContainer is { } containerName)
        {
            // script hasn't filled this container yet (e.g. first read, before preprocessor runs),
            // or the requested slice doesn't fit - report a null value rather than failing the read.
            if (containers is null || !containers.TryGetValue(containerName, out var container)
                || address > (ulong)container.Length || (ulong)Length > (ulong)container.Length - address)
            {
                ClearValue();
                return;
            }
            bytes = container.Slice((int)address, Length);
        }
        else
        {
            var region = resolvedRegion ??= ResolveRegion(address);
            var offset = address - region.BusAddress!.Value;

            // the address itself is valid, but the driver didn't supply this region this read
            // (e.g. RetroArch has no memory map for it) - report a null value instead of failing the whole read
            if (!MemoryRegion.TryReadBytes(segments, region.Id, offset, Length, ref segmentHint, out var regionBytes))
            {
                ClearValue();
                return;
            }
            bytes = regionBytes;
        }

        if (refreshed && Bytes.Span.SequenceEqual(bytes.Span))
        {
            return;
        }

        object? value;
        try
        {
            value = DecodeWithReference(bytes, references);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException(
                $"Property '{Name}' (type {Type}, length {Length}, address 0x{address:X}) failed to decode value 0x{Convert.ToHexString(bytes.Span)}: {ex.Message}",
                ex);
        }

        // commit atomically - a failed decode above must leave Bytes/Value at their last good state, not partially updated
        // Script containers are mutated in place; retain a snapshot for change detection.
        Bytes = MemoryContainer is null ? bytes : bytes.ToArray();
        DecodedValue = value;
        Value = value;
        refreshed = true;
    }

    private void ClearValue()
    {
        if (refreshed && Value is null && DecodedValue is null) return;
        Bytes = ReadOnlyMemory<byte>.Empty;
        DecodedValue = null;
        Value = null;
        refreshed = true;
    }

    private object? DecodeWithReference(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        return Reference is { } reference && references.TryGetValue(reference, out var table)
            && table.Values.TryGetValue(ReadRawValue(bytes), out var mapped)
            ? ResolveReference(table, mapped)
            : Decode(bytes, references);
    }

    // Compatibility path for callers that previously supplied one contiguous snapshot per
    // region. New mapper reads may contain several sparse snapshots for same region.
    public void Refresh(
        IReadOnlyDictionary<string, IDriver.MemorySegmentSnapshot> segments,
        IReadOnlyDictionary<string, ReferenceTable> references) =>
        Refresh(segments.Values.ToArray(), references);

    protected abstract object? Decode(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references);

    /// Reverse of Decode. `currentBytes` is exactly Length bytes long (TryEncode already checked)
    /// and is the merge basis for bit-masked properties; whole-byte-granularity properties (string,
    /// bitArray) are free to ignore it and return a full Length-byte replacement.
    protected abstract ReadOnlyMemory<byte> Encode(object? value, ReadOnlyMemory<byte> currentBytes, IReadOnlyDictionary<string, ReferenceTable> references);

    protected virtual object? DecodeStaticValue(string value) => null;

    // a reference table can map onto either numeric ids or display strings, independent of the property's own declared type
    private static object? ResolveReference(ReferenceTable table, string mapped) =>
        table.IsNumber && int.TryParse(mapped, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : mapped;

    protected ulong ReadRawValue(ReadOnlyMemory<byte> bytes) => ApplyBits(ReadInteger(bytes));

    protected ulong ReadInteger(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        ulong value = 0;
        for (var index = 0; index < span.Length; index++)
        {
            var byteIndex = IntegerEndianness == Endianness.Little ? span.Length - 1 - index : index;
            value = (value << 8) | span[byteIndex];
        }
        return value;
    }

    /// Inverse of ReadInteger + ApplyBits: reads the full integer currently in `currentBytes`,
    /// replaces just this property's bit field with `rawValue`, and re-serializes all Length bytes.
    /// This is what keeps a nibble-field write from clobbering its sibling nibble, provided the
    /// caller passes a `currentBytes` that already reflects any other pending write to the same
    /// byte(s) - Property itself only guarantees the merge arithmetic, not write ordering.
    protected ReadOnlyMemory<byte> MergeBits(ReadOnlyMemory<byte> currentBytes, ulong rawValue)
    {
        // Never silently truncate a value. Besides being surprising for a whole property (e.g.
        // writing 256 into one byte), truncation can alter an adjacent bit field in a shared byte.
        var storageBits = checked(currentBytes.Length * 8);
        var storageMask = storageBits >= 64 ? ulong.MaxValue : (1UL << storageBits) - 1;
        var fieldMask = storageMask;
        if (Bits is not null)
        {
            if (bitShift >= storageBits || bitMask > (storageMask >> bitShift))
            {
                throw new InvalidDataException($"Bit range '{Bits}' does not fit in {currentBytes.Length} byte(s).");
            }
            fieldMask = bitMask;
        }
        if (rawValue > fieldMask)
        {
            throw new InvalidDataException($"Value {rawValue} does not fit in {(Bits is null ? $"{storageBits} bit" : $"bit range {Bits}")} field.");
        }

        var existing = ReadInteger(currentBytes);
        var merged = (existing & ~(bitMask << bitShift)) | ((rawValue & bitMask) << bitShift);
        return WriteInteger(merged, currentBytes.Length);
    }

    private byte[] WriteInteger(ulong value, int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            var shift = 8 * (length - 1 - index);
            var b = (byte)((value >> shift) & 0xFF);
            var byteIndex = IntegerEndianness == Endianness.Little ? length - 1 - index : index;
            bytes[byteIndex] = b;
        }
        return bytes;
    }

    /// Applies this property's own bits selector. The selector is a string in the mapper XML but is
    /// fixed for the property, so it is turned into a shift and a mask once rather than being
    /// re-parsed (and re-regex-matched) on every decode of every frame.
    protected ulong ApplyBits(ulong value) => (value >> bitShift) & bitMask;

    private void CompileBits(string? bits)
    {
        bitShift = 0;
        bitMask = ulong.MaxValue;
        if (string.IsNullOrWhiteSpace(bits)) return;
        if (int.TryParse(bits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit))
        {
            if (bit is < 0 or > 63) throw new InvalidDataException($"Invalid bits value '{bits}'.");
            bitShift = bit;
            bitMask = 1;
            return;
        }
        var match = BitRange.Match(bits);
        if (!match.Success) throw new InvalidDataException($"Invalid bits value '{bits}'.");
        var first = int.Parse(match.Groups["first"].Value, CultureInfo.InvariantCulture);
        var last = int.Parse(match.Groups["last"].Value, CultureInfo.InvariantCulture);
        if (last < first || first < 0 || last > 63) throw new InvalidDataException($"Invalid bits value '{bits}'.");
        bitShift = first;
        bitMask = last - first == 63 ? ulong.MaxValue : (1UL << (last - first + 1)) - 1;
    }
}
