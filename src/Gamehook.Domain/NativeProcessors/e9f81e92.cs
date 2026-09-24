namespace Gamehook.Domain.NativeProcessors;

/// <summary>
/// Decrypts six-slot NDS record tables into <c>{table}_party_structure_{slot}</c>. A slot whose
/// checksum does not match (empty, stale, or mid-write) is published as zeros so the mapper never
/// decodes garbage.
/// </summary>
public abstract class e9f81e92(GamehookSession session, e54dedec layout) : ceb4e08a(session)
{
    protected override bool Preprocess(INativeProcessorHost host)
    {
        var baseAddress = 0u;
        if (layout.BasePointer is { } basePointer)
        {
            baseAddress = ReadUInt32(host, basePointer);
            // Zero during boot/reset, and briefly invalid on some transitions. Skip the frame.
            if (baseAddress < layout.MinBase || baseAddress > layout.MaxBase) return false;
        }

        foreach (var (table, offset) in layout.Tables)
        {
            for (var slot = 0; slot < 6; slot++)
            {
                var source = baseAddress + offset + (uint)(layout.RecordSize * slot);
                var record = DecryptNds(host.ReadMemory(source, layout.RecordSize).Span);
                if (!NdsChecksumMatches(record)) record = new byte[record.Length];
                PublishRegion(host, SlotRegion(table, slot), source, record);
            }
        }
        return true;
    }
}

/// <summary>
/// Where one game keeps its record tables. <c>Tables</c> maps a table name to the address of its
/// first slot. When <c>BasePointer</c> is set, table addresses are offsets from the pointed-to
/// address, which must fall within <c>MinBase</c>..<c>MaxBase</c>.
/// </summary>
public sealed record e54dedec(
    int RecordSize,
    IReadOnlyDictionary<string, uint> Tables,
    uint? BasePointer = null,
    uint MinBase = 0,
    uint MaxBase = 0)
{
    // Observed pointer value +/- 16 KB. Mapper <memory> blocks cover the same window.
    private const uint BaseWindow = 0x4000;

    public static e54dedec FromPointer(uint basePointer, uint observedBase, uint player, uint wild, uint battle, uint battleStride)
    {
        var tables = new Dictionary<string, uint> { ["player"] = player };
        if (wild != 0) tables["static_wild"] = wild;
        tables["dynamic_player"] = battle;
        tables["dynamic_opponent"] = battle + battleStride;
        tables["dynamic_ally"] = battle + battleStride * 2;
        tables["dynamic_opponent_2"] = battle + battleStride * 3;
        return new(ceb4e08a.NdsRecordSizeA, tables, basePointer, observedBase - BaseWindow, observedBase + BaseWindow);
    }

    // Battle tables are 0x560 apart: player, player (copy), opponent, opponent, ally, ally, ...
    public static e54dedec Fixed(uint player, uint battle) =>
        new(ceb4e08a.NdsRecordSizeB, new Dictionary<string, uint>
        {
            ["player"] = player,
            ["dynamic_player"] = battle + 0x560,
            ["dynamic_opponent"] = battle + 0x560 * 3,
            ["dynamic_ally"] = battle + 0x560 * 5,
            ["dynamic_opponent_2"] = battle + 0x560 * 7,
        });
}

public sealed class fdb97126(GamehookSession session)
    : e9f81e92(session, e54dedec.FromPointer(0x02106FACu, 0x02260300u, 0xD2AC, 0, 0x597D8, 0x5B0));

public sealed class bdb9cfce(GamehookSession session)
    : e9f81e92(session, e54dedec.FromPointer(0x02101D2Cu, 0x022711B8u, 0xD094, 0x35AC4, 0x5888C, 0x5B0));

public sealed class c1b41fa3(GamehookSession session)
    : e9f81e92(session, e54dedec.FromPointer(0x0211186Cu, 0x0226F234u, 0xD088, 0x38540, 0x5BA78, 0x5D0));

public sealed class f248e50b(GamehookSession session)
    : e9f81e92(session, e54dedec.Fixed(0x022349B4u, 0x0226A234u));

public sealed class cacbced9(GamehookSession session)
    : e9f81e92(session, e54dedec.Fixed(0x022349B4u + 0x20, 0x0226A234u + 0x20));

public sealed class f2452fcc(GamehookSession session)
    : e9f81e92(session, e54dedec.Fixed(0x0221E42Cu - 0x40, 0x02257DB4u - 0x40));

public sealed class a44171aa(GamehookSession session)
    : e9f81e92(session, e54dedec.Fixed(0x0221E42Cu, 0x02257DB4u));
