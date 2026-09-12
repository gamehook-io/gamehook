using System.Globalization;
using System.Text.RegularExpressions;
using NCalc;
using NCalc.Exceptions;

namespace Gamehook.Infrastructure;

// Shared grammar for mapper address expressions. "{name}" is a compile-time macro/class variable:
// resolved once, substituted textually, and recursed into (a variable's own value can reference
// further variables, e.g. var:moveAddress="{address} + 8"). "{{name}}" is a script-set runtime
// variable (e.g. "{{dma_a}}") whose value only exists once the mapper's preprocessor has run.
//
// Everything here runs at compile time only. A "{name}" address collapses to a plain ulong, and a
// "{{name}}" address collapses to a DeferredAddress - by the time a read happens, no regex is run,
// no expression is parsed, and in the overwhelmingly common "{{token}} + constant" case no NCalc
// evaluation happens either.
internal static class AddressExpression
{
    private static readonly Regex DeferredToken = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    // (?<!\{)/(?!\}) keep this from matching the inner braces of a still-untouched {{name}} -
    // that token is exclusively DeferredToken's to expand, never treated as a compile-time {name}.
    private static readonly Regex VariableToken = new(@"(?<!\{)\{(\w+)\}(?!\})", RegexOptions.Compiled);

    public enum Resolution { Static, Deferred }

    // Fully expands every compile-time {name} (recursively); if a {{name}} remains anywhere in
    // the result, the whole expression is Deferred rather than evaluated now. A {name} that isn't
    // a known compile-time variable is a real authoring error, not an excuse to defer, and throws
    // FormatException - the caller is expected to report it with the full mapper/property context.
    public static Resolution Resolve(string expression, IReadOnlyDictionary<string, string> variables, out ulong value)
    {
        value = 0;
        var expanded = ExpandVariables(expression, variables);
        if (DeferredToken.IsMatch(expanded))
        {
            return Resolution.Deferred;
        }

        value = Evaluate(expanded);
        return Resolution.Static;
    }

    public static string ExpandVariables(string expression, IReadOnlyDictionary<string, string> variables) =>
        ExpandVariablesCore(expression, variables, 0);

    private static string ExpandVariablesCore(string expression, IReadOnlyDictionary<string, string> variables, int depth)
    {
        if (depth > 64 || expression.Length > 16384)
            throw new FormatException("Address variable expansion exceeds supported limits.");
        var expanded =
        VariableToken.Replace(expression, match =>
        {
            if (!variables.TryGetValue(match.Groups[1].Value, out var replacement))
            {
                throw new FormatException($"Unresolved address token '{match.Value}'.");
            }
            // Parenthesized so a multi-term variable's own precedence survives substitution into
            // a larger expression (e.g. "{a}" = "1 + 2" used as "{a} * 3" must become
            // "(1 + 2) * 3" = 9, not "1 + 2 * 3" = 7).
            return $"({ExpandVariablesCore(replacement, variables, depth + 1)})";
        });
        if (expanded.Length > 16384) throw new FormatException("Expanded address expression exceeds 16384 characters.");
        return expanded;
    }

    /// Distinct {{name}} tokens in the order they first appear.
    public static IReadOnlyList<string> DeferredTokenNames(string expanded) =>
        DeferredToken.Matches(expanded).Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).ToArray();

    public static string ReplaceDeferredTokens(string expanded, Func<string, string> replace) =>
        DeferredToken.Replace(expanded, match => replace(match.Groups[1].Value));

    private static ulong Evaluate(string expression)
    {
        object? result;
        try
        {
            result = new Expression(expression).Evaluate();
        }
        catch (NCalcException ex)
        {
            throw new FormatException($"Invalid address expression '{expression}': {ex.Message}", ex);
        }

        var number = Convert.ToDouble(result, CultureInfo.InvariantCulture);
        if (number != Math.Floor(number))
        {
            throw new FormatException($"Address expression '{expression}' does not evaluate to a whole number (got {number}).");
        }

        return checked((ulong)Convert.ToInt64(number, CultureInfo.InvariantCulture));
    }
}
