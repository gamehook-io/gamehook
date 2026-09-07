using GameHook.Domain.Interface;
using GameHook.Infrastructure;

namespace GameHook.Tests.Infrastructure;

public class MapperCompilerRegressionTests
{
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("gamehook-compiler-test-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(root, true);

    [Test]
    public void Recursive_macro_is_rejected_without_exhausting_the_stack()
    {
        var path = Write("""
            <mapper platform="GB"><macros><loop><macro type="loop" /></loop></macros>
            <properties><macro type="loop" /></properties></mapper>
            """);
        Assert.Throws<InvalidDataException>(() => new Mapper(path, new RecordingDriver()));
    }

    [Test]
    public void Recursive_address_variable_is_rejected_without_exhausting_the_stack()
    {
        var path = Write("""
            <mapper platform="GB" xmlns:var="https://schemas.pokeabyte.io/attributes/var">
            <macros><item><property name="value" address="{addr}" /></item></macros>
            <properties><macro type="item" var:addr="{addr}" /></properties></mapper>
            """);
        Assert.Throws<InvalidDataException>(() => new Mapper(path, new RecordingDriver()));
    }

    [Test]
    public async Task Overlapping_explicit_memory_and_property_reads_are_merged()
    {
        var path = Write("""
            <mapper platform="GB"><memory><read start="0xC000" end="0xC003" /></memory>
            <properties><property name="value" address="0xC000" /></properties></mapper>
            """);
        var driver = new RecordingDriver();
        using var mapper = new Mapper(path, driver);
        await mapper.ReadAsync();
        Assert.That(driver.LastRequest!.Segments, Has.Count.EqualTo(1));
        Assert.That(driver.LastRequest.Segments[0].Length, Is.EqualTo(4));
    }

    private string Write(string xml)
    {
        var path = Path.Combine(root, "mapper.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private sealed class RecordingDriver : IDriver
    {
        public IDriver.Request? LastRequest { get; private set; }
        public Task<IDriver.Response> Read(IDriver.Request request)
        {
            LastRequest = request;
            return Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow,
                request.Segments.Select(segment => new IDriver.MemorySegmentSnapshot(
                    segment.RegionId, segment.StartingAddress, new byte[segment.Length])).ToArray()));
        }
    }
}
