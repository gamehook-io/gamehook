namespace Gamehook.Domain.Property;

// A ready-to-call after-read-value-expression. The read loop applies hundreds of these per frame
// (pokemon_emerald binds 429), so which backend evaluates it is decided once, at compile time -
// invoking it is a single delegate call with no dictionary lookup and no string comparison.
public sealed class CompiledExpression
{
    private readonly Func<double, double> evaluate;

    internal CompiledExpression(string source, bool isScripted, Func<double, double> evaluate)
    {
        Source = source;
        IsScripted = isScripted;
        this.evaluate = evaluate;
    }

    public string Source { get; }

    /// True when the expression fell back to JavaScript; native expressions never touch the engine.
    public bool IsScripted { get; }

    public double Invoke(double x) => evaluate(x);
}
