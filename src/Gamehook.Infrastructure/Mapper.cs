using System.Globalization;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Net.Sockets;
using System.Xml.Linq;
using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Property;
using Jint;
using Jint.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gamehook.Infrastructure;

public class Mapper : IMapper, IDisposable
{
    private static readonly (string Type, string Label)[] InspectionTypes =
    [
        ("string", "String"),
        ("int", "Signed integer"),
        ("uint", "Unsigned integer"),
        ("bool", "Boolean"),
        ("bitArray", "Bit array"),
        ("binaryCodedDecimal", "Binary-coded decimal"),
    ];
    private static readonly IReadOnlyList<IDriver.MemorySegmentSnapshot> EmptyMemorySegments = [];
    private static readonly IReadOnlyDictionary<string, ReferenceTable> EmptyReferences = new Dictionary<string, ReferenceTable>(StringComparer.Ordinal);
    private readonly IDriver driver;
    public IDriver MemoryDriver => driver;
    private bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        (driver as IDisposable)?.Dispose();
    }
    private readonly GameSystem system;
    private readonly IReadOnlyDictionary<string, ReferenceTable> references;

    // Everything script-related lives here at the mapper level - Property/NumberProperty never
    // reference the engine at all. Mapper reads each property's decoded value, runs it through
    // script where applicable, and writes results back via Property's public mutation methods
    // (SetValueOverride/SetAddress/...).
    private readonly ExpressionEngine scriptEngine = new();
    private readonly ScriptMemoryAccess memoryAccess;
    private readonly bool hasMapperScript;
    private readonly Dictionary<string, byte[]> containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReadOnlyMemory<byte>> containerSnapshot = new(StringComparer.Ordinal);

    private readonly (Property Property, CompiledExpression Expression)[] expressionBindings;
    private readonly (Property Property, DeferredAddress Address)[] dynamicAddressProperties;

    // Distinct script-set variables the deferred addresses read, resolved once per read into
    // runtimeTokenValues rather than once per property - pokemon_emerald's 1500 deferred addresses
    // between them reference exactly two.
    private readonly string[] runtimeTokenNames;
    private readonly ulong?[] runtimeTokenValues;
    private readonly IReadOnlyList<XmlCondition> conditions;
    private TimeSpan lastPropertyTranslation;
    private TimeSpan lastInlineCalculations;
    private TimeSpan lastPostprocessor;

    private readonly Property[] compiledProperties;
    private readonly Dictionary<string, Property> propertiesByPath;
    private readonly IReadOnlyList<IDriver.MemorySegmentRequest> requests;

    /// Whether this mapper asks the driver for any bytes at all. A static-only mapper is still
    /// valid with an empty response; one that wants memory and gets none has lost the game.
    private readonly bool requestsMemory;
    private readonly ILogger<Mapper> logger;
    private IDriver.Response? lastResponse;
    private int consecutiveReadFailures;

    // Once a connection has produced at least one good read, a single dropped read afterward
    // is treated as a network blip rather than a fatal error - only a run of these actually
    // surfaces as a warning, so a one-off timeout doesn't flash an alarming banner at the user.
    private const int ConnectionWarningThreshold = 3;

    public string MapperPath { get; }
    public GameSystem System => system;
    public string MapperFileName => Path.GetFileName(MapperPath);
    public string GameName { get; }
    public Dictionary<string, IProperty> Properties { get; } = new(StringComparer.Ordinal);
    public ReadMetrics LastReadMetrics { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    public string? ConnectionWarning { get; private set; }
    public int ConsecutiveReadFailures => consecutiveReadFailures;
    public string? LastReadFailureMessage { get; private set; }
    public bool HasConnectionRefusal { get; private set; }
    public IReadOnlyList<IDriver.MemorySegmentSnapshot> LastMemorySegments => lastResponse?.Segments ?? [];

    public IReadOnlyList<PropertyInspection> Inspect(ReadOnlyMemory<byte> bytes) =>
        InspectionTypes
            .Select(entry => CreateInspectionProperty(entry.Type, entry.Label))
            .Select(property => property.TryDecode(bytes, references, out var value, out var error)
                ? new PropertyInspection(property.Name, property.Type, NormalizeInspectionValue(property.Type, value), null)
                : new PropertyInspection(property.Name, property.Type, null, error))
            .ToArray();

    private static object? NormalizeInspectionValue(string type, object? value) =>
        type == "uint" && value is int signedValue
            ? unchecked((uint)signedValue)
            : value;

    private IProperty CreateInspectionProperty(string type, string label)
    {
        var config = new PropertyConfig(label, type, null, 0, null, null, null);
        try
        {
            return Property.Create(config, system.IntegerEndianness, system);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException($"Unsupported inspection property type '{type}'.", ex);
        }
    }

    public Mapper(string mapperPath, IDriver driver, ILogger<Mapper>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapperPath);
        ArgumentNullException.ThrowIfNull(driver);
        this.logger = logger ?? NullLogger<Mapper>.Instance;

        MapperPath = Path.GetFullPath(mapperPath);
        var document = XDocument.Load(MapperPath, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Mapper has no root element.");
        if (root.Name.LocalName != "mapper")
        {
            throw new InvalidDataException("Mapper root element must be 'mapper'.");
        }

        system = GameSystem.All.SingleOrDefault(x => x.Id == (string?)root.Attribute("platform"))
            ?? throw new NotSupportedException($"Unsupported mapper platform '{(string?)root.Attribute("platform")}'.");
        GameName = (string?)root.Attribute("name") ?? Path.GetFileNameWithoutExtension(MapperPath).Replace('_', ' ');
        this.driver = driver;
        memoryAccess = new ScriptMemoryAccess(system);

        var compiled = MapperCompiler.Compile(root, system, scriptEngine);
        references = compiled.References;
        requests = compiled.Requests;
        compiledProperties = compiled.CompiledProperties.ToArray();
        requestsMemory = requests.Any(request => request.Length > 0);
        conditions = compiled.Conditions;
        dynamicAddressProperties = compiled.DynamicAddressProperties.ToArray();
        runtimeTokenNames = compiled.RuntimeTokenNames.ToArray();
        runtimeTokenValues = new ulong?[runtimeTokenNames.Length];
        expressionBindings = compiled.ExpressionBindings.ToArray();

        propertiesByPath = compiledProperties.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
        foreach (var condition in conditions) condition.Validate(propertiesByPath);

        var dynamicAddressPropertySet = new HashSet<Property>(dynamicAddressProperties.Select(x => x.Property));
        foreach (var property in compiledProperties)
        {
            // No address, no container, no static value: the only way this property ever gets a
            // value is a script calling setValue on it later (e.g. gen1's postprocessor computing
            // player.team.N.ivs.hp, or player.active_pokemon.* mirrored fields) - registering it
            // only if it already has one, before any read has ever run, would mean it never shows
            // up at all.
            var isScriptOnly = property.Address is null && property.MemoryContainer is null && property.StaticValue is null;
            var alwaysRegister = isScriptOnly
                || property.MemoryContainer is not null
                || dynamicAddressPropertySet.Contains(property)
                || property.BuildRequest() is not null;
            if (!alwaysRegister)
            {
                // no memory address backs this property (static value or unrecognized type) - resolve it now
                // and only register it if it actually produced a value, matching address-backed properties
                // which likewise only appear in Properties once they have decoded successfully.
                property.Refresh(EmptyMemorySegments, EmptyReferences);
                if (property.Value is null) continue;
            }
            Properties[property.Name] = property;
        }

        var scriptPath = Path.ChangeExtension(MapperPath, ".js");
        hasMapperScript = File.Exists(scriptPath);
        if (!hasMapperScript && dynamicAddressProperties.Length > 0)
        {
            var (property, deferred) = dynamicAddressProperties[0];
            throw new InvalidDataException(
                $"Mapper '{MapperFileName}' requires script '{Path.GetFileName(scriptPath)}' " +
                $"to resolve address '{deferred.Source}' for property '{property.Name}', but the script file is missing. " +
                "Place the matching .js file beside the mapper XML.");
        }
        if (scriptEngine.IsScriptInitialized || hasMapperScript) BindScriptHost();
        if (hasMapperScript)
        {
            try
            {
                scriptEngine.LoadScript(File.ReadAllText(scriptPath));
                ReplaceScriptCopyProperties();
            }
            catch (JintException ex)
            {
                throw new InvalidDataException($"Mapper script '{Path.GetFileName(scriptPath)}' failed to load: {ex.Message}", ex);
            }
        }
    }

    // Wires the __variables/__state/__console/__memory/__mapper globals every mapper script's boilerplate
    // header destructures (e.g. "const mapper = __mapper;"), plus the preprocessor/postprocessor
    // and dynamic-address helper functions Mapper.Refresh calls into. Must run before LoadScript -
    // "const mapper = __mapper" captures whatever __mapper points to at that exact moment, not a
    // live reference, so binding the real objects afterward would leave the mapper script's own local
    // holding the stale placeholder.
    private void BindScriptHost()
    {
        var engine = scriptEngine.Engine;

        // Genuine mutable JS objects (not CLR dictionaries) so arbitrary script assignment
        // (variables.dma_a = 5) behaves exactly like a plain object, and persists for the mapper's
        // whole lifetime - these are never reset between reads.
        engine.SetValue("__variables", engine.Evaluate("({})"));
        engine.SetValue("__state", engine.Evaluate("({})"));

        engine.SetValue("__log_impl", (Action<string>)(message =>
            logger.LogInformation("{Mapper} script: {Message}", MapperFileName, message)));
        engine.Execute("var __console = { log: function () { __log_impl(Array.prototype.slice.call(arguments).map(String).join(' ')); } };");

        engine.SetValue("__memory_namespace", memoryAccess);
        engine.SetValue("__memory_fill", (Action<string, double, object>)FillContainer);
        engine.Execute("var __memory = { defaultNamespace: __memory_namespace, fill: __memory_fill };");

        // No write capability exists (IDriver has no Write method), and nothing in the read path
        // calls it - only an unused write-back party editor export does, in a handful of mappers.
        engine.Execute("var __driver = {};");

        // Plain JS properties retain one handle per property, with no second host dictionary.
        var handles = engine.Evaluate("({})").AsObject();
        foreach (var property in compiledProperties)
            handles.Set(property.Name, JsValue.FromObject(engine, new PropertyHandle(property)), handles);
        engine.SetValue("__mapper_properties", handles);
        engine.SetValue("__copy_properties", (Action<string, string>)CopyProperties);
        // Bound eagerly as plain data properties rather than accessors: the handles never change,
        // so a getter would only add a CLR round-trip to every single property access from script.
        engine.Execute("""
            var __mapper = {
                properties: __mapper_properties,
                get_property_value: function (path) { return __mapper_properties[path].value; },
                set_property_value: function (path, value) { __mapper_properties[path].value = value; },
                get_property: function (path) { return __mapper_properties[path]; },
                set_property: function (path, values) {
                    var target = __mapper_properties[path];
                    if (values.memoryContainer !== undefined) target.memoryContainer = values.memoryContainer;
                    if (values.address !== undefined) target.address = values.address;
                    if (values.length !== undefined) target.length = values.length;
                    if (values.bits !== undefined) target.bits = values.bits;
                    if (values.reference !== undefined) target.reference = values.reference;
                    if (values.value !== undefined) target.value = values.value;
                },
                copy_properties: __copy_properties,
            };
            function __get_variable(name) {
                var v = __variables[name];
                return (typeof v === 'number') ? v : NaN;
            }
            function __run_preprocessor() {
                if (typeof preprocessor !== 'function') return 1;
                var result = preprocessor();
                return (result !== false) ? 1 : 0;
            }
            function __run_postprocessor() {
                if (typeof postprocessor === 'function') postprocessor();
            }
            """);
    }

    // Replace script-declared copy helpers with the host implementation. Const aliases already
    // use the host binding and cannot be reassigned, so the script below leaves them alone.
    private void ReplaceScriptCopyProperties() => scriptEngine.Engine.Execute("""
        (function () {
            try {
                if (typeof copyProperties === 'function' && copyProperties !== __copy_properties) {
                    copyProperties = __copy_properties;
                }
            } catch (error) {
                // A const binding, i.e. already the host implementation.
            }
        })();
        """);

    // Mirrors every property under destinationPath onto the correspondingly-named property under
    // sourcePath - used to make player.active_pokemon alias whichever party slot (or battle
    // structure) is currently active. Bound as mapper.copy_properties; some mapper scripts (e.g.
    // pokemon_emerald.js) assign it directly to a local const with no pure-JS fallback, so this
    // isn't optional the way get/set_property_value's JS-level convenience wrappers are.
    private void CopyProperties(string sourcePath, string destinationPath)
    {
        foreach (var (key, destination) in propertiesByPath)
        {
            if (!key.StartsWith(destinationPath, StringComparison.Ordinal) ||
                !propertiesByPath.TryGetValue(sourcePath + key[destinationPath.Length..], out var source)) continue;

            destination.SetMemoryContainer(source.MemoryContainer);
            destination.SetAddress(source.Address);
            destination.SetLength(source.Length);
            destination.SetBits(source.Bits);
            destination.SetReference(source.Reference);
            destination.SetValueOverride(source.Value);
        }
    }

    private void FillContainer(string name, double offset, object bytesValue)
    {
        var source = bytesValue switch
        {
            byte[] arr => arr,
            object[] arr => arr.Select(o => unchecked((byte)Convert.ToInt64(o, CultureInfo.InvariantCulture))).ToArray(),
            _ => throw new InvalidOperationException("memory.fill expects an array of byte values."),
        };
        var start = checked((int)offset);
        containers.TryGetValue(name, out var existing);
        var required = start + source.Length;
        if (existing is null || existing.Length < required)
        {
            var resized = new byte[required];
            existing?.CopyTo(resized, 0);
            existing = resized;
        }
        source.CopyTo(existing, start);
        containers[name] = existing;
        containerSnapshot[name] = existing;
    }

    public async Task<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var totalTimer = Stopwatch.StartNew();
        IDriver.Response response;
        try
        {
            // Drivers have bounded network waits. Finish the underlying read before releasing
            // session ownership; cancelling only the await would leave a concurrent socket read.
            response = await driver.Read(new IDriver.Request(system, requests)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // A driver can reply successfully while no game is loaded. Treat an empty memory
            // response as unavailable, before decoding can erase the last successful values.
            // Static-only mappers do not request memory and remain valid without any bytes.
            if (requestsMemory && !response.Segments.Any(segment => !segment.Bytes.IsEmpty))
            {
                throw new TimeoutException("Driver returned no memory data. The game may be closed or unavailable.");
            }
        }
        // A drop on the very first read (lastResponse still null) means the connection never
        // worked at all - that stays a hard failure so the caller's "can't connect" handling
        // still fires immediately. Once a read has succeeded before, drops are tolerated here
        // instead, only surfacing once they've repeated past ConnectionWarningThreshold.
        catch (Exception ex) when (lastResponse is not null && ex is TimeoutException or SocketException)
        {
            consecutiveReadFailures++;
            LastReadFailureMessage = ex.Message;
            HasConnectionRefusal = ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused };
            ConnectionWarning = consecutiveReadFailures >= ConnectionWarningThreshold
                ? $"Driver failed the last {consecutiveReadFailures} read(s) in a row: {ex.Message}"
                : null;
            this.logger.LogWarning(ex,
                "Read mapper {Mapper} dropped ({ConsecutiveFailures} consecutive failed read(s)).",
                MapperFileName, consecutiveReadFailures);
            return false;
        }

        consecutiveReadFailures = 0;
        LastReadFailureMessage = null;
        HasConnectionRefusal = false;
        ConnectionWarning = null;

        var driverElapsed = totalTimer.Elapsed;
        if (lastResponse is null || !ResponsesEqual(lastResponse, response))
        {
            Refresh(response);
            lastResponse = response;
        }
        LastReadMetrics = new ReadMetrics(driverElapsed, lastPropertyTranslation, lastInlineCalculations, lastPostprocessor, totalTimer.Elapsed);
        this.logger.LogDebug(
            "Read mapper {Mapper} in {TotalMilliseconds:0.###} ms; driver {DriverMilliseconds:0.###} ms, translation {TranslationMilliseconds:0.###} ms, inline {InlineMilliseconds:0.###} ms, postprocessor {PostprocessorMilliseconds:0.###} ms",
            MapperFileName,
            LastReadMetrics.Total.TotalMilliseconds,
            LastReadMetrics.Driver.TotalMilliseconds,
            LastReadMetrics.PropertyTranslation.TotalMilliseconds,
            LastReadMetrics.InlineCalculations.TotalMilliseconds,
            LastReadMetrics.Postprocessor.TotalMilliseconds);
        return true;
    }

    private static bool ResponsesEqual(IDriver.Response left, IDriver.Response right) =>
        left.Segments.Count == right.Segments.Count &&
        left.Segments.Zip(right.Segments).All(pair =>
            pair.First.RegionId == pair.Second.RegionId &&
            pair.First.StartingAddress == pair.Second.StartingAddress &&
            pair.First.Bytes.Span.SequenceEqual(pair.Second.Bytes.Span));

    public async IAsyncEnumerable<bool> ReadContinuouslyAsync(
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            yield return await ReadAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refresh(IDriver.Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        memoryAccess.UpdateSnapshot(response.Segments);

        // Guard the stopwatch on hasMapperScript so a mapper with no script at all reports an
        // exact TimeSpan.Zero rather than stray timer noise.
        lastPostprocessor = TimeSpan.Zero;
        var preprocessorContinues = true;
        if (hasMapperScript)
        {
            var preprocessorTimer = Stopwatch.StartNew();
            preprocessorContinues = RunPreprocessor();
            lastPostprocessor = preprocessorTimer.Elapsed;
        }
        if (!preprocessorContinues)
        {
            lastPropertyTranslation = TimeSpan.Zero;
            lastInlineCalculations = TimeSpan.Zero;
            return;
        }

        var translationTimer = Stopwatch.StartNew();
        ResolveDynamicAddresses();
        var segments = response.Segments;
        foreach (var property in compiledProperties) property.Refresh(segments, references, containerSnapshot);
        lastPropertyTranslation = translationTimer.Elapsed;

        var inlineTimer = Stopwatch.StartNew();
        ApplyExpressions();

        // Clear all derived outputs first, then run chains in XML order. The companion script
        // sees the current snapshot's derived values, including dependencies between chains.
        foreach (var condition in conditions)
            foreach (var target in condition.Targets) target.SetValueOverride(null);
        foreach (var condition in conditions) condition.Evaluate(propertiesByPath);
        lastInlineCalculations = inlineTimer.Elapsed;

        if (hasMapperScript)
        {
            var postprocessorTimer = Stopwatch.StartNew();
            RunPostprocessor();
            lastPostprocessor += postprocessorTimer.Elapsed;
        }
    }

    private void ApplyExpressions()
    {
        foreach (var (property, expression) in expressionBindings)
        {
            try
            {
                PropertyExpressions.Apply(property, expression);
            }
            catch (JintException ex)
            {
                throw new InvalidDataException($"Property '{property.Name}' after-read-value-expression failed: {ex.Message}", ex);
            }
        }
    }

    // Only called when hasMapperScript is true (see Refresh); no no-op guard needed here.
    private bool RunPreprocessor()
    {
        try
        {
            return scriptEngine.Engine.Invoke("__run_preprocessor").AsNumber() != 0;
        }
        catch (JintException ex)
        {
            throw new InvalidDataException($"Mapper '{MapperFileName}' preprocessor failed: {ex.Message}", ex);
        }
    }

    private void RunPostprocessor()
    {
        try
        {
            scriptEngine.Engine.Invoke("__run_postprocessor");
        }
        catch (JintException ex)
        {
            throw new InvalidDataException($"Mapper '{MapperFileName}' postprocessor failed: {ex.Message}", ex);
        }
    }

    // Re-resolves every address that referenced a token no compile-time class/macro variable
    // defines (e.g. "{{dma_a}}") against the mapper's live script variables, now that this read's
    // preprocessor has had a chance to set them. A property whose address still can't be resolved
    // (game not booted yet, DMA relocating) reports a null value rather than a stale one.
    //
    // The expressions themselves were reduced to arithmetic at compile time, and each distinct
    // script variable is fetched once here, so this whole pass is two engine calls plus one
    // multiply-add per property - not a regex, parse, and evaluate per property per frame.
    private void ResolveDynamicAddresses()
    {
        if (dynamicAddressProperties.Length == 0) return;

        for (var index = 0; index < runtimeTokenNames.Length; index++)
        {
            runtimeTokenValues[index] = TryGetRuntimeVariableOrNull(runtimeTokenNames[index]);
        }

        foreach (var (property, deferred) in dynamicAddressProperties)
        {
            property.SetAddress(deferred.TryResolve(runtimeTokenValues, out var address) ? address : null);
        }
    }

    private ulong? TryGetRuntimeVariableOrNull(string name)
    {
        if (!scriptEngine.IsScriptInitialized) return null;
        var result = scriptEngine.Engine.Invoke("__get_variable", name).AsNumber();
        return double.IsNaN(result) ? null : (ulong)result;
    }
}
