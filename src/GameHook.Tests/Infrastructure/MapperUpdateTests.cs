using System.IO.Compression;
using System.Net;
using GameHook.Infrastructure;
using GameHook.Infrastructure.MapperUpdate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHook.Tests.Infrastructure;

public class MapperUpdateTests
{
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Directory.CreateTempSubdirectory("gamehook-update-test-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [TestCase(true)]
    [TestCase(false)]
    public async Task Archive_is_validated_before_replacing_installed_mappers(bool valid)
    {
        var mapperDirectory = Directory.CreateDirectory(Path.Combine(root, "mappers")).FullName;
        var oldPath = Path.Combine(mapperDirectory, "old.xml");
        File.WriteAllText(oldPath, "old content");
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(valid ? "repo/dist/gb/test.xml" : "repo/README.md");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("new content");
        }
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archiveBytes.ToArray()),
        }));
        var (service, status) = Create(client);
        await service.CheckForUpdatesAsync(CancellationToken.None);
        if (valid)
        {
            Assert.That(File.Exists(oldPath), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(mapperDirectory, "gb", "test.xml")), Is.EqualTo("new content"));
            Assert.That(service.GetInstalledManifest()?.CommitSha, Is.EqualTo(new string('a', 40)));
        }
        else
        {
            Assert.That(File.ReadAllText(oldPath), Is.EqualTo("old content"));
            Assert.That(status.Current.State, Is.EqualTo(MapperUpdateState.Failed));
        }
        Assert.That(Directory.GetDirectories(root, "*.staging-*"), Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(root, "mappers.previous")), Is.False);
    }

    [Test]
    public async Task Network_timeout_does_not_abort_application_startup()
    {
        using var client = new HttpClient(new Handler(_ => throw new TaskCanceledException("Timed out")));
        var (service, status) = Create(client);
        await service.StartAsync(CancellationToken.None);
        Assert.That(status.Current.State, Is.EqualTo(MapperUpdateState.Failed));
    }

    [Test]
    public async Task Interrupted_directory_swap_recovers_previous_installation_offline()
    {
        var previous = Directory.CreateDirectory(Path.Combine(root, "mappers.previous")).FullName;
        File.WriteAllText(Path.Combine(previous, "working.xml"), "working content");
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException("Offline")));
        var (service, _) = Create(client);
        await service.StartAsync(CancellationToken.None);
        Assert.That(File.ReadAllText(Path.Combine(root, "mappers", "working.xml")), Is.EqualTo("working content"));
        Assert.That(Directory.Exists(previous), Is.False);
    }

    [Test]
    public async Task Invalid_pinned_commit_is_rejected_before_download()
    {
        using var client = new HttpClient(new Handler(_ => throw new AssertionException("Unexpected download")));
        var (service, status) = Create(client, "main/../../other");
        await service.StartAsync(CancellationToken.None);
        Assert.That(status.Current.State, Is.EqualTo(MapperUpdateState.Failed));
        Assert.That(status.Current.Message, Does.Contain("40-character"));
    }

    private (MapperUpdateService, MapperUpdateStatusProvider) Create(HttpClient client, string? commit = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GameHookProfileDirectory"] = root,
            ["Mapper:Commit"] = commit ?? new string('a', 40),
        }).Build();
        var status = new MapperUpdateStatusProvider();
        return (new MapperUpdateService(configuration, new FilesystemProvider(configuration),
            new ClientFactory(client), status, NullLogger<MapperUpdateService>.Instance), status);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
