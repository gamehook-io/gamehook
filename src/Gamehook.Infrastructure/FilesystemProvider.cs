using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Gamehook.Infrastructure;

public class FilesystemProvider
{
    private const string ProfileDirectoryName = "Gamehook";
    private const string LogDirectoryName = "logs";
    // Official mappers are downloaded into OfficialMapperDirectoryName and fully replaced on every
    // update. UserMapperDirectoryName is the user's own folder - Gamehook only ever creates it.
    private const string OfficialMapperDirectoryName = "official-mappers";
    private const string UserMapperDirectoryName = "user-mappers";
    // Older builds downloaded official mappers here (see EnsureUserMapperDirectory).
    private const string LegacyMapperDirectoryName = "mappers";
    public const string CustomMapperKeyPrefix = "custom/";
    // Written by MapperUpdateService into every managed installation.
    private const string MapperManifestFileName = ".mapper-manifest.json";
    private const string LastOpenedFileName = "last_opened.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IConfiguration configuration;
    private readonly ILogger<FilesystemProvider> logger;

    public string GamehookProfileDirectory { get; }

    /// False in Debug builds: no official mappers and no user-mappers folder - MapperDirectory
    /// (a developer's mapper checkout) is the only mapper source, listed as custom mappers.
    public bool OfficialMappersEnabled { get; }

#if DEBUG
    private const bool DefaultOfficialMappersEnabled = false;
#else
    private const bool DefaultOfficialMappersEnabled = true;
#endif

    public FilesystemProvider(IConfiguration configuration, ILogger<FilesystemProvider>? logger = null)
        : this(configuration, GetGamehookProfileDirectory(), DefaultOfficialMappersEnabled, logger)
    {
    }

    // Tests only: the profile location and mapper mode are otherwise fixed by the build.
    internal FilesystemProvider(
        IConfiguration configuration,
        string profileDirectory,
        bool officialMappersEnabled,
        ILogger<FilesystemProvider>? logger = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.logger = logger ?? NullLogger<FilesystemProvider>.Instance;
        GamehookProfileDirectory = Path.GetFullPath(profileDirectory);
        OfficialMappersEnabled = officialMappersEnabled;
    }

    public static string GetLogDirectory() => Path.Combine(GetGamehookProfileDirectory(), LogDirectoryName);

    /// Official mappers keyed by their relative path, then custom mappers keyed
    /// "custom/{relative path}": the user mapper folder first, then MapperDirectory when configured.
    /// Debug builds list only MapperDirectory (see OfficialMappersEnabled).
    public Dictionary<string, MapperFile> GetMappers()
    {
        var mappers = new Dictionary<string, MapperFile>(StringComparer.Ordinal);
        if (OfficialMappersEnabled)
        {
            var officialDirectory = GetOfficialMapperDirectory();
            Directory.CreateDirectory(officialDirectory);
            foreach (var (key, path) in GetFilesIn(officialDirectory, ".xml"))
            {
                mappers.Add(key, new MapperFile(path, IsCustom: false));
            }
        }

        foreach (var customDirectory in GetCustomMapperDirectories())
        {
            if (!Directory.Exists(customDirectory))
            {
                logger.LogWarning("Custom mapper directory {Directory} was not found.", customDirectory);
                continue;
            }

            foreach (var (key, path) in GetFilesIn(customDirectory, ".xml"))
            {
                if (!mappers.TryAdd(CustomMapperKeyPrefix + key.Replace('\\', '/'), new MapperFile(path, IsCustom: true)))
                {
                    logger.LogWarning("Skipping custom mapper '{Path}': another mapper already uses its key.", path);
                }
            }
        }

        return mappers;
    }

    /// The managed official mapper installation MapperUpdateService downloads into.
    public string GetOfficialMapperDirectory() => Path.Combine(GamehookProfileDirectory, OfficialMapperDirectoryName);

    public string GetUserMapperDirectory() => Path.Combine(GamehookProfileDirectory, UserMapperDirectoryName);

    /// The optional extra custom mapper directory from MapperDirectory, or null when unset.
    public string? GetConfiguredMapperDirectory() =>
        configuration["MapperDirectory"] is { } directory && !string.IsNullOrWhiteSpace(directory)
            ? Path.GetFullPath(directory)
            : null;

    /// Where users are told to put their own mappers: the user mapper folder, or in Debug builds
    /// MapperDirectory (null when unset).
    public string? GetPrimaryCustomMapperDirectory() =>
        OfficialMappersEnabled ? GetUserMapperDirectory() : GetConfiguredMapperDirectory();

    private IEnumerable<string> GetCustomMapperDirectories()
    {
        var configured = GetConfiguredMapperDirectory();
        if (!OfficialMappersEnabled)
        {
            if (configured is not null) yield return configured;
            yield break;
        }

        var userDirectory = GetUserMapperDirectory();
        yield return userDirectory;
        if (configured is not null && !PathsEqual(configured, userDirectory))
        {
            yield return configured;
        }
    }

