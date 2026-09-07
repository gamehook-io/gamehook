using Gamehook.Domain.Interface;
using Gamehook.Domain.Property;

namespace Gamehook.Tests.Translations;

public class NumberPropertyTests
{
    [Test]
    public void Refresh_reports_null_value_when_driver_omits_the_property_region()
    {
        var property = new NumberProperty(new PropertyConfig("hp", "int", 0xC000, 2, null, null, null));

        property.Refresh(
            new Dictionary<string, IDriver.MemorySegmentSnapshot>(),
            new Dictionary<string, ReferenceTable>());

        Assert.Multiple(() =>
        {
            Assert.That(property.Value, Is.Null);
            Assert.That(property.Bytes.IsEmpty, Is.True);
        });
    }

    [Test]
    public void FromBytes_applies_bits()
    {
        var property = PropertyTestFactory.NumberFromBytes("progress", new byte[] { 0b_1100_0000 }, "int", "6-7");

        Assert.That(property.Name, Is.EqualTo("progress"));
        Assert.That(property.Value, Is.EqualTo(3));
        Assert.That(property.Bytes.ToArray(), Is.EqualTo(new byte[] { 0b_1100_0000 }));
    }

    [Test]
    public void PropertyExpressions_apply_transforms_an_already_decoded_value()
    {
        // Mirrors the real pipeline: Property never touches the engine - Mapper decodes first,
        // then applies a compiled after-read-value-expression externally via PropertyExpressions.
        var engine = new ExpressionEngine();
        var compiled = engine.Compile("x % 64");
        var property = PropertyTestFactory.NumberFromBytes("pp", [130], "int");

        PropertyExpressions.Apply(property, compiled);

        Assert.That(property.Value, Is.EqualTo(2));
    }

    [Test]
    public void PropertyExpressions_apply_does_not_compound_across_reads()
    {
        // Property.Refresh keeps the previous Value when the bytes haven't changed, so an
        // expression re-applied to Value rather than to the raw reading would transform its own
        // output every frame - 'x * 2' over a constant 3 would walk 6, 12, 24, ...
        var engine = new ExpressionEngine();
        var compiled = engine.Compile("x * 2");
        var property = PropertyTestFactory.NumberFromBytes("attack", [3], "int");

        PropertyExpressions.Apply(property, compiled);
        var afterFirstRead = property.Value;
        PropertyExpressions.Apply(property, compiled);
        PropertyExpressions.Apply(property, compiled);

        Assert.That(afterFirstRead, Is.EqualTo(6));
        Assert.That(property.Value, Is.EqualTo(6));
    }

    [Test]
    public void FromBytes_reads_uint()
    {
        var property = PropertyTestFactory.NumberFromBytes("ot_id", new byte[] { 0x00, 0x01 }, "uint");

        Assert.That(property.Value, Is.EqualTo(1));
    }

    [Test]
    public void FromBytes_reads_uint_above_int_max_as_bit_pattern()
    {
        var property = PropertyTestFactory.NumberFromBytes("personality_value", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "uint");

        Assert.That(property.Value, Is.EqualTo(-1));
    }

    [Test]
    public void FromBytes_reads_binary_coded_decimal()
    {
        var property = PropertyTestFactory.NumberFromBytes("score", new byte[] { 0x12, 0x34 }, "binaryCodedDecimal");

        Assert.That(property.Value, Is.EqualTo(1234));
    }

    [Test]
    public void FromBytes_rejects_invalid_binary_coded_decimal()
    {
        Assert.That(
            () => PropertyTestFactory.NumberFromBytes("score", new byte[] { 0x1A }, "binaryCodedDecimal"),
            Throws.TypeOf<InvalidDataException>());
    }
}
