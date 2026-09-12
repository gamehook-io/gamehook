using Gamehook.Domain.Interface;
using Gamehook.Infrastructure;
using Gamehook.Infrastructure.Drivers;

namespace Gamehook.Tests;

public class BaseTest
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));

    private static readonly string MapperDirectory =
        Environment.GetEnvironmentVariable("GAMEHOOK_TEST_MAPPERS")
        ?? Path.GetFullPath(Path.Combine(RepoRoot, "..", "mappers", "dist"));

    // Every shipped mapper file, regardless of which platform subfolder holds it -
    // a test that needs "some real mapper" (a smoke test, a fuzz-style pass) should read from this
    // instead of hardcoding a filename it doesn't actually care about.
    public static IReadOnlyList<string> AllMapperFiles { get; } =
        Directory.EnumerateFiles(MapperDirectory, "*.xml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    // Mapper filenames are unique across every platform folder, so a test that genuinely needs one
    // specific mapper (e.g. asserting Red/Blue's IV-unpacking formula) names only the file, never
    // which platform subfolder it currently lives under - that's exactly the kind of assumption a
    // folder reorganization would silently break.
    public static string GetMapperFilePath(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        return AllMapperFiles.SingleOrDefault(path => string.Equals(Path.GetFileName(path), filename, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"No mapper named '{filename}' under '{MapperDirectory}'.", filename);
    }

    public static string GetSaveStateFilePath(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        return Path.Combine(TestContext.CurrentContext.TestDirectory, "SaveStates", filename);
    }

    public async Task<IMapper> CreateSaveStateMapper(string mapperFilename, string saveStateFilename)
    {
        var mapper = new Mapper(
            GetMapperFilePath(mapperFilename),
            new SaveStateDriver(GetSaveStateFilePath(saveStateFilename)));

        await mapper.ReadAsync();

        return mapper;
    }
}
