using Gamehook.Domain;
using Gamehook.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.Api;

public static class MapperEndpoints
{
    public static void MapMapperEndpoints(this WebApplication app)
    {
        app.MapPost("/mapper", async (LoadMapperRequest request, GamehookRouter router, FilesystemProvider filesystemProvider, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest(new { error = "'value' is required." });

            var mappers = filesystemProvider.GetMappers();
            if (!mappers.TryGetValue(request.Value, out var mapperPath))
                return Results.NotFound(new { error = $"Mapper '{request.Value}' was not found." });

            var (success, error) = await router.LoadMapperAsync(mapperPath, cancellationToken).ConfigureAwait(false);
            return success
                ? Results.Ok(new { value = request.Value, status = router.Session.Status })
                : Results.BadRequest(new { error });
        })
        .WithName("LoadMapper")
        .WithSummary("Loads a mapper (by key, as returned in the mapper directory listing) using the currently selected driver.")
        .WithTags("Mapper");
    }
}

public sealed record LoadMapperRequest(string Value);
