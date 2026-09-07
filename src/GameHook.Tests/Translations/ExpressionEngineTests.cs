using GameHook.Domain.Property;
using Jint;

namespace GameHook.Tests.Translations;

public class ExpressionEngineTests
{
    [TestCase("x % 64")]
    [TestCase("Math.floor(x / 64)")]
    [TestCase("(x - 36027608) / 548")]
    [TestCase("(x % 512) % 7")]
    [TestCase("(x - (36099748 + 0x20)) / 548")]
    [TestCase("Math.round((x / 255) * 100)")]
    [TestCase("-x + 2 * (3 + x) / 7")]
    [TestCase("x / 0")]
    [TestCase("x % 0")]
    public void Basic_expressions_match_javascript_without_initializing_script(string expression)
    {
        var engine = new ExpressionEngine();
        var compiled = engine.Compile(expression);
        var javascript = new Engine();
        javascript.Execute($"function expected(x) {{ return ({expression}); }}");
        foreach (var x in new double[] { -4294967295, -64.5, -1.5, -0.5, -0.1, 0, 0.5, 63, 130, 255, 4294967295 })
        {
            var expected = javascript.Invoke("expected", x).AsNumber();
            var actual = engine.Invoke(compiled, x);
            Assert.That(actual, Is.EqualTo(expected), $"{expression}, x={x}");
            if (expected == 0)
                Assert.That(BitConverter.DoubleToInt64Bits(actual), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)));
        }
        Assert.That(engine.IsScriptInitialized, Is.False);
        Assert.That(engine.Compile(expression), Is.EqualTo(compiled));
        Assert.That(engine.ScriptInvocations, Is.Zero);
    }

    [TestCase("x ^ 1", 5, 4)]
    [TestCase("x--", 5, 5)]
    [TestCase("010 + x", 5, 13)]
    [TestCase("Math.round(x, 2)", 1.5, 2)]
    public void Unsupported_syntax_uses_javascript(string expression, double input, double expected)
    {
        var engine = new ExpressionEngine();
        var compiled = engine.Compile(expression);
        Assert.That(engine.IsScriptInitialized, Is.True);
        Assert.That(engine.Invoke(compiled, input), Is.EqualTo(expected));
    }

    [Test]
    public void Mapper_script_helpers_can_mix_with_basic_expressions()
    {
        var engine = new ExpressionEngine();
        var basic = engine.Compile("x % 64");
        var scripted = engine.Compile("decryptItemQuantity(x)");
        engine.LoadScript("function decryptItemQuantity(x) { return x ^ 1; }");
        Assert.That(engine.Invoke(basic, 130), Is.EqualTo(2));
        Assert.That(engine.Invoke(scripted, 5), Is.EqualTo(4));
    }
}
