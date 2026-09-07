// Message Router

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Protocol;

public sealed class MessageRouter : IMessageRouter
{
    private readonly ILogger<MessageRouter> _logger;
    private readonly ConcurrentDictionary<string, Func<string, MessageEnvelope, CancellationToken, Task<MessageEnvelope?>>> _handlers = new();

    public MessageRouter(ILogger<MessageRouter> logger)
    {
        _logger = logger;
    }

    public void RegisterHandler(string messageType, Func<string, MessageEnvelope, CancellationToken, Task<MessageEnvelope?>> handler)
    {
        _handlers[messageType] = handler;
        _logger.LogDebug("Registered handler for message type: {Type}", messageType);
    }

    public async Task<MessageEnvelope?> RouteAsync(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        if (_handlers.TryGetValue(message.Type, out var handler))
        {
            _logger.LogDebug("Routing {Kind} message {Type} from {ClientId}",
                message.Kind, message.Type, clientId);
            return await handler(clientId, message, ct);
        }

        _logger.LogWarning("No handler for message type: {Type}", message.Type);
        return MessageEnvelope.Response(
            message.Id,
            "error",
            new NormalizedError(
                "unknown_message_type",
                $"Unknown message type: {message.Type}",
                null,
                false,
                null,
                null));
    }
}
