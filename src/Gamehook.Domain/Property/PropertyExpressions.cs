using System.Numerics;

namespace Gamehook.Domain.Property;

// Applies a compiled after-read-value-expression to an already-decoded property's raw value and
// writes the result back via SetValueOverride. Deliberately a free function, not a Property method -
// the engine is owned and driven by the mapper, never by the property itself.
public static class PropertyExpressions
{
    /// Returns true when the expression's result fell outside the property's range and was wrapped
    /// to fit, so the caller can report it. An expression is display logic running every poll, so it
    /// must not abort the read the way a rejected write does.
    public static bool Apply(Property property, Func<double, double> expression)
    {
        // Always transform the raw decoded reading, never Value: Value may already hold this
        // expression's own output from a previous frame (Property.Refresh keeps it when the bytes
        // did not change), and re-transforming that would compound the expression every frame.
        double x;
        switch (property.DecodedValue)
        {
            case long signedRaw: x = signedRaw; break;
            case ulong unsignedRaw: x = unsignedRaw; break;
            default: return false;
        }

        // The double->decimal step is load-bearing, not incidental: it rounds off the float error a
        // division-based expression leaves behind, so e.g. 2.9999999999999996 truncates to 3 the way
        // JavaScript's own result would, rather than to 2.
        var result = new BigInteger(decimal.Truncate((decimal)expression(x)));
        var signed = NumericRange.IsSigned(property.Type);
        var width = property.BitWidth;
        var wrapped = !NumericRange.Fits(result, signed, width);
        if (wrapped) result = NumericRange.Wrap(result, signed, width);
        property.SetValueOverride(NumericRange.Box(result, signed));
        return wrapped;
    }
}
