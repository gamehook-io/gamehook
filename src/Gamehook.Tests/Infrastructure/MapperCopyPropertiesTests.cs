using System.Reflection;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Property;
using Gamehook.Infrastructure;

namespace Gamehook.Tests.Infrastructure;

// Mapper swaps a script's own JavaScript copyProperties for the host binding, because the script
// version rescans every property name in the mapper on every frame. That is only safe while the two
// produce identical results, so this runs both against real mappers and compares the outcome
// field by field - if a mapper ever ships a copyProperties that does something different, this
// fails rather than silently changing what that mapper reports.
public class MapperCopyPropertiesTests : BaseTest
{
    private sealed class ZeroDriver : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request) =>
            Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow, request.Segments
                .Select(s => new IDriver.MemorySegmentSnapshot(s.RegionId, s.StartingAddress, new byte[s.Length]))
                .ToArray()));
    }

    private const string ScriptCopyProperties = """
        function __script_copy_properties(sourcePath, destinationPath) {
            var destPathLength = destinationPath.length;
            Object.keys(mapper.properties)
                .filter(function (key) { return key.startsWith(destinationPath); })
                .forEach(function (key) {
                    var source = mapper.properties[sourcePath + key.slice(destPathLength)];
                    if (source) {
                        var property = mapper.properties[key];
                        if (source.memoryContainer !== undefined) property.memoryContainer = source.memoryContainer;
                        if (source.address !== undefined) property.address = source.address;
                        if (source.length !== undefined) property.length = source.length;
                        if (source.bits !== undefined) property.bits = source.bits;
                        if (source.reference !== undefined) property.reference = source.reference;
                        if (source.value !== undefined) property.value = source.value;
                    }
                });
        }
        """;

    private static IEnumerable<TestCaseData> MappersWithScriptCopyProperties() =>
        AllMapperFiles
            .Where(path => File.Exists(Path.ChangeExtension(path, ".js")))
            .Where(path => File.ReadAllText(Path.ChangeExtension(path, ".js"))
                .Contains("function copyProperties", StringComparison.Ordinal))
            .Select(path => new TestCaseData(path).SetArgDisplayNames(Path.GetFileName(path)));

    [TestCaseSource(nameof(MappersWithScriptCopyProperties))]
    public async Task Host_copy_properties_matches_the_script_implementation(string mapperPath)
    {
        // The pairs the shipped scripts actually use: an arbitrary party slot onto the active
        // Pokemon, which is the only thing copyProperties is ever called for.
        foreach (var (source, destination) in new[]
                 {
                     ("player.team.0", "player.active_pokemon"),
                     ("player.team.3", "player.active_pokemon"),
                     ("battle.player.active_pokemon", "player.active_pokemon"),
                 })
        {
            var viaScript = await Copy(mapperPath, source, destination, useHost: false);
            var viaHost = await Copy(mapperPath, source, destination, useHost: true);

            Assert.That(viaHost, Is.EqualTo(viaScript), $"{Path.GetFileName(mapperPath)}: {source} -> {destination}");
        }
    }

    private static async Task<Dictionary<string, string>> Copy(
        string mapperPath, string sourcePath, string destinationPath, bool useHost)
    {
        var mapper = new Mapper(mapperPath, new ZeroDriver());
        await mapper.ReadAsync();

        if (useHost)
        {
            var copy = typeof(Mapper).GetMethod("CopyProperties", BindingFlags.NonPublic | BindingFlags.Instance)!;
            copy.Invoke(mapper, [sourcePath, destinationPath]);
        }
        else
        {
            var engine = ((ExpressionEngine)typeof(Mapper)
                .GetField("scriptEngine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mapper)!).Engine;
            engine.Execute(ScriptCopyProperties);
            engine.Invoke("__script_copy_properties", sourcePath, destinationPath);
        }

        return mapper.Properties
            .Where(entry => entry.Key.StartsWith(destinationPath, StringComparison.Ordinal))
            .ToDictionary(
                entry => entry.Key,
                entry => $"{entry.Value.MemoryContainer}|{entry.Value.Address}|{entry.Value.Length}|" +
                    $"{entry.Value.Bits}|{entry.Value.Reference}|{entry.Value.Value}",
                StringComparer.Ordinal);
    }
}
