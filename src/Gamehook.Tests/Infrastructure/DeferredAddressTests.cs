using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Gamehook.Infrastructure;
using NCalc;

namespace Gamehook.Tests.Infrastructure;

// DeferredAddress reduces a "{{token}}" address to arithmetic at compile time so the read loop
// never parses or evaluates anything. These tests pin that the reduction is faithful - a fast path
// that quietly disagrees with the expression it replaced would corrupt every property behind it.
public class DeferredAddressTests : BaseTest
{
    private static readonly MethodInfo CompileMethod = typeof(Mapper).Assembly
        .GetType("Gamehook.Infrastructure.DeferredAddress")!
        .GetMethod("Compile", BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo TryResolveMethod = typeof(Mapper).Assembly
        .GetType("Gamehook.Infrastructure.DeferredAddress")!
        .GetMethod("TryResolve", BindingFlags.Public | BindingFlags.Instance)!;

    private static (object Compiled, string[] Tokens) Compile(
        string expression,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var tokens = new List<string>();
        var intern = (Func<string, int>)(name =>
        {
            var index = tokens.IndexOf(name);
            if (index >= 0) return index;
            tokens.Add(name);
            return tokens.Count - 1;
        });
        var compiled = CompileMethod.Invoke(null,
            [expression, variables ?? new Dictionary<string, string>(StringComparer.Ordinal), intern])!;
        return (compiled, tokens.ToArray());
    }

    private static ulong? Resolve(object compiled, ulong?[] tokenValues)
    {
        object?[] args = [tokenValues, null];
        return (bool)TryResolveMethod.Invoke(compiled, args)! ? (ulong)args[1]! : null;
    }

    [TestCase("{{v}}", 1000UL, 1000UL)]
    [TestCase("{{v}} + 4", 1000UL, 1004UL)]
    [TestCase("{{v}} + 0x20", 1000UL, 1032UL)]
    [TestCase("(({{v}} + 8)) + 4", 1000UL, 1012UL)]
    [TestCase("{{v}} - 40", 1000UL, 960UL)]
    [TestCase("{{v}} * 2 + 6", 1000UL, 2006UL)]
    [TestCase("2 * ({{v}} + 3)", 1000UL, 2006UL)]
    [TestCase("{{v}} + {{v}}", 1000UL, 2000UL)]
    [TestCase("{{v}} * ({{v}} - 1) * ({{v}} - 2)", 4UL, 24UL)]
    [TestCase("{{v}} % 3", 4UL, 1UL)]
    public void Resolves_the_same_value_the_expression_evaluates_to(string expression, ulong token, ulong expected)
    {
        var (compiled, tokens) = Compile(expression);
        var values = Enumerable.Repeat((ulong?)token, tokens.Length).ToArray();

        Assert.That(Resolve(compiled, values), Is.EqualTo(expected));
    }

    [Test]
    public void Compile_time_variables_are_folded_away_before_the_runtime_token()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["slot"] = "3", ["size"] = "100" };
        var (compiled, tokens) = Compile("{{base}} + {slot} * {size}", variables);

