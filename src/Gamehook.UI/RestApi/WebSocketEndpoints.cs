using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Gamehook.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

// Pushes property changes over a websocket instead of making clients poll GET /instances/{index}/properties. One
// JSON array per successful read tick, containing only the properties whose value/bytes changed -
// same GamehookSession.PropertiesChanged diff the REST API's Kestrel instance already computes.
public static class WebSocketEndpoints
{
    public static void MapPropertyChangesWebSocket(this RouteGroupBuilder instance)
    {
        instance.MapGet("/ws", async (int index, HttpContext context, GamehookInstances instances, WebSocketConnectionTracker tracker) =>
        {
            if (!instances.TryGet(index, out var router))
            {
                await ApiProblems.InstanceNotFound(index).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                await ApiProblems.BadRequest("Connect to this endpoint using a WebSocket upgrade request.", "websocket_upgrade_required")
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }

            if (!router.Session.IsContinuousReadEnabled)
            {
                await ApiProblems.ContinuousReadDisabled("WebSocket updates")
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            tracker.Add(socket);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            // Separate from lifetime: cancelling a pending ReceiveAsync aborts the socket outright,
            // so disabling continuous read mode (or removing the instance) only stops the pump,
            // leaving the socket open to send a proper close frame below.
            using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var closeReason = "Continuous read mode disabled.";
            var channel = Channel.CreateUnbounded<IReadOnlyList<PropertyChange>>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            void StopPump(string reason)
            {
                closeReason = reason;
                try { pumpStop.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            void OnChanged(IReadOnlyList<PropertyChange> changes) => channel.Writer.TryWrite(changes);
            void OnContinuousReadChanged(bool enabled)
            {
                if (!enabled) StopPump("Continuous read mode disabled.");
            }
            void OnInstanceRemoved(int _, GamehookRouter removed)
            {
                if (ReferenceEquals(removed, router)) StopPump("Instance removed.");
            }
            router.Session.PropertiesChanged += OnChanged;
            router.Session.ContinuousReadChanged += OnContinuousReadChanged;
            instances.InstanceRemoved += OnInstanceRemoved;
            // Continuous read mode may have been disabled, or the instance removed, between the
            // checks above and subscribing.
            if (!router.Session.IsContinuousReadEnabled) StopPump("Continuous read mode disabled.");
            if (instances.IndexOf(router) < 0) StopPump("Instance removed.");

            var receiveTask = DiscardIncomingAsync(socket, lifetime);
            try
            {
                await PumpChangesAsync(socket, channel.Reader, pumpStop.Token).ConfigureAwait(false);
                if (!lifetime.IsCancellationRequested)
                    await CloseGoingAwayAsync(socket, receiveTask, closeReason).ConfigureAwait(false);
            }
            finally
            {
                router.Session.PropertiesChanged -= OnChanged;
                router.Session.ContinuousReadChanged -= OnContinuousReadChanged;
                instances.InstanceRemoved -= OnInstanceRemoved;
                lifetime.Cancel();
                await receiveTask.ConfigureAwait(false);
                tracker.Remove(socket);
            }
        })
        .WithName("PropertyChangesWebSocket")
        .WithSummary("Streams the instance's mapper property changes over WebSocket.")
        .WithDescription("Connect with ws://127.0.0.1:<port>/instances/{index}/ws. After each successful mapper read, the server sends one UTF-8 JSON text frame containing an array of changed properties. Each item has path (slash-free dotted property path), value (decoded value), and bytes (integer array from 0 through 255). No initial snapshot is sent. A normal HTTP request receives ProblemDetails with status 400, or 404 for an unknown instance. While continuous read mode is disabled, upgrade requests receive 409. Disabling continuous read mode or removing the instance closes open connections with status 1001 (going away).")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithTags("WebSocket");
    }

    private static async Task PumpChangesAsync(WebSocket socket, ChannelReader<IReadOnlyList<PropertyChange>> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var changes))
                {
                    var json = JsonSerializer.Serialize(changes.Select(ToJson));
                    await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
        }
    }

    // Sends the close frame and gives the client a moment to answer it (DiscardIncomingAsync ends
    // on the reply) before the caller tears the connection down.
    private static async Task CloseGoingAwayAsync(WebSocket socket, Task receiveTask, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, reason, timeout.Token).ConfigureAwait(false);
            await receiveTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
        }
    }

    // A push-only socket still has to read incoming frames to notice the client's close handshake
    // (or an abrupt disconnect) - without this, PumpChangesAsync would keep sending into a dead
    // connection until the next failed SendAsync.
    private static async Task DiscardIncomingAsync(WebSocket socket, CancellationTokenSource lifetime)
    {
        var buffer = new byte[1024];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
        }
        finally
        {
            lifetime.Cancel();
        }
    }

    // Cancellation, a dropped client, or a socket torn down by shutdown: the connection is simply over.
    private static bool IsConnectionEnd(Exception ex) =>
        ex is OperationCanceledException or WebSocketException or ObjectDisposedException;

    private static object ToJson(PropertyChange change) => new
    {
        path = change.Path,
        value = change.Value,
        bytes = change.Bytes.Select(value => (int)value).ToArray(),
    };
}
