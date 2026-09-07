namespace Gamehook.Tests.Translations;

public class BitArrayPropertyTests
{
    [Test]
    public void FromBytes_reads_bits_least_significant_bit_first()
    {
        var property = PropertyTestFactory.BitArrayFromBytes("flags", new byte[] { 0b_0000_0101 });

        Assert.That(property.Value, Is.EqualTo(new[] { true, false, true, false, false, false, false, false }));
    }
}
