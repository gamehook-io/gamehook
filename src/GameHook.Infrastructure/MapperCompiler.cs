using System.Globalization;
using System.Xml.Linq;
using GameHook.Domain;
using GameHook.Domain.Interface;
using GameHook.Domain.Property;
using Jint;

namespace GameHook.Infrastructure;

// Everything Mapper needs to run reads, derived once from a mapper's <mapper> XML root.
internal sealed record MapperCompilationResult(
    IReadOnlyDictionary<string, ReferenceTable> References,
    IReadOnlyList<IDriver.MemorySegmentRequest> Requests,
    IReadOnlyList<Property> CompiledProperties,
    IReadOnlyList<XmlCondition> Conditions,
    IReadOnlyList<(Property Property, DeferredAddress Address)> DynamicAddressProperties,
    // Distinct script-set variable names every DeferredAddress indexes into, so a read resolves
    // each one once instead of once per property that mentions it.
    IReadOnlyList<string> RuntimeTokenNames,
    IReadOnlyList<(Property Property, CompiledExpression Expression)> ExpressionBindings);

// Turns a mapper's XML into compiled properties, driver requests, reference tables, condition
// chains, and the two script-dependent bindings (dynamic addresses, after-read-value-expression).
// Kept separate from Mapper so "how does XML become properties" and "how does a read cycle work"
// can each be read on their own - this class never touches a driver or runs a read.
internal sealed class MapperCompiler
{
    private static readonly XNamespace VarNamespace = "https://schemas.pokeabyte.io/attributes/var";

    // RetroArch replies with text hex bytes. Reading a sparse 8KB span to obtain two bytes at
    // opposite ends wastes network time and parser work, so only bridge small holes. The driver
    // already sends independent requests concurrently.
    private const ulong MaximumMergedGap = 64;

    private readonly GameSystem system;
    private readonly ExpressionEngine scriptEngine;
    private readonly IReadOnlyDictionary<string, XElement> macros;
    private readonly List<Property> compiledProperties = [];
    private readonly List<XmlCondition> conditions = [];
    private readonly List<(Property Property, DeferredAddress Address)> dynamicAddressProperties = [];
    private readonly List<(Property Property, CompiledExpression Expression)> expressionBindings = [];
    private readonly List<string> runtimeTokenNames = [];

    private MapperCompiler(GameSystem system, ExpressionEngine scriptEngine, IReadOnlyDictionary<string, XElement> macros)
    {
        this.system = system;
        this.scriptEngine = scriptEngine;
        this.macros = macros;
    }

    public static MapperCompilationResult Compile(XElement root, GameSystem system, ExpressionEngine scriptEngine)
    {
        var propertiesElement = root.Element("properties") ?? throw new InvalidDataException("Mapper has no properties element.");
        var compiler = new MapperCompiler(system, scriptEngine, Index(root.Element("macros")));
        var references = ReadReferences(root.Element("references"));
        var memoryBlockRequests = compiler.ReadMemoryBlocks(root.Element("memory"));

        compiler.CompileProperties(propertiesElement.Elements(), new Dictionary<string, string>(StringComparer.Ordinal), null);

        var requests = MergeRequests(BuildRequests(compiler.compiledProperties).Concat(memoryBlockRequests));

        return new MapperCompilationResult(
            references,
            requests,
            compiler.compiledProperties,
            compiler.conditions,
            compiler.dynamicAddressProperties,
            compiler.runtimeTokenNames,
            compiler.expressionBindings);
    }

    private int InternRuntimeToken(string name)
    {
        var index = runtimeTokenNames.IndexOf(name);
        if (index >= 0) return index;
        index = runtimeTokenNames.Count;
        runtimeTokenNames.Add(name);
        return index;
    }

