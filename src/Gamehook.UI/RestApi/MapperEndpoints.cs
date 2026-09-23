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

        app.MapGet("/mappers", (GamehookRouter router, FilesystemProvider filesystemProvider) =>
        {
            var loadedPath = (router.Mapper as Gamehook.Infrastructure.Mapper)?.MapperPath;
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mappers = filesystemProvider.GetMappers().Select(pair => DescribeMapper(
                pair.Key,
                pair.Value,
                loadedPath is not null && string.Equals(Path.GetFullPath(pair.Value), loadedPath, pathComparison)));
            return Results.Ok(mappers);
        })
        .WithName("GetMappers")
        .WithSummary("Lists available mappers and indicates which mapper is loaded.")
        .WithDescription("The value field is the key to send as value to POST /mapper. An empty list means the configured mapper directory contains no mapper files.")
        .Produces<AvailableMapperResponse[]>(StatusCodes.Status200OK)
        .WithTags("Mapper");

        app.MapGet("/instance", (GamehookRouter router) =>
        {
            if (router.Mapper is not Gamehook.Infrastructure.Mapper mapper)
                return ApiProblems.NotFound("No mapper is loaded.", "mapper_not_loaded");

            return Results.Ok(new LoadedMapperResponse(
                mapper.Id,
                mapper.GameName,
                mapper.System.Id,
                mapper.Version,
                mapper.NativeProcessorId));
        })
        .WithName("GetInstance")
        .WithSummary("Returns metadata for the active instance, or 404 when no mapper is loaded.")
        .Produces<LoadedMapperResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithTags("Instance");

        app.MapPost("/mapper", async (LoadMapperRequest request, GamehookRouter router, FilesystemProvider filesystemProvider, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
                return ApiProblems.BadRequest("'value' is required.", "mapper_key_required");

            var mappers = filesystemProvider.GetMappers();
            if (!mappers.TryGetValue(request.Value, out var mapperPath))
                return ApiProblems.NotFound($"Mapper '{request.Value}' was not found.", "mapper_not_found");

            var (success, error) = await router.LoadMapperAsync(mapperPath, cancellationToken).ConfigureAwait(false);
            return success
                ? Results.Ok(new { value = request.Value, status = router.Session.Status })
                : ApiProblems.Unprocessable(error ?? "Mapper could not be loaded.", "mapper_load_failed");
        })
        .WithName("LoadMapper")
        .WithSummary("Loads a mapper (by key, as returned in the mapper directory listing) using the currently selected driver.")
        .Produces<MapperLoadResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Mapper");
    }

    private static AvailableMapperResponse DescribeMapper(string value, string path, bool loaded)
    {
        var name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ');
        try
        {
            var root = XDocument.Load(path).Root;
            return new AvailableMapperResponse(
                value,
                (string?)root?.Attribute("id"),
                (string?)root?.Attribute("name") ?? name,
                (string?)root?.Attribute("platform"),
                (string?)root?.Attribute("version"),
                loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new AvailableMapperResponse(value, null, name, null, null, loaded);
        }
    }
}
