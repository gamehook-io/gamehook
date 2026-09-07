using System.Globalization;
using System.Text.RegularExpressions;
using Gamehook.Domain.Property;
using NCalc;

namespace Gamehook.Infrastructure;

internal sealed class ConditionExpression
{
    // Preserve literals and already bracketed parameters; quote only bare dotted mapper paths.
    private static readonly Regex Paths = new(
        "'([^'\\\\]|\\\\.)*'|\"([^\"\\\\]|\\\\.)*\"|\\[[^\\]]+\\]|(?<path>[A-Za-z_][A-Za-z_0-9]*(?:\\.[A-Za-z_0-9]+)+)",
        RegexOptions.Compiled);
    private readonly string source;
    private readonly Expression expression;
    private readonly List<string> parameters;

    public ConditionExpression(string source)
    {
        this.source = source;
        expression = new Expression(Paths.Replace(source, m => m.Groups["path"].Success ? $"[{m.Value}]" : m.Value),
            CultureInfo.InvariantCulture)
        {
            Options = ExpressionOptions.AllowNullParameter | ExpressionOptions.OrdinalStringComparer
        };
        if (expression.HasErrors()) throw new InvalidDataException($"Condition expression '{source}': {expression.Error?.Message}", expression.Error);
        parameters = expression.GetParameterNames().Where(p => p != "null").ToList();
    }

    public void Validate(IReadOnlyDictionary<string, Property> properties)
    {
        foreach (var path in parameters)
            if (!properties.ContainsKey(path)) throw new InvalidDataException($"Condition expression '{source}': unknown property '{path}'.");
    }

    public bool Evaluate(IReadOnlyDictionary<string, Property> properties)
    {
        foreach (var path in parameters) expression.Parameters[path] = properties[path].Value;
        try
        {
            return expression.Evaluate() is bool result ? result
                : throw new InvalidDataException("Expected a boolean result.");
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Condition expression '{source}' failed: {ex.Message}", ex);
        }
    }
}
