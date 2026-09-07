using Gamehook.Domain.Interface;
using Gamehook.Domain.Property;

namespace Gamehook.Tests.Translations;

internal static class PropertyTestFactory
{
    private const string Region = "WRAM";
    private const ulong Address = 0xC000;

    public static NumberProperty NumberFromBytes(string name, byte[] bytes, string type, string? bits = null) =>
        Refresh(new NumberProperty(Config(name, type, bytes.Length, bits)), bytes);

    public static StringProperty StringFromBytes(string name, byte[] bytes) =>
        Refresh(new StringProperty(Config(name, "string", bytes.Length, null)), bytes);

    public static StringProperty StringFromBytes(string name, byte[] bytes, IReadOnlyDictionary<ulong, string> characterMap)
    {
        var property = new StringProperty(Config(name, "string", bytes.Length, null));
        property.Refresh(
            new Dictionary<string, IDriver.MemorySegmentSnapshot> { [Region] = new IDriver.MemorySegmentSnapshot(Region, 0, bytes) },
            new Dictionary<string, ReferenceTable> { ["defaultCharacterMap"] = new ReferenceTable(false, characterMap) });
        return property;
    }

    public static BooleanProperty BooleanFromBytes(string name, byte[] bytes, string? bits = null) =>
        Refresh(new BooleanProperty(Config(name, "bool", bytes.Length, bits)), bytes);

    public static BitArrayProperty BitArrayFromBytes(string name, byte[] bytes) =>
        Refresh(new BitArrayProperty(Config(name, "bitArray", bytes.Length, null)), bytes);

    private static PropertyConfig Config(string name, string type, int length, string? bits) => new(name, type, Address, length, bits, null, null);

    private static TProperty Refresh<TProperty>(TProperty property, byte[] bytes) where TProperty : Property
    {
        property.Refresh(
            new Dictionary<string, IDriver.MemorySegmentSnapshot> { [Region] = new IDriver.MemorySegmentSnapshot(Region, 0, bytes) },
            new Dictionary<string, ReferenceTable>());
        return property;
    }
}
