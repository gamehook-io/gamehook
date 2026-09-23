using Gamehook.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/settings", (SettingsService settings) => Results.Ok(ToResponse(settings.Current)))
            .WithName("GetSettings")
            .WithSummary("Returns the runtime-changeable Gamehook settings.")
            .Produces<SettingsResponse>(StatusCodes.Status200OK)
            .WithTags("Settings");

        app.MapPost("/settings", (UpdateSettingsRequest request, SettingsService settings) =>
            Results.Ok(ToResponse(settings.Update(request.ContinuousRead))))
        .WithName("UpdateSettings")
        .WithSummary("Changes settings for the current session.")
        .WithDescription("Only supplied fields are changed. Changes apply immediately and are not saved; the next launch starts from appsettings.json again. continuousRead: false stops the continuous driver read loop, closes and refuses WebSocket connections, and refuses writes; property GETs then read the driver on demand. continuousRead: true resumes continuous reads.")
        .Produces<SettingsResponse>(StatusCodes.Status200OK)
        .WithTags("Settings");
    }

    private static SettingsResponse ToResponse(GamehookSettings settings) => new(settings.ContinuousRead);
}
