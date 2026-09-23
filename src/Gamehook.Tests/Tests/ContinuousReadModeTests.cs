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
            using var session = new GamehookSession(new StubMapperFactory(mapper), new StubDriverFactory());
            var router = new GamehookRouter(session);
            var settings = new SettingsService(session, initialContinuousRead: true);
            var api = new GamehookApiHostedService(router, new FilesystemProvider(configuration, directory.FullName, officialMappersEnabled: true), configuration,
                NullLoggerFactory.Instance, new ApiBindStatus(), settings);
            await api.StartAsync(CancellationToken.None);
            try
            {
                Assert.That((await router.LoadAsync("stub.xml", "stub", null)).Success, Is.True);
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

                var initial = await http.GetFromJsonAsync<JsonObject>("/settings/");
                Assert.That((bool)initial!["continuousRead"]!, Is.True);

                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);

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
                    refused.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None));

                var readsBefore = mapper.Reads;
                var properties = await http.GetAsync("/instance/properties/");
                Assert.That(properties.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(mapper.Reads, Is.EqualTo(readsBefore + 1), "GET reads the driver on demand");

                var write = await http.PostAsJsonAsync("/instance/properties/anything", new { value = 1 });
                Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                var rawWrite = await http.PostAsJsonAsync("/driver/RAM", new { address = 0, data = new[] { 1 } });
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

    private sealed class StubMapperFactory(IMapper mapper) : IMapperFactory
    {
        public IMapper Create(string mapperPath, string driverName, string? driverSourcePath = null) => mapper;
    }

    private sealed class StubDriverFactory : IDriverFactory
    {
        public IDriver Create(string name, string? sourcePath = null) => new StubDriver();
    }

    private sealed class StubDriver : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request) =>
            Task.FromResult(new IDriver.Response(DateTimeOffset.UtcNow, []));
    }

    private sealed class CountingMapper : IMapper
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
