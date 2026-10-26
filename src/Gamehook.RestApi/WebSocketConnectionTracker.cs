using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Gamehook.RestApi;

// Lets host shutdown kill every open /ws connection immediately. Kestrel's own graceful stop
// aborts connections via the request's cancellation token, but that plumbing is unreliable for a
// long-lived upgraded WebSocket connection - holding the WebSocket objects directly and calling
// Abort() on them ourselves is the only deterministic way to unblock a pending Send/ReceiveAsync.
public sealed class WebSocketConnectionTracker
{
    private readonly ConcurrentDictionary<WebSocket, byte> sockets = new();

    public void Add(WebSocket socket) => sockets.TryAdd(socket, 0);

    public void Remove(WebSocket socket) => sockets.TryRemove(socket, out _);

    public void AbortAll()
    {
        foreach (var socket in sockets.Keys)
        {
            try { socket.Abort(); }
            catch (ObjectDisposedException) { }
        }
    }
}
