using GameHook.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace GameHook.Tests.Infrastructure;

public class FilesystemProviderTests
{
    [Test]
    public void GetMappers_returns_xml_files_recursively_with_relative_keys()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            var nestedDirectory = Directory.CreateDirectory(Path.Combine(root, "GB"));
            File.WriteAllText(Path.Combine(root, "ignore.txt"), "");
            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "pokemon.XML"), "");

            var provider = new FilesystemProvider(CreateConfiguration("MapperDirectory", root));

            var mappers = provider.GetMappers();

            Assert.That(mappers, Has.Count.EqualTo(1));
            Assert.That(mappers[Path.Combine("GB", "pokemon.XML")], Is.EqualTo(Path.Combine(nestedDirectory.FullName, "pokemon.XML")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetSaveStates_returns_state_files_recursively()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            var statePath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "GB")).FullName, "blue.state");
            File.WriteAllText(statePath, "");

            var provider = new FilesystemProvider(CreateConfiguration("SaveStateDirectory", root));

            Assert.That(provider.GetSaveStates(), Is.EqualTo(new Dictionary<string, string>
            {
                [Path.Combine("GB", "blue.state")] = statePath
            }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetMappers_falls_back_to_default_directory_when_unconfigured()
    {
        var profileDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = new FilesystemProvider(CreateConfiguration("GameHookProfileDirectory", profileDirectory));
            var expectedMapperDirectory = Path.Combine(profileDirectory, "mappers");

            Assert.That(provider.GetMappers(), Is.Empty);
            Assert.That(provider.GetMapperDirectory(), Is.EqualTo(expectedMapperDirectory));
            Assert.That(Directory.Exists(expectedMapperDirectory), Is.True);
        }
        finally
        {
            Directory.Delete(profileDirectory, recursive: true);
        }
    }

    [Test]
    public void Last_opened_save_state_directory_persists_in_profile()
    {
        var root = CreateTemporaryDirectory();
        var saveStateDirectory = Directory.CreateDirectory(Path.Combine(root, "states")).FullName;

        try
        {
            var configuration = CreateConfiguration("GameHookProfileDirectory", Path.Combine(root, "profile"));
            var writer = new FilesystemProvider(configuration);

            Assert.That(writer.RememberLastOpenedSaveStateDirectory(saveStateDirectory), Is.True);
            Assert.That(writer.RememberLastOpenedMapperDirectory(root), Is.True);

            var reader = new FilesystemProvider(configuration);
            Assert.That(reader.GetLastOpenedSaveStateDirectory(), Is.EqualTo(saveStateDirectory));
            Assert.That(reader.GetLastOpenedMapperDirectory(), Is.EqualTo(root));
            Assert.That(File.Exists(Path.Combine(reader.GameHookProfileDirectory, "last_opened.json")), Is.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Last_opened_save_state_directory_ignores_invalid_profile()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            File.WriteAllText(Path.Combine(root, "last_opened.json"), "not json");
            var provider = new FilesystemProvider(CreateConfiguration("GameHookProfileDirectory", root));

            Assert.That(provider.GetLastOpenedSaveStateDirectory(), Is.Null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IConfiguration CreateConfiguration(string key, string value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
        .Build();

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        return Directory.CreateDirectory(directory).FullName;
    }
}
