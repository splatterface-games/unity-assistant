// Permission Engine Interfaces

using Splatter.Protocol;

namespace Splatter.Service.Permissions;

public interface IPermissionEngine
{
    /// <summary>
    /// Check if a tool call is allowed.
    /// </summary>
    Task<PermissionCheckResult> CheckPermissionAsync(AgentSession session, ToolCall toolCall, CancellationToken ct);

    /// <summary>
    /// Record a user's decision on a permission request.
    /// </summary>
    Task<PermissionDecision> RecordDecisionAsync(string requestId, PermissionOutcome outcome, PermissionScope scope, string? reason, CancellationToken ct);

    /// <summary>
    /// Bubble a permission request for an arbitrary tool (including harness-native tools
    /// like Bash/Write that aren't in the registry) to the UI, and await the user's
    /// decision. Returns a Denied decision on timeout.
    /// </summary>
    Task<PermissionDecision> RequestAndAwaitAsync(string sessionId, string? turnId, string toolId, string title, string description, CancellationToken ct);

    /// <summary>
    /// Gates an MCP tools/call from an interactive harness terminal (launcher mode).
    /// Reads/captures and mode-approved calls pass silently; read-only sessions get an
    /// immediate denial; otherwise blocks on an editor Allow/Deny prompt up to
    /// <paramref name="timeout"/> and fails safe to Denied.
    /// </summary>
    Task<PermissionDecision> GateToolCallAsync(AgentSession session, ToolCall toolCall, string argsSummary, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Get a pending permission request.
    /// </summary>
    Task<PermissionRequest?> GetPendingRequestAsync(string requestId, CancellationToken ct);

    /// <summary>
    /// Check if a session has any pending permission requests.
    /// </summary>
    bool HasPendingRequests(string sessionId);

    /// <summary>
    /// List all active grants for a session.
    /// </summary>
    Task<IReadOnlyList<PermissionGrant>> ListGrantsAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Revoke a specific grant.
    /// </summary>
    Task<bool> RevokeGrantAsync(string grantId, CancellationToken ct);

    /// <summary>
    /// Expire all pending requests for a session (e.g., on disconnect).
    /// </summary>
    Task ExpirePendingRequestsAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Set the permission policy for a workspace.
    /// </summary>
    Task SetPolicyAsync(string workspaceId, PermissionPolicy policy, CancellationToken ct);

    /// <summary>
    /// Get the permission policy for a workspace.
    /// </summary>
    Task<PermissionPolicy?> GetPolicyAsync(string workspaceId, CancellationToken ct);

    /// <summary>
    /// Check if a specific path is allowed for writing.
    /// </summary>
    bool IsPathAllowed(string path, PermissionClass permissionClass, PermissionPolicy? policy);

    /// <summary>
    /// Check if a host is allowed for network access.
    /// </summary>
    bool IsHostAllowed(string host, PermissionPolicy? policy);

    /// <summary>
    /// Event fired when a permission request is created.
    /// </summary>
    event EventHandler<PermissionRequest>? PermissionRequested;

    /// <summary>
    /// Event fired when a permission decision is made.
    /// </summary>
    event EventHandler<PermissionDecision>? PermissionDecided;
}

public sealed record PermissionCheckResult(
    bool Allowed,
    PermissionRequest? Request,
    PermissionGrant? Grant,
    string? Reason = null);

public sealed record PermissionPolicy(
    AgentPermissionMode DefaultMode,
    IReadOnlyDictionary<PermissionClass, PolicyRule>? ClassRules,
    IReadOnlyList<string>? AllowedHosts,
    IReadOnlyList<string>? BlockedHosts,
    IReadOnlyList<string>? AllowedPathPatterns,
    IReadOnlyList<string>? BlockedPathPatterns,
    IReadOnlyList<string>? BlockedTools,
    bool AllowShellExecution,
    bool AllowPackageManagement,
    bool AllowExternalNetwork,
    TimeSpan RequestTimeout);

public sealed record PolicyRule(
    PermissionOutcome DefaultOutcome,
    bool AlwaysPrompt,
    PermissionScope MaxGrantScope);
