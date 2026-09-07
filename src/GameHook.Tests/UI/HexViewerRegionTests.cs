using GameHook.Domain;
using GameHook.Domain.Property;
using GameHook.UI.ViewModels.Tools;

namespace GameHook.Tests.UI;

public class HexViewerRegionTests
{
    [Test]
    public void Gba_exposes_regions_with_known_sizes()
    {
        Assert.That(HexViewerToolViewModel.GetRegions(GameSystem.GBA), Is.EqualTo(
            new[] { "EWRAM", "IWRAM", "OAM", "Palette RAM", "VRAM" }));
    }

    [Test]
    public void Viewer_uses_system_metadata_without_a_system_allowlist()
    {
        var system = new GameSystem("Custom", [new("RAM", 16, 0x100), new("Unknown")]);
        Assert.That(HexViewerToolViewModel.GetRegions(system), Is.EqualTo(new[] { "RAM" }));
        Assert.That(HexViewerToolViewModel.GetRegions(null), Is.Empty);
    }

    [TestCase(0xFF30ul, "IO", 0x30ul)]
    [TestCase(0xDFFFul, "WRAM", 0x1FFFul)]
    [TestCase(0xFFFFul, "IE", 0ul)]
    public void Gb_translation_preserves_boundaries_and_overlapping_io(ulong address, string region, ulong offset)
    {
        Assert.That(MemoryRegion.ToRegion(address, GameSystem.GB), Is.EqualTo(region));
        Assert.That(MemoryRegion.ToOffset(address, GameSystem.GB), Is.EqualTo(offset));
    }
}
