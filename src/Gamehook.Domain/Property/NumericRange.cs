using System.Globalization;
using System.Numerics;

namespace Gamehook.Domain.Property;

// Range arithmetic for integer properties. "int" is two's complement and decodes to long, "uint"
// (and binaryCodedDecimal) is unsigned and decodes to ulong; both are measured against the field
// width in bits, which is the bits selector's width when there is one and Length * 8 otherwise.
// BigInteger keeps every width up to 64 bits exact without per-type special cases.
internal static class NumericRange
{
    public static bool IsSigned(string type) => type == "int";

    public static BigInteger Min(bool signed, int width) => signed && width > 0 ? -(BigInteger.One << (width - 1)) : BigInteger.Zero;

    public static BigInteger Max(bool signed, int width) =>
        signed ? (width > 0 ? (BigInteger.One << (width - 1)) - 1 : BigInteger.Zero) : (BigInteger.One << width) - 1;

    public static bool Fits(BigInteger value, bool signed, int width) => value >= Min(signed, width) && value <= Max(signed, width);

    /// Reduces `value` modulo 2^width, reading the result as two's complement when signed.
    public static BigInteger Wrap(BigInteger value, bool signed, int width)
    {
        if (width <= 0) return BigInteger.Zero;
        var modulus = BigInteger.One << width;
        var wrapped = value & (modulus - 1);
        return signed && wrapped >= modulus >> 1 ? wrapped - modulus : wrapped;
    }

    /// The boxed form a property of this type exposes: long for signed, ulong for unsigned.
    public static object Box(BigInteger value, bool signed) => signed ? (long)value : (ulong)value;

    /// Raw bit pattern (masked to width) of an in-range value.
    public static ulong ToRaw(BigInteger value, int width) =>
        width >= 64 ? (ulong)(value & ulong.MaxValue) : (ulong)(value & ((BigInteger.One << width) - 1));

    /// Converts any numeric value (CLR integral or floating type, or a numeric string) to an exact
    /// integer. Fractions truncate toward zero when `truncate` is set and are an error otherwise;
    /// NaN and infinity are always an error.
    public static BigInteger ToInteger(object? value, bool truncate) => value switch
    {
        null => throw new InvalidDataException("Cannot use a null value as a number."),
        BigInteger big => big,
        sbyte v => v,
        byte v => v,
        short v => v,
        ushort v => v,
        int v => v,
        uint v => v,
        long v => v,
        ulong v => v,
        double d => FromDouble(d, truncate),
        float f => FromDouble(f, truncate),
        decimal m => truncate || decimal.Truncate(m) == m
            ? new BigInteger(decimal.Truncate(m))
            : throw new InvalidDataException($"'{m.ToString(CultureInfo.InvariantCulture)}' is not a whole number."),
        string s => BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidDataException($"'{s}' is not a valid integer."),
        _ => throw new InvalidDataException($"'{value}' is not a valid integer."),
    };

    private static BigInteger FromDouble(double d, bool truncate)
    {
        if (!double.IsFinite(d)) throw new InvalidDataException($"'{d.ToString(CultureInfo.InvariantCulture)}' is not a valid integer.");
        var whole = Math.Truncate(d);
        if (!truncate && whole != d) throw new InvalidDataException($"'{d.ToString(CultureInfo.InvariantCulture)}' is not a whole number.");
        return new BigInteger(whole);
    }

    public static bool IsNumeric(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or double or float or decimal or BigInteger;
}
