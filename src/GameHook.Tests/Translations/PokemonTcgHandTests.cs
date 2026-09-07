using GameHook.Domain.Property;
using Jint;

namespace GameHook.Tests.Translations;

public class PokemonTcgHandTests : BaseTest
{
    [Test]
    public void Shrinking_hand_clears_discarded_cards()
    {
        var engine = new ExpressionEngine();
        var values = new Dictionary<string, object?>
        {
            ["count"] = 2, ["raw.0"] = 0, ["raw.1"] = 1,
            ["deck.0"] = "First", ["deck.1"] = "Second",
        };
        engine.Engine.SetValue("read", (Func<string, object?>)(path => values.GetValueOrDefault(path)));
        engine.Engine.SetValue("write", (Action<string, object?>)((path, value) => values[path] = value));
        engine.Engine.Execute("var __variables = {}, __state = {}, __memory = {}, __console = {}; var __mapper = { get_property_value: read, set_property_value: write }; ");
        engine.LoadScript(File.ReadAllText(Path.ChangeExtension(GetMapperFilePath("pokemon_tcg.xml"), ".js")));
        engine.Engine.Invoke("getHand", "count", "raw", "hand", "deck");
        Assert.That(values["hand.1"], Is.EqualTo("First"));
        values["count"] = 1;
        engine.Engine.Invoke("getHand", "count", "raw", "hand", "deck");
        Assert.That(values["hand.0"], Is.EqualTo("First"));
        Assert.That(values["hand.1"], Is.Null);
        Assert.That(values["hand.59"], Is.Null);
    }
}
