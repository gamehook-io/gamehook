namespace Gamehook.Domain.Interface;

public sealed record ReferenceTable(bool IsNumber, IReadOnlyDictionary<ulong, string> Values);
