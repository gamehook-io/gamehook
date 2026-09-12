using System.Buffers.Binary;

namespace Gamehook.Domain.NativeProcessors;

/// <summary>
/// Native processor for setting up memory regions for Gen 3.
/// </summary>
public sealed class b42f3152(GamehookSession session) : INativeProcessor
{
    private INativeProcessorHost? mapper;
    private uint? cachedDmaA;
    private uint? cachedDmaB;
    private uint? cachedDmaC;
    private ushort? cachedQuantityDecryptionKey;
    private ushort? cachedPlayerId;
    private ushort? cachedFirstItemType;
    private ushort? cachedSecondItemType;
    private long dmaDelayStart;
    private long dmaUpdateDelay;
    private long dmaSafetyDelay;
    private uint dmaA;
    private ushort quantityDecryptionKey;
    private uint moneyDecryptionKey;

    public bool Preprocessor()
    {
        if (session.Mapper is not INativeProcessorHost processorHost)
            throw new InvalidOperationException("Gen 3 native processor requires a native processor host mapper.");
        mapper = processorHost;
        if (!UpdateDmaPointers(processorHost)) return false;

        var playerParty = new byte[6][];
        for (var slot = 0; slot < playerParty.Length; slot++)
        {
            var pokemon = Decrypt(processorHost.ReadMemory(0x020244ECu + (ulong)(PartyPokemonSize * slot), PartyPokemonSize));
            // Corrupt first party member means transient DMA relocation. Keep last good containers.
            if (slot == 0 && BinaryPrimitives.ReadUInt16LittleEndian(pokemon.AsSpan(32, 2)) > 415) return false;
            playerParty[slot] = pokemon;
            PublishRegion(processorHost, $"player_party_structure_{slot}", 0x020244ECu + (ulong)(PartyPokemonSize * slot), pokemon);
        }

        var opponentParty = new byte[6][];
        for (var slot = 0; slot < opponentParty.Length; slot++)
        {
            var pokemon = Decrypt(processorHost.ReadMemory(0x02024744u + (ulong)(PartyPokemonSize * slot), PartyPokemonSize));
            opponentParty[slot] = pokemon;
            PublishRegion(processorHost, $"opponent_party_structure_{slot}", 0x02024744u + (ulong)(PartyPokemonSize * slot), pokemon);
        }

        var activeOpponent = BinaryPrimitives.ReadUInt16LittleEndian(processorHost.ReadMemory(0x02024070u, 2).Span);
        var activeOpponentBytes = activeOpponent < opponentParty.Length ? opponentParty[activeOpponent] : new byte[PartyPokemonSize];
        var activeOpponentSource = activeOpponent < opponentParty.Length
            ? 0x02024744u + (ulong)(PartyPokemonSize * activeOpponent)
            : (ulong?)null;
        PublishRegion(processorHost, "opponent_party_structure_active_pokemon", activeOpponentSource, activeOpponentBytes);
        return true;
    }

    private bool UpdateDmaPointers(INativeProcessorHost processorHost)
    {
        var dmaA = ReadUInt32(processorHost, 0x03005D8Cu);
        var dmaB = ReadUInt32(processorHost, 0x03005D90u);
        var dmaC = ReadUInt32(processorHost, 0x03005D94u);
        if (dmaA == 0 || dmaB == 0 || dmaC == 0) return false;

        PublishLiveRegion(processorHost, SaveBlock1Region, dmaA, SaveBlock1Length);
        PublishLiveRegion(processorHost, SaveBlock2Region, dmaB, SaveBlock2Length);

        var keyBytes = processorHost.ReadMemory(dmaB + 172u, 4).Span;
        var quantityDecryptionKey = BinaryPrimitives.ReadUInt16LittleEndian(keyBytes);
        var moneyDecryptionKey = BinaryPrimitives.ReadUInt32LittleEndian(keyBytes);
        var playerId = ReadUInt16(processorHost, dmaB + 10u);
        var firstItemType = ReadUInt16(processorHost, dmaA + 1376u);
        var secondItemType = ReadUInt16(processorHost, dmaA + 1380u);

        if (cachedDmaA is null)
        {
            dmaUpdateDelay = 0;
        }
        else if (dmaUpdateDelay == 0 &&
            (cachedDmaA != dmaA || cachedDmaB != dmaB || cachedDmaC != dmaC ||
             cachedQuantityDecryptionKey != quantityDecryptionKey))
        {
            var gameTime = GetGameTime(processorHost, dmaB);
            dmaDelayStart = gameTime;
            dmaUpdateDelay = gameTime + 60;
            dmaSafetyDelay = gameTime + 300;
            cachedQuantityDecryptionKey = quantityDecryptionKey;
        }

        cachedDmaA = dmaA;
        cachedDmaB = dmaB;
        cachedDmaC = dmaC;
        if (dmaUpdateDelay != 0 && !DmaUpdateIsSafe(processorHost, dmaB, firstItemType, secondItemType, playerId)) return false;

        cachedPlayerId = playerId;
        cachedFirstItemType = firstItemType;
        cachedSecondItemType = secondItemType;
        this.dmaA = dmaA;
        this.quantityDecryptionKey = quantityDecryptionKey;
        this.moneyDecryptionKey = moneyDecryptionKey;
        return true;
    }

