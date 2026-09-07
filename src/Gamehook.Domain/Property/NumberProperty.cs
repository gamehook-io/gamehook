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

    protected override ReadOnlyMemory<byte> Encode(object? value, ReadOnlyMemory<byte> currentBytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        var raw = Type switch
        {
            "int" or "uint" => ToRaw(value),
            _ => ToBinaryCodedDecimalRaw(value),
        };
        return MergeBits(currentBytes, raw);
    }

    private static ulong ToRaw(object? value) => value switch
    {
        null => throw new InvalidDataException("Cannot encode a null value."),
        ulong u => u,
        string s => ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? unchecked((ulong)parsed)
            : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)
                ? unchecked((ulong)signed)
                : throw new InvalidDataException($"'{s}' is not a valid integer."),
        _ => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
    };

    private static ulong ToBinaryCodedDecimalRaw(object? value)
    {
        var text = value switch
        {
            null => throw new InvalidDataException("Cannot encode a null value."),
            string s => s,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        };

        if (!decimal.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue) || decimalValue < 0)
            throw new InvalidDataException($"'{text}' is not a valid non-negative binary-coded decimal.");

        ulong raw = 0;
        var shift = 0;
        while (decimalValue > 0)
        {
            var digit = (byte)(decimalValue % 10);
            decimalValue /= 10;
            raw |= (ulong)digit << shift;
            shift += 4;
        }
        return raw;
    }

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
