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

    IAsyncEnumerable<bool> ReadContinuouslyAsync(TimeSpan interval, CancellationToken cancellationToken = default);
}

public sealed record PropertyInspection(string Name, string Type, object? Value, string? Error);
