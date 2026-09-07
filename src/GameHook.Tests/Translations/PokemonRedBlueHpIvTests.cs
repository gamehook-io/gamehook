using System.Xml.Linq;
using GameHook.Domain.Property;

namespace GameHook.Tests.Translations;

public class PokemonRedBlueHpIvTests : BaseTest
{
    [Test]
    public void Xml_hp_iv_matches_original_calculation_for_every_packed_iv_value_without_javascript()
    {
        var document = XDocument.Load(GetMapperFilePath("pokemon_red_blue.xml"));
        var hp = document.Root!.Element("macros")!.Element("party_pokemon")!
            .Element("ivs")!.Elements("property").Single(p => (string?)p.Attribute("name") == "hp");
        Assert.That((string?)hp.Attribute("address"), Is.EqualTo("{address} + 27"));
        Assert.That((string?)hp.Attribute("length"), Is.EqualTo("2"));
        var engine = new ExpressionEngine();
        var expression = engine.Compile((string)hp.Attribute("after-read-value-expression")!);

        for (var packed = 0; packed <= ushort.MaxValue; packed++)
        {
            var expected = ((packed >> 12) & 1) << 3
                | ((packed >> 8) & 1) << 2
                | ((packed >> 4) & 1) << 1
                | (packed & 1);
            var property = PropertyTestFactory.NumberFromBytes("hp", [(byte)(packed >> 8), (byte)packed], "int");
            PropertyExpressions.Apply(property, expression);
            Assert.That(property.Value, Is.EqualTo(expected), $"Packed IVs: {packed:X4}");
        }

        Assert.That(engine.IsScriptInitialized, Is.False);
    }
}
