using Gamehook.Domain.Interface;
using Gamehook.Domain.Mapping;
using Gamehook.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Gamehook.Tests.Tests;

public sealed class MapperNumericTests
{
    [Test]
    public async Task Out_of_range_expression_results_wrap_and_warn_when_the_data_changes()
    {
        var directory = Directory.CreateTempSubdirectory("gamehook-numeric-");
        try
        {
            var mapperPath = Path.Combine(directory.FullName, "numeric.xml");
            await File.WriteAllTextAsync(mapperPath, """
                <mapper id="test" name="Numeric" platform="GB">
                  <properties>
                    <property name="below_zero" type="uint" address="0xC000" length="4" after-read-value-expression="x - 16843010" />
                    <property name="in_range" type="uint" address="0xC010" length="1" after-read-value-expression="x + 1" />
                  </properties>
                </mapper>
                """);
            var logger = new CapturingLogger();
            var driver = new ZeroMemoryDriver();
            using var mapper = new Mapper(MapperCompiler.Load(mapperPath), driver, logger);

            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(mapper.Properties["below_zero"].Value, Is.EqualTo(4278124286UL));
            Assert.That(mapper.Properties["in_range"].Value, Is.EqualTo(1UL));
            Assert.That(logger.Warnings, Has.Count.EqualTo(1));
            Assert.That(logger.Warnings[0], Does.Contain("below_zero"));

            // Unchanged bytes are not re-evaluated, so nothing new is logged.
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(logger.Warnings, Has.Count.EqualTo(1));

            // Changed bytes re-run the expression: 0x01010101 - 0x01010102 is still out of range, so it warns again.
            driver.Fill = 1;
            Assert.That(await mapper.ReadAsync(), Is.True);
            Assert.That(logger.Warnings, Has.Count.EqualTo(2));
            Assert.That(mapper.Properties["below_zero"].Value, Is.EqualTo(4294967295UL));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Inspector_returns_signed_and_unsigned_readings_of_the_same_bytes()
    {
        var directory = Directory.CreateTempSubdirectory("gamehook-numeric-");
        try
        {
            var mapperPath = Path.Combine(directory.FullName, "numeric.xml");
            await File.WriteAllTextAsync(mapperPath, """
                <mapper id="test" name="Numeric" platform="GB">
                  <properties>
                    <property name="p" type="uint" address="0xC000" length="1" />
                  </properties>
                </mapper>
                """);
            using var mapper = new Mapper(MapperCompiler.Load(mapperPath), new ZeroMemoryDriver());

            var inspections = mapper.Inspect(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            Assert.That(inspections.Single(i => i.Type == "int").Value, Is.EqualTo(-1L));
            Assert.That(inspections.Single(i => i.Type == "uint").Value, Is.EqualTo(4294967295UL));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class CapturingLogger : ILogger<Mapper>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class ZeroMemoryDriver : IDriver
    {
        public byte Fill { get; set; }

        public Task<IDriver.Response> Read(IDriver.Request request) => Task.FromResult(new IDriver.Response(
            DateTimeOffset.UtcNow,
            request.Segments.Select(segment => new IDriver.MemorySegmentSnapshot(
                segment.RegionId, segment.StartingAddress, Enumerable.Repeat(Fill, segment.Length).ToArray())).ToArray()));

        public Task Write(IDriver.WriteRequest request) => Task.CompletedTask;
    }
}
