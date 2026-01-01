using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace HowlDev.Web.Helpers.WebSockets;

/// <summary>
/// Register a socket by passing in an HttpContext value and the key to register with. Currently only 
/// supports sending messages.
/// </summary>
/// <typeparam name="T">Type for the keys in the inner ConcurrentDictionary</typeparam>
public class WebSocketService<T> where T : notnull {
    private readonly ConcurrentDictionary<T, ConcurrentDictionary<string, WebSocket>> sockets = new();
    private readonly ILogger<WebSocketService<T>> _logger;
    private readonly CancellationTokenRegistration _shutdownRegistration;

    /// <summary>
    /// Do not instantiate directly. Use the DI container.
    /// </summary>
    public WebSocketService(ILogger<WebSocketService<T>> logger, IHostApplicationLifetime lifetime) {
        _logger = logger;

        // Register shutdown handler
        _shutdownRegistration = lifetime.ApplicationStopping.Register(() => {
            _logger.LogInformation("Application is stopping, closing all WebSocket connections...");
            CloseAllSocketsSync();
            _logger.LogInformation("All WebSocket connections closed.");
        });
    }

    /// <summary>
    /// Call directly in your program/endpoint. Await it and presume that you won't return anything after it; it 
    /// swallows the response until it closes. <br/>
    /// This "subscribes" to the key until the connection closes or the app stops.
    /// </summary>
    public async Task RegisterSocket(HttpContext context, T key) {
        if (!context.WebSockets.IsWebSocketRequest) throw new Exception("Not a web socket request. Did you enable the middleware?");
        var inner = sockets.GetOrAdd(key, _ => new ConcurrentDictionary<string, WebSocket>());
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await AddNewWebsocket(webSocket, inner);
    }

    /// <summary>
    /// Send a string to the subscribers of the current key. 
    /// </summary>
    public async Task SendSocketMessage(T key, string message) {
        if (!sockets.TryGetValue(key, out var inner)) {
            return;
        }
        await SendMessage(inner, message);
    }

    private async Task AddNewWebsocket(WebSocket webSocket, ConcurrentDictionary<string, WebSocket> inner) {
        var connectionId = Guid.NewGuid().ToString();
        inner.TryAdd(connectionId, webSocket);

        var buffer = new byte[1024 * 4];
        WebSocketReceiveResult? result = null;

        try {
            do {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
            } while (!(result?.CloseStatus.HasValue ?? false));
        } catch (Exception ex) {
            _logger.LogError("WebSocket error: {message}", ex.Message);
        } finally {
            // Clean up this connection from the inner map
            inner.TryRemove(connectionId, out var _);

            if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted) {
                try {
                    await webSocket.CloseAsync(result?.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result?.CloseStatusDescription, CancellationToken.None);
                } catch { }
            }
            try { webSocket.Dispose(); } catch { }
        }
    }

    private static async Task SendMessage(ConcurrentDictionary<string, WebSocket> inner, string message) {
        var buffer = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(buffer);

        // Snapshot the connections to avoid enumeration issues
        var snapshot = inner.ToArray();

        foreach (var (id, socket) in snapshot) {
            if (socket == null) {
                inner.TryRemove(id, out var _);
                continue;
            }

            if (socket.State != WebSocketState.Open) {
                inner.TryRemove(id, out var _);
                try { socket.Dispose(); } catch { }
                continue;
            }

            try {
                await socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            } catch {
                inner.TryRemove(id, out var _);
                try { socket.Dispose(); } catch { }
            }
        }
    }

    private static async Task CloseSocketAsync(WebSocket socket) {
        try {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived) {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service disposed", CancellationToken.None);
            }
        } catch { }

        try { socket.Dispose(); } catch { }
    }

    private void CloseAllSocketsSync() {
        var closeTasks = new List<Task>();

        foreach (var (outerKey, inner) in sockets.ToArray()) {
            foreach (var (id, socket) in inner.ToArray()) {
                if (socket != null) {
                    inner.TryRemove(id, out _);
                    closeTasks.Add(CloseSocketAsync(socket));
                }
            }
            sockets.TryRemove(outerKey, out _);
        }

        if (closeTasks.Count > 0) {
            Task.WhenAll(closeTasks).Wait(TimeSpan.FromSeconds(5));
        }
    }
}