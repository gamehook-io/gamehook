using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Property;
using Gamehook.Infrastructure;

namespace Gamehook.Tests.Infrastructure;

// Not an assertion test - a stopwatch. The read loop runs at 60 Hz against a live emulator, so the
// whole cycle has a single-digit-millisecond budget; this reports where a mapper's read time
// actually goes so a regression shows up as a number rather than as a laggy UI.
//
//   dotnet test -c Release --filter "FullyQualifiedName~MapperReadBenchmark" -l "console;verbosity=detailed"
//
// Release matters: a Debug run measures the JIT's unoptimised output and is off by several times.
//
// The two phases below are timed directly rather than through ReadAsync, because they are the two
// that scale with property count and they are measurable for every mapper. A full read is only
// timed for mappers whose companion script survives synthetic memory - most do not, since their
// preprocessors decode zeroed RAM as if it were a real save.
[Explicit("Performance measurement, not a pass/fail test - run it deliberately.")]
public class MapperReadBenchmark : BaseTest
{
    private const int WarmupIterations = 10;
    private const int MeasuredIterations = 100;

    private sealed class ZeroDriver : IDriver
    {
        public IDriver.Response? Last;

        public Task<IDriver.Response> Read(IDriver.Request request)
        {
            Last = new IDriver.Response(DateTimeOffset.UtcNow, request.Segments
                .Select(s => new IDriver.MemorySegmentSnapshot(s.RegionId, s.StartingAddress, new byte[s.Length]))
                .ToArray());
            return Task.FromResult(Last);
        }
    }

    [TestCaseSource(nameof(MapperFiles))]
    public async Task Measure(string mapperPath)
    {
        var driver = new ZeroDriver();
        var loadTimer = Stopwatch.StartNew();
        Mapper mapper;
        try
        {
            mapper = new Mapper(mapperPath, driver);
        }
        catch (NotSupportedException ex)
        {
            Assert.Ignore($"{Path.GetFileName(mapperPath)}: {ex.Message}");
            return;
        }
        var load = loadTimer.Elapsed;
        try
        {
            // Seeds the script's runtime variables so the deferred addresses below resolve. Most
            // companion scripts choke part-way through on synthetic memory, which is fine: by then
            // they have set the pointers, and the phases measured here don't depend on the rest.
            await mapper.ReadAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            TestContext.Out.WriteLine($"{Path.GetFileName(mapperPath)}: script stopped early on synthetic memory ({ex.Message})");
        }

        var properties = (IReadOnlyList<Property>)Field(mapper, "compiledProperties");
        var references = (IReadOnlyDictionary<string, ReferenceTable>)Field(mapper, "references");
        var deferred = ((ICollection)Field(mapper, "dynamicAddressProperties")).Count;
        var resolveDynamicAddresses = typeof(Mapper)
            .GetMethod("ResolveDynamicAddresses", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var applyExpressions = typeof(Mapper)
            .GetMethod("ApplyExpressions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var expressions = ((ICollection)Field(mapper, "expressionBindings")).Count;
        var segments = driver.Last!.Segments;

        var resolveMs = deferred == 0 ? 0 : Measure(() => resolveDynamicAddresses.Invoke(mapper, null));
        var expressionMs = expressions == 0 ? 0 : Measure(() => applyExpressions.Invoke(mapper, null));
        // A property the aborted script left pointing at a junk address throws here; that is a
        // property of the synthetic memory, so drop it from the set being timed rather than the
        // whole mapper - the remaining thousands still give a representative decode cost.
        var decodable = properties.Where(property =>
        {
            try
            {
                property.Refresh(segments, references);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }).ToArray();

        var decodeMs = Measure(() =>
        {
            for (var index = 0; index < decodable.Length; index++) decodable[index].Refresh(segments, references);
        });

        var skipped = properties.Count - decodable.Length;
        TestContext.Out.WriteLine(
            $"{Path.GetFileName(mapperPath),-34} load={load.TotalMilliseconds,7:0.0}ms " +
            $"properties={decodable.Length,5}{(skipped == 0 ? "     " : $"(-{skipped,3})")} " +
            $"deferredAddresses={deferred,5} expressions={expressions,4} " +
            $"| resolveAddresses={resolveMs,7:0.000}ms  decodeProperties={decodeMs,7:0.000}ms " +
            $"applyExpressions={expressionMs,7:0.000}ms | {await MeasureFullReadAsync(mapperPath)}");
    }

    // Bytes every property type can decode: each nibble stays 0-9, so binaryCodedDecimal properties
    // are happy, and the pattern shifts every read so nothing short-circuits on unchanged memory the
    // way a zero-filled buffer would - this is the closest stand-in for a live emulator we have.
    private sealed class RotatingDriver : IDriver
    {
        private int tick;

        public Task<IDriver.Response> Read(IDriver.Request request)
        {
            tick++;
            var segments = request.Segments.Select(segment =>
            {
                var bytes = new byte[segment.Length];
                for (var index = 0; index < bytes.Length; index++)
                {
                    var value = (index + tick) % 100;
                    bytes[index] = (byte)(((value / 10) << 4) | (value % 10));
                }
                return new IDriver.MemorySegmentSnapshot(segment.RegionId, segment.StartingAddress, bytes);
            }).ToArray();
            return Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow, segments));
        }
    }

    private static async Task<string> MeasureFullReadAsync(string mapperPath)
    {
        var mapper = new Mapper(mapperPath, new RotatingDriver());
        try
        {
            for (var i = 0; i < WarmupIterations; i++) await mapper.ReadAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return $"fullRead: n/a ({ex.Message})";
        }

        double total = 0, driverTime = 0, translation = 0, inline = 0, script = 0;
        for (var i = 0; i < MeasuredIterations; i++)
        {
            await mapper.ReadAsync();
            var metrics = mapper.LastReadMetrics;
            total += metrics.Total.TotalMilliseconds;
            driverTime += metrics.Driver.TotalMilliseconds;
            translation += metrics.PropertyTranslation.TotalMilliseconds;
            inline += metrics.InlineCalculations.TotalMilliseconds;
            script += metrics.Postprocessor.TotalMilliseconds;
        }

        return $"fullRead={total / MeasuredIterations,7:0.000}ms (driver={driverTime / MeasuredIterations:0.000} " +
            $"translation={translation / MeasuredIterations:0.000} inline={inline / MeasuredIterations:0.000} " +
            $"script={script / MeasuredIterations:0.000})";
    }

    private static double Measure(Action action)
    {
        for (var i = 0; i < WarmupIterations; i++) action();
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < MeasuredIterations; i++) action();
        return timer.Elapsed.TotalMilliseconds / MeasuredIterations;
    }

    private static object Field(Mapper mapper, string name) =>
        typeof(Mapper).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mapper)!;

    private static IEnumerable<TestCaseData> MapperFiles() =>
        AllMapperFiles.Select(path => new TestCaseData(path).SetArgDisplayNames(Path.GetFileName(path)));
}
