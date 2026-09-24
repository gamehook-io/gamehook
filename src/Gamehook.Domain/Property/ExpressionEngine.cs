using System.Text.RegularExpressions;
using Jint;

namespace Gamehook.Domain.Property;

// Compiles numeric expressions and owns the lazily initialized JavaScript engine.
public sealed class ExpressionEngine
{
    private static readonly Regex TopLevelExport = new(@"(?m)^(\s*)export\s+", RegexOptions.Compiled);
    private static readonly Regex TopLevelImport = new(@"(?m)^\s*import\s+.*;\s*$", RegexOptions.Compiled);
    private Engine? engine;
    private readonly Dictionary<string, Func<double, double>> compiled = new(StringComparer.Ordinal);
    private int nextExpressionId;

    public Engine Engine => engine ??= new Engine(options => options
        .LimitMemory(64 * 1024 * 1024)
        .MaxStatements(1_000_000)
        .TimeoutInterval(TimeSpan.FromSeconds(2))
        .LimitRecursion(128));
    public bool IsScriptInitialized => engine is not null;

    // Mapper modules run as scripts: strip exports/imports; helpers still need host bindings.
    public void LoadScript(string source) =>
        Engine.Execute(TopLevelExport.Replace(TopLevelImport.Replace(source, ""), "$1"));

    // Share identical expressions across properties. Unsupported grammar is compiled once as
    // JavaScript; native expressions never initialize or invoke the scripting engine. The backend
    // is bound into the returned delegate here so read time never has to look it up again - the
    // read loop applies hundreds of these per frame (one large mapper binds 429).
    public Func<double, double> Compile(string expression)
    {
        if (expression.Length > 4096) throw new InvalidDataException("Property expression exceeds 4096 characters.");
        if (compiled.TryGetValue(expression, out var existing)) return existing;
        if (!InlineScriptingProvider.TryCompile(expression, out var evaluate))
        {
            var name = $"__expr_{nextExpressionId++}";
            Engine.Execute($"function {name}(x) {{ return ({expression}); }}");
            evaluate = x => Engine.Invoke(name, x).AsNumber();
        }
        compiled.Add(expression, evaluate);
        return evaluate;
    }
}
