namespace Gamehook.Domain.Models;

/// <summary>Read-only virtual bytes derived from a declared slice of device RAM.</summary>
public sealed record VirtualMemoryRegion(
    string Id,
    ulong? SourceAddress,
    int Length,
    ReadOnlyMemory<byte> Bytes);
