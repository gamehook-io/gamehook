using Microsoft.Extensions.Configuration;

namespace GameHook.Domain
{
    public enum EndianTypes
    {
        BigEndian,
        LittleEndian
    }

    public record MemoryAddressBlock(int Index, string Name, MemoryAddress StartingAddress, MemoryAddress EndingAddress);
    public record MemoryAddressBlockResult(int Index, string Name, MemoryAddress StartingAddress, MemoryAddress EndingAddress, byte[] Data);

    public class DriverOptions
    {
        public DriverOptions(IConfiguration configuration)
        {
            IpAddress = configuration.GetRequiredValue("DRIVER_LISTEN_IP_ADDRESS");
            Port = int.Parse(configuration.GetRequiredValue("DRIVER_LISTEN_PORT"));
            DriverTimeoutCounter = int.Parse(configuration.GetRequiredValue("DRIVER_TIMEOUT_COUNTER"));
        }

        public string IpAddress { get; }
        public int Port { get; }
        public int DriverTimeoutCounter { get; }
    }

    public class GlossaryItem
    {
        public GlossaryItem(uint key, object? value)
        {
            Key = key;
            Value = value;
        }

        public uint Key { get; private set; }
        public object? Value { get; private set; }
    }
}
