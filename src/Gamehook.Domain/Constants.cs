namespace Gamehook.Domain;

public enum Endianness
{
    Little,
    Big,
}

// A null length means the region size is not yet defined (or depends on the cartridge).
// A null bus address means it cannot be read through the normal CPU memory map.
public sealed record MemoryRegionDefinition(string Id, int? Length = null, uint? BusAddress = null);

public sealed class GameSystem(string id, MemoryRegionDefinition[] memoryRegions, Endianness integerEndianness = Endianness.Big)
{
    public string Id { get; } = id;
    public IReadOnlyList<MemoryRegionDefinition> RegionDefinitions { get; } = Array.AsReadOnly(memoryRegions);
    public IReadOnlyList<string> MemoryRegions { get; } = Array.AsReadOnly(memoryRegions.Select(region => region.Id).ToArray());
    public Endianness IntegerEndianness { get; } = integerEndianness;

    private static readonly MemoryRegionDefinition[] GameBoyRegions =
    [
        new("SRAM", 0x2000, 0xA000), new("VRAM", 0x2000, 0x8000),
        new("WRAM", 0x2000, 0xC000), new("OAM", 0xA0, 0xFE00),
        new("IO", 0x80, 0xFF00), new("HRAM", 0x7F, 0xFF80),
        new("IE", 1, 0xFFFF), new("Wave RAM", 0x10, 0xFF30),
    ];

    public static readonly GameSystem GB = new("GB", GameBoyRegions, Endianness.Big);
    public static readonly GameSystem GBC = new("GBC", GameBoyRegions, Endianness.Big);
    public static readonly GameSystem SNES = new("SNES",
        [new("SRAM"), new("WRAM", BusAddress: 0x7E0000), new("VRAM"), new("OAM"), new("CGRAM")], Endianness.Little);
    public static readonly GameSystem GBA = new("GBA",
    [
        new("SRAM"), new("EWRAM", 0x40000, 0x02000000), new("IWRAM", 0x8000, 0x03000000),
        new("VRAM", 0x18000, 0x06000000), new("OAM", 0x400, 0x07000000),
        new("Palette RAM", 0x400, 0x05000000),
    ], Endianness.Little);
    public static readonly GameSystem N64 = new("N64",
        [new("RDRAM", BusAddress: 0), new("SP DMEM"), new("SP IMEM"), new("PIF RAM")], Endianness.Big);

    public static readonly GameSystem[] All = [GB, GBC, SNES, GBA, N64];
}
