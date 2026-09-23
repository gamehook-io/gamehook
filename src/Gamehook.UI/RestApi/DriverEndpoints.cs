using Gamehook.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

public static class DriverEndpoints
{
    public static void MapDriverEndpoints(this WebApplication app)
    {
        app.MapGet("/driver", (GamehookRouter router) =>
            router.DriverName is null
                ? ApiProblems.NotFound("No driver is selected.", "driver_not_selected")
                : Results.Ok(new { value = router.DriverName }))
            .WithName("GetDriver")
            .WithSummary("Gets the currently selected driver.")
            .Produces<DriverResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithTags("Driver");

        app.MapPost("/driver", async (SetDriverRequest request, GamehookRouter router, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
                return ApiProblems.BadRequest("'value' is required.", "driver_name_required");

            var (success, error) = await router.SetDriverAsync(request.Value, request.Source, cancellationToken).ConfigureAwait(false);
            return success
                ? Results.Ok(new { value = router.DriverName })
                : ApiProblems.Unprocessable(error ?? "Driver selection failed.", "driver_selection_failed");
        })
        .WithName("SetDriver")
        .WithSummary("Changes the driver. If a mapper is already loaded, it is reloaded against the new driver.")
        .Produces<DriverResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Driver");

        app.MapGet("/driver/{region}", async (string region, ulong? address, int? length, GamehookRouter router) =>
        {
            var (bytes, error) = await router.ReadDriverRegionAsync(region, address, length).ConfigureAwait(false);
            return bytes is null
                ? ApiProblems.Unprocessable(error ?? "Memory region could not be read.", "memory_read_failed")
                : Results.Ok(bytes.Value.ToArray());
        })
        .WithName("ReadDriverRegion")
        .WithSummary("Reads raw bytes from a memory region. Omit address/length to read the whole region (where its size is known).")
        .WithDescription("The response is a base64-encoded JSON byte string. Supply both address and length, or omit both.")
        .Produces<byte[]>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Driver");

        app.MapPost("/driver/{region}", async (string region, WriteDriverRequest request, GamehookRouter router, CancellationToken cancellationToken) =>
        {
            if (request.Data is not { Length: > 0 })
                return ApiProblems.BadRequest("'data' must contain at least one byte.", "memory_write_data_required");

            if (request.Data.Any(value => value is < 0 or > 255))
                return ApiProblems.BadRequest("Each data item must be an integer from 0 through 255.", "memory_write_data_invalid");

            var bytes = new byte[request.Data.Length];
            for (var i = 0; i < request.Data.Length; i++) bytes[i] = checked((byte)request.Data[i]);

            var (success, error) = await router.WriteDriverRegionAsync(region, request.Address, bytes, cancellationToken).ConfigureAwait(false);
            return success
                ? Results.Ok(new SuccessResponse(true))
                : ApiProblems.Unprocessable(error ?? "Memory write failed.", "memory_write_failed");
        })
        .WithName("WriteDriverRegion")
        .WithSummary("Writes raw bytes directly to a memory region, bypassing property encoding.")
        .Produces<SuccessResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Driver");
    }
}

/// <summary>Selects a driver and optional source path.</summary>
public sealed record SetDriverRequest(
    [property: System.ComponentModel.Description("Driver name to select.")] string Value,
    [property: System.ComponentModel.Description("Optional driver-specific source path.")] string? Source = null);

/// <summary>Writes bytes to a memory region at a region-relative offset.</summary>
public sealed record WriteDriverRequest(
    [property: System.ComponentModel.Description("Region-relative byte offset.")] ulong Address,
    [property: System.ComponentModel.Description("Bytes to write as integers from 0 through 255.")] int[] Data);
