namespace Gamehook.Domain.Interface;

public interface IDriver
{
    public sealed record Request(GameSystem System, IReadOnlyList<MemorySegmentRequest> Segments);
    public sealed record MemorySegmentRequest(string RegionId, ulong StartingAddress, int Length);

    public sealed record Response(DateTimeOffset CapturedAt, IReadOnlyList<MemorySegmentSnapshot> Segments);
    public sealed record MemorySegmentSnapshot(string RegionId, ulong StartingAddress, ReadOnlyMemory<byte> Bytes);
    
    Task<Response> Read(Request request);
}
