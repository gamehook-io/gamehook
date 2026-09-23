using Gamehook.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Gamehook.Tests.Tests;

public sealed class CustomMapperDirectoryTests
{
    private DirectoryInfo profile = null!;

    [SetUp]
    public void SetUp() => profile = Directory.CreateTempSubdirectory("gamehook-profile-");

    [TearDown]
    public void TearDown() => profile.Delete(recursive: true);

    private FilesystemProvider CreateProvider(string? mapperDirectory = null, bool officialMappersEnabled = true) => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MapperDirectory"] = mapperDirectory })
            .Build(),
        profile.FullName,
        officialMappersEnabled);

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<mapper />");
    }

    [Test]
    public void EnsureUserMapperDirectory_creates_empty_user_folder()
    {
        CreateProvider().EnsureUserMapperDirectory();

        Assert.That(Directory.Exists(Path.Combine(profile.FullName, "user-mappers")), Is.True);
    }

    [Test]
    public void EnsureUserMapperDirectory_moves_legacy_managed_install_to_official_folder()
    {
        var legacy = Path.Combine(profile.FullName, "mappers");
        WriteFile(Path.Combine(legacy, "gb", "official.xml"));
        File.WriteAllText(Path.Combine(legacy, ".mapper-manifest.json"), "{}");

        CreateProvider().EnsureUserMapperDirectory();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(profile.FullName, "official-mappers", "gb", "official.xml")), Is.True);
            Assert.That(Directory.Exists(legacy), Is.False);
            Assert.That(Directory.Exists(Path.Combine(profile.FullName, "user-mappers")), Is.True);
        });
    }

    [Test]
    public void EnsureUserMapperDirectory_leaves_unmanaged_legacy_folder_alone()
    {
        var file = Path.Combine(profile.FullName, "mappers", "mine.xml");
        WriteFile(file);

        CreateProvider().EnsureUserMapperDirectory();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(file), Is.True);
            Assert.That(Directory.Exists(Path.Combine(profile.FullName, "official-mappers")), Is.False);
        });
    }

    [Test]
    public void GetMappers_lists_official_then_prefixed_user_mappers()
    {
        var official = Path.Combine(profile.FullName, "official-mappers", "gb", "official.xml");
        var user = Path.Combine(profile.FullName, "user-mappers", "gb", "mine.xml");
        WriteFile(official);
        WriteFile(user);
        var provider = CreateProvider();

        var mappers = provider.GetMappers();

        Assert.Multiple(() =>
        {
            Assert.That(mappers, Has.Count.EqualTo(2));
            Assert.That(mappers[Path.Combine("gb", "official.xml")], Is.EqualTo(new MapperFile(official, IsCustom: false)));
            Assert.That(mappers["custom/gb/mine.xml"], Is.EqualTo(new MapperFile(user, IsCustom: true)));
            Assert.That(provider.IsCustomMapperPath(official), Is.False);
            Assert.That(provider.IsCustomMapperPath(user), Is.True);
            Assert.That(provider.IsCustomMapperPath(Path.Combine(Path.GetTempPath(), "elsewhere.xml")), Is.True);
        });
    }

    [Test]
    public void GetMappers_adds_configured_mapper_directory_alongside_user_mappers()
    {
        var official = Path.Combine(profile.FullName, "official-mappers", "gb", "official.xml");
        var user = Path.Combine(profile.FullName, "user-mappers", "gb", "mine.xml");
        var extraDirectory = Path.Combine(profile.FullName, "extra");
        var extra = Path.Combine(extraDirectory, "nes", "extra.xml");
        var duplicate = Path.Combine(extraDirectory, "gb", "mine.xml");
        WriteFile(official);
        WriteFile(user);
        WriteFile(extra);
        WriteFile(duplicate);

        var mappers = CreateProvider(extraDirectory).GetMappers();

        Assert.Multiple(() =>
        {
            Assert.That(mappers, Has.Count.EqualTo(3));
            Assert.That(mappers["custom/nes/extra.xml"], Is.EqualTo(new MapperFile(extra, IsCustom: true)));
            // The user folder wins a key collision with MapperDirectory.
            Assert.That(mappers["custom/gb/mine.xml"].Path, Is.EqualTo(user));
        });
    }

    [Test]
    public void GetMappers_skips_missing_configured_mapper_directory()
    {
        var official = Path.Combine(profile.FullName, "official-mappers", "gb", "official.xml");
        WriteFile(official);

        var mappers = CreateProvider(Path.Combine(profile.FullName, "missing")).GetMappers();

        Assert.That(mappers.Keys, Is.EqualTo(new[] { Path.Combine("gb", "official.xml") }));
    }

    [Test]
    public void Debug_mode_lists_only_mapper_directory_as_custom_mappers()
    {
        WriteFile(Path.Combine(profile.FullName, "official-mappers", "gb", "official.xml"));
        WriteFile(Path.Combine(profile.FullName, "user-mappers", "gb", "mine.xml"));
        var extraDirectory = Path.Combine(profile.FullName, "checkout");
        var extra = Path.Combine(extraDirectory, "nes", "extra.xml");
        WriteFile(extra);
        var provider = CreateProvider(extraDirectory, officialMappersEnabled: false);

        var mappers = provider.GetMappers();

        Assert.Multiple(() =>
        {
            Assert.That(mappers.Keys, Is.EqualTo(new[] { "custom/nes/extra.xml" }));
            Assert.That(mappers["custom/nes/extra.xml"], Is.EqualTo(new MapperFile(extra, IsCustom: true)));
            Assert.That(provider.GetPrimaryCustomMapperDirectory(), Is.EqualTo(Path.GetFullPath(extraDirectory)));
        });
    }

    [Test]
    public void Debug_mode_does_not_create_or_migrate_profile_mapper_folders()
    {
        var legacy = Path.Combine(profile.FullName, "mappers");
        WriteFile(Path.Combine(legacy, "gb", "official.xml"));
        File.WriteAllText(Path.Combine(legacy, ".mapper-manifest.json"), "{}");

        CreateProvider(officialMappersEnabled: false).EnsureUserMapperDirectory();

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(legacy), Is.True);
            Assert.That(Directory.Exists(Path.Combine(profile.FullName, "user-mappers")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(profile.FullName, "official-mappers")), Is.False);
        });
    }
}
