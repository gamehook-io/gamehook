namespace GameHook.Domain.Interfaces
{
    public record ReadBytesResult
    {
        public Dictionary<string, byte[]> Bytes { get; init; } = new Dictionary<string, byte[]>();
    }

    /// <summary>
    /// Driver interface for interacting with a emulator.
    /// 
    /// - Driver should not log anything above LogDebug.
    /// - Any errors encountered should be thrown as exceptions.
    /// </summary>
    public interface IGameHookDriver
    {
        string ProperName { get; }

        Task<ReadBytesResult> ReadBytes(IEnumerable<MemoryAddressBlock> blocks);

        Task WriteBytes(MemoryAddress startingMemoryAddress, byte[] values);
    }
}