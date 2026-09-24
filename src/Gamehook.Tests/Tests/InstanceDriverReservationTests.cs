using Gamehook.Domain;
using Gamehook.Infrastructure;
using static Gamehook.Tests.Tests.ContinuousReadModeTests;

namespace Gamehook.Tests.Tests;

public sealed class InstanceDriverReservationTests
{
    private static readonly DriverRegistration[] Registrations =
    [
        new("RetroArch", (_, _) => new StubDriver(), 55355),
        new("SuperShuckie", (_, _) => new StubDriver(), 55356),
        new("Save State", (_, _) => new StubDriver()),
    ];

    private static GamehookInstances CreateInstances(List<CountingMapper>? mappers = null) => new(
        () =>
        {
            var mapper = new CountingMapper();
            mappers?.Add(mapper);
            return new GamehookSession(new StubMapperFactory(mapper), new StubDriverFactory());
        },
        (driverName, sourcePath) => DriverResources.Identify(Registrations, driverName, sourcePath));

    private static (GamehookRouter First, GamehookRouter Second) TwoInstances(GamehookInstances instances)
    {
        instances.Add();
        instances.TryGet(0, out var first);
        instances.TryGet(1, out var second);
        return (first, second);
    }

    [TestCase("RetroArch", null)]
    [TestCase("RetroArch", "127.0.0.1:55355")]
    [TestCase("RetroArch", "localhost")]
    [TestCase("SuperShuckie", "127.0.0.1:55355")]
    public async Task Second_instance_cannot_load_an_endpoint_already_in_use(string driver, string? source)
    {
        using var instances = CreateInstances();
        var (first, second) = TwoInstances(instances);
        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.True);

        var (success, error) = await second.LoadAsync("stub.xml", driver, source);

        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("127.0.0.1:55355").And.Contain("instance 0"));
        Assert.That(second.Mapper, Is.Null);
        Assert.That(second.DriverName, Is.Null, "a refused load changes nothing");
        Assert.That(first.Mapper, Is.Not.Null, "the holder keeps running");
    }

    [Test]
    public async Task Different_ports_and_reloading_your_own_endpoint_are_allowed()
    {
        using var instances = CreateInstances();
        var (first, second) = TwoInstances(instances);
        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.True);
        Assert.That((await second.LoadAsync("stub.xml", "RetroArch", "127.0.0.1:55400")).Success, Is.True);
        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", "127.0.0.1:55355")).Success, Is.True);
    }

    [Test]
    public async Task Same_save_state_file_cannot_be_shared()
    {
        using var instances = CreateInstances();
        var (first, second) = TwoInstances(instances);
        var path = Path.Combine(Path.GetTempPath(), "shared.state");
        Assert.That((await first.LoadAsync("stub.xml", "Save State", path)).Success, Is.True);
        Assert.That((await second.LoadAsync("stub.xml", "Save State", path)).Success, Is.False);
        Assert.That((await second.LoadAsync("stub.xml", "Save State", path + "2")).Success, Is.True);
    }

    [Test]
    public async Task Selecting_a_taken_driver_without_a_mapper_is_refused()
    {
        using var instances = CreateInstances();
        var (first, second) = TwoInstances(instances);
        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.True);

        Assert.That((await second.SetDriverAsync("RetroArch", null)).Success, Is.False);
        Assert.That(second.DriverName, Is.Null);
        Assert.That((await second.SetDriverAsync("RetroArch", "127.0.0.1:55400")).Success, Is.True);
    }

    [Test]
    public async Task Unload_and_failed_reloads_keep_the_endpoint_until_removal()
    {
        var mappers = new List<CountingMapper>();
        using var instances = CreateInstances(mappers);
        var (first, second) = TwoInstances(instances);

        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.True);
        first.Unload();
        Assert.That((await second.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.False, "unload keeps the endpoint");

        mappers[0].FailNextRead = true;
        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.False);
        Assert.That((await second.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.False, "a failed reload keeps it");

        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", "127.0.0.1:55400")).Success, Is.True);
        Assert.That((await second.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.True, "moving to another endpoint frees the old one");

        Assert.That(instances.Remove(1, out _), Is.True);
        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.True, "removal releases");
    }

    [Test]
    public async Task A_failed_first_connection_does_not_hold_the_endpoint()
    {
        var mappers = new List<CountingMapper>();
        using var instances = CreateInstances(mappers);
        var (first, second) = TwoInstances(instances);

        mappers[0].FailNextRead = true;
        Assert.That((await first.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.False);
        Assert.That((await second.LoadAsync("stub.xml", "RetroArch", null)).Success, Is.True);
    }
}
