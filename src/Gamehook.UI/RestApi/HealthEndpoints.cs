using Gamehook.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

public static class HealthEndpoints
{
    public static void MapHealthEndpoint(this WebApplication app)
    {
        app.MapGet("/health", (GamehookRouter router) =>
        {
            var session = router.Session;
            var healthy = router.Mapper is not null
                && router.DriverName is not null
                && session.IsConnected
                && !session.IsConnecting
                && session.ConnectionWarning is null
                && session.DataWarning is null;

            return healthy
                ? Results.Text("Healthy")
                : Results.Text("Unhealthy", statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("GetHealth")
        .WithSummary("Reports whether Gamehook has a loaded mapper and a healthy mapper session.")
        .WithDescription("Returns plain-text Healthy with HTTP 200 when a mapper and driver are loaded and the session has no connection or data warnings. Otherwise returns Unhealthy with HTTP 503.")
        .Produces(StatusCodes.Status200OK, contentType: "text/plain")
        .Produces(StatusCodes.Status503ServiceUnavailable, contentType: "text/plain")
        .WithTags("Gamehook");
    }
}
