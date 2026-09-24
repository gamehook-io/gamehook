using Gamehook.Domain;
using Gamehook.Infrastructure;
using Gamehook.Infrastructure.Drivers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

public static class DriverEndpoints
{
    public static void MapDriverEndpoints(this RouteGroupBuilder instance)
    {
        instance.MapGet("/driver", (int index, GamehookInstances instances, IEnumerable<DriverRegistration> registrations) =>
            !instances.TryGet(index, out var router) ? ApiProblems.InstanceNotFound(index)
            : router.DriverName is null ? ApiProblems.NotFound("No driver is selected.", "driver_not_selected")
            : Results.Ok(CreateDriverResponse(router, registrations)))
            .WithName("GetDriver")
            .WithSummary("Gets the currently selected driver.")
            .Produces<DriverResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithTags("Driver");

        instance.MapPost("/driver", async (int index, SetDriverRequest request, GamehookInstances instances, IEnumerable<DriverRegistration> registrations, CancellationToken cancellationToken) =>
        {
            if (!instances.TryGet(index, out var router))
                return ApiProblems.InstanceNotFound(index);
            if (string.IsNullOrWhiteSpace(request.Value))
                return ApiProblems.BadRequest("'value' is required.", "driver_name_required");

            var source = request.Source;
            if (request.Port is { } port)
            {
                var registration = FindRegistration(registrations, request.Value);
                if (registration is not null && registration.DefaultPort is null)
                    return ApiProblems.BadRequest($"The {registration.Name} driver does not use a port.", "driver_port_not_supported");

                if (!NetworkEndpoint.IsValidPort(port))
                    return ApiProblems.BadRequest("'port' must be from 1 through 65535.", "driver_port_invalid");

                if (source?.Contains(':', StringComparison.Ordinal) is true)
                    return ApiProblems.BadRequest("Supply the port in either 'port' or 'source', not both.", "driver_port_conflict");

                source = NetworkEndpoint.Format(source, port);
            }

            var (success, error) = await router.SetDriverAsync(request.Value, source, cancellationToken).ConfigureAwait(false);
            return success
                ? Results.Ok(CreateDriverResponse(router, registrations))
                : ApiProblems.Unprocessable(error ?? "Driver selection failed.", "driver_selection_failed");
        })
        .WithName("SetDriver")
        .WithSummary("Changes the driver. If a mapper is already loaded, it is reloaded against the new driver.")
        .WithDescription("Network drivers (RetroArch, SuperShuckie) connect to localhost on their default port unless 'port' is supplied. 'source' may also give a host or host:port. Returns 422 when another instance is already using the same host:port (or save-state file); no two instances may share one.")
        .Produces<DriverResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Driver");

        instance.MapGet("/driver/{region}", async (int index, string region, ulong? address, int? length, GamehookInstances instances) =>
        {
            if (!instances.TryGet(index, out var router))
                return ApiProblems.InstanceNotFound(index);
            var (bytes, error) = await router.ReadDriverRegionAsync(region, address, length).ConfigureAwait(false);
            return bytes is null
                ? ApiProblems.Unprocessable(error ?? "Memory region could not be read.", "memory_read_failed")
                : Results.Ok(bytes.Value.ToArray());
        })
        .WithName("ReadDriverRegion")
        .WithSummary("Reads raw bytes from a memory region. Omit address/length to read the whole region (where its size is known).")
        .WithDescription("The response is a base64-encoded JSON byte string. Supply both address and length, or omit both.")
        .Produces<byte[]>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Driver");

        instance.MapPost("/driver/{region}", async (int index, string region, WriteDriverRequest request, GamehookInstances instances, CancellationToken cancellationToken) =>
        {
            if (!instances.TryGet(index, out var router))
                return ApiProblems.InstanceNotFound(index);
            if (!router.Session.IsContinuousReadEnabled)
                return ApiProblems.ContinuousReadDisabled("Writing");

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
        .WithDescription("Returns 409 while continuous read mode is disabled.")
        .Produces<SuccessResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .WithTags("Driver");
    }

    private static DriverRegistration? FindRegistration(IEnumerable<DriverRegistration> registrations, string name) =>
        registrations.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static DriverResponse CreateDriverResponse(GamehookRouter router, IEnumerable<DriverRegistration> registrations)
    {
        var name = router.DriverName!;
        int? port = null;
        if (FindRegistration(registrations, name) is { DefaultPort: { } defaultPort })
        {
            try
            {
                port = NetworkEndpoint.Parse(router.DriverSourcePath, defaultPort, name).Port;
            }
            catch (ArgumentException)
            {
                // Router keeps an unparseable source when the driver failed to load; report no port.
            }
        }

        return new DriverResponse(name, port);
    }
}

/// <summary>Selects a driver and optional source path.</summary>
public sealed record SetDriverRequest(
    [property: System.ComponentModel.Description("Driver name to select.")] string Value,
    [property: System.ComponentModel.Description("Optional driver-specific source path.")] string? Source = null,
    [property: System.ComponentModel.Description("Optional port for network drivers (RetroArch default 55355, SuperShuckie default 55356).")] int? Port = null);

/// <summary>Writes bytes to a memory region at a region-relative offset.</summary>
public sealed record WriteDriverRequest(
    [property: System.ComponentModel.Description("Region-relative byte offset.")] ulong Address,
    [property: System.ComponentModel.Description("Bytes to write as integers from 0 through 255.")] int[] Data);
