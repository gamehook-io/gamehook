using GameHook.Domain;
using GameHook.Domain.Interfaces;

namespace GameHook.Application
{
    public class NES_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.BigEndian;

        public IEnumerable<PlatformRange> Ranges { get; } = new List<PlatformRange>()
        {
            new PlatformRange("Internal RAM", 0x0000, 0x0400) // 2kB Internal RAM, mirrored 4 times
        };
    }

    public class SNES_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.LittleEndian;

        public IEnumerable<PlatformRange> Ranges { get; } = new List<PlatformRange>()
        {
            new PlatformRange("?", 0x7E6D00, 0x7E7FFF)
        };
    }

    public class GB_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.BigEndian;

        public IEnumerable<PlatformRange> Ranges { get; } = new List<PlatformRange>()
        {
            new PlatformRange("ROM Bank 00", 0x0000, 0x3FFF),
            new PlatformRange("ROM Bank 01", 0x4000, 0x7FFF),
            new PlatformRange("VRAM", 0x8000, 0x9FFF),
            new PlatformRange("External RAM (Part 1)", 0xA000, 0xAFFF),
            new PlatformRange("External RAM (Part 2)", 0xB000, 0xBFFF),
            new PlatformRange("Work RAM (Part 1)", 0xC000, 0xCFFF),
            new PlatformRange("Work RAM (Part 2)", 0xD000, 0xDFFF),
            new PlatformRange("High RAM", 0xFF80, 0xFFFF)
        };
    }

    public class GBA_PlatformOptions : IPlatformOptions
    {
        public EndianTypes EndianType { get; } = EndianTypes.LittleEndian;

        public IEnumerable<PlatformRange> Ranges { get; } = new List<PlatformRange>()
        {
            // new PlatformRange("BIOS",  0x00000000, 0x00003FF0),
            new PlatformRange("Partial EWRAM", 0x02024280, 0x02024280 + 9999),
            // new PlatformRange("IWRAM", 0x03000000, 0x03007FF0),
        };
    }
}
