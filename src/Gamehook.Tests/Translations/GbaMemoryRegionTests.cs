using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Property;

namespace Gamehook.Tests.Translations;

public class GbaMemoryRegionTests
{
    [Test]
    public void Resolves_ewram_address()
    {
        var property = new NumberProperty(new PropertyConfig("dma_a_probe", "uint", 0x02024744, 4, null, null, null), system: GameSystem.GBA);

        Assert.That(property.Region, Is.EqualTo("EWRAM"));
        Assert.That(property.BuildRequest(), Is.EqualTo(new IDriver.MemorySegmentRequest("EWRAM", 0x24744, 4)));
    }

    [Test]
    public void Resolves_iwram_address()
    {
        var property = new NumberProperty(new PropertyConfig("dma_pointer_table", "uint", 0x03005D8C, 4, null, null, null), system: GameSystem.GBA);

        Assert.That(property.Region, Is.EqualTo("IWRAM"));
        Assert.That(property.BuildRequest(), Is.EqualTo(new IDriver.MemorySegmentRequest("IWRAM", 0x5D8C, 4)));
    }

    [Test]
    public void Reports_null_region_for_an_unmapped_gba_address()
    {
        var property = new NumberProperty(new PropertyConfig("out_of_range", "uint", 0x09000000, 4, null, null, null), system: GameSystem.GBA);

        Assert.That(property.Region, Is.Null);
    }

    [Test]
    public void Same_raw_address_resolves_differently_without_a_gba_system()
    {
        // 0x8000 is a valid Game Boy VRAM address but not a mapped GBA region - confirms the
        // system parameter actually changes resolution rather than being ignored.
        var gbProperty = new NumberProperty(new PropertyConfig("gb_vram", "uint", 0x8000, 1, null, null, null));
        var gbaProperty = new NumberProperty(new PropertyConfig("gba_unmapped", "uint", 0x8000, 1, null, null, null), system: GameSystem.GBA);

        Assert.That(gbProperty.Region, Is.EqualTo("VRAM"));
        Assert.That(gbaProperty.Region, Is.Null);
    }
}
