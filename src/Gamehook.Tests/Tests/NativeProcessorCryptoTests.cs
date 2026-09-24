using System.Buffers.Binary;
using Gamehook.Domain;
using Gamehook.Domain.Models;
using Gamehook.Domain.NativeProcessors;
using Gamehook.Domain.Property;

namespace Gamehook.Tests.Tests;

public sealed class NativeProcessorCryptoTests
{
    [Test]
    public void Gba_decrypt_reads_blocks_from_their_stored_positions()
    {
        // seed % 24 == 9 stores logical block 0 in stored slot 3.
        var encrypted = new byte[ceb4e08a.GbaRecordSize];
        const uint seed = 9;
        const uint otId = 0x12345678;
        BinaryPrimitives.WriteUInt32LittleEndian(encrypted, seed);
        BinaryPrimitives.WriteUInt32LittleEndian(encrypted.AsSpan(4), otId);
        var key = seed ^ otId;
        for (var word = 0; word < 48; word += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(encrypted.AsSpan(32 + word), key);
        BinaryPrimitives.WriteUInt32LittleEndian(encrypted.AsSpan(32 + 3 * 12), 25u ^ key);

        var decrypted = ceb4e08a.DecryptGba(encrypted);

        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(decrypted.AsSpan(32)), Is.EqualTo(25));
    }

    [TestCase(ceb4e08a.NdsRecordSizeA)]
    [TestCase(ceb4e08a.NdsRecordSizeB)]
    public void Nds_decrypt_reverses_encryption_for_every_block_order(int size)
    {
        for (uint order = 0; order < 24; order++)
        {
            var plain = new byte[size];
            var seed = order << 13;
            BinaryPrimitives.WriteUInt32LittleEndian(plain, seed);
            for (var i = 8; i < size; i++) plain[i] = (byte)(i * 7);
            var checksum = 0;
            for (var i = 8; i < 136; i += 2) checksum += BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(i));
            BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(6), (ushort)checksum);

            Assert.That(ceb4e08a.DecryptNds(EncryptNds(plain)), Is.EqualTo(plain), $"order {order}");
        }
    }

    [Test]
    public void Character_maps_with_wide_codes_decode_two_bytes_per_character()
    {
        var property = Property.Create(
            new PropertyConfig("name", "string", 0x02000000, 6, null, null, null, "map"),
            Endianness.Little, GameSystem.NDS);
        var references = new Dictionary<string, ReferenceTable>
        {
            ["map"] = new(false, new Dictionary<ulong, string> { [0x0121] = "A", [0x0122] = "B" }, CharacterWidth: 2),
        };

        Assert.That(property.TryDecode(new byte[] { 0x21, 0x01, 0x22, 0x01, 0xFF, 0xFF }, references, out var value, out _), Is.True);
        Assert.That(value, Is.EqualTo("AB"));
    }

    // Inverse of ceb4e08a.DecryptNds.
    private static byte[] EncryptNds(byte[] plain)
    {
        byte[][] positions =
        [
            [0, 1, 2, 3], [0, 1, 3, 2], [0, 2, 1, 3], [0, 3, 1, 2], [0, 2, 3, 1], [0, 3, 2, 1],
            [1, 0, 2, 3], [1, 0, 3, 2], [2, 0, 1, 3], [3, 0, 1, 2], [2, 0, 3, 1], [3, 0, 2, 1],
            [1, 2, 0, 3], [1, 3, 0, 2], [2, 1, 0, 3], [3, 1, 0, 2], [2, 3, 0, 1], [3, 2, 0, 1],
            [1, 2, 3, 0], [1, 3, 2, 0], [2, 1, 3, 0], [3, 1, 2, 0], [2, 3, 1, 0], [3, 2, 1, 0],
        ];
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(plain);
        var order = positions[((seed >> 13) & 31) % 24];
        var output = plain.ToArray();
        for (var block = 0; block < 4; block++)
            plain.AsSpan(8 + block * 32, 32).CopyTo(output.AsSpan(8 + order[block] * 32));
        Crypt(output.AsSpan(8, 128), BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(6)));
        Crypt(output.AsSpan(136), seed);
        return output;
    }

    private static void Crypt(Span<byte> data, uint seed)
    {
        for (var i = 0; i + 2 <= data.Length; i += 2)
        {
            seed = unchecked(seed * 0x41C64E6D + 0x6073);
            BinaryPrimitives.WriteUInt16LittleEndian(data[i..], (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[i..]) ^ (seed >> 16)));
        }
    }
}
