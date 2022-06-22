namespace GameHook.Domain.Interfaces
{
    public interface IPlatformOptions
    {
        public EndianTypes EndianType { get; }

        public IEnumerable<MemoryAddressBlock> Ranges { get; }
    }
}
