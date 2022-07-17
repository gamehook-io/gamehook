using GameHook.Domain;
using GameHook.Domain.Interfaces;

namespace GameHook.Application
{
    public class NES_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.BigEndian;

        public MemoryAddressBlock[] Ranges { get; } = new List<MemoryAddressBlock>()
        {
            new MemoryAddressBlock(0, "Internal RAM", 0x0000, 0x0400) // 2kB Internal RAM, mirrored 4 times
        }.ToArray();
    }

    public class SNES_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.LittleEndian;

        public MemoryAddressBlock[] Ranges { get; } = new List<MemoryAddressBlock>()
        {
            new MemoryAddressBlock(0, "?", 0x7E6D00, 0x7E7FFF)
        }.ToArray();
    }

    public class GB_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.LittleEndian;

        public MemoryAddressBlock[] Ranges { get; } = new List<MemoryAddressBlock>()
        {
            new MemoryAddressBlock(0, "ROM Bank 00", 0x0000, 0x3FFF),
            new MemoryAddressBlock(1, "ROM Bank 01", 0x4000, 0x7FFF),
            new MemoryAddressBlock(2, "VRAM", 0x8000, 0x9FFF),
            new MemoryAddressBlock(3, "External RAM (Part 1)", 0xA000, 0xAFFF),
            new MemoryAddressBlock(4, "External RAM (Part 2)", 0xB000, 0xBFFF),
            new MemoryAddressBlock(5, "Work RAM (Part 1)", 0xC000, 0xCFFF),
            new MemoryAddressBlock(6, "Work RAM (Part 2)", 0xD000, 0xDFFF),
            new MemoryAddressBlock(7, "High RAM", 0xFF80, 0xFFFF)
        }.ToArray();
    }

    public class GBA_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.BigEndian;

        public MemoryAddressBlock[] Ranges { get; } = new List<MemoryAddressBlock>()
        {
            // new PlatformRange("BIOS",  0x00000000, 0x00003FF0),
            new MemoryAddressBlock(0, "Partial EWRAM", 0x02024000, 0x02024000 + 9999),
            // new PlatformRange("IWRAM", 0x03000000, 0x03007FF0),
        }.ToArray();
    }
}
