using Gamehook.Domain;
using Gamehook.Domain.Models;
using Gamehook.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;

namespace Gamehook.RestApi;

/// Runs the REST API's own Kestrel instance inside the app's existing process/host, sharing the
/// same GamehookInstances (and so the same per-instance session/mapper/driver state) the Avalonia
/// UI uses - one set of instances, two ways in, no duplicated load/read/write logic.
public sealed class GamehookApiHostedService(
    GamehookInstances instances,
    IEnumerable<DriverRegistration> driverRegistrations,
    FilesystemProvider filesystemProvider,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ApiBindStatus bindStatus,
    SettingsService settings) : IHostedService
{
    private WebApplication? app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);
        builder.Services.AddSingleton(instances);
        foreach (var registration in driverRegistrations) builder.Services.AddSingleton(registration);
        builder.Services.AddSingleton(filesystemProvider);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<WebSocketConnectionTracker>();
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Description = """
                ## Instances

                Gamehook can run several instances at once, each with its own driver and mapper (for example Pokemon Red over RetroArch and Pokemon Blue over SuperShuckie); the UI shows each as a tab. `GET /instances` lists them. Instances are numbered 0 through n-1 and every instance-specific route lives under `/instances/{index}`. There is always at least one instance, `0`. `POST /instances` adds one; `DELETE /instances/{index}` removes one and shifts later instances down by one index.

                ## Read instance properties

                Call `GET /instances/{index}/properties` once to get a full snapshot of the instance's loaded mapper's properties. The response is a nested JSON object; dots in mapper property paths become nested objects.

                Then connect to `ws://127.0.0.1:<port>/instances/{index}/ws` to receive updates. Each successful mapper read sends an array containing only changed properties, with each item's dotted `path`, decoded `value`, and raw `bytes`. The WebSocket sends no initial snapshot. Apply updates to the snapshot by `path`; after reconnecting, call `GET /instances/{index}/properties` again.

                A mapper must be loaded before the snapshot or updates are available.

                ## Continuous read mode

                Continuous read mode is on by default and applies to every instance. `POST /settings` with `{ "continuousRead": false }` switches to a low-power mode for the current session: the continuous driver read loops stop, WebSocket connections are closed and refused, and writes are refused. `GET /instances/{index}/properties` (and single-property GETs) then return the values from the last read; add `?read=true` to read the driver first and get values as of that request.
                """;
            return Task.CompletedTask;
        }));

        var port = configuration.GetValue<int>("Port");
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapOpenApi();
        app.MapScalarApiReference("/scalar", options =>
        {
            options.DefaultOpenAllTags = true;
        });
        app.UseWebSockets();
        app.MapMapperEndpoints();
        app.MapInstanceEndpoints();
        app.MapHealthEndpoint();
        app.MapSettingsEndpoints();

        // A bind failure (almost always another Gamehook instance already holding the port) must
        // not take the whole app down with it - the mapper/driver session works fine with no API,
        // so swallow it here, park the reason on ApiBindStatus for the UI to surface once, and
        // carry on with app left null (StopAsync below then has nothing to stop).
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            loggerFactory.CreateLogger("Gamehook.RestApi").LogWarning(ex, "Failed to bind REST API to port {Port}.", port);
            bindStatus.Error = $"Gamehook could not start the REST API/WebSocket server on port {port}.\n\n{ex.Message}\n\nGamehook will still run, but API and websocket connectivity will be unavailable for this session. Is another Gamehook instance running?";
            await app.DisposeAsync().ConfigureAwait(false);
            app = null;
            return;
        }

        loggerFactory.CreateLogger("Gamehook.RestApi")
            .LogInformation("REST API listening on http://127.0.0.1:{Port}.", port);
        loggerFactory.CreateLogger("Gamehook.RestApi")
            .LogInformation("API documentation: http://127.0.0.1:{Port}/scalar.", port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (app is not null)
        {
            // Kestrel's graceful stop waits for open connections to finish on their own, and its
            // own cancellation-triggered abort is unreliable for a long-lived upgraded WebSocket
            // connection - an open /ws client could hang shutdown (and Ctrl+C/the window close
            // button with it) indefinitely. Abort every tracked socket directly first so the
            // pending Send/ReceiveAsync in WebSocketEndpoints unblocks immediately, then let
            // Kestrel stop with an already-cancelled token as a belt-and-suspenders timeout.
            app.Services.GetRequiredService<WebSocketConnectionTracker>().AbortAll();
            await app.StopAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
