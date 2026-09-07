namespace GameHook.Tests.Translations;

public class StringPropertyTests
{
    [Test]
    public void FromBytes_maps_each_byte_through_the_default_character_map()
    {
        var characterMap = new Dictionary<ulong, string> { [0x80] = "A", [0x81] = "B", [0xA0] = "a" };
        var property = PropertyTestFactory.StringFromBytes("nickname", new byte[] { 0x80, 0x81, 0xA0 }, characterMap);

        Assert.That(property.Value, Is.EqualTo("ABa"));
    }

    [Test]
    public void FromBytes_stops_at_the_first_byte_missing_from_the_character_map()
    {
        var characterMap = new Dictionary<ulong, string> { [0x80] = "A", [0x81] = "B" };
        var property = PropertyTestFactory.StringFromBytes("nickname", new byte[] { 0x80, 0x81, 0x50, 0x80 }, characterMap);

        Assert.That(property.Value, Is.EqualTo("AB"));
    }

    [Test]
    public void FromBytes_falls_back_to_latin1_when_no_character_map_is_defined()
    {
        var property = PropertyTestFactory.StringFromBytes("name", new byte[] { 0x43, 0xE9, 0x00, 0x00 });

        Assert.That(property.Value, Is.EqualTo("Cé"));
        Assert.That(property.Bytes.ToArray(), Is.EqualTo(new byte[] { 0x43, 0xE9, 0x00, 0x00 }));
    }
}
