// Splatter - Core ID Types
// Shared between Unity package and local service
// Plain readonly structs (C# 9 compatible so the schema compiles in Unity 6).

#nullable enable

using System;

namespace Splatter.Protocol
{
public readonly struct WorkspaceId : IEquatable<WorkspaceId>
{
    public string Value { get; }
    public WorkspaceId(string value) => Value = value;
    public static WorkspaceId New() => new($"ws_{Guid.NewGuid():N}");
    public static WorkspaceId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(WorkspaceId id) => id.Value;
    public bool Equals(WorkspaceId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is WorkspaceId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(WorkspaceId a, WorkspaceId b) => a.Equals(b);
    public static bool operator !=(WorkspaceId a, WorkspaceId b) => !a.Equals(b);
}

public readonly struct ConversationId : IEquatable<ConversationId>
{
    public string Value { get; }
    public ConversationId(string value) => Value = value;
    public static ConversationId New() => new($"conv_{Guid.NewGuid():N}");
    public static ConversationId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(ConversationId id) => id.Value;
    public bool Equals(ConversationId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is ConversationId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(ConversationId a, ConversationId b) => a.Equals(b);
    public static bool operator !=(ConversationId a, ConversationId b) => !a.Equals(b);
}

public readonly struct TurnId : IEquatable<TurnId>
{
    public string Value { get; }
    public TurnId(string value) => Value = value;
    public static TurnId New() => new($"turn_{Guid.NewGuid():N}");
    public static TurnId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(TurnId id) => id.Value;
    public bool Equals(TurnId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is TurnId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(TurnId a, TurnId b) => a.Equals(b);
    public static bool operator !=(TurnId a, TurnId b) => !a.Equals(b);
}

public readonly struct SessionId : IEquatable<SessionId>
{
    public string Value { get; }
    public SessionId(string value) => Value = value;
    public static SessionId New() => new($"ses_{Guid.NewGuid():N}");
    public static SessionId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(SessionId id) => id.Value;
    public bool Equals(SessionId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SessionId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(SessionId a, SessionId b) => a.Equals(b);
    public static bool operator !=(SessionId a, SessionId b) => !a.Equals(b);
}

public readonly struct EventId : IEquatable<EventId>
{
    public string Value { get; }
    public EventId(string value) => Value = value;
    public static EventId New() => new($"evt_{Guid.NewGuid():N}");
    public static EventId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(EventId id) => id.Value;
    public bool Equals(EventId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is EventId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(EventId a, EventId b) => a.Equals(b);
    public static bool operator !=(EventId a, EventId b) => !a.Equals(b);
}

public readonly struct ToolCallId : IEquatable<ToolCallId>
{
    public string Value { get; }
    public ToolCallId(string value) => Value = value;
    public static ToolCallId New() => new($"tool_{Guid.NewGuid():N}");
    public static ToolCallId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(ToolCallId id) => id.Value;
    public bool Equals(ToolCallId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is ToolCallId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(ToolCallId a, ToolCallId b) => a.Equals(b);
    public static bool operator !=(ToolCallId a, ToolCallId b) => !a.Equals(b);
}

public readonly struct PermissionRequestId : IEquatable<PermissionRequestId>
{
    public string Value { get; }
    public PermissionRequestId(string value) => Value = value;
    public static PermissionRequestId New() => new($"perm_{Guid.NewGuid():N}");
    public static PermissionRequestId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(PermissionRequestId id) => id.Value;
    public bool Equals(PermissionRequestId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PermissionRequestId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(PermissionRequestId a, PermissionRequestId b) => a.Equals(b);
    public static bool operator !=(PermissionRequestId a, PermissionRequestId b) => !a.Equals(b);
}

public readonly struct GrantId : IEquatable<GrantId>
{
    public string Value { get; }
    public GrantId(string value) => Value = value;
    public static GrantId New() => new($"grant_{Guid.NewGuid():N}");
    public static GrantId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(GrantId id) => id.Value;
    public bool Equals(GrantId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is GrantId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(GrantId a, GrantId b) => a.Equals(b);
    public static bool operator !=(GrantId a, GrantId b) => !a.Equals(b);
}

public readonly struct CheckpointId : IEquatable<CheckpointId>
{
    public string Value { get; }
    public CheckpointId(string value) => Value = value;
    public static CheckpointId New() => new($"ckpt_{Guid.NewGuid():N}");
    public static CheckpointId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(CheckpointId id) => id.Value;
    public bool Equals(CheckpointId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is CheckpointId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(CheckpointId a, CheckpointId b) => a.Equals(b);
    public static bool operator !=(CheckpointId a, CheckpointId b) => !a.Equals(b);
}

public readonly struct JobId : IEquatable<JobId>
{
    public string Value { get; }
    public JobId(string value) => Value = value;
    public static JobId New() => new($"job_{Guid.NewGuid():N}");
    public static JobId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(JobId id) => id.Value;
    public bool Equals(JobId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is JobId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(JobId a, JobId b) => a.Equals(b);
    public static bool operator !=(JobId a, JobId b) => !a.Equals(b);
}

public readonly struct ArtifactId : IEquatable<ArtifactId>
{
    public string Value { get; }
    public ArtifactId(string value) => Value = value;
    public static ArtifactId New() => new($"art_{Guid.NewGuid():N}");
    public static ArtifactId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(ArtifactId id) => id.Value;
    public bool Equals(ArtifactId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is ArtifactId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(ArtifactId a, ArtifactId b) => a.Equals(b);
    public static bool operator !=(ArtifactId a, ArtifactId b) => !a.Equals(b);
}

public readonly struct IndexId : IEquatable<IndexId>
{
    public string Value { get; }
    public IndexId(string value) => Value = value;
    public static IndexId New() => new($"idx_{Guid.NewGuid():N}");
    public static IndexId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(IndexId id) => id.Value;
    public bool Equals(IndexId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is IndexId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(IndexId a, IndexId b) => a.Equals(b);
    public static bool operator !=(IndexId a, IndexId b) => !a.Equals(b);
}

public readonly struct MessageId : IEquatable<MessageId>
{
    public string Value { get; }
    public MessageId(string value) => Value = value;
    public static MessageId New() => new($"msg_{Guid.NewGuid():N}");
    public static MessageId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(MessageId id) => id.Value;
    public bool Equals(MessageId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is MessageId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(MessageId a, MessageId b) => a.Equals(b);
    public static bool operator !=(MessageId a, MessageId b) => !a.Equals(b);
}

public readonly struct EditorInstanceId : IEquatable<EditorInstanceId>
{
    public string Value { get; }
    public EditorInstanceId(string value) => Value = value;
    public static EditorInstanceId New() => new($"editor_{Guid.NewGuid():N}");
    public static EditorInstanceId Parse(string value) => new(value);
    public override string ToString() => Value;
    public static implicit operator string(EditorInstanceId id) => id.Value;
    public bool Equals(EditorInstanceId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is EditorInstanceId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(EditorInstanceId a, EditorInstanceId b) => a.Equals(b);
    public static bool operator !=(EditorInstanceId a, EditorInstanceId b) => !a.Equals(b);
}
}
