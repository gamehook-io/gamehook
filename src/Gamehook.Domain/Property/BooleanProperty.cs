using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Domain.Property;

public sealed class BooleanProperty : Property
{
    public BooleanProperty(PropertyConfig config, Endianness integerEndianness = Endianness.Big, GameSystem? system = null)
        : base(config, integerEndianness, system)
    {
    }

    protected override object? DecodeStaticValue(string value)
    {
        var parsed = bool.TryParse(value, out var boolValue);
        if (!parsed && value is not ("0" or "1")) return null;
        return boolValue || value == "1";
    }

    protected override object? Decode(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references) => ReadRawValue(bytes) != 0;
}
