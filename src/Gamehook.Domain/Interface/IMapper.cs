namespace Gamehook.Domain.Interface;

public interface IMapper
{
    // A shared driver serializes protocols whose emulator memory mapping is global.
    IDriver? MemoryDriver => null;

    GameSystem System { get; }

    string GameName { get; }

    Dictionary<string, IProperty> Properties { get; }

    ReadMetrics LastReadMetrics { get; }

    string? ConnectionWarning { get; }

    int ConsecutiveReadFailures { get; }

    string? LastReadFailureMessage { get; }

    bool HasConnectionRefusal { get; }

    IReadOnlyList<IDriver.MemorySegmentSnapshot> LastMemorySegments { get; }

    IReadOnlyList<PropertyInspection> Inspect(ReadOnlyMemory<byte> bytes);

    Task<bool> ReadAsync(CancellationToken cancellationToken = default);

    /// Encodes `value` into bytes for the named property and writes them to the device. Returns
    /// (false, error) rather than throwing for an unknown property, an unwritable driver/property,
    /// or a value that fails to encode.
    Task<(bool Success, string? Error)> WriteAsync(string propertyPath, object? value, CancellationToken cancellationToken = default);

    /// Raw byte poke (hex editor) - writes `bytes` straight to `regionId`/`startingAddress` with no
    /// property/encoding involved. Still serialized through the same write path as WriteAsync.
    Task<(bool Success, string? Error)> WriteRawBytesAsync(string regionId, ulong startingAddress, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    IAsyncEnumerable<bool> ReadContinuouslyAsync(TimeSpan interval, CancellationToken cancellationToken = default);
}

public sealed record PropertyInspection(string Name, string Type, object? Value, string? Error);
