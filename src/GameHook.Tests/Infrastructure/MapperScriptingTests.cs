using GameHook.Domain.Interface;
using GameHook.Infrastructure;

namespace GameHook.Tests.Infrastructure;

// End-to-end coverage of the Jint-backed mapper scripting pipeline: a <memory> block snapshot
// feeding memory.defaultNamespace, a preprocessor resolving a dynamic "{dma_a}"-style address,
// after-read-value-expression calling a mapper script helper function, a memoryContainer filled via
// memory.fill, and a script-only property set through mapper.set_property_value in postprocessor.
public class MapperScriptingTests
{
    [Test]
    public void Dynamic_address_without_companion_script_fails_during_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gamehook-missing-script-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <mapper platform="GBA" name="Missing script">
                <properties>
                    <player>
                        <property name="name" type="string" address="{{dma_b}}" length="8" />
                    </player>
                </properties>
            </mapper>
            """);
        try
        {
            var driver = new RegionBackedDriver(new Dictionary<string, byte[]>());
            var error = Assert.Throws<InvalidDataException>(() => new Mapper(path, driver));
            Assert.That(error!.Message, Does.Contain(Path.GetFileName(Path.ChangeExtension(path, ".js")))
                .And.Contain("player.name").And.Contain("{{dma_b}}").And.Contain("missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Basic_expression_mapper_reads_without_a_mapper_script()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gamehook-basic-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <mapper platform="GBA" name="Basic">
                <properties>
                    <property name="pp" type="int" address="0x03000000" after-read-value-expression="x % 64" />
                    <property name="pp_up" type="int" address="0x03000000" after-read-value-expression="Math.floor(x / 64)" />
                </properties>
            </mapper>
            """);
        try
        {
            var driver = new RegionBackedDriver(new Dictionary<string, byte[]> { ["IWRAM"] = [130] });
            var mapper = new Mapper(path, driver);
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(mapper.Properties["pp"].Value, Is.EqualTo(2));
            Assert.That(mapper.Properties["pp_up"].Value, Is.EqualTo(2));
            Assert.That(mapper.LastReadMetrics.Postprocessor, Is.EqualTo(TimeSpan.Zero));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Static_address_supports_subtraction_multiplication_and_division()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gamehook-arithmetic-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <mapper platform="GBA" name="Arithmetic">
                <properties>
                    <property name="subtract" type="int" address="0x03000010 - 4" length="1" />
                    <property name="multiply" type="int" address="0x03000000 + 2 * 4" length="1" />
                    <property name="divide"   type="int" address="0x03000000 + 16 / 4" length="1" />
                </properties>
            </mapper>
            """);
        try
        {
            var iwram = new byte[16];
            iwram[0x0C] = 0xAA; // 0x03000010 - 4
            iwram[0x08] = 0xBB; // 0x03000000 + 2 * 4 - also proves * binds tighter than +
            iwram[0x04] = 0xCC; // 0x03000000 + 16 / 4 - also proves / binds tighter than +
            var driver = new RegionBackedDriver(new Dictionary<string, byte[]> { ["IWRAM"] = iwram });

            var mapper = new Mapper(path, driver);
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(mapper.Properties["subtract"].Value, Is.EqualTo(0xAA));
                Assert.That(mapper.Properties["multiply"].Value, Is.EqualTo(0xBB));
                Assert.That(mapper.Properties["divide"].Value, Is.EqualTo(0xCC));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Backs every requested region from one fixed byte array per region, regardless of how many
    // separate (possibly overlapping) segment requests a single read asks for - simpler than
    // modeling the driver's own request-splitting for a synthetic test fixture.
    private sealed class RegionBackedDriver(IReadOnlyDictionary<string, byte[]> regions) : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request)
        {
            var segments = request.Segments.Select(segment =>
            {
                var backing = regions[segment.RegionId];
                var bytes = backing.AsSpan(checked((int)segment.StartingAddress), segment.Length).ToArray();
                return new IDriver.MemorySegmentSnapshot(segment.RegionId, segment.StartingAddress, bytes);
            }).ToArray();
            return Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow, segments));
        }
    }

    private const string MapperXml = """
        <mapper platform="GBA" name="Synthetic">
            <memory>
                <read start="0x03000000" end="0x0300000F" />
            </memory>
            <properties>
                <property name="pointer_value" type="uint" address="{{dma_a}}" length="4" />
                <property name="quantity_raw" type="int" address="0x0300000C" length="1" after-read-value-expression="decryptItemQuantity(x)" />
                <property name="container_value" type="int" memoryContainer="slot0" address="0" length="1" />
                <property name="doubled" type="int" />
                <property name="mirror" type="int" />
            </properties>
        </mapper>
        """;

    // Mirrors the real pokemon_emerald.js pattern that broke in practice: "const copyProperties =
    // mapper.copy_properties;" has no pure-JS fallback the way some other mappers' mapper scripts do -
    // calling it later throws if mapper.copy_properties isn't a real bound function.
    private const string MapperScript = """
        const variables = __variables;
        const memory = __memory;
        const mapper = __mapper;
        const copyProperties = mapper.copy_properties;
        function decryptItemQuantity(x) {
            return x ^ 1;
        }
        function preprocessor() {
            variables.dma_a = memory.defaultNamespace.get_uint32_le(0x03000000);
            memory.fill('slot0', 0, [42]);
        }
        function postprocessor() {
            mapper.set_property_value('doubled', mapper.get_property_value('quantity_raw') * 2);
            copyProperties('quantity_raw', 'mirror');
        }
        export { preprocessor, postprocessor };
        """;

    private static (string MapperPath, string ScriptPath) WriteMapperWithScript()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"gamehook-scripting-test-{Guid.NewGuid():N}");
        var mapperPath = basePath + ".xml";
        var scriptPath = basePath + ".js";
        File.WriteAllText(mapperPath, MapperXml);
        File.WriteAllText(scriptPath, MapperScript);
        return (mapperPath, scriptPath);
    }

    [Test]
    public async Task ReadAsync_resolves_dynamic_address_container_and_script_only_properties()
    {
        var (mapperPath, scriptPath) = WriteMapperWithScript();
        // IWRAM offsets 0-15 (base 0x03000000): 0-3 is a pointer to offset 8 within this same
        // block, 8-11 is the uint32 pointer_value reads once dma_a resolves, 12 is quantity_raw's
        // raw byte (5, decrypted by XOR 1 down to 4).
        var iwram = new byte[16];
        iwram[0] = 0x08; iwram[1] = 0x00; iwram[2] = 0x00; iwram[3] = 0x03;
        iwram[8] = 0x33; iwram[9] = 0x22; iwram[10] = 0x11; iwram[11] = 0x00;
        iwram[12] = 5;
        var driver = new RegionBackedDriver(new Dictionary<string, byte[]> { ["IWRAM"] = iwram });

        try
        {
            var mapper = new Mapper(mapperPath, driver);
            Assert.That(await mapper.ReadAsync(), Is.True);

            Assert.Multiple(() =>
            {
                // dma_a resolved to 0x03000008 by preprocessor, then pointer_value decoded 0x00112233 from there.
                Assert.That(mapper.Properties["pointer_value"].Value, Is.EqualTo(0x00112233));
                // raw byte 5, after-read-value-expression "decryptItemQuantity(x)" = 5 ^ 1 = 4.
                Assert.That(mapper.Properties["quantity_raw"].Value, Is.EqualTo(4));
                // memory.fill('slot0', 0, [42]) in preprocessor backs this memoryContainer property.
                Assert.That(mapper.Properties["container_value"].Value, Is.EqualTo(42));
                // script-only property, set by postprocessor via mapper.set_property_value.
                Assert.That(mapper.Properties["doubled"].Value, Is.EqualTo(8));
                // script-only property, mirrored from quantity_raw via mapper.copy_properties.
                Assert.That(mapper.Properties["mirror"].Value, Is.EqualTo(4));
            });
        }
        finally
        {
            File.Delete(mapperPath);
            File.Delete(scriptPath);
        }
    }
}
