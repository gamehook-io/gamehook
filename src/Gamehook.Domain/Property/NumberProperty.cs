using System.Globalization;
using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Domain.Property;

public sealed class NumberProperty : Property
{
    public NumberProperty(PropertyConfig config, Endianness integerEndianness = Endianness.Big, GameSystem? system = null)
        : base(config, integerEndianness, system)
    {
        if (config.Type is not ("int" or "uint" or "binaryCodedDecimal"))
            throw new NotSupportedException($"Unsupported numeric property type '{config.Type}'.");
    }

    protected override object? DecodeStaticValue(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    protected override object? Decode(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references) => Type switch
    {
        "int" => checked((int)ReadRawValue(bytes)),
        // decimal->int always range-checks regardless of checked/unchecked, so go through ulong first: a 4-byte
        // uint (e.g. personality_value) can exceed int.MaxValue, and we store its bit pattern rather than throw.
        "uint" => unchecked((int)ReadRawValue(bytes)),
        _ => checked((int)ReadBinaryCodedDecimal(bytes.Span)),
    };

    private static decimal ReadBinaryCodedDecimal(ReadOnlySpan<byte> bytes)
    {
        decimal value = 0;
        foreach (var b in bytes)
        {
            if ((b >> 4) > 9 || (b & 0x0F) > 9) throw new InvalidDataException("Invalid binary-coded decimal.");
            value = value * 100 + ((b >> 4) * 10) + (b & 0x0F);
        }
        return value;
    }
}
