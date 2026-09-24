namespace Gamehook.Domain.NativeProcessors;

/// <summary>
/// Tracks two GBA save blocks that the game relocates in RAM (DMA) on map changes. Publishes them as
/// <c>ram.save_block_1</c> and <c>ram.save_block_2</c> once the pointers are safe to follow.
/// </summary>
/// <param name="pointerAddress">IWRAM address of the first block pointer; the second block pointer
/// and a third DMA pointer follow it.</param>
/// <param name="encryptionKeyOffset">Second-block offset of the item/money encryption key.</param>
/// <param name="firstItemOffset">First-block offset of the first bag item, used to spot a half-copied block.</param>
/// <param name="saveBlock1Length">Bytes of the first block to publish; must cover the mapper's largest offset.</param>
/// <param name="saveBlock2Length">Bytes of the second block to publish; must cover the mapper's largest offset.</param>
public sealed class f12aeb6b(
    uint pointerAddress,
    uint encryptionKeyOffset,
    uint firstItemOffset,
    int saveBlock1Length,
    int saveBlock2Length)
{
    public const string SaveBlock1Region = "ram.save_block_1";
    public const string SaveBlock2Region = "ram.save_block_2";

    private uint? cachedDmaA;
    private uint? cachedDmaB;
    private uint? cachedDmaC;
    private uint? cachedEncryptionKey;
    private ushort? cachedPlayerId;
    private ushort? cachedFirstItemType;
    private ushort? cachedSecondItemType;
    private long dmaDelayStart;
    private long dmaUpdateDelay;
    private long dmaSafetyDelay;

    public uint SaveBlock1 { get; private set; }
    public uint SaveBlock2 { get; private set; }
    /// <summary>Full 32-bit key. Money uses all of it; item quantities use the low 16 bits.</summary>
    public uint EncryptionKey { get; private set; }

    /// <summary>Returns false while a relocation is in progress; keep the last good values.</summary>
    public bool Update(INativeProcessorHost host)
    {
        var dmaA = ceb4e08a.ReadUInt32(host, pointerAddress);
        var dmaB = ceb4e08a.ReadUInt32(host, pointerAddress + 4);
        var dmaC = ceb4e08a.ReadUInt32(host, pointerAddress + 8);
        if (dmaA == 0 || dmaB == 0 || dmaC == 0) return false;

        PublishLiveRegion(host, SaveBlock1Region, dmaA, saveBlock1Length);
        PublishLiveRegion(host, SaveBlock2Region, dmaB, saveBlock2Length);

        var encryptionKey = ceb4e08a.ReadUInt32(host, dmaB + encryptionKeyOffset);
        var playerId = ceb4e08a.ReadUInt16(host, dmaB + 10u);
        var firstItemType = ceb4e08a.ReadUInt16(host, dmaA + firstItemOffset);
        var secondItemType = ceb4e08a.ReadUInt16(host, dmaA + firstItemOffset + 4);

        if (cachedDmaA is null)
        {
            dmaUpdateDelay = 0;
        }
        else if (dmaUpdateDelay == 0 &&
            (cachedDmaA != dmaA || cachedDmaB != dmaB || cachedDmaC != dmaC ||
             (ushort?)cachedEncryptionKey != (ushort)encryptionKey))
        {
            var gameTime = GetGameTime(host, dmaB);
            dmaDelayStart = gameTime;
            dmaUpdateDelay = gameTime + 60;
            dmaSafetyDelay = gameTime + 300;
            cachedEncryptionKey = encryptionKey;
        }

        cachedDmaA = dmaA;
        cachedDmaB = dmaB;
        cachedDmaC = dmaC;
        if (dmaUpdateDelay != 0 && !DmaUpdateIsSafe(host, dmaB, firstItemType, secondItemType, playerId)) return false;

        cachedPlayerId = playerId;
        cachedFirstItemType = firstItemType;
        cachedSecondItemType = secondItemType;
        SaveBlock1 = dmaA;
        SaveBlock2 = dmaB;
        EncryptionKey = encryptionKey;
        return true;
    }

    private bool DmaUpdateIsSafe(
        INativeProcessorHost host,
        uint dmaB,
        ushort firstItemType,
        ushort secondItemType,
        ushort playerId)
    {
        var gameTime = GetGameTime(host, dmaB);
        if (dmaDelayStart > gameTime)
        {
            dmaUpdateDelay = 0;
            return true;
        }
        if (dmaUpdateDelay > gameTime) return false;

        var itemTypesDisagree = cachedFirstItemType is not null && cachedSecondItemType is not null &&
            cachedFirstItemType != firstItemType && cachedSecondItemType != secondItemType;
        var playerIdDisagrees = cachedPlayerId is not null && cachedPlayerId != playerId;
        if ((itemTypesDisagree || playerIdDisagrees) && dmaSafetyDelay > gameTime) return false;

        dmaUpdateDelay = 0;
        return true;
    }

    private static long GetGameTime(INativeProcessorHost host, uint dmaB)
    {
        var bytes = host.ReadMemory(dmaB + 14u, 5).Span;
        return bytes[0] * 216000L + bytes[2] * 3600L + bytes[3] * 60L + bytes[4];
    }

    private static void PublishLiveRegion(INativeProcessorHost host, string id, uint address, int length) =>
        ceb4e08a.PublishRegion(host, id, address, host.ReadMemory(address, length).ToArray());
}
