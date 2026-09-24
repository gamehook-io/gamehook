using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Gamehook.Domain;
using Gamehook.Domain.Interface;
using Gamehook.Domain.Models;
using Gamehook.Infrastructure;
using Gamehook.RestApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gamehook.Tests.Tests;

public sealed class ContinuousReadModeTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    [Test]
    public async Task Disabling_continuous_read_stops_polling_and_reads_only_on_demand()
    {
        var mapper = new CountingMapper();
        using var session = new GamehookSession(new StubMapperFactory(mapper), new StubDriverFactory());
        Assert.That(await session.LoadAsync("stub.xml", "stub", null), Is.True);

        session.SetContinuousRead(false);
        session.StartPolling(PollInterval);
        var readsAfterLoad = mapper.Reads;
        await Task.Delay(100);
        Assert.That(mapper.Reads, Is.EqualTo(readsAfterLoad), "no background reads while continuous read mode is off");

        Assert.That(await session.ReadOnDemandAsync(), Is.True);
        Assert.That(mapper.Reads, Is.EqualTo(readsAfterLoad + 1));

        session.SetContinuousRead(true);
        await WaitUntilAsync(() => mapper.Reads > readsAfterLoad + 3);
    }

    [Test]
    public async Task Router_load_starts_polling_for_any_host()
    {
        var mapper = new CountingMapper();
        using var session = new GamehookSession(new StubMapperFactory(mapper), new StubDriverFactory());
        var router = new GamehookRouter(session);

        Assert.That((await router.LoadAsync("stub.xml", "stub", null)).Success, Is.True);
        var readsAfterLoad = mapper.Reads;
        await WaitUntilAsync(() => mapper.Reads > readsAfterLoad + 3);
    }

    [Test]
    public async Task Reenabling_continuous_read_resumes_polling_after_a_read_error()
    {
        var mapper = new CountingMapper();
        using var session = new GamehookSession(new StubMapperFactory(mapper), new StubDriverFactory());
        Assert.That(await session.LoadAsync("stub.xml", "stub", null), Is.True);

        mapper.FailNextRead = true;
        session.StartPolling(PollInterval);
        await WaitUntilAsync(() => session.Status.StartsWith("Error:", StringComparison.Ordinal));
        var readsAfterError = mapper.Reads;
        await Task.Delay(100);
        Assert.That(mapper.Reads, Is.EqualTo(readsAfterError), "a failed read stops the loop");

        session.SetContinuousRead(false);
        session.SetContinuousRead(true);
        await WaitUntilAsync(() => mapper.Reads > readsAfterError + 3);
        Assert.That(session.IsConnected, Is.True);
    }

    [Test]
    public async Task Router_refuses_every_write_path_while_continuous_read_is_off()
    {
        var mapper = new CountingMapper();
        using var session = new GamehookSession(new StubMapperFactory(mapper), new StubDriverFactory());
        var router = new GamehookRouter(session);
        Assert.That((await router.LoadAsync("stub.xml", "stub", null)).Success, Is.True);

        session.SetContinuousRead(false);
        Assert.That(await router.WritePropertyValueAsync("anything", 1),
            Is.EqualTo((false, GamehookRouter.ContinuousReadDisabledWriteError)));
        Assert.That(await router.WriteDriverRegionAsync("System RAM", 0, new byte[] { 1 }),
            Is.EqualTo((false, GamehookRouter.ContinuousReadDisabledWriteError)));
        Assert.That(mapper.Writes, Is.Zero);

        session.SetContinuousRead(true);
        Assert.That((await router.WriteDriverRegionAsync("System RAM", 0, new byte[] { 1 })).Success, Is.True);
        Assert.That(mapper.Writes, Is.EqualTo(1));
    }

    [Test]
    public async Task Settings_endpoint_changes_continuous_read_for_the_session_and_gates_api()
    {
        var directory = Directory.CreateTempSubdirectory("gamehook-settings-");
        try
        {
            var port = GetFreePort();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Port"] = port.ToString(),
                })
                .Build();

            var mapper = new CountingMapper();
            using var instances = new GamehookInstances(() => new GamehookSession(new StubMapperFactory(mapper), new StubDriverFactory()));
            Assert.That(instances.TryGet(0, out var router), Is.True);
            var session = router.Session;
            var settings = new SettingsService(instances, initialContinuousRead: true);
            var api = new GamehookApiHostedService(instances, [], new FilesystemProvider(configuration, directory.FullName, officialMappersEnabled: true), configuration,
                NullLoggerFactory.Instance, new ApiBindStatus(), settings);
            await api.StartAsync(CancellationToken.None);
            try
            {
                Assert.That((await router.LoadAsync("stub.xml", "stub", null)).Success, Is.True);
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

                var initial = await http.GetFromJsonAsync<JsonObject>("/settings/");
                Assert.That((bool)initial!["continuousRead"]!, Is.True);

                // Freeze the poll loop so the read count is stable, then check ?read=true is a no-op
                // while continuous read mode is on.
                session.StopPolling();
                // Waits out any poll read still in flight (it shares the read gate) before counting.
                await session.ReadOnDemandAsync();
                var readsWhileOn = mapper.Reads;
                var ignoredRead = await http.GetAsync("/instances/0/properties?read=true");
                Assert.That(ignoredRead.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(mapper.Reads, Is.EqualTo(readsWhileOn), "?read=true is ignored while continuous read mode is on");

                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/instances/0/ws"), CancellationToken.None);

                var disabled = await http.PostAsJsonAsync("/settings/", new { continuousRead = false });
                Assert.That(disabled.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(session.IsContinuousReadEnabled, Is.False);

                // An open socket gets a close frame rather than silently going quiet.
                var buffer = new byte[256];
                using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var frame = await socket.ReceiveAsync(buffer, receiveTimeout.Token);
                Assert.That(frame.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                Assert.That(socket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));

                using var refused = new ClientWebSocket();
                Assert.ThrowsAsync<WebSocketException>(() =>
                    refused.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/instances/0/ws"), CancellationToken.None));

                var readsBefore = mapper.Reads;
                var properties = await http.GetAsync("/instances/0/properties");
                Assert.That(properties.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(mapper.Reads, Is.EqualTo(readsBefore), "a plain GET returns last-read values without reading");

                var readProperties = await http.GetAsync("/instances/0/properties?read=true");
                Assert.That(readProperties.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(mapper.Reads, Is.EqualTo(readsBefore + 1), "?read=true reads the driver first");

                var write = await http.PostAsJsonAsync("/instances/0/properties/anything", new { value = 1 });
                Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                var rawWrite = await http.PostAsJsonAsync("/instances/0/driver/RAM", new { address = 0, data = new[] { 1 } });
                Assert.That(rawWrite.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

                var enabled = await http.PostAsJsonAsync("/settings", new { continuousRead = true });
                Assert.That((bool)(await enabled.Content.ReadFromJsonAsync<JsonObject>())!["continuousRead"]!, Is.True);
                Assert.That(session.IsContinuousReadEnabled, Is.True);
                Assert.That(Directory.EnumerateFileSystemEntries(directory.FullName), Is.Empty,
                    "settings changes are session-only; nothing is written to the profile");
            }
            finally
            {
                await api.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Instances_are_independent_and_addressed_by_index()
    {
        var directory = Directory.CreateTempSubdirectory("gamehook-instances-");
        try
        {
            var port = GetFreePort();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Port"] = port.ToString() })
                .Build();

            using var instances = new GamehookInstances(() => new GamehookSession(new StubMapperFactory(new CountingMapper()), new StubDriverFactory()));
            var settings = new SettingsService(instances, initialContinuousRead: true);
            var api = new GamehookApiHostedService(instances, [], new FilesystemProvider(configuration, directory.FullName, officialMappersEnabled: true), configuration,
                NullLoggerFactory.Instance, new ApiBindStatus(), settings);
            await api.StartAsync(CancellationToken.None);
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                Assert.That((await http.GetFromJsonAsync<JsonArray>("/instances"))!.Count, Is.EqualTo(1));

                var created = await http.PostAsync("/instances", null);
                Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That((int)(await created.Content.ReadFromJsonAsync<JsonObject>())!["index"]!, Is.EqualTo(1));
                Assert.That(created.Headers.Location?.ToString(), Is.EqualTo("/instances/1"));

                Assert.That(instances.TryGet(1, out var second), Is.True);
                Assert.That((await second.LoadAsync("stub.xml", "stub", null)).Success, Is.True);

                var listed = (await http.GetFromJsonAsync<JsonArray>("/instances"))!;
                Assert.That(listed.Count, Is.EqualTo(2));
                Assert.That((bool)listed[0]!["connected"]!, Is.False, "loading instance 1 leaves instance 0 alone");
                Assert.That((bool)listed[1]!["connected"]!, Is.True);

                Assert.That((await http.GetAsync("/instances/1/properties")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                var unloaded = await http.GetAsync("/instances/0/properties");
                Assert.That(unloaded.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((string)(await unloaded.Content.ReadFromJsonAsync<JsonObject>())!["code"]!, Is.EqualTo("mapper_not_loaded"));
                var missing = await http.GetAsync("/instances/5/properties");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((string)(await missing.Content.ReadFromJsonAsync<JsonObject>())!["code"]!, Is.EqualTo("instance_not_found"));

                // Removing an instance closes its WebSocket and shifts later indexes down.
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/instances/1/ws"), CancellationToken.None);
                Assert.That((await http.DeleteAsync("/instances/1")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var frame = await socket.ReceiveAsync(new byte[256], receiveTimeout.Token);
                Assert.That(frame.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                Assert.That(second.Mapper, Is.Null, "a removed instance is unloaded");

                Assert.That((await http.GetFromJsonAsync<JsonArray>("/instances"))!.Count, Is.EqualTo(1));
                Assert.That((await http.GetAsync("/instances/1")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((await http.DeleteAsync("/instances/0")).StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
                    "the last instance cannot be removed");

                // Continuous read mode is global: it reaches instances added after the change.
                await http.PostAsJsonAsync("/settings", new { continuousRead = false });
                await http.PostAsync("/instances", null);
                Assert.That(instances.TryGet(1, out var third), Is.True);
                Assert.That(third.Session.IsContinuousReadEnabled, Is.False);
            }
            finally
            {
                await api.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    internal sealed class StubMapperFactory(IMapper mapper) : IMapperFactory
    {
        public IMapper Create(string mapperPath, string driverName, string? driverSourcePath = null) => mapper;
    }

    internal sealed class StubDriverFactory : IDriverFactory
    {
        public IDriver Create(string name, string? sourcePath = null) => new StubDriver();
    }

    internal sealed class StubDriver : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request) =>
            Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow, []));
    }

    internal sealed class CountingMapper : IMapper
    {
        private int reads;
        private int writes;

        public int Reads => Volatile.Read(ref reads);
        public int Writes => Volatile.Read(ref writes);
        public GameSystem System => GameSystem.NES;
        public string GameName => "Stub";
        public Dictionary<string, IProperty> Properties { get; } = [];
        public IReadOnlyDictionary<string, ReferenceTable> References { get; } = new Dictionary<string, ReferenceTable>();
        public ReadMetrics LastReadMetrics { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        public string? ConnectionWarning => null;
        public int ConsecutiveReadFailures => 0;
        public string? LastReadFailureMessage => null;
        public bool HasConnectionRefusal => false;
        public IReadOnlyList<IDriver.MemorySegmentSnapshot> LastMemorySegments => [];

        public IReadOnlyList<PropertyInspection> Inspect(ReadOnlyMemory<byte> bytes) => [];

        public volatile bool FailNextRead;

        public Task<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new IOException("Simulated driver failure.");
            }
            return Task.FromResult(true);
        }

        public Task<(bool Success, string? Error)> WriteAsync(string propertyPath, object? value, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult<(bool, string?)>((true, null));
        }

        public Task<(bool Success, string? Error)> WriteRawBytesAsync(string regionId, ulong startingAddress, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult<(bool, string?)>((true, null));
        }

        public async IAsyncEnumerable<bool> ReadContinuouslyAsync(TimeSpan interval, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
