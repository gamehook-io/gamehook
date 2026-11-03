using System.Globalization;
using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Models;

namespace Gamehook.Domain.Property;

public sealed class NumberProperty : Property
{
    public NumberProperty(PropertyConfig config, Endianness integerEndianness = Endianness.Big, GameSystem? system = null)
        : base(config, integerEndianness, system)
    {
        if (config.Type is not ("int" or "uint" or "binaryCodedDecimal"))
            throw new NotSupportedException($"Unsupported numeric property type '{config.Type}'.");
    }

    protected override object? DecodeStaticValue(string value) => Type switch
    {
        "int" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed) ? signed : null,
        _ => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned) ? unsigned : null,
    };

    protected override object? Decode(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references) => Type switch
    {
        "int" => ReadSigned(bytes),
        "uint" => ReadRawValue(bytes),
        _ => checked((ulong)ReadBinaryCodedDecimal(bytes.Span)),
    };

    protected override ReadOnlyMemory<byte> Encode(object? value, ReadOnlyMemory<byte> currentBytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        var raw = Type switch
        {
            "int" or "uint" => ToRaw(value, NumericRange.IsSigned(Type), FieldWidth(currentBytes.Length)),
            _ => ToBinaryCodedDecimalRaw(value, currentBytes.Length),
        };
        return MergeBits(currentBytes, raw);
    }

    // Strict: a value outside the type's range for this field width is an error, never wrapped or
    // truncated. A negative int is stored as its two's complement bit pattern.
    private static ulong ToRaw(object? value, bool signed, int width)
    {
        var number = NumericRange.ToInteger(value, truncate: false);
        if (!NumericRange.Fits(number, signed, width))
        {
            throw new InvalidDataException($"Value {number} is out of range for a {width}-bit {(signed ? "signed" : "unsigned")} field.");
        }
        return NumericRange.ToRaw(number, width);
    }

    private static ulong ToBinaryCodedDecimalRaw(object? value, int byteLength)
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

        var digits = text.TrimStart('0');
        if (digits.Length > byteLength * 2)
        {
            throw new InvalidDataException($"'{text}' exceeds {byteLength * 2} binary-coded decimal digit(s).");
        }

        ulong raw = 0;
        foreach (var digit in digits)
        {
            raw = (raw << 4) | (ulong)(byte)(digit - '0');
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
