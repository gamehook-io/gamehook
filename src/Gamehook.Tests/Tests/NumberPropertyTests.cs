using Gamehook.Domain;
using Gamehook.Domain.Models;
using Gamehook.Domain.Property;

namespace Gamehook.Tests.Tests;

public sealed class NumberPropertyTests
{
    private static readonly IReadOnlyDictionary<string, ReferenceTable> NoReferences = new Dictionary<string, ReferenceTable>();

    private static Property Make(string type, int length, string? bits = null, Endianness endianness = Endianness.Big, string? reference = null, string? staticValue = null) =>
        Property.Create(new PropertyConfig("p", type, 0xC000, length, bits, reference, staticValue), endianness);

    private static object? Decode(Property property, params byte[] bytes)
    {
        Assert.That(property.TryDecode(bytes, NoReferences, out var value, out var error), Is.True, error);
        return value;
    }

    [TestCase("int", 1, 0xFF, -1L)]
    [TestCase("int", 1, 0x7F, 127L)]
    [TestCase("int", 1, 0x80, -128L)]
    [TestCase("uint", 1, 0xFF, 255UL)]
    [TestCase("uint", 1, 0x80, 128UL)]
    public void Decodes_one_byte(string type, int length, int raw, object expected) =>
        Assert.That(Decode(Make(type, length), (byte)raw), Is.EqualTo(expected));

    [Test]
    public void Decodes_four_bytes_across_the_sign_boundary()
    {
        var max = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var half = new byte[] { 0x80, 0x00, 0x00, 0x00 };
        var below = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF };

