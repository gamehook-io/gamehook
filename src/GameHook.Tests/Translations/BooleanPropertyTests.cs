namespace GameHook.Tests.Translations;

public class BooleanPropertyTests
{
    [TestCase(0b_0000_0000, "3", false)]
    [TestCase(0b_0000_1000, "3", true)]
    public void FromBytes_reads_selected_bit(byte value, string bits, bool expected)
    {
        var property = PropertyTestFactory.BooleanFromBytes("flag", new[] { value }, bits);

        Assert.That(property.Value, Is.EqualTo(expected));
    }

    [Test]
    public void FromBytes_treats_nonzero_unmasked_value_as_true()
    {
        var property = PropertyTestFactory.BooleanFromBytes("flag", new byte[] { 2 });

        Assert.That(property.Value, Is.True);
    }
}