        Assert.That(tokens, Is.EqualTo(new[] { "base" }));
        Assert.That(Resolve(compiled, [0x2000000UL]), Is.EqualTo(0x2000000UL + 300));
    }

    [Test]
    public void An_unset_runtime_token_resolves_to_no_address()
    {
        var (compiled, _) = Compile("{{dma_a}} + 4");

        Assert.That(Resolve(compiled, [null]), Is.Null);
    }

    [Test]
    public void A_negative_result_resolves_to_no_address()
    {
        var (compiled, _) = Compile("{{v}} - 100");

        Assert.That(Resolve(compiled, [10UL]), Is.Null);
    }

    [Test]
    public void Overflow_resolves_to_no_address()
    {
        var (compiled, _) = Compile("{{v}} * 2 + 6");
        Assert.That(Resolve(compiled, [ulong.MaxValue]), Is.Null);
    }

    // The real proof: every deferred address the shipped mappers declare, compiled and then checked
    // against a straight NCalc evaluation of the same expression at several token values. This is
    // what catches an over-eager affine reduction on an expression that isn't actually affine.
    [TestCaseSource(nameof(DeferredAddressesInShippedMappers))]
    public void Matches_direct_evaluation_for_every_shipped_deferred_address(
        string expression,
        Dictionary<string, string> variables)
    {
        var (compiled, tokens) = Compile(expression, variables);
        var expanded = ExpandVariables(expression, variables);

        foreach (var tokenValue in new ulong[] { 0x2000000, 0x2024284, 0x3005008, 0x203C000, 1, 12345 })
        {
            var values = Enumerable.Repeat((ulong?)tokenValue, tokens.Length).ToArray();
            var substituted = tokens.Aggregate(expanded,
                (current, token) => current.Replace($"{{{{{token}}}}}", $"({tokenValue})", StringComparison.Ordinal));
            var expected = Convert.ToDouble(new Expression(substituted, CultureInfo.InvariantCulture).Evaluate(),
                CultureInfo.InvariantCulture);

            // A small probe value can push an offset-subtracting address below zero; that is not an
            // address, and both paths have to agree that it isn't one.
            Assert.That(Resolve(compiled, values),
                expected < 0 || expected != Math.Floor(expected) ? Is.Null : Is.EqualTo((ulong)expected),
                $"'{expression}' with token {tokenValue}");
        }
    }

    private static string ExpandVariables(string expression, IReadOnlyDictionary<string, string> variables)
    {
        var method = typeof(Mapper).Assembly.GetType("Gamehook.Infrastructure.AddressExpression")!
            .GetMethod("ExpandVariables", BindingFlags.Public | BindingFlags.Static)!;
        return (string)method.Invoke(null, [expression, variables])!;
    }

    // Deferred addresses are declared both directly and through macro var: attributes, so this
    // walks the same var-merging the compiler does rather than only reading address= attributes.
    private static IEnumerable<TestCaseData> DeferredAddressesInShippedMappers()
    {
        XNamespace varNamespace = "https://schemas.pokeabyte.io/attributes/var";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in AllMapperFiles)
        {
            var root = XDocument.Load(path).Root!;
            var macros = root.Element("macros")?.Elements()
                .ToDictionary(x => x.Name.LocalName, StringComparer.Ordinal)
                ?? new Dictionary<string, XElement>(StringComparer.Ordinal);

            foreach (var (expression, variables) in Walk(
                         root.Element("properties")?.Elements() ?? [],
                         new Dictionary<string, string>(StringComparer.Ordinal), macros, varNamespace))
            {
                if (!expression.Contains("{{", StringComparison.Ordinal)) continue;
                var key = expression + "|" + string.Join(',', variables.OrderBy(v => v.Key, StringComparer.Ordinal)
                    .Select(v => $"{v.Key}={v.Value}"));
                if (!seen.Add(key)) continue;
                yield return new TestCaseData(expression, variables)
                    .SetArgDisplayNames($"{Path.GetFileNameWithoutExtension(path)}: {expression}");
            }
        }
    }

    private static IEnumerable<(string Expression, Dictionary<string, string> Variables)> Walk(
        IEnumerable<XElement> elements,
        Dictionary<string, string> variables,
        IReadOnlyDictionary<string, XElement> macros,
        XNamespace varNamespace)
    {
        foreach (var element in elements)
        {
            if (element.Name.LocalName == "property")
            {
                if ((string?)element.Attribute("address") is { } address) yield return (address, variables);
                continue;
            }
            if (element.Name.LocalName == "macro")
            {
                var type = (string?)element.Attribute("type");
                if (type is null || !macros.TryGetValue(type, out var definition)) continue;
                var merged = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == varNamespace))
                    merged[attribute.Name.LocalName] = attribute.Value;
                foreach (var result in Walk(definition.Elements(), merged, macros, varNamespace)) yield return result;
                continue;
            }
            foreach (var result in Walk(element.Elements(), variables, macros, varNamespace)) yield return result;
        }
    }
}
