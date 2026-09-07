using Gamehook.Domain.Interface;
using Gamehook.Infrastructure;

namespace Gamehook.Tests.Infrastructure;

public class MapperConditionTests
{
    private sealed class Driver : IDriver
    {
        public byte Mode { get; set; }
        public Task<IDriver.Response> Read(IDriver.Request request) => Task.FromResult(
            new IDriver.Response(DateTimeOffset.UtcNow, request.Segments.Select(s =>
                new IDriver.MemorySegmentSnapshot(s.RegionId, s.StartingAddress,
                    Enumerable.Repeat(Mode, s.Length).ToArray())).ToArray()));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"gamehook-condition-{Guid.NewGuid():N}.xml");
        public Driver Driver { get; } = new();
        public Fixture(string properties, string? script = null)
        {
            File.WriteAllText(path, $"<mapper platform=\"GB\"><properties>{properties}</properties></mapper>");
            if (script is not null) File.WriteAllText(Path.ChangeExtension(path, ".js"), script);
        }
        public Mapper Load() => new(path, Driver);
        public void Dispose()
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".js"));
        }
    }

    private const string Mode = "<property name=\"battle.mode\" type=\"int\" address=\"0xC000\" />";

    [Test]
    public async Task Conditions_recompute_in_order_without_javascript_and_keep_property_identity()
    {
        using var fixture = new Fixture(Mode + """
            <if expression="battle.mode == 1">
                <property name="meta.state" type="string" value="First" />
            </if>
            <elif expression="battle.mode > 0">
                <property name="meta.state" type="string" value="Second" />
            </elif>
            <else>
                <property name="meta.state" type="string" value="Overworld" />
            </else>
            <if expression="meta.state == 'First' || meta.state == 'Second'">
                <property name="active" type="bool" value="true" />
            </if>
            """);
        var mapper = fixture.Load();
        var state = mapper.Properties["meta.state"];
        Assert.That(state.Value, Is.Null, "Conditions must not execute at load.");
        foreach (var (mode, expected) in new[] { (1, "First"), (2, "Second"), (0, "Overworld"), (1, "First"), (1, "First") })
        {
            fixture.Driver.Mode = (byte)mode;
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(state.Value, Is.EqualTo(expected));
            Assert.That(mapper.Properties["meta.state"], Is.SameAs(state));
            Assert.That(mapper.Properties["active"].Value, Is.EqualTo(mode == 0 ? null : (object)true));
            Assert.That(mapper.LastReadMetrics.Postprocessor, Is.EqualTo(TimeSpan.Zero));
        }
    }

    [Test]
    public async Task Conditions_support_null_numbers_strings_boolean_operators_and_group_paths_before_script()
    {
        using var fixture = new Fixture(Mode + """
            <property name="missing" type="string" />
            <property name="label" type="string" value="battle.mode" />
            <property name="enabled" type="bool" value="true" />
            <meta>
                <if expression="missing == null &amp;&amp; !(battle.mode != 0) &amp;&amp; enabled &amp;&amp; label == 'battle.mode' &amp;&amp; (battle.mode &lt; 1 || false)">
                    <property name="state" type="int" value="7" />
                </if>
                <else><property name="state" type="int" /></else>
            </meta>
            <property name="script_seen" type="int" />
            """, "function postprocessor() { __mapper.properties['script_seen'].value = __mapper.properties['meta.state'].value; }");
        var mapper = fixture.Load();
        Assert.That(await mapper.ReadAsync(), Is.True);
        Assert.That(mapper.Properties["script_seen"].Value, Is.EqualTo(7));
        fixture.Driver.Mode = 2;
        Assert.That(await mapper.ReadAsync(), Is.True);
        Assert.That(mapper.Properties["meta.state"].Value, Is.Null);
        Assert.That(mapper.Properties["script_seen"].Value, Is.Null);
    }

    [TestCase("<else><property name='x' /></else>", "Orphan")]
    [TestCase("<elif expression='true'><property name='x' /></elif>", "Orphan")]
    [TestCase("<if><property name='x' /></if>", "requires an expression")]
    [TestCase("<if expression='battle.mode =='><property name='x' /></if>", "Condition expression")]
    [TestCase("<if expression='typo == 0'><property name='x' /></if>", "unknown property")]
    [TestCase("<if expression='true'><property name='x' type='int' /></if><else><property name='x' type='string' /></else>", "conflicting types")]
    [TestCase("<if expression='true'><property name='x' address='0xC001' /></if>", "literal")]
    [TestCase("<if expression='true'><property name='x' value='oops' /></if>", "invalid int")]
    [TestCase("<if expression='true'><property name='x' /></if><else><property name='x' /></else><elif expression='true'><property name='x' /></elif>", "last branch")]
    public void Invalid_conditions_fail_during_load(string xml, string message)
    {
        using var fixture = new Fixture(Mode + xml);
        Assert.That(() => fixture.Load(), Throws.TypeOf<InvalidDataException>().With.Message.Contains(message));
    }
}
