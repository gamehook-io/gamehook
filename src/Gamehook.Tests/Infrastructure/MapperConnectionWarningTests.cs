using Gamehook.Domain.Interface;
using Gamehook.Infrastructure;
using System.Net.Sockets;

namespace Gamehook.Tests.Infrastructure;

public class MapperConnectionWarningTests
{
    private sealed class FakeDriver : IDriver
    {
        private readonly Queue<Func<IDriver.Response>> responses = new();

        public void EnqueueTimeout() => responses.Enqueue(() => throw new TimeoutException("no response"));

        public void EnqueueConnectionRefused() => responses.Enqueue(() => throw new SocketException((int)SocketError.ConnectionRefused));

        public void EnqueueSuccess() => responses.Enqueue(() =>
            new IDriver.Response(DateTimeOffset.UtcNow, [new IDriver.MemorySegmentSnapshot("WRAM", 0, new byte[] { 5 })]));

        public void EnqueueEmpty(bool includeSegment) => responses.Enqueue(() =>
            new IDriver.Response(DateTimeOffset.UtcNow, includeSegment
                ? [new IDriver.MemorySegmentSnapshot("WRAM", 0, ReadOnlyMemory<byte>.Empty)]
                : []));

        public Task<IDriver.Response> Read(IDriver.Request request) => Task.FromResult(responses.Dequeue()());
    }

