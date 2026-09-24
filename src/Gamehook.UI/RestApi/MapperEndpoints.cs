using Gamehook.Domain;
using Gamehook.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Reflection;
using System.Xml.Linq;

namespace Gamehook.RestApi;

public static class MapperEndpoints
{
    public static void MapMapperEndpoints(this WebApplication app)
    {
        app.MapGet("/", () =>
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(MapperEndpoints).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? assembly.GetName().Version?.ToString()
                ?? string.Empty;
            return Results.Ok(new { version });
        })
        .WithName("GetGamehookInfo")
        .WithSummary("Returns Gamehook version information.")
        .Produces<GamehookInfoResponse>(StatusCodes.Status200OK)
        .WithTags("Gamehook");

        app.MapGet("/mappers", (GamehookInstances instances, FilesystemProvider filesystemProvider) =>
        {
            var loadedPaths = instances.Snapshot()
                .Select((router, index) => (Index: index, Path: (router.Mapper as Gamehook.Infrastructure.Mapper)?.MapperPath))
                .Where(entry => entry.Path is not null)
                .ToArray();
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mappers = filesystemProvider.GetMappers().Select(pair =>
            {
                var fullPath = Path.GetFullPath(pair.Value.Path);
                var loadedIn = loadedPaths.Where(entry => string.Equals(fullPath, entry.Path, pathComparison)).Select(entry => entry.Index).ToArray();
                return DescribeMapper(pair.Key, pair.Value, loadedIn);
            });
            return Results.Ok(mappers);
        })
        .WithName("GetMappers")
        .WithSummary("Lists available mappers and which instances have each one loaded.")
        .WithDescription("The value field is the key to send as value to POST /instances/{index}/mapper. Custom mappers (the \"user-mappers\" folder in the Gamehook profile directory, plus MapperDirectory when configured) have custom: true and keys prefixed \"custom/\". An empty list means no mapper files were found.")
        .Produces<AvailableMapperResponse[]>(StatusCodes.Status200OK)
        .WithTags("Mapper");
    }

    public static void MapInstanceMapperEndpoints(this RouteGroupBuilder instance)
    {
        instance.MapGet("/mapper", (int index, GamehookInstances instances, FilesystemProvider filesystemProvider) =>
        {
            if (!instances.TryGet(index, out var router))
                return ApiProblems.InstanceNotFound(index);

            return CreateLoadedMapperResponse(router, filesystemProvider) is { } mapper
                ? Results.Ok(mapper)
                : ApiProblems.NotFound("No mapper is loaded.", "mapper_not_loaded");
        })
        .WithName("GetInstanceMapper")
        .WithSummary("Returns metadata for the instance's loaded mapper, or 404 when no mapper is loaded.")
        .Produces<LoadedMapperResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithTags("Mapper");

        instance.MapPost("/mapper", async (int index, LoadMapperRequest request, GamehookInstances instances, FilesystemProvider filesystemProvider, CancellationToken cancellationToken) =>
        {
            if (!instances.TryGet(index, out var router))
                return ApiProblems.InstanceNotFound(index);

            if (string.IsNullOrWhiteSpace(request.Value))
                return ApiProblems.BadRequest("'value' is required.", "mapper_key_required");

            var mappers = filesystemProvider.GetMappers();
            if (!mappers.TryGetValue(request.Value, out var mapperFile))
                return ApiProblems.NotFound($"Mapper '{request.Value}' was not found.", "mapper_not_found");

            var (success, error) = await router.LoadMapperAsync(mapperFile.Path, cancellationToken).ConfigureAwait(false);
            return success
                ? Results.Ok(new MapperLoadResponse(request.Value, router.Session.Status))
                : ApiProblems.Unprocessable(error ?? "Mapper could not be loaded.", "mapper_load_failed");
        })
        .WithName("LoadInstanceMapper")
        .WithSummary("Loads a mapper (by key, as returned by GET /mappers) into the instance using its selected driver.")
        .WithDescription("Returns 422 if the load fails, including when another instance is already using the selected driver's host:port (or save-state file).")
        .Produces<MapperLoadResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Mapper");

        instance.MapDelete("/mapper", (int index, GamehookInstances instances) =>
        {
            if (!instances.TryGet(index, out var router))
                return ApiProblems.InstanceNotFound(index);

            router.Unload();
            return Results.Ok(new SuccessResponse(true));
        })
        .WithName("UnloadInstanceMapper")
        .WithSummary("Unloads the instance's mapper. The selected driver is kept.")
        .Produces<SuccessResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithTags("Mapper");
    }

    internal static LoadedMapperResponse? CreateLoadedMapperResponse(GamehookRouter router, FilesystemProvider filesystemProvider) =>
        router.Mapper is Gamehook.Infrastructure.Mapper mapper
            ? new LoadedMapperResponse(
                mapper.Id,
                mapper.GameName,
                mapper.System.Id,
                mapper.NativeProcessorId,
                filesystemProvider.IsCustomMapperPath(mapper.MapperPath))
            : null;

    private static AvailableMapperResponse DescribeMapper(string value, MapperFile file, int[] loadedIn)
    {
        var path = file.Path;
        var name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ');
        try
        {
            var root = XDocument.Load(path).Root;
            return new AvailableMapperResponse(
                value,
                (string?)root?.Attribute("id"),
                (string?)root?.Attribute("name") ?? name,
                (string?)root?.Attribute("platform"),
                file.IsCustom,
                loadedIn.Length > 0,
                loadedIn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new AvailableMapperResponse(value, null, name, null, file.IsCustom, loadedIn.Length > 0, loadedIn);
        }
    }
}
