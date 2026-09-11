using System.Globalization;
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

    protected override ReadOnlyMemory<byte> Encode(object? value, ReadOnlyMemory<byte> currentBytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        var raw = value switch
        {
            bool b => b ? 1UL : 0UL,
            string s when bool.TryParse(s, out var parsed) => parsed ? 1UL : 0UL,
            string s when s is "0" or "1" => s == "1" ? 1UL : 0UL,
            null => throw new InvalidDataException("Cannot encode a null value."),
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1UL : 0UL,
        };
        return MergeBits(currentBytes, raw);
    }
}