    private bool DmaUpdateIsSafe(
        INativeProcessorHost processorHost,
        uint dmaB,
        ushort firstItemType,
        ushort secondItemType,
        ushort playerId)
    {
        var gameTime = GetGameTime(processorHost, dmaB);
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

    private static long GetGameTime(INativeProcessorHost processorHost, uint dmaB)
    {
        var bytes = processorHost.ReadMemory(dmaB + 14u, 5).Span;
        return bytes[0] * 216000L + bytes[2] * 3600L + bytes[3] * 60L + bytes[4];
    }

    private static ushort ReadUInt16(INativeProcessorHost processorHost, uint address) =>
        BinaryPrimitives.ReadUInt16LittleEndian(processorHost.ReadMemory(address, 2).Span);

    private static uint ReadUInt32(INativeProcessorHost processorHost, uint address) =>
        BinaryPrimitives.ReadUInt32LittleEndian(processorHost.ReadMemory(address, 4).Span);

    public void Postprocessor()
    {
        if (mapper is null) return;

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
            // Every Emerald bag pocket is a contiguous sequence of 4-byte item/quantity pairs.
            for (var offset = 210; offset + 2 <= 956; offset += 4)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    bag.AsSpan(offset, 2),
                    (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bag.AsSpan(offset, 2)) ^ quantityDecryptionKey));
            }
            PublishRegion(mapper, "native.gba.bag", dmaA + 1168u, bag);
        }
    }

    private const int PartyPokemonSize = 100;
    // Largest XML offsets are 5014 (SaveBlock1) and 9004 (SaveBlock2).
    private const string SaveBlock1Region = "ram.save_block_1";
    private const string SaveBlock2Region = "ram.save_block_2";
    private const int SaveBlock1Length = 5015;
    private const int SaveBlock2Length = 9005;

    private static void PublishLiveRegion(INativeProcessorHost mapper, string id, uint address, int length) =>
        PublishRegion(mapper, id, address, mapper.ReadMemory(address, length).ToArray());

    private static void PublishRegion(INativeProcessorHost mapper, string id, ulong? sourceAddress, byte[] bytes)
    {
        mapper.DefineMemoryRegion(id, sourceAddress, bytes.Length);
        mapper.SetMemoryRegionBytes(id, bytes);
    }
    private static readonly byte[][] SubstructureOrders =
    [
        [0, 1, 2, 3], [0, 1, 3, 2], [0, 2, 1, 3], [0, 3, 1, 2], [0, 2, 3, 1], [0, 3, 2, 1],
        [1, 0, 2, 3], [1, 0, 3, 2], [2, 0, 1, 3], [3, 0, 1, 2], [2, 0, 3, 1], [3, 0, 2, 1],
        [1, 2, 0, 3], [1, 3, 0, 2], [2, 1, 0, 3], [3, 1, 0, 2], [2, 3, 0, 1], [3, 2, 0, 1],
        [1, 2, 3, 0], [1, 3, 2, 0], [2, 1, 3, 0], [3, 1, 2, 0], [2, 3, 1, 0], [3, 2, 1, 0],
    ];

    // Gen 3 party data: 32-byte clear header, then four shuffled XOR-encrypted 12-byte blocks.
    private static byte[] Decrypt(ReadOnlyMemory<byte> encrypted)
    {
        if (encrypted.Length != PartyPokemonSize) throw new InvalidDataException("Gen 3 party structure must contain 100 bytes.");

        var source = encrypted.Span;
        var output = source.ToArray();
        var key = BinaryPrimitives.ReadUInt32LittleEndian(source) ^ BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        var order = SubstructureOrders[key % 24];
        for (var storedBlock = 0; storedBlock < 4; storedBlock++)
        {
            var destinationOffset = 32 + (order[storedBlock] * 12);
            var sourceOffset = 32 + (storedBlock * 12);
            for (var word = 0; word < 3; word++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(sourceOffset + (word * 4), 4)) ^ key;
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(destinationOffset + (word * 4), 4), value);
            }
        }
        return output;
    }
}
