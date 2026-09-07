using System.Text.RegularExpressions;
using Jint;

namespace GameHook.Domain.Property;

// Compiles numeric expressions and owns the lazily initialized JavaScript engine.
public sealed class ExpressionEngine
{
    private static readonly Regex TopLevelExport = new(@"(?m)^(\s*)export\s+", RegexOptions.Compiled);
    private static readonly Regex TopLevelImport = new(@"(?m)^\s*import\s+.*;\s*$", RegexOptions.Compiled);
    private Engine? engine;
    private readonly Dictionary<string, CompiledExpression> compiled = new(StringComparer.Ordinal);
    private int nextExpressionId;

    public Engine Engine => engine ??= new Engine(options => options
        .LimitMemory(64 * 1024 * 1024)
        .MaxStatements(1_000_000)
        .TimeoutInterval(TimeSpan.FromSeconds(2))
        .LimitRecursion(128));
    public bool IsScriptInitialized => engine is not null;

    /// How many times an expression has been evaluated through JavaScript. Stays zero for a mapper
    /// whose expressions the inline provider handles natively.
    public long ScriptInvocations { get; private set; }

    // Mapper modules run as scripts: strip exports/imports; helpers still need host bindings.
    public void LoadScript(string source) =>
        Engine.Execute(TopLevelExport.Replace(TopLevelImport.Replace(source, ""), "$1"));

    // Share identical expressions across properties. Unsupported grammar is compiled once as
    // JavaScript; native expressions never initialize or invoke the scripting engine. The backend
    // is bound into the returned delegate here so read time never has to look it up again.
    public CompiledExpression Compile(string expression)
    {
        if (expression.Length > 4096) throw new InvalidDataException("Property expression exceeds 4096 characters.");
        if (compiled.TryGetValue(expression, out var existing)) return existing;
        CompiledExpression result;
        if (InlineScriptingProvider.TryCompile(expression, out var evaluate))
        {
            result = new CompiledExpression(expression, isScripted: false, evaluate);
        }
        else
        {
            var name = $"__expr_{nextExpressionId++}";
            Engine.Execute($"function {name}(x) {{ return ({expression}); }}");
            result = new CompiledExpression(expression, isScripted: true, x =>
            {
                ScriptInvocations++;
                return Engine.Invoke(name, x).AsNumber();
            });
        }
        compiled.Add(expression, result);
        return result;
    }

    public double Invoke(CompiledExpression expression, double x) => expression.Invoke(x);

    public void ResetInvocations() => ScriptInvocations = 0;
}
