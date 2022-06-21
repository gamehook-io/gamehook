using GameHook.Domain.DTOs;

namespace GameHook.Domain.Interfaces
{
    public record UpdatedMemoryAddressEvent
    {
        public UpdatedMemoryAddressEvent(MemoryAddress memoryAddress, byte[] value)
        {
            MemoryAddress = memoryAddress;
            Value = value;
        }

        public MemoryAddress MemoryAddress { get; }
        public byte[] Value { get; }
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

        Task WriteBytes(MemoryAddress memoryAddress, byte[] values);

        bool StartWatching(IContainerForDriver handler);

        void StopWatchingAndReset();
    }
}