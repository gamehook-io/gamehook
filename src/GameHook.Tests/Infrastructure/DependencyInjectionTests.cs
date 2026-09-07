using GameHook.Domain;
using GameHook.Domain.Interface;
using GameHook.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameHook.Tests.Infrastructure;

public class DependencyInjectionTests
{
    [Test]
    public void DriverFactory_creates_runtime_selected_custom_driver()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["GameHookProfileDirectory"] = root })
                .Build();

            using var services = new ServiceCollection()
                .AddGameHook(configuration)
                .AddGameHookDriver("test", (_, _) => new TestDriver())
                .BuildServiceProvider();

            var driver = services.GetRequiredService<IDriverFactory>().Create("test");

            Assert.That(driver, Is.TypeOf<TestDriver>());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class TestDriver : IDriver
    {
        public Task<IDriver.Response> Read(IDriver.Request request) => throw new NotSupportedException();
    }
}
