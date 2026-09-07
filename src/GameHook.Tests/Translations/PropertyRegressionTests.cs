using GameHook.Domain.Interface;
using GameHook.Domain.Property;
using Jint;

namespace GameHook.Tests.Translations;

public class PropertyRegressionTests
{
    private static readonly Dictionary<string, ReferenceTable> References = new();

    [Test]
    public void Container_changes_are_decoded_even_when_buffer_is_reused()
    {
        var property = new NumberProperty(new("test", "int", 0, 1, null, null, null, MemoryContainer: "buffer"));
        byte[] bytes = [1];
        var containers = new Dictionary<string, ReadOnlyMemory<byte>> { ["buffer"] = bytes };
        property.Refresh([], References, containers);
        bytes[0] = 2;
        property.Refresh([], References, containers);
        Assert.That(property.Value, Is.EqualTo(2));
    }

    [Test]
    public void Removing_dynamic_address_clears_previous_value()
    {
        var property = new NumberProperty(new("test", "int", 0xC000, 1, null, null, null));
        property.Refresh([new IDriver.MemorySegmentSnapshot("WRAM", 0, new byte[] { 42 })], References);
        property.SetAddress(null);
        property.Refresh([], References);
        Assert.That(property.Value, Is.Null);
        Assert.That(property.Bytes.IsEmpty, Is.True);
    }

    [Test]
    public void Changing_bits_redecodes_unchanged_bytes()
    {
        var property = new NumberProperty(new("test", "int", 0xC000, 1, "0", null, null));
        IDriver.MemorySegmentSnapshot[] segments = [new("WRAM", 0, new byte[] { 2 })];
        property.Refresh(segments, References);
        property.SetBits("1");
        property.Refresh(segments, References);
        Assert.That(property.Value, Is.EqualTo(1));
    }

    [TestCase("-1")]
    [TestCase("64")]
    [TestCase("0-64")]
    public void Invalid_bits_are_rejected(string bits) =>
        Assert.Throws<InvalidDataException>(() => new NumberProperty(new("test", "int", 0, 1, bits, null, null)));

    [Test]
    public void Wrapped_memory_range_does_not_match()
    {
        IDriver.MemorySegmentSnapshot[] segments = [new("WRAM", 0, new byte[4])];
        Assert.That(MemoryRegion.TryReadBytes(segments, "WRAM", ulong.MaxValue, 2, out _), Is.False);
        Assert.That(MemoryRegion.TryReadBytes(segments, "WRAM", 0, -1, out _), Is.False);
    }

    [Test]
    public void Runaway_script_hits_execution_limit()
    {
        var engine = new ExpressionEngine();
        Assert.That(() => engine.LoadScript("while (true) {}"), Throws.Exception);
        Assert.That(engine.Engine.Evaluate("1 + 1").AsNumber(), Is.EqualTo(2));
    }
}
