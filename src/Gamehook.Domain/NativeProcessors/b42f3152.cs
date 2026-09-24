using System.Buffers.Binary;

namespace Gamehook.Domain.NativeProcessors;

/// <summary>
/// Native processor for setting up GBA memory regions, including the active opponent and the bag.
/// </summary>
public sealed class b42f3152(GamehookSession session)
    : b8bf3030(session, 0x020244ECu, 0x02024744u, new f12aeb6b(0x03005D8Cu, 172u, 1376u, SaveBlock1Length, SaveBlock2Length))
{
    // Largest XML offsets are 5014 (first save block) and 9004 (second save block).
    private const int SaveBlock1Length = 5015;
    private const int SaveBlock2Length = 9005;

    protected override bool Preprocess(INativeProcessorHost host)
    {
        if (!base.Preprocess(host)) return false;

        var activeOpponent = ReadUInt16(host, 0x02024070u);
        var inRange = activeOpponent < OpponentRecords.Length;
        PublishRegion(host, "opponent_party_structure_active",
            inRange ? SlotAddress(OpponentTable, activeOpponent) : null,
            inRange ? OpponentRecords[activeOpponent] : new byte[RecordSize]);
        return true;
    }

    // Corrupt first player record means transient DMA relocation. Keep last good containers.
    protected override bool IsUsable(string table, int slot, byte[] record) =>
        table != "player" || slot != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(32, 2)) <= 415;

    public override void Postprocessor()
    {
        if (Host is not { } mapper || SaveBlocks is not { } saveBlocks) return;

        var quantityDecryptionKey = (ushort)saveBlocks.EncryptionKey;
        var moneyDecryptionKey = saveBlocks.EncryptionKey;
        var dmaA = saveBlocks.SaveBlock1;

        foreach (var path in mapper.PropertyNames)
        {
            if (!path.StartsWith("bag.", StringComparison.Ordinal) || !path.EndsWith(".quantity", StringComparison.Ordinal)) continue;
            var bytes = mapper.GetPropertyBytes(path).Span;
            if (bytes.Length == 2)
            {
                var rawValue = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                mapper.SetPropertyValue(path, (int)(rawValue ^ quantityDecryptionKey));
            }
        }

        var coinsBytes = mapper.GetPropertyBytes("bag.coins").Span;
        if (coinsBytes.Length == 2)
        {
            var rawCoins = BinaryPrimitives.ReadUInt16LittleEndian(coinsBytes);
            mapper.SetPropertyValue("bag.coins", (int)(rawCoins ^ quantityDecryptionKey));
        }

        var moneyBytes = mapper.GetPropertyBytes("bag.money").Span;
        if (moneyBytes.Length == 4)
        {
            var rawMoney = BinaryPrimitives.ReadUInt32LittleEndian(moneyBytes);
            mapper.SetPropertyValue("bag.money", unchecked((int)(rawMoney ^ moneyDecryptionKey)));
        }

        if (dmaA != 0)
        {
            // Inspectable native virtual region: starts at money and includes every bag pocket.
            var bag = mapper.ReadMemory(dmaA + 1168u, 1024).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bag, BinaryPrimitives.ReadUInt32LittleEndian(bag) ^ moneyDecryptionKey);
            BinaryPrimitives.WriteUInt16LittleEndian(bag.AsSpan(4), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bag.AsSpan(4)) ^ quantityDecryptionKey));
            // Every bag pocket is a contiguous sequence of 4-byte item/quantity pairs.
            for (var offset = 210; offset + 2 <= 956; offset += 4)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    bag.AsSpan(offset, 2),
                    (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bag.AsSpan(offset, 2)) ^ quantityDecryptionKey));
            }
            PublishRegion(mapper, "native.gba.bag", dmaA + 1168u, bag);
        }
    }
}
