using GameHook.Infrastructure;
using GameHook.UI.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameHook.Tests.UI;

public class StartupTests
{
    [Test]
    public void Missing_custom_mapper_directory_leaves_load_screen_available()
    {
        var root = Directory.CreateTempSubdirectory("gamehook-startup-test-").FullName;
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GameHookProfileDirectory"] = root,
                ["MapperDirectory"] = Path.Combine(root, "missing"),
            }).Build();
            using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddGameHook(configuration)
                .AddSingleton<MainWindowViewModel>().BuildServiceProvider();
            var main = services.GetRequiredService<MainWindowViewModel>();
            Assert.That(main.IsLoadScreenVisible, Is.True);
            Assert.That(main.HasError, Is.True);
            Assert.That(main.Mappers, Is.Empty);
        }
        finally { Directory.Delete(root, true); }
    }
}
