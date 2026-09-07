namespace GameHook.Domain.Interface;

public interface IDriver
{
    public sealed record Request(GameSystem System, IReadOnlyList<MemorySegmentRequest> Segments);
    public sealed record MemorySegmentRequest(string RegionId, ulong StartingAddress, int Length);

    public sealed record Response(DateTimeOffset CapturedAt, IReadOnlyList<MemorySegmentSnapshot> Segments);
    public sealed record MemorySegmentSnapshot(string RegionId, ulong StartingAddress, ReadOnlyMemory<byte> Bytes);

    public sealed record WriteRequest(GameSystem System, IReadOnlyList<MemorySegmentWrite> Segments);
    public sealed record MemorySegmentWrite(string RegionId, ulong StartingAddress, ReadOnlyMemory<byte> Bytes);

    Task<Response> Read(Request request);

    /// Not every driver can write (e.g. a save-state snapshot is read-only) - the default throws
    /// NotSupportedException, so only drivers that actually support writing need to override this.
    Task Write(WriteRequest request) => throw new NotSupportedException($"{GetType().Name} does not support writing.");
}
