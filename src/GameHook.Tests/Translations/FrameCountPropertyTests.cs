namespace GameHook.Tests.Translations;

// mirrors the "frameCount" property found in the Pokemon mappers: type="int" length="4"
public class FrameCountPropertyTests
{
    [Test]
    public void FrameCount_decodes_a_4_byte_counter()
    {
        var property = PropertyTestFactory.NumberFromBytes("frameCount", new byte[] { 0x0F, 0x2A, 0x00, 0x8B }, "int");

        Assert.That(property.Value, Is.EqualTo(254410891));
    }

    [Test]
    public void FrameCount_overflow_reports_property_name_type_length_and_value()
    {
        // a running frame counter eventually sets the top bit, which overflows a checked int cast
        var exception = Assert.Throws<InvalidDataException>(() =>
            PropertyTestFactory.NumberFromBytes("frameCount", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "int"));

        Assert.That(exception!.Message, Does.Contain("'frameCount'"));
        Assert.That(exception.Message, Does.Contain("type int"));
        Assert.That(exception.Message, Does.Contain("length 4"));
        Assert.That(exception.Message, Does.Contain("0xFFFFFFFF"));
        Assert.That(exception.InnerException, Is.TypeOf<OverflowException>());
    }
}