    private void CompileProperties(IEnumerable<XElement> elements, IReadOnlyDictionary<string, string> variables, string? path, int depth = 0)
    {
        if (depth > 128) throw new InvalidDataException("Mapper nesting or macro recursion exceeds 128 levels.");
        var siblings = elements.ToArray();
        for (var index = 0; index < siblings.Length; index++)
        {
            var element = siblings[index];
            var elementName = element.Name.LocalName;
            if (elementName == "if")
            {
                var condition = XmlCondition.Compile(siblings, ref index, path);
                conditions.Add(condition);
                compiledProperties.AddRange(condition.Targets);
                continue;
            }
            if (elementName is "elif" or "else")
                throw new InvalidDataException($"Orphan <{elementName}>: expected an adjacent <if> branch.");
            if (elementName == "property")
            {
                compiledProperties.Add(CreateProperty(element, variables, path));
                continue;
            }

            if (elementName == "macro")
            {
                var type = (string?)element.Attribute("type") ?? throw new InvalidDataException("Macro has no type.");
                if (!macros.TryGetValue(type, out var definition))
                {
                    throw new InvalidDataException($"Macro '{type}' is not defined.");
                }
                // An optional name namespaces the macro's properties under it (e.g. six
                // party_pokemon instances need party.0.*, party.1.*, ... instead of colliding
                // on the same names) - omit it when the macro is only ever used once in scope.
                var name = (string?)element.Attribute("name");
                var childPath = name is null ? path : CombinePath(path, name);
                CompileProperties(definition.Elements(), MergeVariables(variables, element), childPath, depth + 1);
                continue;
            }

            CompileProperties(element.Elements(), variables, CombinePath(path, elementName), depth + 1);
        }
    }

    private Property CreateProperty(XElement element, IReadOnlyDictionary<string, string> variables, string? path)
    {
        var name = (string?)element.Attribute("name") ?? throw new InvalidDataException("Property has no name.");
        var addressAttribute = (string?)element.Attribute("address");
        var type = (string?)element.Attribute("type") ?? "int";
        var memoryContainer = ResolveTemplate((string?)element.Attribute("memoryContainer"), variables);

        ulong? address = null;
        DeferredAddress? deferredAddress = null;
        if (addressAttribute is not null)
        {
            try
            {
                if (AddressExpression.Resolve(addressAttribute, variables, out var resolved) == AddressExpression.Resolution.Deferred)
                {
                    // {{token}}: depends on a script-set runtime variable (e.g. "{{dma_a}}"), so it
                    // can only take a value once the preprocessor has run. Compiled here down to a
                    // token slot plus arithmetic; Mapper.ResolveDynamicAddresses just applies it.
                    deferredAddress = DeferredAddress.Compile(addressAttribute, variables, InternRuntimeToken);
                }
                else
                {
                    address = resolved;
                }
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Property '{CombinePath(path, name)}' has an unresolved address token in '{addressAttribute}'. " +
                    "Use {{token}} instead of {token} if it's meant to be resolved at read time from a runtime variable.",
                    ex);
            }
        }

        var config = new PropertyConfig(
            CombinePath(path, name),
            type,
            address,
            GetLength(element),
            (string?)element.Attribute("bits"),
            (string?)element.Attribute("reference"),
            (string?)element.Attribute("value"),
            (string?)element.Attribute("characterMap"),
            memoryContainer,
            (string?)element.Attribute("description"));

        var property = Property.Create(config, system.IntegerEndianness, system);

        if (deferredAddress is not null)
        {
            dynamicAddressProperties.Add((property, deferredAddress));
        }

        if ((string?)element.Attribute("after-read-value-expression") is { } expression)
        {
            try
            {
                expressionBindings.Add((property, scriptEngine.Compile(expression)));
            }
            catch (JintException ex)
            {
                throw new InvalidDataException($"Property '{property.Name}' has an invalid after-read-value-expression '{expression}': {ex.Message}", ex);
            }
        }

