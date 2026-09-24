using System.Buffers.Binary;

namespace Gamehook.Domain.NativeProcessors;

/// <summary>
/// Base for record-table native processors: resolves the mapper host, and shares record decryption,
/// memory reads, and virtual-region publishing.
/// </summary>
public abstract class ceb4e08a(GamehookSession session) : INativeProcessor
{
    /// <summary>The mapper host from the latest preprocessor run; null before the first run.</summary>
    protected INativeProcessorHost? Host { get; private set; }

    public bool Preprocessor()
    {
        Host = session.Mapper as INativeProcessorHost
            ?? throw new InvalidOperationException($"Native processor '{GetType().Name}' requires a native processor host mapper.");
        return Preprocess(Host);
    }

    /// <summary>Return false to skip this read, keeping the last good values.</summary>
    protected abstract bool Preprocess(INativeProcessorHost host);

    public virtual void Postprocessor()
    {
    }

    public static ushort ReadUInt16(INativeProcessorHost host, uint address) =>
        BinaryPrimitives.ReadUInt16LittleEndian(host.ReadMemory(address, 2).Span);

    public static uint ReadUInt32(INativeProcessorHost host, uint address) =>
        BinaryPrimitives.ReadUInt32LittleEndian(host.ReadMemory(address, 4).Span);

    public static void PublishRegion(INativeProcessorHost host, string id, ulong? sourceAddress, byte[] bytes)
    {
        host.DefineMemoryRegion(id, sourceAddress, bytes.Length);
        host.SetMemoryRegionBytes(id, bytes);
    }

    /// <summary>Region id for one slot of a record table, as mapper XML names it.</summary>
    protected static string SlotRegion(string table, int slot) => $"{table}_party_structure_{slot}";

    public const int GbaRecordSize = 100;
    public const int NdsRecordSizeA = 236;
    public const int NdsRecordSizeB = 220;

    // Row n lists, for each logical block, the stored position that holds it when the shuffle
    // value is n.
    private static readonly byte[][] BlockPositions =
    [
        [0, 1, 2, 3], [0, 1, 3, 2], [0, 2, 1, 3], [0, 3, 1, 2], [0, 2, 3, 1], [0, 3, 2, 1],
        [1, 0, 2, 3], [1, 0, 3, 2], [2, 0, 1, 3], [3, 0, 1, 2], [2, 0, 3, 1], [3, 0, 2, 1],
        [1, 2, 0, 3], [1, 3, 0, 2], [2, 1, 0, 3], [3, 1, 0, 2], [2, 3, 0, 1], [3, 2, 0, 1],
        [1, 2, 3, 0], [1, 3, 2, 0], [2, 1, 3, 0], [3, 1, 2, 0], [2, 3, 1, 0], [3, 2, 1, 0],
    ];

    /// <summary>
    /// GBA record: 32-byte clear header, then four shuffled 12-byte blocks XORed with the first two
    /// header words. The 20-byte tail is clear.
    /// </summary>
    public static byte[] DecryptGba(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length != GbaRecordSize) throw new InvalidDataException($"GBA record must contain {GbaRecordSize} bytes.");

        var output = encrypted.ToArray();
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(encrypted);
        var key = seed ^ BinaryPrimitives.ReadUInt32LittleEndian(encrypted[4..]);
        var positions = BlockPositions[seed % 24];
        for (var block = 0; block < 4; block++)
        {
            var source = encrypted.Slice(32 + positions[block] * 12, 12);
            var destination = output.AsSpan(32 + block * 12, 12);
            for (var word = 0; word < 12; word += 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination[word..], BinaryPrimitives.ReadUInt32LittleEndian(source[word..]) ^ key);
            }
        }
        return output;
    }

    /// <summary>
    /// NDS record: 8-byte clear header, four shuffled 32-byte blocks encrypted with an LCRNG seeded by
    /// the header checksum, then a tail encrypted with an LCRNG seeded by the first header word.
    /// </summary>
    public static byte[] DecryptNds(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length is not (NdsRecordSizeA or NdsRecordSizeB))
            throw new InvalidDataException($"NDS record must contain {NdsRecordSizeA} or {NdsRecordSizeB} bytes.");

        var decrypted = encrypted.ToArray();
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(encrypted);
        var checksum = BinaryPrimitives.ReadUInt16LittleEndian(encrypted[6..]);
        Crypt(decrypted.AsSpan(8, 128), checksum);
        Crypt(decrypted.AsSpan(136), seed);

        var output = decrypted.ToArray();
        var positions = BlockPositions[((seed >> 13) & 31) % 24];
        for (var block = 0; block < 4; block++)
        {
            decrypted.AsSpan(8 + positions[block] * 32, 32).CopyTo(output.AsSpan(8 + block * 32));
        }
        return output;
    }

    /// <summary>True when an NDS record's block checksum matches its header.</summary>
    public static bool NdsChecksumMatches(ReadOnlySpan<byte> decrypted)
    {
        var checksum = 0;
        for (var offset = 8; offset < 136; offset += 2)
            checksum += BinaryPrimitives.ReadUInt16LittleEndian(decrypted[offset..]);
        return (ushort)checksum == BinaryPrimitives.ReadUInt16LittleEndian(decrypted[6..]);
    }

    private static void Crypt(Span<byte> data, uint seed)
    {
        for (var offset = 0; offset + 2 <= data.Length; offset += 2)
        {
            seed = unchecked(seed * 0x41C64E6D + 0x6073);
            var value = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) ^ (seed >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(data[offset..], value);
        }
    }
}
