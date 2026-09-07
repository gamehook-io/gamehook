namespace Gamehook.Domain.Property;

// Bound as each entry of mapper.properties[path] for mapper scripts (getValue/setValue/setProperty/
// copyProperties in the JS all go through this). Field names are lowercase to match the JS call
// sites verbatim ("property.value", "property.address", ...). All writes go through Property's
// public mutation methods - this class holds no state of its own beyond the Property reference.
public sealed class PropertyHandle(Property property)
{
    public object? value
    {
        get => property.Value;
        set => property.SetValueOverride(value is double d ? unchecked((int)d) : value);
    }

    public double? address
    {
        get => property.Address;
        set => property.SetAddress(value is { } v ? (ulong)v : null);
    }

    public string? memoryContainer
    {
        get => property.MemoryContainer;
        set => property.SetMemoryContainer(value);
    }

    public double length
    {
        get => property.Length;
        set => property.SetLength(checked((int)value));
    }

    public string? bits
    {
        get => property.Bits;
        set => property.SetBits(value);
    }

    public string? reference
    {
        get => property.Reference;
        set => property.SetReference(value);
    }

    // Accepted for compatibility with the JS setProperty()'s generic passthrough of every field on
    // a source property - nothing in the current mapper pack reads either of these back, only ever
    // assigns them as part of copying one property's shape onto another.
    public object? size { get; set; }
    public object? bytes { get; set; }
}
