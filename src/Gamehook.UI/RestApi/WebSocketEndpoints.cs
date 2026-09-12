using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Gamehook.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gamehook.RestApi;

// Pushes property changes over a websocket instead of making clients poll GET /properties. One
// JSON array per successful read tick, containing only the properties whose value/bytes changed -
// same GamehookSession.PropertiesChanged diff the REST API's Kestrel instance already computes.
public static class WebSocketEndpoints
{
    public static void MapPropertyChangesWebSocket(this WebApplication app)
    {
        app.UseWebSockets();

        app.Map("/ws", async (HttpContext context, GamehookRouter router, WebSocketConnectionTracker tracker) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            tracker.Add(socket);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var channel = Channel.CreateUnbounded<IReadOnlyList<PropertyChange>>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            void OnChanged(IReadOnlyList<PropertyChange> changes) => channel.Writer.TryWrite(changes);
            router.Session.PropertiesChanged += OnChanged;

            var receiveTask = DiscardIncomingAsync(socket, lifetime);
            try
            {
                await PumpChangesAsync(socket, channel.Reader, lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                router.Session.PropertiesChanged -= OnChanged;
                lifetime.Cancel();
                await receiveTask.ConfigureAwait(false);
                tracker.Remove(socket);
            }
        })
        .WithName("PropertyChangesWebSocket")
        .WithSummary("Upgrades to a websocket. After every successful read tick, pushes a JSON array of changed properties: [{ path, value, bytes }].")
        .WithTags("Properties");
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
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
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
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lifetime.Cancel();
        }
    }

    private static object ToJson(PropertyChange change) => new
    {
        path = change.Path,
        value = change.Value,
        bytes = change.Bytes,
    };
}
