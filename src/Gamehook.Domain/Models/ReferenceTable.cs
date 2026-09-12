namespace Gamehook.Domain.Models;

/// <summary>Maps raw numeric values to mapper-defined display values.</summary>
public sealed record ReferenceTable(bool IsNumber, IReadOnlyDictionary<ulong, string> Values);
