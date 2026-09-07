using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Property;

namespace Gamehook.Tests.Translations;

public class PropertyEncodeTests
{
    private static readonly IReadOnlyDictionary<string, ReferenceTable> NoReferences = new Dictionary<string, ReferenceTable>();

    [Test]
    public void Two_nibble_fields_sharing_a_byte_do_not_clobber_each_other()
    {
        // Regression for the exact hazard this feature was built to avoid: two Bits-masked
        // properties packed into one byte must be able to merge onto each other's output in turn
        // without wiping out the sibling nibble.
        var low = new NumberProperty(new PropertyConfig("low", "int", 0xC000, 1, "0-3", null, null));
        var high = new NumberProperty(new PropertyConfig("high", "int", 0xC000, 1, "4-7", null, null));

        ReadOnlyMemory<byte> current = new byte[] { 0x00 };

        Assert.That(low.TryEncode(5, current, NoReferences, out var afterLow, out var lowError), Is.True, lowError);
        Assert.That(afterLow.ToArray(), Is.EqualTo(new byte[] { 0x05 }));

        Assert.That(high.TryEncode(9, afterLow, NoReferences, out var afterHigh, out var highError), Is.True, highError);
        Assert.That(afterHigh.ToArray(), Is.EqualTo(new byte[] { 0x95 }));

        // low's own nibble must have survived high's merge
        Assert.That(low.TryDecode(afterHigh, NoReferences, out var lowValue, out _), Is.True);
        Assert.That(lowValue, Is.EqualTo(5));
        Assert.That(high.TryDecode(afterHigh, NoReferences, out var highValue, out _), Is.True);
        Assert.That(highValue, Is.EqualTo(9));
    }

    [Test]
    public void Encoding_onto_a_stale_basis_would_clobber_the_sibling_nibble()
    {
        // Sanity check for the test above: proves MergeBits genuinely depends on `currentBytes`
        // reflecting the other field's write - merging both edits onto the *same* stale 0x00 basis
        // (what a naive independent read-modify-write would do) loses one of them.
        var low = new NumberProperty(new PropertyConfig("low", "int", 0xC000, 1, "0-3", null, null));
        var high = new NumberProperty(new PropertyConfig("high", "int", 0xC000, 1, "4-7", null, null));
        ReadOnlyMemory<byte> stale = new byte[] { 0x00 };

        low.TryEncode(5, stale, NoReferences, out var lowOnly, out _);
        high.TryEncode(9, stale, NoReferences, out var highOnly, out _);

        Assert.That(lowOnly.ToArray(), Is.Not.EqualTo(highOnly.ToArray()));
    }

    [Test]
    public void NumberProperty_encode_respects_little_endian_length()
    {
        var property = new NumberProperty(new PropertyConfig("hp", "uint", 0xC000, 2, null, null, null), Endianness.Little);
        Assert.That(property.TryEncode("4660", new byte[2], NoReferences, out var bytes, out var error), Is.True, error);
        Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 0x34, 0x12 }));
    }

    [Test]
    public void BooleanProperty_encode_merges_a_single_bit()
    {
        var flag = new BooleanProperty(new PropertyConfig("caught", "bool", 0xC000, 1, "3", null, null));
        ReadOnlyMemory<byte> current = new byte[] { 0xFF };

        Assert.That(flag.TryEncode(false, current, NoReferences, out var bytes, out var error), Is.True, error);
        Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 0b1111_0111 }));
    }

    [Test]
    public void BitArrayProperty_round_trips_through_encode()
    {
        var property = PropertyTestFactory.BitArrayFromBytes("flags", new byte[] { 0b1010_0101 });
        var bits = (bool[])property.Value!;

        Assert.That(property.TryEncode(bits, new byte[1], NoReferences, out var bytes, out var error), Is.True, error);
        Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 0b1010_0101 }));
    }

    [Test]
    public void StringProperty_encode_clears_old_trailing_bytes_when_new_value_is_shorter()
    {
        var property = new Gamehook.Domain.Property.StringProperty(new PropertyConfig("name", "string", 0xC000, 5, null, null, null));
        Assert.That(property.TryEncode("hi", new byte[] { (byte)'o', (byte)'l', (byte)'d', (byte)'!', (byte)'!' }, NoReferences, out var bytes, out var error), Is.True, error);
        Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { (byte)'h', (byte)'i', 0, 0, 0 }));
    }

    [Test]
    public void StringProperty_encode_rejects_text_longer_than_length()
    {
        var property = new Gamehook.Domain.Property.StringProperty(new PropertyConfig("name", "string", 0xC000, 2, null, null, null));
        Assert.That(property.TryEncode("too long", new byte[2], NoReferences, out _, out var error), Is.False);
        Assert.That(error, Does.Contain("exceeds"));
    }

    [Test]
    public void NumberProperty_encode_rejects_values_that_do_not_fit_in_its_bytes_or_bit_range()
    {
        var byteValue = new NumberProperty(new PropertyConfig("byte", "uint", 0xC000, 1, null, null, null));
        var nibble = new NumberProperty(new PropertyConfig("nibble", "uint", 0xC000, 1, "0-3", null, null));

        Assert.That(byteValue.TryEncode(256, new byte[1], NoReferences, out _, out var byteError), Is.False);
        Assert.That(byteError, Does.Contain("does not fit"));
        Assert.That(nibble.TryEncode(16, new byte[] { 0xA0 }, NoReferences, out _, out var nibbleError), Is.False);
        Assert.That(nibbleError, Does.Contain("does not fit"));
    }

    [Test]
    public void BinaryCodedDecimal_encode_rejects_values_that_exceed_available_digits()
    {
        var property = new NumberProperty(new PropertyConfig("score", "binaryCodedDecimal", 0xC000, 1, null, null, null));

        Assert.That(property.TryEncode("123", new byte[1], NoReferences, out _, out var error), Is.False);
        Assert.That(error, Does.Contain("exceeds"));
    }

    [Test]
    public void Reference_backed_property_reverse_resolves_the_display_string_to_its_raw_key()
    {
        var references = new Dictionary<string, ReferenceTable>
        {
            ["species"] = new ReferenceTable(false, new Dictionary<ulong, string> { [1] = "Bulbasaur", [2] = "Ivysaur" }),
        };
        var property = new NumberProperty(new PropertyConfig("species_id", "int", 0xC000, 1, null, "species", null));

        Assert.That(property.TryEncode("Ivysaur", new byte[1], references, out var bytes, out var error), Is.True, error);
        Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public void TryEncode_fails_when_currentBytes_length_does_not_match_property_length()
    {
        var property = new NumberProperty(new PropertyConfig("hp", "int", 0xC000, 2, null, null, null));
        Assert.That(property.TryEncode(1, new byte[1], NoReferences, out _, out var error), Is.False);
        Assert.That(error, Does.Contain("2"));
    }
}