    /// Anything outside the official mapper directory is custom - either custom folder, or a file
    /// browsed from anywhere else on disk.
    public bool IsCustomMapperPath(string path)
    {
        var officialDirectory = Path.TrimEndingDirectorySeparator(GetOfficialMapperDirectory()) + Path.DirectorySeparatorChar;
        return !Path.GetFullPath(path).StartsWith(officialDirectory, PathComparison);
    }

    /// Run once at startup, before MapperUpdateService: creates the user mapper folder so users
    /// can find it. Older builds downloaded official mappers into "mappers"; such an installation
    /// (recognised by its manifest) is moved to the official folder so it isn't downloaded again.
    public void EnsureUserMapperDirectory()
    {
        if (!OfficialMappersEnabled) return;

        var userDirectory = GetUserMapperDirectory();
        var legacyDirectory = Path.Combine(GamehookProfileDirectory, LegacyMapperDirectoryName);
        var officialDirectory = GetOfficialMapperDirectory();
        try
        {
            if (File.Exists(Path.Combine(legacyDirectory, MapperManifestFileName)) && !Directory.Exists(officialDirectory))
            {
                Directory.Move(legacyDirectory, officialDirectory);
                logger.LogInformation("Moved official mappers from {LegacyDirectory} to {OfficialDirectory}.", legacyDirectory, officialDirectory);
            }

            Directory.CreateDirectory(userDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare user mapper directory {UserDirectory}.", userDirectory);
        }
    }

    public string? GetLastOpenedSaveStateDirectory()
    {
        var directory = ReadLastOpenedProfile()?.SaveStateDirectory;
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    public string? GetLastOpenedMapperDirectory()
    {
        var directory = ReadLastOpenedProfile()?.MapperDirectory;
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    public bool RememberLastOpenedSaveStateDirectory(string directory) =>
        RememberLastOpenedField(directory, profile => profile with { SaveStateDirectory = Path.GetFullPath(directory) });

    public bool RememberLastOpenedMapperDirectory(string directory) =>
        RememberLastOpenedField(directory, profile => profile with { MapperDirectory = Path.GetFullPath(directory) });

    public string? GetLastDriver() => ReadLastOpenedProfile()?.LastDriver;

    public string? GetLastMapperPath()
    {
        var path = ReadLastOpenedProfile()?.LastMapperPath;
        return path is not null && File.Exists(path) ? path : null;
    }

    public bool RememberLastDriver(string driverName) =>
        RememberLastOpenedField(driverName, profile => profile with { LastDriver = driverName });

    public int? GetLastDriverPort(string driverName) =>
        ReadLastOpenedProfile()?.DriverPorts is { } ports && ports.TryGetValue(driverName, out var port) ? port : null;

    public bool RememberLastDriverPort(string driverName, int port) =>
        RememberLastOpenedField(driverName, profile => profile with
        {
            DriverPorts = new Dictionary<string, int>(profile.DriverPorts ?? [], StringComparer.OrdinalIgnoreCase) { [driverName] = port },
        });

    public bool RememberLastMapperPath(string mapperPath) =>
        RememberLastOpenedField(mapperPath, profile => profile with { LastMapperPath = Path.GetFullPath(mapperPath) });

    private bool RememberLastOpenedField(string value, Func<LastOpenedProfile, LastOpenedProfile> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        try
        {
            Directory.CreateDirectory(GamehookProfileDirectory);
            var profile = update(ReadLastOpenedProfile() ?? new LastOpenedProfile());
            AtomicFile.WriteAllText(Path.Combine(GamehookProfileDirectory, LastOpenedFileName), JsonSerializer.Serialize(profile, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remember last opened profile value '{Value}' in profile '{GamehookProfileDirectory}'.", value, GamehookProfileDirectory);
            return false;
        }
    }

    private static Dictionary<string, string> GetFilesIn(string fullDirectory, string extension)
    {
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"Configured directory '{fullDirectory}' was not found.");
        }

        return Directory
            .EnumerateFiles(fullDirectory, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(fullDirectory, path),
                Path.GetFullPath,
            StringComparer.Ordinal);
    }

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        PathComparison);

    private LastOpenedProfile? ReadLastOpenedProfile()
    {
        var path = Path.Combine(GamehookProfileDirectory, LastOpenedFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LastOpenedProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not read last opened profile at '{Path}'.", path);
            return null;
        }
    }

    /// The user's optional appsettings.json override in the profile directory - Release builds load
    /// it on top of the bundled one (see Program.cs).
    public static string GetProfileSettingsPath() =>
        Path.Combine(GetGamehookProfileDirectory(), "appsettings.json");

    /// Always "Gamehook" under the OS local application-data directory - deliberately not
    /// configurable.
    public static string GetGamehookProfileDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(localAppData, ProfileDirectoryName);
    }

    private sealed record LastOpenedProfile(
        string? SaveStateDirectory = null,
        string? MapperDirectory = null,
        string? LastDriver = null,
        string? LastMapperPath = null,
        Dictionary<string, int>? DriverPorts = null);
}

public sealed record MapperFile(string Path, bool IsCustom);
