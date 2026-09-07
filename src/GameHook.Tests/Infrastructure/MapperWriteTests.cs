using GameHook.Domain.Interface;
using GameHook.Infrastructure;

namespace GameHook.Tests.Infrastructure;

public class MapperWriteTests
{
    // Records every write and serves reads from a single mutable byte store, so tests can assert
    // on what actually ended up in "device memory" after a sequence of writes.
    private sealed class FakeWritableDriver : IDriver
    {
        private readonly Dictionary<(string RegionId, ulong Address), byte> memory = new();
        public List<IDriver.WriteRequest> WriteRequests { get; } = [];

        // Simulates network latency on the write path specifically - long enough that a second
        // WriteAsync call issued before the first completes would race it if Mapper didn't
        // serialize writes.
        public TimeSpan WriteDelay { get; set; } = TimeSpan.Zero;

        public void Seed(string regionId, ulong address, byte value) => memory[(regionId, address)] = value;

        public Task<IDriver.Response> Read(IDriver.Request request)
        {
            var segments = request.Segments.Select(segment =>
            {
                var bytes = new byte[segment.Length];
                for (var i = 0; i < segment.Length; i++)
                {
                    memory.TryGetValue((segment.RegionId, segment.StartingAddress + (ulong)i), out bytes[i]);
                }
                return new IDriver.MemorySegmentSnapshot(segment.RegionId, segment.StartingAddress, bytes);
            }).ToArray();
            return Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow, segments));
        }

        public async Task Write(IDriver.WriteRequest request)
        {
            if (WriteDelay > TimeSpan.Zero) await Task.Delay(WriteDelay);
            WriteRequests.Add(request);
            foreach (var segment in request.Segments)
            {
                for (var i = 0; i < segment.Bytes.Length; i++)
                {
                    memory[(segment.RegionId, segment.StartingAddress + (ulong)i)] = segment.Bytes.Span[i];
                }
            }
        }

        public byte Peek(string regionId, ulong address) => memory.TryGetValue((regionId, address), out var b) ? b : (byte)0;
    }

    private static string WriteNibblePairMapperFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path,
            "<mapper platform=\"GB\"><properties>" +
            "<property name=\"low\" type=\"int\" address=\"0xC000\" length=\"1\" bits=\"0-3\" />" +
            "<property name=\"high\" type=\"int\" address=\"0xC000\" length=\"1\" bits=\"4-7\" />" +
            "</properties></mapper>");
        return path;
    }

    [Test]
    public async Task WriteAsync_round_trips_a_simple_property()
    {
        var driver = new FakeWritableDriver();
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "<mapper platform=\"GB\"><properties><property name=\"hp\" type=\"int\" address=\"0xC000\" length=\"1\" /></properties></mapper>");
        try
        {
            var mapper = new Mapper(path, driver);
            var (success, error) = await mapper.WriteAsync("hp", "42");

            Assert.That(success, Is.True, error);
            Assert.That(driver.Peek("WRAM", 0), Is.EqualTo(42));
            Assert.That(mapper.Properties["hp"].Value, Is.EqualTo(42));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Concurrent_writes_to_sibling_nibbles_do_not_clobber_each_other()
    {
        var driver = new FakeWritableDriver { WriteDelay = TimeSpan.FromMilliseconds(50) };
        var path = WriteNibblePairMapperFile();
        try
        {
            var mapper = new Mapper(path, driver);

            // Fire both writes without awaiting the first - if Mapper didn't serialize through
            // writeGate + the shadow byte cache, "high" would build its merge on the pre-"low"
            // byte and overwrite "low"'s change once its own (slower-issued) write lands.
            var lowTask = mapper.WriteAsync("low", "5");
            var highTask = mapper.WriteAsync("high", "9");
            var results = await Task.WhenAll(lowTask, highTask);

            Assert.That(results, Has.All.Matches<(bool Success, string? Error)>(r => r.Success), string.Join(", ", results.Select(r => r.Error)));
            Assert.That(driver.Peek("WRAM", 0), Is.EqualTo(0x95));
            Assert.That(driver.WriteRequests, Has.Count.EqualTo(2));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task WriteAsync_reports_failure_for_an_unknown_property()
    {
        var driver = new FakeWritableDriver();
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "<mapper platform=\"GB\"><properties><property name=\"hp\" type=\"int\" address=\"0xC000\" length=\"1\" /></properties></mapper>");
        try
        {
            var mapper = new Mapper(path, driver);
            var (success, error) = await mapper.WriteAsync("does_not_exist", "1");

            Assert.That(success, Is.False);
            Assert.That(error, Does.Contain("Unknown property"));
        }
        finally { File.Delete(path); }
    }
}
