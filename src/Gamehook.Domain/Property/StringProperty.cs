using System.Text;
using Gamehook.Domain;
using Gamehook.Domain.Interface;

namespace Gamehook.Domain.Property;

public sealed class StringProperty : Property
{
    private const string DefaultCharacterMapReference = "defaultCharacterMap";

    public StringProperty(PropertyConfig config, Endianness integerEndianness = Endianness.Big, GameSystem? system = null)
        : base(config, integerEndianness, system)
    {
    }

    protected override object? DecodeStaticValue(string value) => value;

    protected override object? Decode(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        var characterMapName = CharacterMap ?? DefaultCharacterMapReference;
        if (references.TryGetValue(characterMapName, out var characterMap))
        {
            return DecodeCharacterMap(bytes.Span, characterMap);
        }

        if (CharacterMap is not null)
        {
            throw new InvalidDataException($"Character map '{CharacterMap}' is not defined.");
        }

        return Encoding.Latin1.GetString(bytes.Span).TrimEnd('\0');
    }

    // game text isn't ASCII/Latin1 - each byte is a tile index that only the mapper's own character map can translate;
    // an unmapped byte (e.g. the string terminator) ends the string, matching how these ROMs delimit text.
    private static string DecodeCharacterMap(ReadOnlySpan<byte> bytes, ReferenceTable characterMap)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (!characterMap.Values.TryGetValue(b, out var character)) break;
            builder.Append(character);
        }
        return builder.ToString();
    }
}
