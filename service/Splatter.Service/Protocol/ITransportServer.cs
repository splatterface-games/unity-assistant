// Splatter Transport Interfaces

using Splatter.Protocol;

namespace Splatter.Service.Protocol;

public interface ITransportServer
{
    event EventHandler<ClientConnectedEventArgs>? ClientConnected;
    event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;
    event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    Task<string> StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task SendAsync(string clientId, MessageEnvelope message, CancellationToken ct);
    Task BroadcastAsync(string workspaceId, MessageEnvelope message, CancellationToken ct);
    /// <summary>Send to every connected client (for workspace-agnostic events such as
    /// permission prompts). The transport is loopback / single-editor.</summary>
    Task BroadcastToAllAsync(MessageEnvelope message, CancellationToken ct);
    void DisconnectClient(string clientId, string reason);
}

public sealed class ClientConnectedEventArgs : EventArgs
{
    public required string ClientId { get; init; }
    public required string RemoteEndpoint { get; init; }
}

public sealed class ClientDisconnectedEventArgs : EventArgs
{
    public required string ClientId { get; init; }
    public required string? Reason { get; init; }
}

public sealed class MessageReceivedEventArgs : EventArgs
{
    public required string ClientId { get; init; }
    public required MessageEnvelope Message { get; init; }
}

public interface IMessageRouter
{
    void RegisterHandler(string messageType, Func<string, MessageEnvelope, CancellationToken, Task<MessageEnvelope?>> handler);
    Task<MessageEnvelope?> RouteAsync(string clientId, MessageEnvelope message, CancellationToken ct);
}
