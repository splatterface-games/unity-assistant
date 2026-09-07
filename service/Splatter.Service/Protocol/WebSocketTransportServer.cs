// WebSocket Transport Server

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Protocol;

public sealed class WebSocketTransportServer : ITransportServer, IDisposable
{
    private readonly ILogger<WebSocketTransportServer> _logger;
    private readonly ServiceConfiguration _config;
    private readonly IMessageRouter _router;
    private readonly McpHttpHandler _mcp;
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _workspaceClients = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    // Canonical wire-protocol options (camelCase, case-insensitive, string enums).
    private static readonly JsonSerializerOptions JsonOptions = ProtocolJson.Options;

    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    public WebSocketTransportServer(
        ILogger<WebSocketTransportServer> logger,
        ServiceConfiguration config,
        IMessageRouter router,
        McpHttpHandler mcp)
    {
        _logger = logger;
        _config = config;
        _router = router;
        _mcp = mcp;
    }

    public async Task<string> StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Find available port if not specified
        var port = _config.Port == 0 ? GetAvailablePort() : _config.Port;
        var prefix = $"http://{_config.Host}:{port}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        _acceptTask = AcceptConnectionsAsync(_cts.Token);

        var endpoint = $"ws://{_config.Host}:{port}/";
        _logger.LogInformation("WebSocket server started on {Endpoint}", endpoint);
        return endpoint;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();

        // Close all clients
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.CloseAsync("Server shutting down", ct);
            }
            catch { }
        }

        _listener?.Stop();
        _listener?.Close();

        if (_acceptTask != null)
        {
            try { await _acceptTask; } catch { }
        }
    }

    public async Task SendAsync(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            await client.SendAsync(message, ct);
        }
    }

    public async Task BroadcastAsync(string workspaceId, MessageEnvelope message, CancellationToken ct)
    {
        var targets = new List<ClientConnection>();
        if (_workspaceClients.TryGetValue(workspaceId, out var clientIds))
        {
            lock (clientIds)
            {
                foreach (var id in clientIds)
                    if (_clients.TryGetValue(id, out var client))
                        targets.Add(client);
            }
        }

        // Fallback: if no client is associated with this workspace, send to all connected
        // clients. The association can go stale when the editor reconnects after a domain
        // reload mid-turn (its new socket was never re-associated), which would otherwise
        // strand tool.execute broadcasts and hang the agent on tool timeouts. Safe here
        // because the transport is loopback / single-editor.
        if (targets.Count == 0)
            targets.AddRange(_clients.Values);

        await Task.WhenAll(targets.Select(c => c.SendAsync(message, ct)));
    }

    public async Task BroadcastToAllAsync(MessageEnvelope message, CancellationToken ct)
    {
        await Task.WhenAll(_clients.Values.Select(c => c.SendAsync(message, ct)));
    }

    public void DisconnectClient(string clientId, string reason)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            _ = client.CloseAsync(reason, CancellationToken.None);
            ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs
            {
                ClientId = clientId,
                Reason = reason
            });
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var expected = _config.AuthToken;
        if (string.IsNullOrEmpty(expected))
        {
            // No token configured -> loopback dev mode, allow.
            return true;
        }

        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = header[scheme.Length..].Trim();
        // Constant-time comparison to avoid leaking the token via timing.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    // Enforce the per-editor bearer token when one is configured.
                    // Loopback-only standalone runs may leave it unset for dev/tests.
                    if (!IsAuthorized(context.Request))
                    {
                        _logger.LogWarning("Rejected unauthenticated WebSocket connection from {Remote}",
                            context.Request.RemoteEndPoint);
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var clientId = $"client_{Guid.NewGuid():N}";

                    var client = new ClientConnection(clientId, wsContext.WebSocket, _logger);
                    _clients[clientId] = client;

                    _logger.LogDebug("Client connected: {ClientId} from {Remote}",
                        clientId, context.Request.RemoteEndPoint);

                    ClientConnected?.Invoke(this, new ClientConnectedEventArgs
                    {
                        ClientId = clientId,
                        RemoteEndpoint = context.Request.RemoteEndPoint?.ToString() ?? "unknown"
                    });

                    _ = HandleClientAsync(client, ct);
                }
                else if (context.Request.Url?.AbsolutePath?.TrimEnd('/').EndsWith("/mcp") == true)
                {
                    // MCP HTTP endpoint for harness CLIs (handled off the accept loop).
                    _ = _mcp.HandleAsync(context, ct);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task HandleClientAsync(ClientConnection client, CancellationToken ct)
    {
        try
        {
            await foreach (var message in client.ReceiveAsync(ct))
            {
                MessageReceived?.Invoke(this, new MessageReceivedEventArgs
                {
                    ClientId = client.Id,
                    Message = message
                });

                // Route and respond
                try
                {
                    var response = await _router.RouteAsync(client.Id, message, ct);
                    if (response != null)
                    {
                        await client.SendAsync(response, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error routing message {Type}", message.Type);
                    var errorResponse = MessageEnvelope.Response(
                        message.Id,
                        "error",
                        new NormalizedError(
                            ErrorCodes.ToolFailed,
                            "Internal error processing request",
                            ex.Message,
                            false,
                            null,
                            null));
                    await client.SendAsync(errorResponse, ct);
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("Client {ClientId} disconnected", client.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ClientId}", client.Id);
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            RemoveClientFromWorkspaces(client.Id);
            ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs
            {
                ClientId = client.Id,
                Reason = null
            });
        }
    }

    public void AssociateClientWithWorkspace(string clientId, string workspaceId)
    {
        var clients = _workspaceClients.GetOrAdd(workspaceId, _ => new HashSet<string>());
        lock (clients)
        {
            clients.Add(clientId);
        }
    }

    private void RemoveClientFromWorkspaces(string clientId)
    {
        foreach (var (_, clients) in _workspaceClients)
        {
            lock (clients)
            {
                clients.Remove(clientId);
            }
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Close();
    }

    private sealed class ClientConnection
    {
        public string Id { get; }
        private readonly WebSocket _socket;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ClientConnection(string id, WebSocket socket, ILogger logger)
        {
            Id = id;
            _socket = socket;
            _logger = logger;
        }

        public async Task SendAsync(MessageEnvelope message, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async IAsyncEnumerable<MessageEnvelope> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var messageBuffer = new List<byte>();

            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    yield break;
                }

                messageBuffer.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.Clear();

                    MessageEnvelope? envelope = null;
                    try
                    {
                        envelope = JsonSerializer.Deserialize<MessageEnvelope>(json, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Invalid JSON from client {ClientId}", Id);
                    }

                    if (envelope != null)
                    {
                        yield return envelope;
                    }
                }
            }
        }

        public async Task CloseAsync(string reason, CancellationToken ct)
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, ct);
            }
        }
    }
}
