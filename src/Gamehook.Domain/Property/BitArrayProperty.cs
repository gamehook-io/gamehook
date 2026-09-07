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
}