        return property;
    }

    private static string? ResolveTemplate(string? raw, IReadOnlyDictionary<string, string> variables)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}')
        {
            return variables.TryGetValue(trimmed[1..^1], out var resolved) ? resolved : null;
        }
        return trimmed;
    }

    private static string CombinePath(string? path, string name) => string.IsNullOrEmpty(path) ? name : $"{path}.{name}";

    private static IReadOnlyDictionary<string, XElement> Index(XElement? parent) => parent is null
        ? new Dictionary<string, XElement>(StringComparer.Ordinal)
        : parent.Elements().ToDictionary(x => x.Name.LocalName, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, ReferenceTable> ReadReferences(XElement? parent) => parent is null
        ? new Dictionary<string, ReferenceTable>(StringComparer.Ordinal)
        : parent.Elements().ToDictionary(
            x => x.Name.LocalName,
            x => new ReferenceTable((string?)x.Attribute("type") == "number", ReadEntries(x)),
            StringComparer.Ordinal);

    // A handful of mapper reference tables (e.g. pokemon_emerald.xml's battle_action) declare the
    // same key twice with different values - a pre-existing authoring issue, unrelated to scripting.
    // ToDictionary throws on that; last-entry-wins is the least surprising way to tolerate it.
    private static IReadOnlyDictionary<ulong, string> ReadEntries(XElement table)
    {
        var entries = new Dictionary<ulong, string>(EqualityComparer<ulong>.Default);
        foreach (var entry in table.Elements("entry"))
        {
            if (entry.Attribute("value") is not { } value) continue;
            entries[ParseNumber((string)entry.Attribute("key")!)] = (string)value;
        }
        return entries;
    }

    private static IReadOnlyDictionary<string, string> MergeVariables(IReadOnlyDictionary<string, string> inherited, XElement element)
    {
        var result = new Dictionary<string, string>(inherited, StringComparer.Ordinal);
        foreach (var attribute in element.Attributes().Where(x => x.Name.Namespace == VarNamespace))
        {
            result[attribute.Name.LocalName] = attribute.Value;
        }
        return result;
    }

    public static ulong ParseNumber(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : ulong.Parse(value, CultureInfo.InvariantCulture);

    private static int GetLength(XElement property) => (int?)property.Attribute("length") ?? 1;

    // <mapper><memory><read start="0x.." end="0x.."/></memory></mapper>: full ranges a mapper's
    // script needs to see (via memory.defaultNamespace.get_*) regardless of which properties are
    // declared. Folded into the same combined request set properties use, so script reads and
    // property decode share one driver round-trip and one snapshot per read.
    private IReadOnlyList<IDriver.MemorySegmentRequest> ReadMemoryBlocks(XElement? parent)
    {
        if (parent is null) return [];
        return parent.Elements("read").Select(e =>
        {
            var start = ParseNumber((string?)e.Attribute("start") ?? throw new InvalidDataException("Memory read has no start."));
            var end = ParseNumber((string?)e.Attribute("end") ?? throw new InvalidDataException("Memory read has no end."));
            var length = checked((int)(end - start + 1));
            return new IDriver.MemorySegmentRequest(MemoryRegion.ToRegion(start, system), MemoryRegion.ToOffset(start, system), length);
        }).ToArray();
    }

    private static IReadOnlyList<IDriver.MemorySegmentRequest> BuildRequests(IEnumerable<IProperty> properties) =>
        MergeRequests(properties
            .Select(x => x.BuildRequest())
            .Where(x => x is not null)
            .Select(x => x!));

    private static IReadOnlyList<IDriver.MemorySegmentRequest> MergeRequests(IEnumerable<IDriver.MemorySegmentRequest> segments)
    {
        return segments
            .GroupBy(x => x.RegionId, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var ranges = group.OrderBy(x => x.StartingAddress).ToArray();
                var requests = new List<IDriver.MemorySegmentRequest>();
                var start = ranges[0].StartingAddress;
                var end = checked(start + (ulong)ranges[0].Length);
                foreach (var range in ranges.Skip(1))
                {
                    var rangeEnd = checked(range.StartingAddress + (ulong)range.Length);
                    if (range.StartingAddress <= end + MaximumMergedGap)
                    {
                        end = Math.Max(end, rangeEnd);
                        continue;
                    }

                    requests.Add(new IDriver.MemorySegmentRequest(group.Key, start, checked((int)(end - start))));
                    start = range.StartingAddress;
                    end = rangeEnd;
                }

                requests.Add(new IDriver.MemorySegmentRequest(group.Key, start, checked((int)(end - start))));
                return requests;
            })
            .ToArray();
    }
}
