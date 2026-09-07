// Splatter - Agent Events
// Normalized event stream for all agent adapters


#nullable enable

using System;
using System.Collections.Generic;

namespace Splatter.Protocol
{
public interface IAgentEvent
{
    string Type { get; }
    string SessionId { get; }
    DateTimeOffset Timestamp { get; }
}

public sealed record AgentSessionStartedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string ConversationId,
    string ProviderId,
    string? ModelId,
    AgentPermissionMode Mode) : IAgentEvent
{
    public string Type => "session.started";
}

public sealed record AgentSessionReadyEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string? AdapterSessionId) : IAgentEvent
{
    public string Type => "session.ready";
}

public sealed record AgentMessageDeltaEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string Text,
    bool IsComplete = false) : IAgentEvent
{
    public string Type => "message.delta";
}

public sealed record AgentThoughtDeltaEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string Text,
    ThoughtVisibility Visibility = ThoughtVisibility.Summary) : IAgentEvent
{
    public string Type => "thought.delta";
}

public sealed record PlanItem(
    string Id,
    string Description,
    PlanItemStatus Status,
    IReadOnlyList<PlanItem>? Children);

public sealed record AgentPlanUpdateEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    IReadOnlyList<PlanItem> Items) : IAgentEvent
{
    public string Type => "plan.updated";
}

public sealed record AgentToolRequestedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    ToolCall ToolCall) : IAgentEvent
{
    public string Type => "tool.requested";
}

public sealed record AgentToolStartedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string ToolCallId,
    string ToolId) : IAgentEvent
{
    public string Type => "tool.started";
}

public sealed record AgentToolDeltaEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string ToolCallId,
    string Output) : IAgentEvent
{
    public string Type => "tool.delta";
}

public sealed record AgentToolCompletedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string ToolCallId,
    ToolResult Result) : IAgentEvent
{
    public string Type => "tool.completed";
}

public sealed record AgentPermissionRequestedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    PermissionRequest Request) : IAgentEvent
{
    public string Type => "permission.requested";
}

public sealed record FileDiff(
    string Path,
    string? OriginalContent,
    string NewContent,
    IReadOnlyList<DiffHunk>? Hunks,
    bool IsNewFile = false,
    bool IsDelete = false);

public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string Content);

public sealed record AgentDiffProposedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    FileDiff Diff) : IAgentEvent
{
    public string Type => "diff.proposed";
}

public sealed record AgentFileChangedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string Path,
    FileChangeKind Change,
    string? CheckpointId) : IAgentEvent
{
    public string Type => "file.changed";
}

public sealed record AgentCommandStartedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string ToolCallId,
    string Command,
    string? WorkingDirectory) : IAgentEvent
{
    public string Type => "command.started";
}

public sealed record AgentCommandCompletedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string TurnId,
    string ToolCallId,
    int ExitCode,
    string? Output,
    string? Error) : IAgentEvent
{
    public string Type => "command.completed";
}

public sealed record AgentSessionCancelledEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string? TurnId,
    string? Reason) : IAgentEvent
{
    public string Type => "session.cancelled";
}

public sealed record AgentSessionFailedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string? TurnId,
    NormalizedError Error) : IAgentEvent
{
    public string Type => "session.failed";
}

public sealed record AgentSessionCompletedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string? TurnId,
    UsageInfo? Usage) : IAgentEvent
{
    public string Type => "session.completed";
}

public sealed record IndexUpdatedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string IndexType,
    IndexState State,
    int? ItemCount,
    int? PendingCount) : IAgentEvent
{
    public string Type => "index.updated";
}

public sealed record GeneratorJobUpdatedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    GeneratorJob Job) : IAgentEvent
{
    public string Type => "generator.job.updated";
}

public sealed record CheckpointCreatedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    Checkpoint Checkpoint) : IAgentEvent
{
    public string Type => "checkpoint.created";
}

public sealed record ServiceReadyEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string Version,
    string WorkspaceId) : IAgentEvent
{
    public string Type => "service.ready";
}

public sealed record ServiceDisconnectedEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string? Reason) : IAgentEvent
{
    public string Type => "service.disconnected";
}
}
