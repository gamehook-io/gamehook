namespace Gamehook.Domain.NativeProcessors;

/// <summary>
/// Decrypts two fixed six-slot GBA record tables into <c>player_party_structure_N</c> and
/// <c>opponent_party_structure_N</c>, plus relocating save blocks when the game has them.
/// </summary>
public abstract class b8bf3030(GamehookSession session, uint playerTable, uint opponentTable, f12aeb6b? saveBlocks = null)
    : ceb4e08a(session)
{
    protected const int RecordSize = GbaRecordSize;

    protected f12aeb6b? SaveBlocks => saveBlocks;
    protected uint OpponentTable => opponentTable;
    /// <summary>Decrypted opponent records from the latest read.</summary>
    protected byte[][] OpponentRecords { get; } = new byte[6][];

    protected override bool Preprocess(INativeProcessorHost host)
    {
        if (saveBlocks is not null && !saveBlocks.Update(host)) return false;
        return PublishTable(host, "player", playerTable, null)
            && PublishTable(host, "opponent", opponentTable, OpponentRecords);
    }

    /// <summary>Return false to skip the read before any slot of that table is published.</summary>
    protected virtual bool IsUsable(string table, int slot, byte[] record) => true;

    private bool PublishTable(INativeProcessorHost host, string table, uint address, byte[][]? records)
    {
        var decrypted = new byte[6][];
        for (var slot = 0; slot < decrypted.Length; slot++)
        {
            decrypted[slot] = DecryptGba(host.ReadMemory(SlotAddress(address, slot), RecordSize).Span);
            if (!IsUsable(table, slot, decrypted[slot])) return false;
        }
        for (var slot = 0; slot < decrypted.Length; slot++)
        {
            PublishRegion(host, SlotRegion(table, slot), SlotAddress(address, slot), decrypted[slot]);
            if (records is not null) records[slot] = decrypted[slot];
        }
        return true;
    }

    protected static uint SlotAddress(uint table, int slot) => table + (uint)(RecordSize * slot);
}

// Largest XML offsets: first save block 4091, second block the encryption key at 0xF20.
public sealed class ccd04ebe(GamehookSession session)
    : b8bf3030(session, 0x02024284u, 0x0202402Cu, new f12aeb6b(0x03005008u, 0xF20u, 0x310u, 4096, 0xF24));

// Save blocks never relocate here, so XML reads them at fixed addresses.
public sealed class fa4e8780(GamehookSession session)
    : b8bf3030(session, 0x03004360u, 0x030045C0u);
