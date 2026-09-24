namespace Gamehook.Domain.Models;

/// <summary>
/// Maps raw numeric values to mapper-defined display values. As a string character map,
/// <c>CharacterWidth</c> is the bytes per character: 2 when any key exceeds 0xFF, otherwise 1.
/// </summary>
public sealed record ReferenceTable(bool IsNumber, IReadOnlyDictionary<ulong, string> Values, int CharacterWidth = 1);
