using System.Globalization;
using System.Text.RegularExpressions;
using NCalc;

namespace Gamehook.Infrastructure;

// A "{{token}}" address, reduced at compile time to the cheapest form that still reproduces the
// expression exactly. The read loop touches thousands of these per frame (pokemon_emerald compiles
// 1500), so nothing here may parse, allocate, or call into a scripting engine at read time.
//
// Every compile-time "{name}" is already substituted away before this type sees the expression, so
// the only thing left that can change between reads is the runtime tokens - and those are resolved
// once per read by the caller, into the shared values span this type indexes into.
internal sealed class DeferredAddress
{
    // Affine form: value = tokenValue * multiplier + offset. Real mappers are overwhelmingly
    // "{{dma_a}} + 0x1234", so this covers essentially every deferred address in the shipped
    // pack and reduces a read to one multiply and one add.
    private readonly int tokenIndex = -1;
    private readonly long multiplier;
    private readonly long offset;

    // Fallback for anything not affine in a single token (several tokens, division, ...): the
    // parsed NCalc expression is still built once and reused, with only its parameters rebound.
    private readonly Expression? expression;
    private readonly string[] parameterNames = [];
    private readonly int[] parameterIndexes = [];

    public string Source { get; }

    private DeferredAddress(string source, int tokenIndex, long multiplier, long offset)
    {
        Source = source;
        this.tokenIndex = tokenIndex;
        this.multiplier = multiplier;
        this.offset = offset;
    }

    private DeferredAddress(string source, Expression expression, string[] parameterNames, int[] parameterIndexes)
    {
        Source = source;
        this.expression = expression;
        this.parameterNames = parameterNames;
        this.parameterIndexes = parameterIndexes;
    }

    /// <summary>Compiles a mapper address expression for allocation-free reads.</summary>
    /// <param name="expression">Address expression to compile.</param>
    /// <param name="variables">Compile-time mapper variables.</param>
    /// <param name="internToken">
    /// Maps a runtime token name onto its slot in the mapper-wide token table, so a read resolves
    /// each distinct token once (pokemon_emerald's 1500 deferred addresses share just two) instead
    /// of once per property.
    /// </param>
    public static DeferredAddress Compile(
        string expression,
        IReadOnlyDictionary<string, string> variables,
        Func<string, int> internToken)
    {
        // Compile-time {name} variables can themselves expand to further {name} references, and are
        // fixed for the life of the mapper - fold them away now so read time never sees a brace.
        var expanded = AddressExpression.ExpandVariables(expression, variables);
        var tokens = AddressExpression.DeferredTokenNames(expanded);

        if (tokens.Count == 1 && TryCompileAffine(expanded, tokens[0], out var multiplier, out var offset))
        {
            return new DeferredAddress(expression, internToken(tokens[0]), multiplier, offset);
        }

        var names = tokens.ToArray();
        var indexes = names.Select(internToken).ToArray();
        // NCalc has no {{...}} syntax; its own [name] parameter form is what survives into the
        // parsed tree, so the same slot can be rebound each read without reparsing.
        var parameterized = AddressExpression.ReplaceDeferredTokens(expanded, name => $"[{name}]");
        var compiled = new Expression(parameterized, CultureInfo.InvariantCulture);
        if (compiled.HasErrors())
        {
            throw new FormatException($"Invalid address expression '{expression}': {compiled.Error?.Message}");
        }
        return new DeferredAddress(expression, compiled, names, indexes);
    }

    // Probes the expression at token = 0, 1, 2. Equal first differences mean it is affine in that
    // token, and the three samples fully determine it - which beats pattern-matching the text,
    // because it also folds arbitrary nesting and parenthesization ("(({{v}} + 8)) + 4") flat.
    private static bool TryCompileAffine(string expanded, string token, out long multiplier, out long offset)
    {
        multiplier = 0;
        offset = 0;
        // Sampling alone cannot prove linearity. Only a single token combined with
        // constants through addition, subtraction and multiplication qualifies.
        var occurrences = 0;
        var constants = AddressExpression.ReplaceDeferredTokens(expanded, _ => { occurrences++; return "0"; });
        if (occurrences != 1 || constants.Contains("**", StringComparison.Ordinal)
            || !Regex.IsMatch(constants, @"\A[0-9a-fA-FxX\s()+*\-]+\z")) return false;
        try
        {
            if (!TryProbe(expanded, token, 0, out var at0) ||
                !TryProbe(expanded, token, 1, out var at1) ||
                !TryProbe(expanded, token, 2, out var at2))
            {
                return false;
            }
            if (at1 - at0 != at2 - at1) return false;
            multiplier = at1 - at0;
            offset = at0;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryProbe(string expanded, string token, long value, out long result)
    {
        result = 0;
        var substituted = AddressExpression.ReplaceDeferredTokens(expanded,
            name => name == token ? $"({value.ToString(CultureInfo.InvariantCulture)})" : throw new FormatException());
        object? evaluated;
        try
        {
            evaluated = new Expression(substituted, CultureInfo.InvariantCulture).Evaluate();
        }
        catch (Exception ex) when (ex is NCalc.Exceptions.NCalcException or FormatException)
        {
            return false;
        }

        var number = Convert.ToDouble(evaluated, CultureInfo.InvariantCulture);
        if (number != Math.Floor(number) || number < long.MinValue || number > long.MaxValue) return false;
        result = Convert.ToInt64(number, CultureInfo.InvariantCulture);
        return true;
    }

    /// Slots in <paramref name="tokenValues"/> this address reads; a null slot means the mapper
    /// script has not set that variable yet (game not booted, DMA still relocating) and the
    /// property reports no address rather than a stale one.
    public bool TryResolve(ulong?[] tokenValues, out ulong address)
    {
        address = 0;
        if (expression is null)
        {
            if (tokenValues[tokenIndex] is not { } token) return false;
            try
            {
                var value = checked((long)token * multiplier + offset);
                if (value < 0) return false;
                address = (ulong)value;
                return true;
            }
            catch (OverflowException) { return false; }
        }

        for (var index = 0; index < parameterNames.Length; index++)
        {
            if (tokenValues[parameterIndexes[index]] is not { } token) return false;
            expression.Parameters[parameterNames[index]] = (double)token;
        }

        object? evaluated;
        try
        {
            evaluated = expression.Evaluate();
        }
        catch (Exception ex) when (ex is NCalc.Exceptions.NCalcException or FormatException or OverflowException)
        {
            return false;
        }

        var number = Convert.ToDouble(evaluated, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number) || number != Math.Floor(number) || number < 0 || number >= 18446744073709551616d) return false;
        address = (ulong)number;
        return true;
    }
}
