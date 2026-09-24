using Gamehook.Domain.Interface;
using Gamehook.Domain.Mapping;
using Gamehook.Infrastructure;

namespace Gamehook.Tests.Tests;

public sealed class MapperRawWriteTests
{
    [Test]
    public async Task Raw_write_updates_the_properties_it_overlaps()
    {
        var directory = Directory.CreateTempSubdirectory("gamehook-raw-write-");
        try
        {
            var mapperPath = Path.Combine(directory.FullName, "raw_write.xml");
            await File.WriteAllTextAsync(mapperPath, """
                <mapper id="test" name="Raw write" platform="GB">
                  <properties>
                    <property name="counter" type="int" address="0xC000" length="2" />
                    <property name="untouched" type="int" address="0xC010" length="1" />
                  </properties>
                </mapper>
                """);
            using var mapper = new Mapper(MapperCompiler.Load(mapperPath), new ZeroMemoryDriver());
            Assert.That(await mapper.ReadAsync(), Is.True);

            // WRAM starts at 0xC000, so region offset 1 is the counter's low byte (GB is big-endian).
            var (success, error) = await mapper.WriteRawBytesAsync("WRAM", 1, new byte[] { 0x05 });

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.True, error);
                Assert.That(mapper.Properties["counter"].Value, Is.EqualTo(5));
                Assert.That(mapper.Properties["untouched"].Value, Is.EqualTo(0));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class ZeroMemoryDriver : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request) => Task.FromResult(new IDriver.Response(
            DateTimeOffset.UtcNow,
            request.Segments.Select(segment => new IDriver.MemorySegmentSnapshot(
                segment.RegionId, segment.StartingAddress, new byte[segment.Length])).ToArray()));

        public Task Write(IDriver.WriteRequest request) => Task.CompletedTask;
    }
}
