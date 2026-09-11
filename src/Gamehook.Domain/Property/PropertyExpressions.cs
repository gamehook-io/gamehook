namespace Gamehook.Domain.Property;

// Applies a compiled after-read-value-expression to an already-decoded property's raw value and
// writes the result back via SetValueOverride. Deliberately a free function, not a Property method -
// the engine is owned and driven by the mapper, never by the property itself.
public static class PropertyExpressions
{
    public static void Apply(Property property, CompiledExpression expression)
    {
        var unsigned = property.Type == "uint";
        // Always transform the raw decoded reading, never Value: Value may already hold this
        // expression's own output from a previous frame (Property.Refresh keeps it when the bytes
        // did not change), and re-transforming that would compound the expression every frame.
        if (property.DecodedValue is not int raw) return;
        double x = unsigned ? unchecked((uint)raw) : raw;
        // The double->decimal step is load-bearing, not incidental: it rounds off the float error a
        // division-based expression leaves behind, so e.g. 2.9999999999999996 truncates to 3 the way
        // JavaScript's own result would, rather than to 2.
        var result = (decimal)expression.Invoke(x);
        property.SetValueOverride(unsigned
            // decimal->int always range-checks regardless of checked/unchecked, so go through ulong
            // first: a result that's technically a uint's bit pattern can exceed int.MaxValue.
            ? unchecked((int)(ulong)result)
            : checked((int)result));
    }
}