    private static string WriteMapperFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "<mapper platform=\"GB\"><properties><property name=\"hp\" type=\"int\" address=\"0xC000\" length=\"1\" /></properties></mapper>");
        return path;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Empty_reads_preserve_values_and_snapshot_until_recovery(bool includeSegment)
    {
        var driver = new FakeDriver();
        var path = WriteMapperFile();
        try
        {
            var mapper = new Mapper(path, driver);
            driver.EnqueueSuccess();
            Assert.That(await mapper.ReadAsync(), Is.True);
            var snapshot = mapper.LastMemorySegments;
            for (var failure = 1; failure <= 5; failure++)
            {
                driver.EnqueueEmpty(includeSegment);
                Assert.That(await mapper.ReadAsync(), Is.False);
                Assert.That(mapper.ConsecutiveReadFailures, Is.EqualTo(failure));
                Assert.That(mapper.Properties["hp"].Value, Is.EqualTo(5));
                Assert.That(mapper.Properties["hp"].Bytes.ToArray(), Is.EqualTo(new byte[] { 5 }));
                Assert.That(mapper.LastMemorySegments, Is.SameAs(snapshot));
                Assert.That(mapper.LastReadFailureMessage, Does.Contain("no memory data"));
            }
            Assert.That(mapper.ConnectionWarning, Is.Not.Null);
            driver.EnqueueSuccess();
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(mapper.ConsecutiveReadFailures, Is.Zero);
            Assert.That(mapper.ConnectionWarning, Is.Null);
            Assert.That(mapper.LastReadFailureMessage, Is.Null);
            Assert.That(mapper.Properties["hp"].Value, Is.EqualTo(5));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void Empty_first_read_reports_unable_to_connect()
    {
        var driver = new FakeDriver();
        var path = WriteMapperFile();
        try
        {
            driver.EnqueueEmpty(false);
            var mapper = new Mapper(path, driver);
            Assert.That(async () => await mapper.ReadAsync(),
                Throws.TypeOf<TimeoutException>().With.Message.Contains("no memory data"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Static_mapper_does_not_require_memory_data()
    {
        var driver = new FakeDriver();
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "<mapper platform=\"GB\"><properties><property name=\"constant\" type=\"int\" value=\"7\" /></properties></mapper>");
        try
        {
            driver.EnqueueEmpty(false);
            var mapper = new Mapper(path, driver);
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(mapper.Properties["constant"].Value, Is.EqualTo(7));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ReadAsync_splits_sparse_region_ranges_and_refreshes_each_property()
    {
        var requests = new List<IDriver.MemorySegmentRequest>();
        var driver = new RecordingDriver(requests);
        var mapperPath = Path.GetTempFileName();
        File.WriteAllText(mapperPath, "<mapper platform=\"GB\"><properties><property name=\"first\" type=\"int\" address=\"0xC000\" length=\"1\" /><property name=\"last\" type=\"int\" address=\"0xC100\" length=\"1\" /></properties></mapper>");
        try
        {
            var mapper = new Mapper(mapperPath, driver);
            Assert.That(await mapper.ReadAsync(), Is.True);

            Assert.That(requests, Is.EqualTo(new[]
            {
                new IDriver.MemorySegmentRequest("WRAM", 0, 1),
                new IDriver.MemorySegmentRequest("WRAM", 0x100, 1),
            }));
            Assert.That(mapper.Properties["first"].Value, Is.EqualTo(1));
            Assert.That(mapper.Properties["last"].Value, Is.EqualTo(2));
        }
        finally
        {
            File.Delete(mapperPath);
        }
    }

    private sealed class RecordingDriver(List<IDriver.MemorySegmentRequest> requests) : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request)
        {
            requests.AddRange(request.Segments);
            return Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow,
            [
                new IDriver.MemorySegmentSnapshot("WRAM", 0, new byte[] { 1 }),
                new IDriver.MemorySegmentSnapshot("WRAM", 0x100, new byte[] { 2 }),
            ]));
        }
    }

    [Test]
    public async Task ReadAsync_throws_when_the_very_first_read_times_out()
    {
        var driver = new FakeDriver();
        driver.EnqueueTimeout();
        var mapperPath = WriteMapperFile();
        try
        {
            var mapper = new Mapper(mapperPath, driver);
            Assert.ThrowsAsync<TimeoutException>(async () => await mapper.ReadAsync());
        }
        finally
        {
            File.Delete(mapperPath);
        }
    }

    [Test]
    public async Task ReadAsync_tolerates_connection_refused_after_a_connection_is_established()
    {
        var driver = new FakeDriver();
        var mapperPath = WriteMapperFile();
        try
        {
            var mapper = new Mapper(mapperPath, driver);
            driver.EnqueueSuccess();
            Assert.That(await mapper.ReadAsync(), Is.True);

            driver.EnqueueConnectionRefused();
            Assert.That(await mapper.ReadAsync(), Is.False);
            Assert.That(mapper.HasConnectionRefusal, Is.True);
            Assert.That(mapper.ConsecutiveReadFailures, Is.EqualTo(1));

            driver.EnqueueSuccess();
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(mapper.HasConnectionRefusal, Is.False);
        }
        finally
        {
            File.Delete(mapperPath);
        }
    }

    [Test]
    public async Task ReadAsync_tolerates_drops_after_a_connection_is_established_and_warns_once_they_repeat()
    {
        var driver = new FakeDriver();
        var mapperPath = WriteMapperFile();
        try
        {
            var mapper = new Mapper(mapperPath, driver);

            driver.EnqueueSuccess();
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(mapper.ConnectionWarning, Is.Null);

            driver.EnqueueTimeout();
            Assert.That(await mapper.ReadAsync(), Is.False);
            Assert.That(mapper.ConnectionWarning, Is.Null, "a single drop shouldn't warn yet");
            Assert.That(mapper.ConsecutiveReadFailures, Is.EqualTo(1));
            Assert.That(mapper.LastReadFailureMessage, Is.EqualTo("no response"));

            driver.EnqueueTimeout();
            Assert.That(await mapper.ReadAsync(), Is.False);
            Assert.That(mapper.ConnectionWarning, Is.Null, "still below the threshold");

            driver.EnqueueTimeout();
            Assert.That(await mapper.ReadAsync(), Is.False);
            Assert.That(mapper.ConnectionWarning, Is.Not.Null, "three drops in a row should warn");

            driver.EnqueueSuccess();
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(mapper.ConnectionWarning, Is.Null, "a recovered read clears the warning");
            Assert.That(mapper.ConsecutiveReadFailures, Is.Zero);
            Assert.That(mapper.LastReadFailureMessage, Is.Null);
        }
        finally
        {
            File.Delete(mapperPath);
        }
    }
}
