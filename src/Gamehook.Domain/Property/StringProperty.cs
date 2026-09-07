using System.Linq;
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

    protected override ReadOnlyMemory<byte> Encode(object? value, ReadOnlyMemory<byte> currentBytes, IReadOnlyDictionary<string, ReferenceTable> references)
    {
        if (value is not string text)
        {
            throw new InvalidDataException("string properties can only be encoded from a string.");
        }

        var characterMapName = CharacterMap ?? DefaultCharacterMapReference;
        var encoded = references.TryGetValue(characterMapName, out var characterMap)
            ? EncodeCharacterMap(text, characterMap)
            : Encoding.Latin1.GetBytes(text);

        if (encoded.Length > currentBytes.Length)
        {
            throw new InvalidDataException($"Encoded text is {encoded.Length} byte(s), which exceeds this property's length of {currentBytes.Length}.");
        }

        var bytes = new byte[currentBytes.Length];
        encoded.CopyTo(bytes, 0);
        return bytes;
    }

    // Inverse of DecodeCharacterMap: each character must map back to exactly one tile-index byte.
    private static byte[] EncodeCharacterMap(string text, ReferenceTable characterMap)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i].ToString();
            var match = characterMap.Values.FirstOrDefault(entry => entry.Value == character);
            if (match.Value is null)
            {
                throw new InvalidDataException($"Character '{character}' has no entry in the character map.");
            }
            bytes[i] = checked((byte)match.Key);
        }
        return bytes;
    }
}
