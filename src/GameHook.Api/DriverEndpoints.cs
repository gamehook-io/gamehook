using GameHook.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameHook.Api;

public static class DriverEndpoints
{
    public static void MapDriverEndpoints(this WebApplication app)
    {
        app.MapGet("/driver", (GameHookRouter router) =>
            router.DriverName is null
                ? Results.NotFound(new { error = "No driver selected." })
                : Results.Ok(new { value = router.DriverName }))
            .WithName("GetDriver")
            .WithSummary("Gets the currently selected driver.")
            .WithTags("Driver");

        app.MapPost("/driver", async (SetDriverRequest request, GameHookRouter router, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest(new { error = "'value' is required." });

            var (success, error) = await router.SetDriverAsync(request.Value, request.Source, cancellationToken).ConfigureAwait(false);
            return success ? Results.Ok(new { value = router.DriverName }) : Results.BadRequest(new { error });
        })
        .WithName("SetDriver")
        .WithSummary("Changes the driver. If a mapper is already loaded, it is reloaded against the new driver.")
        .WithTags("Driver");

        app.MapGet("/driver/{region}", async (string region, ulong? address, int? length, GameHookRouter router) =>
        {
            var (bytes, error) = await router.ReadDriverRegionAsync(region, address, length).ConfigureAwait(false);
            return bytes is null ? Results.BadRequest(new { error }) : Results.Ok(bytes.Value.ToArray());
        })
        .WithName("ReadDriverRegion")
        .WithSummary("Reads raw bytes from a memory region. Omit address/length to read the whole region (where its size is known).")
        .WithTags("Driver");

        app.MapPost("/driver/{region}", async (string region, WriteDriverRequest request, GameHookRouter router, CancellationToken cancellationToken) =>
        {
            if (request.Data is not { Length: > 0 })
                return Results.BadRequest(new { error = "'data' is required." });

            var bytes = new byte[request.Data.Length];
            for (var i = 0; i < request.Data.Length; i++) bytes[i] = checked((byte)request.Data[i]);

            var (success, error) = await router.WriteDriverRegionAsync(region, request.Address, bytes, cancellationToken).ConfigureAwait(false);
            return success ? Results.Ok(new { success = true }) : Results.BadRequest(new { error });
        })
        .WithName("WriteDriverRegion")
        .WithSummary("Writes raw bytes directly to a memory region, bypassing property encoding.")
        .WithTags("Driver");
    }
}

public sealed record SetDriverRequest(string Value, string? Source = null);
public sealed record WriteDriverRequest(ulong Address, int[] Data);
