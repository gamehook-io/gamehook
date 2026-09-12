using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gamehook.Infrastructure.MapperUpdate;

// Runs once at startup (blocking - StartWithClassicDesktopLifetime doesn't render until the host
// has finished starting, and this is registered to start after AppUpdateService - see
// DependencyInjection.cs) so mappers are current before MainWindowViewModel reads them.
//
// Only ever touches the *default* managed mapper directory (FilesystemProvider's fallback under
// the Gamehook profile folder). If MapperDirectory is explicitly configured, that's someone's own
// mapper checkout - never download over it.
//
// The mapper repository is hardcoded (not configurable) - this only ever pulls from
// github.com/gamehook-io/mappers. Everything else is controlled by three "Mapper" config values:
//
//   Mapper:Commit - an exact commit sha. When set, this is exactly what gets installed, no
//     lookup, no "latest" of anything - a release build should always bake this in (via
//     appsettings.json at packaging time) pointing at whatever mapper commit that build was
//     tested against. This is what keeps a rolled-back Gamehook binary from pulling mappers whose
//     schema/features it doesn't understand: rolling back the app means shipping an older pinned
//     commit too, and DownloadAndReplaceAsync's full delete+replace means an already-newer
//     mapper directory downgrades to match it automatically, the same code path that normally
//     upgrades it.
//   Mapper:Branch - which branch to track when Commit is unset (dev/local builds). Defaults to
//     "main".
//   Mapper:Source - Proxy or Direct: how the *latest commit on Branch* gets resolved when Commit
//     is unset. Irrelevant once Commit is set - a pinned commit downloads straight from
//     codeload.github.com by sha, which isn't subject to any rate limit, so there's no lookup to
//     proxy in the first place.
public sealed class MapperUpdateService(
    IConfiguration configuration,
    FilesystemProvider filesystemProvider,
    IHttpClientFactory httpClientFactory,
    MapperUpdateStatusProvider statusProvider,
    ILogger<MapperUpdateService> logger) : IHostedService
{
    public const string HttpClientName = "MapperUpdate";
    private const string Repository = "gamehook-io/mappers";
    private const string ManifestFileName = ".mapper-manifest.json";
    private const string DefaultBranch = "main";
    private const string DefaultProxyUrl = "https://gamehook.io/mapper.json";
    private const string PinnedReferenceLabel = "pinned";

    public Task StartAsync(CancellationToken cancellationToken) => CheckForUpdatesAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // For the About window - reads the manifest written after the last successful mapper update
    // (see WriteManifest below) without re-checking anything over the network. Null if a custom
    // MapperDirectory is configured (manifests only ever live in the default managed directory)
    // or nothing has been downloaded there yet.
    public MapperVersionManifest? GetInstalledManifest() => filesystemProvider.HasCustomMapperDirectory()
        ? null : ReadManifest(filesystemProvider.GetDefaultMapperDirectory());

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        var customMapperDirectory = configuration["MapperDirectory"];
        if (!string.IsNullOrWhiteSpace(customMapperDirectory))
        {
            logger.LogInformation("Using custom mapper directory {MapperDirectory}; automatic updates are disabled.", customMapperDirectory);
            statusProvider.SetSkipped("MapperDirectory is explicitly configured; automatic updates are disabled.");
            return;
        }

        var branch = configuration["Mapper:Branch"] is { Length: > 0 } b ? b : DefaultBranch;
        var pinnedCommit = configuration["Mapper:Commit"] is { Length: > 0 } c ? c : null;
        var source = ParseSource(configuration["Mapper:Source"]);
        var mapperDirectory = filesystemProvider.GetDefaultMapperDirectory();

        try
        {
            Directory.CreateDirectory(filesystemProvider.GamehookProfileDirectory);
            // One process may replace a managed mapper installation at a time.
            using var updateLock = new FileStream(Path.Combine(filesystemProvider.GamehookProfileDirectory, ".mapper-update.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var previousDirectory = mapperDirectory + ".previous";
            if (!Directory.Exists(mapperDirectory) && Directory.Exists(previousDirectory))
                Directory.Move(previousDirectory, mapperDirectory);

            logger.LogInformation(
                "Checking for mapper updates in {MapperDirectory} (commit {Commit}, branch {Branch}, source {Source}).",
                mapperDirectory, pinnedCommit ?? "(none - tracking branch)", branch, source);

            Directory.CreateDirectory(mapperDirectory);
            var client = httpClientFactory.CreateClient(HttpClientName);

            var resolved = pinnedCommit is not null
                ? new ResolvedMapperReference(PinnedReferenceLabel, pinnedCommit)
                : source == MapperUpdateSource.Proxy
                    ? await ResolveLatestViaProxyAsync(
                        client,
                        configuration["Mapper:ProxyUrl"] is { Length: > 0 } proxyUrl ? proxyUrl : DefaultProxyUrl,
                        branch,
                        cancellationToken).ConfigureAwait(false)
                    : await ResolveLatestViaGithubAsync(client, branch, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Resolved mapper reference {Reference} ({CommitSha}).", resolved.Reference, resolved.CommitSha);
            if (resolved.CommitSha is not { Length: 40 } || !resolved.CommitSha.All(Uri.IsHexDigit))
                throw new InvalidDataException("Mapper commit must be a full 40-character hexadecimal SHA.");

            var previousManifest = ReadManifest(mapperDirectory);

            if (previousManifest is { } existing && string.Equals(existing.CommitSha, resolved.CommitSha, StringComparison.Ordinal))
            {
                logger.LogInformation("Mappers in {MapperDirectory} are up to date.", mapperDirectory);
                statusProvider.SetUpToDate(source, resolved.Reference, resolved.CommitSha);
                return;
            }

            var downloadUrl = BuildCodeloadDownloadUrl(resolved.CommitSha);
            logger.LogInformation("Downloading mappers from {DownloadUrl} into {MapperDirectory}.", downloadUrl, mapperDirectory);
            await DownloadAndReplaceAsync(client, downloadUrl, mapperDirectory,
                new MapperVersionManifest(Repository, resolved.Reference, resolved.CommitSha, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Mappers in {MapperDirectory} updated to {Reference} ({CommitSha}).",
                mapperDirectory, resolved.Reference, resolved.CommitSha);
            statusProvider.SetUpdated(source, resolved.Reference, resolved.CommitSha, previousManifest?.CommitSha);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Mapper update check failed.");
            statusProvider.SetFailed(source, ex.Message);
        }
    }

    // codeload.github.com resolves an archive directly from any commit-ish, including a bare full
    // sha - not just "refs/heads/<branch>" or "refs/tags/<tag>" - and isn't subject to
    // api.github.com's 60/hr unauthenticated rate limit either way.
    private static string BuildCodeloadDownloadUrl(string commitSha) =>
        $"https://codeload.github.com/{Repository}/zip/{commitSha}";

    private static async Task<ResolvedMapperReference> ResolveLatestViaGithubAsync(
        HttpClient client, string branch, CancellationToken cancellationToken)
    {
        var commitSha = await GetCommitShaAsync(client, branch, cancellationToken).ConfigureAwait(false);
        return new ResolvedMapperReference(branch, commitSha);
    }

    private static async Task<string> GetCommitShaAsync(HttpClient client, string reference, CancellationToken cancellationToken)
    {
        using var response = await client
            .GetAsync($"https://api.github.com/repos/{Repository}/commits/{Uri.EscapeDataString(reference)}", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var commit = await response.Content.ReadFromJsonAsync<GithubCommit>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return commit?.Sha ?? throw new InvalidOperationException($"GitHub did not return a commit sha for '{reference}'.");
    }

    // A site-hosted manifest (typically a small proxy in front of the GitHub API the user
    // controls) resolves the latest-commit-on-branch lookup api.github.com/repos/.../commits/...
    // would - it does not itself supply the download; that still comes straight from
    // codeload.github.com. If the site has nothing published, fall back to resolving the branch
    // directly against GitHub.
    private static async Task<ResolvedMapperReference> ResolveLatestViaProxyAsync(
        HttpClient client, string proxyUrl, string branch, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(proxyUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            var entry = await response.Content.ReadFromJsonAsync<ProxyManifest>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entry is not null)
            {
                return new ResolvedMapperReference(entry.Reference, entry.CommitSha);
            }
        }

        return await ResolveLatestViaGithubAsync(client, branch, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadAndReplaceAsync(
        HttpClient client, string downloadUrl, string mapperDirectory, MapperVersionManifest manifest, CancellationToken cancellationToken)
    {
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"gamehook-mappers-{Guid.NewGuid():N}.zip");
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"gamehook-mappers-{Guid.NewGuid():N}");
        var stagingPath = mapperDirectory + $".staging-{Guid.NewGuid():N}";
        var backupPath = mapperDirectory + ".previous";

        try
        {
            await using (var fileStream = File.Create(tempZipPath))
            await using (var responseStream = await client.GetStreamAsync(downloadUrl, cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > 128 * 1024 * 1024) throw new InvalidDataException("Mapper archive exceeds 128 MiB.");
                    await fileStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
            }

            Directory.CreateDirectory(tempExtractPath);
            using (var archive = ZipFile.OpenRead(tempZipPath))
            {
                if (archive.Entries.Count > 10000 || archive.Entries.Sum(entry => entry.Length) > 256 * 1024 * 1024)
                    throw new InvalidDataException("Expanded mapper archive exceeds supported limits.");
            }
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath, overwriteFiles: true);

            // GitHub's archive downloads wrap the whole tree in a single "{owner}-{repo}-{sha7}"
            // folder, and the repo itself keeps the built mappers under "dist" - copy dist's
            // contents into the mapper directory, not the dist folder itself.
            var archiveRoot = Directory.GetDirectories(tempExtractPath).SingleOrDefault() ?? tempExtractPath;
            var distRoot = Path.Combine(archiveRoot, "dist");
            if (!Directory.Exists(distRoot) || !Directory.EnumerateFiles(distRoot, "*.xml", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("Mapper archive contains no dist mapper files.");

            Directory.CreateDirectory(stagingPath);
            CopyDirectory(distRoot, stagingPath);
            WriteManifest(stagingPath, manifest);
            cancellationToken.ThrowIfCancellationRequested();

            // Stage on the same filesystem, then replace with rollback on installation failure.
            // A fixed backup name also permits recovery after termination between the two moves.
            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, recursive: true);
            Directory.Move(mapperDirectory, backupPath);
            try
            {
                Directory.Move(stagingPath, mapperDirectory);
            }
            catch
            {
                Directory.Move(backupPath, mapperDirectory);
                throw;
            }
            Directory.Delete(backupPath, recursive: true);
        }
        finally
        {
            File.Delete(tempZipPath);
            if (Directory.Exists(tempExtractPath))
            {
                Directory.Delete(tempExtractPath, recursive: true);
            }
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, file)), overwrite: true);
        }
    }

    private static MapperVersionManifest? ReadManifest(string mapperDirectory)
    {
        var path = Path.Combine(mapperDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<MapperVersionManifest>(File.ReadAllText(path));
            return manifest is { CommitSha.Length: 40, Reference: not null, Repository: Repository }
                && manifest.CommitSha.All(Uri.IsHexDigit) ? manifest : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteManifest(string mapperDirectory, MapperVersionManifest manifest) =>
        File.WriteAllText(
            Path.Combine(mapperDirectory, ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    private static MapperUpdateSource ParseSource(string? value) =>
        Enum.TryParse<MapperUpdateSource>(value, ignoreCase: true, out var source) ? source : MapperUpdateSource.Direct;

    private sealed record ResolvedMapperReference(string Reference, string CommitSha);

    private sealed record GithubCommit([property: JsonPropertyName("sha")] string Sha);

    // Contract for the site-hosted manifest (e.g. https://gamehook.io/mapper.json) - it only ever
    // needs to answer "what's the latest commit on the tracked branch", never host the download:
    // { "reference": "main", "commitSha": "..." }
    private sealed record ProxyManifest(
        [property: JsonPropertyName("reference")] string Reference,
        [property: JsonPropertyName("commitSha")] string CommitSha);
}
