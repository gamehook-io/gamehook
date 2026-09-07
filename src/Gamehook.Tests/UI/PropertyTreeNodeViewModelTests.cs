using Gamehook.Domain.Property;
using Gamehook.Domain.Interface;
using Gamehook.UI.ViewModels;

namespace Gamehook.Tests.UI;

public class PropertyTreeNodeViewModelTests
{
    [Test]
    public void BuildSortsNumericPathSegmentsNumerically()
    {
        var properties = Enumerable.Range(0, 12)
            .Reverse()
            .Select(index => new NumberProperty(new PropertyConfig(
                $"items.{index}.value", "uint", null, 1, null, null, null)))
            .Cast<Gamehook.Domain.Interface.IProperty>();

        var items = PropertyTreeNodeViewModel.Build(properties).Single();

        Assert.That(items.Children.Select(node => node.Name), Is.EqualTo(
            Enumerable.Range(0, 12).Select(index => index.ToString())));
    }

    [Test]
    public void RefreshDisplayValuePublishesCurrentValueAndRawBytes()
    {
        var property = new NumberProperty(new PropertyConfig(
            "frame", "uint", 0xC000, 1, null, null, null));
        property.Refresh(
            [new IDriver.MemorySegmentSnapshot("WRAM", 0, new byte[] { 42 })],
            new Dictionary<string, ReferenceTable>());
        var node = new PropertyTreeNodeViewModel("frame", property);

        node.RefreshDisplayValue();

        Assert.That(node.DisplayValue, Is.EqualTo("42"));
        Assert.That(node.RawBytesHex, Is.EqualTo("2A"));
    }
}
