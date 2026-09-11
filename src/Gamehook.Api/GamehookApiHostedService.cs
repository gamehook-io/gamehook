using Gamehook.Domain;
using Gamehook.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;

namespace Gamehook.Api;

/// Runs the REST API's own Kestrel instance inside the app's existing process/host, sharing the
/// same GamehookRouter (and so the same GamehookSession/Mapper/Driver state) the Avalonia UI
/// uses - one instance, two ways in, no duplicated load/read/write logic.
public sealed class GamehookApiHostedService(
    GamehookRouter router,
    FilesystemProvider filesystemProvider,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ApiBindStatus bindStatus) : IHostedService
{
    private WebApplication? app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);
        builder.Services.AddSingleton(router);
        builder.Services.AddSingleton(filesystemProvider);
        builder.Services.AddSingleton<WebSocketConnectionTracker>();
        builder.Services.AddOpenApi();

        var port = configuration.GetValue<int>("Port");
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        app = builder.Build();
        app.MapOpenApi();
        app.MapScalarApiReference("/", options =>
        {
            // Trim the client-picker down to what's actually useful for this API instead of
            // Scalar's full ~30-client default list.
            options.EnabledTargets = [ScalarTarget.Shell, ScalarTarget.JavaScript, ScalarTarget.Node, ScalarTarget.CSharp];
            options.EnabledClients = [ScalarClient.Curl, ScalarClient.Fetch, ScalarClient.HttpClient];
            options.WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl);
        });
        app.MapMapperEndpoints();
        app.MapPropertiesEndpoints();
        app.MapDriverEndpoints();
        app.MapPropertyChangesWebSocket();

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
            loggerFactory.CreateLogger("Gamehook.Api").LogWarning(ex, "Failed to bind REST API to port {Port}.", port);
            bindStatus.Error = $"Gamehook could not start the REST API/WebSocket server on port {port}.\n\n{ex.Message}\n\nGamehook will still run, but API and websocket connectivity will be unavailable for this session. Is another Gamehook instance running?";
            await app.DisposeAsync().ConfigureAwait(false);
            app = null;
            return;
        }

        loggerFactory.CreateLogger("Gamehook.Api")
            .LogInformation("REST API listening on http://127.0.0.1:{Port}.", port);
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
