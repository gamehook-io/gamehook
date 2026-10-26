using Gamehook.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

public static class HealthEndpoints
{
    public static void MapHealthEndpoint(this WebApplication app)
    {
        app.MapGet("/health", (GamehookInstances instances) =>
        {
            // Instances nobody has picked a driver for (a fresh, untouched tab) don't count against health.
            var inUse = instances.Snapshot().Where(router => router.DriverName is not null).ToArray();
            return HealthResult(inUse.Length > 0 && inUse.All(router => router.IsHealthy));
        })
        .WithName("GetHealth")
        .WithSummary("Reports whether every instance in use has a loaded mapper and a healthy mapper session.")
        .WithDescription("Returns plain-text Healthy with HTTP 200 when at least one instance has a driver selected and every instance with a driver selected has a loaded mapper with no connection or data warnings. Otherwise returns Unhealthy with HTTP 503. Use GET /instances/{index}/health for one instance.")
        .Produces(StatusCodes.Status200OK, contentType: "text/plain")
        .Produces(StatusCodes.Status503ServiceUnavailable, contentType: "text/plain")
        .WithTags("Gamehook");
    }

    public static void MapInstanceHealthEndpoint(this RouteGroupBuilder instance)
    {
        instance.MapGet("/health", (int index, GamehookInstances instances) =>
            instances.TryGet(index, out var router)
                ? HealthResult(router.IsHealthy)
                : ApiProblems.InstanceNotFound(index))
        .WithName("GetInstanceHealth")
        .WithSummary("Reports whether this instance has a loaded mapper and a healthy mapper session.")
        .WithDescription("Returns plain-text Healthy with HTTP 200 when a mapper and driver are loaded and the session has no connection or data warnings. Otherwise returns Unhealthy with HTTP 503.")
        .Produces(StatusCodes.Status200OK, contentType: "text/plain")
        .Produces(StatusCodes.Status503ServiceUnavailable, contentType: "text/plain")
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithTags("Instance");
    }

    private static IResult HealthResult(bool healthy) => healthy
        ? Results.Text("Healthy")
        : Results.Text("Unhealthy", statusCode: StatusCodes.Status503ServiceUnavailable);
}
