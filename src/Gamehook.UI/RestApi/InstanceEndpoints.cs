using Gamehook.Domain;
using Gamehook.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

public static class InstanceEndpoints
{
    public const string InstanceRoute = "/instances/{index:int}";

    public static void MapInstanceEndpoints(this WebApplication app)
    {
        app.MapGet("/instances", (GamehookInstances instances, FilesystemProvider filesystemProvider, IEnumerable<DriverRegistration> registrations) =>
            Results.Ok(instances.Snapshot()
                .Select((router, index) => CreateInstanceResponse(index, router, filesystemProvider, registrations))
                .ToArray()))
        .WithName("GetInstances")
        .WithSummary("Lists every instance with its driver, mapper, and status.")
        .WithDescription("Instances are numbered 0 through n-1 in creation order. There is always at least one instance.")
        .Produces<InstanceResponse[]>(StatusCodes.Status200OK)
        .WithTags("Instance");

        app.MapPost("/instances", (GamehookInstances instances, FilesystemProvider filesystemProvider, IEnumerable<DriverRegistration> registrations) =>
        {
            var (index, router) = instances.Add();
            return Results.Created($"/instances/{index}", CreateInstanceResponse(index, router, filesystemProvider, registrations));
        })
        .WithName("CreateInstance")
        .WithSummary("Adds a new, empty instance and returns it. It appears as a new tab in the UI.")
        .WithDescription("Select a driver with POST /instances/{index}/driver, then load a mapper with POST /instances/{index}/mapper.")
        .Produces<InstanceResponse>(StatusCodes.Status201Created)
        .WithTags("Instance");

        var instance = app.MapGroup(InstanceRoute);

        instance.MapGet("/", (int index, GamehookInstances instances, FilesystemProvider filesystemProvider, IEnumerable<DriverRegistration> registrations) =>
            instances.TryGet(index, out var router)
                ? Results.Ok(CreateInstanceResponse(index, router, filesystemProvider, registrations))
                : ApiProblems.InstanceNotFound(index))
        .WithName("GetInstance")
        .WithSummary("Returns the instance's driver, mapper, and status.")
        .Produces<InstanceResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithTags("Instance");

        instance.MapDelete("/", (int index, GamehookInstances instances) =>
        {
            if (!instances.TryGet(index, out _))
                return ApiProblems.InstanceNotFound(index);

            return instances.Remove(index, out var error)
                ? Results.Ok(new SuccessResponse(true))
                : ApiProblems.Conflict(error ?? "Instance could not be removed.", "instance_remove_failed");
        })
        .WithName("DeleteInstance")
        .WithSummary("Unloads and removes the instance. Later instances shift down by one index.")
        .WithDescription("Its WebSocket connections are closed. Returns 409 for the last remaining instance.")
        .Produces<SuccessResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithTags("Instance");

        instance.MapInstanceMapperEndpoints();
        instance.MapPropertiesEndpoints();
        instance.MapDriverEndpoints();
        instance.MapInstanceHealthEndpoint();
        instance.MapPropertyChangesWebSocket();
    }

    private static InstanceResponse CreateInstanceResponse(int index, GamehookRouter router, FilesystemProvider filesystemProvider, IEnumerable<DriverRegistration> registrations) => new(
        index,
        router.Session.Status,
        router.Session.IsConnected,
        router.DriverName is null ? null : DriverEndpoints.CreateDriverResponse(router, registrations),
        MapperEndpoints.CreateLoadedMapperResponse(router, filesystemProvider));
}
