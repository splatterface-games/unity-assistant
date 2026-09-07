// Splatter Persistence Interfaces

using Splatter.Protocol;

namespace Splatter.Service.Storage;

public interface IPersistenceStore
{
    Task InitializeAsync(CancellationToken ct);

    // Conversations
    Task<Conversation> CreateConversationAsync(string workspaceId, string? title, string? providerId, string? modelId, AgentPermissionMode mode, CancellationToken ct);
    Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct);
    Task<IReadOnlyList<Conversation>> ListConversationsAsync(string workspaceId, int limit, int offset, CancellationToken ct);
    Task UpdateConversationAsync(Conversation conversation, CancellationToken ct);
    Task DeleteConversationAsync(string conversationId, CancellationToken ct);

    // Turns
    Task<ConversationTurn> CreateTurnAsync(string conversationId, TurnRole role, CancellationToken ct);
    Task UpdateTurnAsync(string conversationId, ConversationTurn turn, CancellationToken ct);

    // Sessions
    Task<AgentSession> CreateSessionAsync(string workspaceId, string conversationId, string providerId, string? modelId, AgentPermissionMode mode, CancellationToken ct);
    Task<AgentSession?> GetSessionAsync(string sessionId, CancellationToken ct);
    Task UpdateSessionAsync(AgentSession session, CancellationToken ct);
    /// <summary>The most recent harness/adapter session id recorded for a conversation,
    /// used to resume the underlying CLI session (e.g. Claude Code --resume) on a new turn.</summary>
    Task<string?> GetLatestAdapterSessionIdAsync(string conversationId, CancellationToken ct);

    // Permissions
    Task<PermissionGrant?> GetGrantAsync(string sessionId, string toolId, PermissionClass permissionClass, CancellationToken ct);
    Task CreateGrantAsync(PermissionGrant grant, CancellationToken ct);
    Task<IReadOnlyList<PermissionGrant>> GetSessionGrantsAsync(string sessionId, CancellationToken ct);

    // Checkpoints
    Task CreateCheckpointAsync(Checkpoint checkpoint, CancellationToken ct);
    Task<Checkpoint?> GetCheckpointAsync(string checkpointId, CancellationToken ct);
    Task<IReadOnlyList<Checkpoint>> GetSessionCheckpointsAsync(string sessionId, CancellationToken ct);

    // Tool Audit
    Task CreateToolAuditAsync(ToolAuditRecord audit, CancellationToken ct);
    Task<IReadOnlyList<ToolAuditRecord>> GetSessionAuditsAsync(string sessionId, CancellationToken ct);

    // Generator Jobs
    Task CreateGeneratorJobAsync(GeneratorJob job, CancellationToken ct);
    Task<GeneratorJob?> GetGeneratorJobAsync(string jobId, CancellationToken ct);
    Task UpdateGeneratorJobAsync(GeneratorJob job, CancellationToken ct);
    Task<IReadOnlyList<GeneratorJob>> GetRecoverableJobsAsync(string workspaceId, CancellationToken ct);
}

public interface IEventJournal
{
    Task AppendAsync(string workspaceId, string sessionId, IAgentEvent agentEvent, CancellationToken ct);
    IAsyncEnumerable<IAgentEvent> ReplayAsync(string sessionId, DateTimeOffset? since, CancellationToken ct);
    Task<IReadOnlyList<IAgentEvent>> GetRecentAsync(string sessionId, int count, CancellationToken ct);
}

public interface ICredentialStore
{
    Task<string?> GetAsync(string providerId, CancellationToken ct);
    Task SetAsync(string providerId, string credential, CancellationToken ct);
    Task DeleteAsync(string providerId, CancellationToken ct);
    Task<bool> ExistsAsync(string providerId, CancellationToken ct);
}
