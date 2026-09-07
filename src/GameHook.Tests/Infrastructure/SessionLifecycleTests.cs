using GameHook.Domain;
using GameHook.Domain.Interface;
using GameHook.Infrastructure;

namespace GameHook.Tests.Infrastructure;

public class SessionLifecycleTests
{
    private string root = null!;
    private string mapperPath = null!;

    [SetUp]
    public void SetUp()
    {
        root = Directory.CreateTempSubdirectory("gamehook-session-test-").FullName;
        mapperPath = Path.Combine(root, "mapper.xml");
        File.WriteAllText(mapperPath, """
            <mapper platform="GB"><properties><property name="value" address="0xC000" /></properties></mapper>
            """);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, true);

    [Test]
    public async Task Unload_during_read_does_not_restore_stale_status_and_disposes_driver()
    {
        var driver = new ControlledDriver();
        using var session = new GameHookSession(new Factory(driver), new UnusedFactory());
        Assert.That(await session.LoadAsync(mapperPath, "test", null), Is.True);
        Assert.That(session.HexDriver, Is.SameAs(driver));
        driver.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = session.RefreshAsync();
        session.Unload();
        Assert.That(driver.Disposed.Task.IsCompleted, Is.False);
        driver.Pending.SetResult(ControlledDriver.Response());
        Assert.That(await refresh, Is.False);
        await driver.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(session.Mapper, Is.Null);
        Assert.That(session.Status, Is.EqualTo("Choose a driver and mapper to load."));
    }

    [Test]
    public async Task Cancelled_refresh_waits_for_driver_and_does_not_become_an_error()
    {
        var driver = new ControlledDriver();
        using var session = new GameHookSession(new Factory(driver), new UnusedFactory());
        await session.LoadAsync(mapperPath, "test", null);
        using var cancellation = new CancellationTokenSource();
        driver.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = session.RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.That(refresh.IsCompleted, Is.False);
        driver.Pending.SetResult(ControlledDriver.Response());
        Assert.That(await refresh, Is.False);
        Assert.That(session.Status, Does.Not.StartWith("Error:"));
    }

    [Test]
    public async Task Invalid_xml_is_reported_as_load_error()
    {
        File.WriteAllText(mapperPath, "<mapper>");
        using var session = new GameHookSession(new Factory(new ControlledDriver()), new UnusedFactory());
        Assert.That(await session.LoadAsync(mapperPath, "test", null), Is.False);
        Assert.That(session.IsConnecting, Is.False);
        Assert.That(session.Status, Does.StartWith("Error:"));
    }

    private sealed class Factory(ControlledDriver driver) : IMapperFactory
    {
        public IMapper Create(string mapperPath, string driverName, string? driverSourcePath = null) => new Mapper(mapperPath, driver);
    }

    private sealed class UnusedFactory : IDriverFactory
    {
        public IDriver Create(string name, string? sourcePath = null) => throw new AssertionException("Driver must be shared.");
    }

    private sealed class ControlledDriver : IDriver, IDisposable
    {
        public TaskCompletionSource<IDriver.Response>? Pending { get; set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IDriver.Response> Read(IDriver.Request request) => Pending?.Task ?? Task.FromResult(Response());
        public static IDriver.Response Response() => new(DateTimeOffset.UtcNow, [new("WRAM", 0, new byte[] { 42 })]);
        public void Dispose() => Disposed.TrySetResult();
    }
}
