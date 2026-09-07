using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Domain.Property;

public sealed class BitArrayProperty : Property
{
    public BitArrayProperty(PropertyConfig config, Endianness integerEndianness = Endianness.Big, GameSystem? system = null)
        : base(config, integerEndianness, system)
    {
    }

    protected override object? Decode(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        var value = new bool[checked(bytes.Length * 8)];
        for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
        {
            for (var bitIndex = 0; bitIndex < 8; bitIndex++)
            {
                value[byteIndex * 8 + bitIndex] = (bytes.Span[byteIndex] & (1 << bitIndex)) != 0;
            }
        }
        return value;
    }

    protected override ReadOnlyMemory<byte> Encode(object? value, ReadOnlyMemory<byte> currentBytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        if (value is not bool[] bits)
        {
            throw new InvalidDataException("bitArray properties can only be encoded from a bool[].");
        }
        if (bits.Length != currentBytes.Length * 8)
        {
            throw new InvalidDataException($"Expected {currentBytes.Length * 8} bit(s), got {bits.Length}.");
        }

        var bytes = new byte[currentBytes.Length];
        for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
        {
            byte b = 0;
            for (var bitIndex = 0; bitIndex < 8; bitIndex++)
            {
                if (bits[byteIndex * 8 + bitIndex]) b |= (byte)(1 << bitIndex);
            }
            bytes[byteIndex] = b;
        }
        return bytes;
    }
}
