using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Gamehook.Infrastructure;

public class FilesystemProvider
{
    private const string ProfileDirectoryName = "Gamehook";
    private const string LogDirectoryName = "logs";
    private const string MapperDirectoryName = "mappers";
    private const string LastOpenedFileName = "last_opened.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<FilesystemProvider> logger;

    public string GamehookProfileDirectory { get; }

    public FilesystemProvider(IConfiguration configuration, ILogger<FilesystemProvider>? logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.logger = logger ?? NullLogger<FilesystemProvider>.Instance;
        GamehookProfileDirectory = GetGamehookProfileDirectory(configuration);
    }

    public string GetLogDirectory() => Path.Combine(GamehookProfileDirectory, LogDirectoryName);

    public static string GetLogDirectory(IConfiguration configuration) =>
        Path.Combine(GetGamehookProfileDirectory(configuration), LogDirectoryName);

    public Dictionary<string, string> GetMappers()
    {
        return GetFiles("MapperDirectory", ".xml");
    }

    public string GetMapperDirectory() => GetConfiguredDirectory("MapperDirectory");

    public Dictionary<string, string> GetSaveStates()
    {
        return GetFiles("SaveStateDirectory", ".state");
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

    public bool RememberLastMapperPath(string mapperPath) =>
        RememberLastOpenedField(mapperPath, profile => profile with { LastMapperPath = Path.GetFullPath(mapperPath) });

    private bool RememberLastOpenedField(string value, Func<LastOpenedProfile, LastOpenedProfile> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        try
        {
            Directory.CreateDirectory(GamehookProfileDirectory);
            var profile = update(ReadLastOpenedProfile() ?? new LastOpenedProfile());
            var path = Path.Combine(GamehookProfileDirectory, LastOpenedFileName);
            var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profile, JsonOptions));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remember last opened profile value '{Value}' in profile '{GamehookProfileDirectory}'.", value, GamehookProfileDirectory);
            return false;
        }
    }

    private Dictionary<string, string> GetFiles(string configurationKey, string extension)
    {
        var fullDirectory = GetConfiguredDirectory(configurationKey);
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

    private string GetConfiguredDirectory(string configurationKey)
    {
        var directory = _configuration[configurationKey];
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return Path.GetFullPath(directory);
        }

        if (configurationKey == "MapperDirectory")
        {
            var defaultDirectory = GetDefaultMapperDirectory();
            Directory.CreateDirectory(defaultDirectory);
            return defaultDirectory;
        }

        throw new InvalidOperationException($"Configuration value '{configurationKey}' is required.");
    }

    public string GetDefaultMapperDirectory() => Path.Combine(GamehookProfileDirectory, MapperDirectoryName);

    public bool HasCustomMapperDirectory() => !string.IsNullOrWhiteSpace(_configuration["MapperDirectory"]);

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

    public static string GetGamehookProfileDirectory(IConfiguration configuration)
    {
        if (configuration["GamehookProfileDirectory"] is { Length: > 0 } configuredDirectory)
        {
            return Path.GetFullPath(configuredDirectory);
        }

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
        string? LastMapperPath = null);
}