        Assert.Multiple(() =>
        {
            Assert.That(Decode(Make("uint", 4), max), Is.EqualTo(4294967295UL));
            Assert.That(Decode(Make("int", 4), max), Is.EqualTo(-1L));
            Assert.That(Decode(Make("uint", 4), half), Is.EqualTo(2147483648UL));
            Assert.That(Decode(Make("int", 4), half), Is.EqualTo(-2147483648L));
            Assert.That(Decode(Make("uint", 4), below), Is.EqualTo(2147483647UL));
            Assert.That(Decode(Make("int", 4), below), Is.EqualTo(2147483647L));
        });
    }

    // Values from the reported "Arithmetic operation resulted in an overflow" errors.
    [TestCase(0xFFFFFFFFUL)]
    [TestCase(0xFE08448FUL)]
    [TestCase(0xC44048C8UL)]
    [TestCase(0xB92211E9UL)]
    public void Decodes_reported_high_bit_values_without_error(ulong value)
    {
        var bytes = new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        Assert.Multiple(() =>
        {
            Assert.That(Decode(Make("uint", 4), bytes), Is.EqualTo(value));
            Assert.That(Decode(Make("int", 4), bytes), Is.EqualTo((long)(int)(uint)value));
        });
    }

    [Test]
    public void Honours_little_endian_and_eight_byte_fields()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Decode(Make("uint", 2, endianness: Endianness.Little), 0x34, 0x12), Is.EqualTo(0x1234UL));
            Assert.That(Decode(Make("int", 2, endianness: Endianness.Little), 0xFE, 0xFF), Is.EqualTo(-2L));
            Assert.That(Decode(Make("uint", 8), 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF), Is.EqualTo(ulong.MaxValue));
            Assert.That(Decode(Make("int", 8), 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF), Is.EqualTo(-1L));
        });
    }

    [Test]
    public void Signed_bit_fields_sign_extend_over_the_field_width()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Decode(Make("int", 1, "0-4"), 0b0001_1111), Is.EqualTo(-1L));
            Assert.That(Decode(Make("int", 1, "0-4"), 0b0000_1111), Is.EqualTo(15L));
            Assert.That(Decode(Make("uint", 1, "0-4"), 0b0001_1111), Is.EqualTo(31UL));
            Assert.That(Decode(Make("int", 1, "3-5"), 0b0010_0000), Is.EqualTo(-4L));
        });
    }

    [Test]
    public void Encode_round_trips_and_preserves_sibling_bits()
    {
        var current = new ReadOnlyMemory<byte>([0b1110_0000]);

        var low = Make("uint", 1, "0-4");
        Assert.That(low.TryEncode(21UL, current, NoReferences, out var merged, out var error), Is.True, error);
        Assert.That(merged.ToArray(), Is.EqualTo(new byte[] { 0b1111_0101 }));

        var signed = Make("int", 1, "0-4");
        Assert.That(signed.TryEncode(-1L, current, NoReferences, out merged, out error), Is.True, error);
        Assert.That(merged.ToArray(), Is.EqualTo(new byte[] { 0b1111_1111 }));

        var wide = Make("int", 4);
        Assert.That(wide.TryEncode(-2L, new byte[4], NoReferences, out merged, out error), Is.True, error);
        Assert.That(merged.ToArray(), Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFE }));

        var unsigned = Make("uint", 4);
        Assert.That(unsigned.TryEncode(4294967295UL, new byte[4], NoReferences, out merged, out error), Is.True, error);
        Assert.That(merged.ToArray(), Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
    }

    [TestCase("uint", 1, 256L)]
    [TestCase("uint", 1, -1L)]
    [TestCase("int", 1, 128L)]
    [TestCase("int", 1, -129L)]
    [TestCase("uint", 4, 4294967296L)]
    public void Encode_rejects_values_outside_the_type_range(string type, int length, long value) =>
        Assert.That(Make(type, length).TryEncode(value, new byte[length], NoReferences, out _, out var error), Is.False, error);

    [Test]
    public void Encode_rejects_fractions()
    {
        Assert.That(Make("uint", 1).TryEncode(1.5, new byte[1], NoReferences, out _, out _), Is.False);
        Assert.That(Make("uint", 1).TryEncode(3.0, new byte[1], NoReferences, out var bytes, out _), Is.True);
        Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 3 }));
    }

    [Test]
    public void Reference_tables_key_on_the_unsigned_raw_value()
    {
        var table = new ReferenceTable(false, new Dictionary<ulong, string> { [0xFF] = "None" });
        var references = new Dictionary<string, ReferenceTable> { ["items"] = table };
        foreach (var type in new[] { "int", "uint" })
        {
            var property = Make(type, 1, reference: "items");
            Assert.That(property.TryDecode(new byte[] { 0xFF }, references, out var value, out var error), Is.True, error);
            Assert.That(value, Is.EqualTo("None"), type);
        }
    }

    [Test]
    public void Static_values_parse_per_type()
    {
        var unsigned = Make("uint", 4, staticValue: "4294967295");
        var signed = Make("int", 4, staticValue: "-5");
        unsigned.Refresh([], NoReferences);
        signed.Refresh([], NoReferences);
        Assert.Multiple(() =>
        {
            Assert.That(unsigned.Value, Is.EqualTo(4294967295UL));
            Assert.That(signed.Value, Is.EqualTo(-5L));
        });
    }

    [Test]
    public void Script_values_must_fit_the_property()
    {
        var unsigned = Make("uint", 1);
        unsigned.SetScriptValue(200.0);
        Assert.That(unsigned.Value, Is.EqualTo(200UL));

        Assert.Throws<InvalidDataException>(() => unsigned.SetScriptValue(256.0));
        Assert.Throws<InvalidDataException>(() => unsigned.SetScriptValue(-1.0));

        var signed = Make("int", 1);
        signed.SetScriptValue(-128.0);
        Assert.That(signed.Value, Is.EqualTo(-128L));
        Assert.Throws<InvalidDataException>(() => signed.SetScriptValue(128.0));

        unsigned.SetScriptValue(null);
        Assert.That(unsigned.Value, Is.Null);
    }

    private static Property WithDecodedValue(string type, int length, string staticValue)
    {
        var property = Make(type, length, staticValue: staticValue);
        property.Refresh([], NoReferences);
        return property;
    }

    [Test]
    public void Expressions_use_the_full_unsigned_range_and_do_not_wrap_in_range_results()
    {
        var property = WithDecodedValue("uint", 4, "36099748");
        Assert.That(PropertyExpressions.Apply(property, x => (x - 36099748 + 548 * 3) / 548), Is.False);
        Assert.That(property.Value, Is.EqualTo(3UL));

        var high = WithDecodedValue("uint", 4, "4294967295");
        Assert.That(PropertyExpressions.Apply(high, x => x), Is.False);
        Assert.That(high.Value, Is.EqualTo(4294967295UL));
    }

    [Test]
    public void Expressions_wrap_out_of_range_results_and_report_it()
    {
        var unsigned = WithDecodedValue("uint", 4, "0");
        Assert.That(PropertyExpressions.Apply(unsigned, x => x - 1), Is.True);
        Assert.That(unsigned.Value, Is.EqualTo(4294967295UL));

        var oneByte = WithDecodedValue("uint", 1, "255");
        Assert.That(PropertyExpressions.Apply(oneByte, x => x + 2), Is.True);
        Assert.That(oneByte.Value, Is.EqualTo(1UL));

        var signed = WithDecodedValue("int", 1, "127");
        Assert.That(PropertyExpressions.Apply(signed, x => x + 1), Is.True);
        Assert.That(signed.Value, Is.EqualTo(-128L));
    }
}
