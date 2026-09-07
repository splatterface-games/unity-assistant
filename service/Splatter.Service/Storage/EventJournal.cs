// Event Journal - Durable event stream storage

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Storage;

public sealed class EventJournal : IEventJournal
{
    private readonly ILogger<EventJournal> _logger;
    private readonly ServiceConfiguration _config;
    private readonly ConcurrentDictionary<string, List<StoredEvent>> _memoryCache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public EventJournal(ILogger<EventJournal> logger, ServiceConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task AppendAsync(string workspaceId, string sessionId, IAgentEvent agentEvent, CancellationToken ct)
    {
        var stored = new StoredEvent
        {
            Id = Splatter.Protocol.EventId.New().Value,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            EventType = agentEvent.Type,
            Timestamp = agentEvent.Timestamp,
            Payload = JsonSerializer.Serialize(agentEvent, agentEvent.GetType(), JsonOptions)
        };

        // Add to memory cache
        var events = _memoryCache.GetOrAdd(sessionId, _ => new List<StoredEvent>());
        lock (events)
        {
            events.Add(stored);
        }

        // Persist to disk
        await PersistEventAsync(workspaceId, stored, ct);

        _logger.LogDebug("Appended event {Type} to session {SessionId}", agentEvent.Type, sessionId);
    }

    public async IAsyncEnumerable<IAgentEvent> ReplayAsync(string sessionId, DateTimeOffset? since, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Try memory cache first
        if (_memoryCache.TryGetValue(sessionId, out var cachedEvents))
        {
            List<StoredEvent> snapshot;
            lock (cachedEvents)
            {
                snapshot = cachedEvents.ToList();
            }

            foreach (var stored in snapshot.OrderBy(e => e.Timestamp))
            {
                if (since.HasValue && stored.Timestamp <= since.Value)
                    continue;

                var evt = DeserializeEvent(stored);
                if (evt != null)
                    yield return evt;
            }
            yield break;
        }

        // Load from disk
        await foreach (var stored in LoadEventsFromDiskAsync(sessionId, ct))
        {
            if (since.HasValue && stored.Timestamp <= since.Value)
                continue;

            var evt = DeserializeEvent(stored);
            if (evt != null)
                yield return evt;
        }
    }

    public async Task<IReadOnlyList<IAgentEvent>> GetRecentAsync(string sessionId, int count, CancellationToken ct)
    {
        var events = new List<IAgentEvent>();

        await foreach (var evt in ReplayAsync(sessionId, null, ct))
        {
            events.Add(evt);
        }

        return events.TakeLast(count).ToList();
    }

    private async Task PersistEventAsync(string workspaceId, StoredEvent evt, CancellationToken ct)
    {
        var dir = Path.Combine(_config.DataDirectory, "events", workspaceId);
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, $"{evt.SessionId}.jsonl");
        var line = JsonSerializer.Serialize(evt, JsonOptions) + "\n";

        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(file, line, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async IAsyncEnumerable<StoredEvent> LoadEventsFromDiskAsync(string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Search all workspace directories for this session
        var eventsDir = Path.Combine(_config.DataDirectory, "events");
        if (!Directory.Exists(eventsDir))
            yield break;

        foreach (var workspaceDir in Directory.GetDirectories(eventsDir))
        {
            var file = Path.Combine(workspaceDir, $"{sessionId}.jsonl");
            if (!File.Exists(file))
                continue;

            await foreach (var line in File.ReadLinesAsync(file, ct))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                StoredEvent? stored = null;
                try
                {
                    stored = JsonSerializer.Deserialize<StoredEvent>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse event line");
                }

                if (stored != null)
                    yield return stored;
            }
        }
    }

    private IAgentEvent? DeserializeEvent(StoredEvent stored)
    {
        try
        {
            return stored.EventType switch
            {
                "session.started" => JsonSerializer.Deserialize<AgentSessionStartedEvent>(stored.Payload, JsonOptions),
                "session.ready" => JsonSerializer.Deserialize<AgentSessionReadyEvent>(stored.Payload, JsonOptions),
                "message.delta" => JsonSerializer.Deserialize<AgentMessageDeltaEvent>(stored.Payload, JsonOptions),
                "thought.delta" => JsonSerializer.Deserialize<AgentThoughtDeltaEvent>(stored.Payload, JsonOptions),
                "plan.updated" => JsonSerializer.Deserialize<AgentPlanUpdateEvent>(stored.Payload, JsonOptions),
                "tool.requested" => JsonSerializer.Deserialize<AgentToolRequestedEvent>(stored.Payload, JsonOptions),
                "tool.started" => JsonSerializer.Deserialize<AgentToolStartedEvent>(stored.Payload, JsonOptions),
                "tool.delta" => JsonSerializer.Deserialize<AgentToolDeltaEvent>(stored.Payload, JsonOptions),
                "tool.completed" => JsonSerializer.Deserialize<AgentToolCompletedEvent>(stored.Payload, JsonOptions),
                "permission.requested" => JsonSerializer.Deserialize<AgentPermissionRequestedEvent>(stored.Payload, JsonOptions),
                "diff.proposed" => JsonSerializer.Deserialize<AgentDiffProposedEvent>(stored.Payload, JsonOptions),
                "file.changed" => JsonSerializer.Deserialize<AgentFileChangedEvent>(stored.Payload, JsonOptions),
                "session.cancelled" => JsonSerializer.Deserialize<AgentSessionCancelledEvent>(stored.Payload, JsonOptions),
                "session.failed" => JsonSerializer.Deserialize<AgentSessionFailedEvent>(stored.Payload, JsonOptions),
                "session.completed" => JsonSerializer.Deserialize<AgentSessionCompletedEvent>(stored.Payload, JsonOptions),
                _ => null
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize event {Type}", stored.EventType);
            return null;
        }
    }

    private sealed class StoredEvent
    {
        public required string Id { get; init; }
        public required string WorkspaceId { get; init; }
        public required string SessionId { get; init; }
        public required string EventType { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public required string Payload { get; init; }
    }
}
