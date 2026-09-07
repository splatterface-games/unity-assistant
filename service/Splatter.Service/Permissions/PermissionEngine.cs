// Permission Engine Implementation

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Storage;
using Splatter.Service.Tools;

namespace Splatter.Service.Permissions;

public sealed class PermissionEngine : IPermissionEngine
{
    private readonly ILogger<PermissionEngine> _logger;
    private readonly IPersistenceStore _persistence;
    private readonly IToolRegistry _tools;
    private readonly ServiceConfiguration _config;
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, PermissionGrant> _sessionGrants = new();
    private readonly ConcurrentDictionary<string, PermissionPolicy> _policies = new();
    // Callers awaiting a user decision (RequestAndAwaitAsync), keyed by request id.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _decisionWaiters = new();

    public event EventHandler<PermissionRequest>? PermissionRequested;
    public event EventHandler<PermissionDecision>? PermissionDecided;

    public PermissionEngine(
        ILogger<PermissionEngine> logger,
        IPersistenceStore persistence,
        IToolRegistry tools,
        ServiceConfiguration config)
    {
        _logger = logger;
        _persistence = persistence;
        _tools = tools;
        _config = config;
    }

    public async Task<PermissionCheckResult> CheckPermissionAsync(AgentSession session, ToolCall toolCall, CancellationToken ct)
    {
        var tool = _tools.GetTool(toolCall.ToolId);
        if (tool == null)
        {
            return new PermissionCheckResult(false, null, null);
        }

        var permission = tool.Permission;

        // Check mode-based auto-approval
        if (IsAutoApprovedByMode(session.Mode, permission))
        {
            _logger.LogDebug("Tool {ToolId} auto-approved by mode {Mode}", toolCall.ToolId, session.Mode);
            return new PermissionCheckResult(true, null, null);
        }

        // Check existing grants
        var grant = await _persistence.GetGrantAsync(session.Id, toolCall.ToolId, permission.Class, ct);
        if (grant != null)
        {
            _logger.LogDebug("Tool {ToolId} approved by existing grant", toolCall.ToolId);
            return new PermissionCheckResult(true, null, grant);
        }

        // Check policy rules
        var policy = await GetPolicyAsync(session.WorkspaceId, ct);
        if (policy != null)
        {
            // Check if tool is blocked
            if (policy.BlockedTools?.Contains(toolCall.ToolId) == true)
            {
                _logger.LogWarning("Tool {ToolId} blocked by policy", toolCall.ToolId);
                return new PermissionCheckResult(false, null, null, "Tool blocked by policy");
            }

            // Check shell execution
            if (!policy.AllowShellExecution && permission.Class == PermissionClass.ExecuteShell)
            {
                _logger.LogWarning("Shell execution blocked by policy");
                return new PermissionCheckResult(false, null, null, "Shell execution blocked by policy");
            }

            // Check package management
            if (!policy.AllowPackageManagement && permission.Class == PermissionClass.PackageManage)
            {
                _logger.LogWarning("Package management blocked by policy");
                return new PermissionCheckResult(false, null, null, "Package management blocked by policy");
            }
        }

        // Create permission request
        var request = CreatePermissionRequest(session, toolCall, tool);
        _pendingRequests[request.Id] = new PendingRequest(request, DateTimeOffset.UtcNow);

        _logger.LogInformation("Permission requested for tool {ToolId}: {Title}", toolCall.ToolId, request.Preview.Title);

        // Fire event for UI
        OnPermissionRequested(request);

        return new PermissionCheckResult(false, request, null, "Requires user approval");
    }

    public async Task<PermissionDecision> RecordDecisionAsync(string requestId, PermissionOutcome outcome, PermissionScope scope, string? reason, CancellationToken ct)
    {
        var decision = new PermissionDecision(
            requestId,
            outcome,
            scope,
            PermissionDecisionSource.User,
            DateTimeOffset.UtcNow,
            reason);

        if (_pendingRequests.TryRemove(requestId, out var pending))
        {
            // If allowed and scoped beyond once, create a grant
            if (outcome == PermissionOutcome.Allowed && scope != PermissionScope.Once)
            {
                var tool = _tools.GetTool(pending.Request.ToolCallId);
                if (tool != null)
                {
                    var grantId = GrantId.New();
                    var expiresAt = scope switch
                    {
                        PermissionScope.Session => DateTimeOffset.UtcNow.AddHours(24),
                        PermissionScope.Workspace => DateTimeOffset.UtcNow.AddDays(7),
                        _ => (DateTimeOffset?)null
                    };

                    var grant = new PermissionGrant(
                        grantId,
                        "", // workspaceId from session - would extract from pending request
                        pending.Request.SessionId,
                        pending.Request.ToolCallId,
                        tool.Permission.Class,
                        scope,
                        DateTimeOffset.UtcNow,
                        expiresAt);

                    // Store in memory for session grants
                    _sessionGrants[grantId] = grant;

                    // Also persist for workspace/global grants
                    if (scope is PermissionScope.Workspace or PermissionScope.Global)
                    {
                        await _persistence.CreateGrantAsync(grant, ct);
                    }
                }
            }
        }

        // Wake any caller awaiting this decision (RequestAndAwaitAsync).
        if (_decisionWaiters.TryRemove(requestId, out var waiter))
            waiter.TrySetResult(decision);

        // Fire event
        OnPermissionDecided(decision);

        _logger.LogInformation("Permission decision recorded: {RequestId} -> {Outcome} ({Scope})", requestId, outcome, scope);
        return decision;
    }

    public Task<PermissionDecision> RequestAndAwaitAsync(
        string sessionId, string? turnId, string toolId, string title, string description, CancellationToken ct)
    {
        var request = new PermissionRequest(
            PermissionRequestId.New(),
            sessionId,
            turnId ?? "",
            toolId,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, title, null),
            new PermissionPreview(title, description, null, null, null, null, null),
            new[]
            {
                new PermissionOption("allow_once", "Allow Once", PermissionOutcome.Allowed, PermissionScope.Once),
                new PermissionOption("allow_session", "Allow for Session", PermissionOutcome.Allowed, PermissionScope.Session),
                new PermissionOption("deny_once", "Deny", PermissionOutcome.Denied, PermissionScope.Once)
            },
            DateTimeOffset.UtcNow.Add(_config.PermissionRequestTimeout));

        return AwaitDecisionAsync(request, _config.PermissionRequestTimeout, ct);
    }

    // Gates an MCP tools/call from an interactive harness terminal. Unlike
    // CheckPermissionAsync (which returns "requires approval" for the chat loop to
    // relay), this resolves the whole decision here: silent pass for reads/captures
    // and mode-approved calls, immediate denial for read-only sessions, otherwise a
    // blocking editor prompt that fails safe to Denied on timeout.
    public async Task<PermissionDecision> GateToolCallAsync(
        AgentSession session, ToolCall toolCall, string argsSummary, TimeSpan timeout, CancellationToken ct)
    {
        PermissionDecision Auto(PermissionOutcome outcome, PermissionDecisionSource source, string reason) =>
            new("gate_" + toolCall.Id, outcome, PermissionScope.Once, source, DateTimeOffset.UtcNow, reason);

        var tool = _tools.GetTool(toolCall.ToolId);
        if (tool == null)
            return Auto(PermissionOutcome.Denied, PermissionDecisionSource.Policy, $"Unknown tool '{toolCall.ToolId}'.");

        var permission = tool.Permission;

        // Reads and visual captures are pre-approved ("looking is free") - the same
        // contract the MCP tool list advertises to the harness.
        if (permission.Class is PermissionClass.ReadProject or PermissionClass.ReadExternal or PermissionClass.ScreenCapture)
            return Auto(PermissionOutcome.Allowed, PermissionDecisionSource.Mode, "Read/capture: pre-approved.");

        // Mode-based auto-approval (FullAuto / WorkspaceWrite; never spend/credentials).
        if (IsAutoApprovedByMode(session.Mode, permission))
            return Auto(PermissionOutcome.Allowed, PermissionDecisionSource.Mode, $"Auto-approved by {session.Mode} mode.");

        // Read-only sessions never prompt: mutations fail fast with a clear reason
        // the harness can show verbatim.
        if (session.Mode == AgentPermissionMode.ReadOnly)
            return Auto(PermissionOutcome.Denied, PermissionDecisionSource.Mode,
                "This Splatter session is read-only. Ask the user to launch a write-enabled session to make changes.");

        // "Allow for Session" from a prior prompt (in-memory), or a persisted
        // workspace/global grant.
        if (HasLiveSessionGrant(session.Id, toolCall.ToolId) ||
            await _persistence.GetGrantAsync(session.Id, toolCall.ToolId, permission.Class, ct) != null)
            return Auto(PermissionOutcome.Allowed, PermissionDecisionSource.User, "Approved by existing grant.");

        // Workspace policy blocks (same rules CheckPermissionAsync enforces).
        var policy = await GetPolicyAsync(session.WorkspaceId, ct);
        if (policy != null)
        {
            if (policy.BlockedTools?.Contains(toolCall.ToolId) == true)
                return Auto(PermissionOutcome.Denied, PermissionDecisionSource.Policy, "Tool blocked by workspace policy.");
            if (!policy.AllowShellExecution && permission.Class == PermissionClass.ExecuteShell)
                return Auto(PermissionOutcome.Denied, PermissionDecisionSource.Policy, "Shell execution blocked by workspace policy.");
            if (!policy.AllowPackageManagement && permission.Class == PermissionClass.PackageManage)
                return Auto(PermissionOutcome.Denied, PermissionDecisionSource.Policy, "Package management blocked by workspace policy.");
        }

        // Prompt the user in the editor and wait. The toolId rides in the ToolCallId
        // slot (matching RequestAndAwaitAsync) so a session-scoped grant recorded from
        // this prompt keys off the toolId.
        var request = new PermissionRequest(
            PermissionRequestId.New(),
            session.Id,
            "",
            toolCall.ToolId,
            permission,
            new PermissionPreview($"Allow {tool.DisplayName}?", argsSummary, null, null, null, null, null),
            new[]
            {
                new PermissionOption("allow_once", "Allow Once", PermissionOutcome.Allowed, PermissionScope.Once),
                new PermissionOption("allow_session", "Allow for Session", PermissionOutcome.Allowed, PermissionScope.Session),
                new PermissionOption("deny_once", "Deny", PermissionOutcome.Denied, PermissionScope.Once)
            },
            DateTimeOffset.UtcNow.Add(timeout));

        return await AwaitDecisionAsync(request, timeout, ct);
    }

    // A live in-memory "Allow for Session" grant for this exact tool. Matches ToolId
    // only (not class) so approving one mutation never silently approves others.
    private bool HasLiveSessionGrant(string sessionId, string toolId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var g in _sessionGrants.Values)
        {
            if (g.SessionId != sessionId || g.ToolId != toolId) continue;
            if (g.ExpiresAt != null && g.ExpiresAt <= now) continue;
            return true;
        }
        return false;
    }

    private async Task<PermissionDecision> AwaitDecisionAsync(PermissionRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decisionWaiters[request.Id] = tcs;
        _pendingRequests[request.Id] = new PendingRequest(request, DateTimeOffset.UtcNow);

        // Bubble to the UI (ApiMessageHandler broadcasts this to the editor).
        OnPermissionRequested(request);

        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return new PermissionDecision(request.Id, PermissionOutcome.Denied, PermissionScope.Once,
                PermissionDecisionSource.Timeout, DateTimeOffset.UtcNow, "Timed out waiting for the user.");
        }
        catch (OperationCanceledException)
        {
            return new PermissionDecision(request.Id, PermissionOutcome.Denied, PermissionScope.Once,
                PermissionDecisionSource.User, DateTimeOffset.UtcNow, "Cancelled.");
        }
        finally
        {
            _decisionWaiters.TryRemove(request.Id, out _);
            _pendingRequests.TryRemove(request.Id, out _);
        }
    }

    public Task<PermissionRequest?> GetPendingRequestAsync(string requestId, CancellationToken ct)
    {
        if (_pendingRequests.TryGetValue(requestId, out var pending))
        {
            // Check expiry
            if (pending.CreatedAt.Add(_config.PermissionRequestTimeout) < DateTimeOffset.UtcNow)
            {
                _pendingRequests.TryRemove(requestId, out _);
                return Task.FromResult<PermissionRequest?>(null);
            }
            return Task.FromResult<PermissionRequest?>(pending.Request);
        }
        return Task.FromResult<PermissionRequest?>(null);
    }

    public bool HasPendingRequests(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _pendingRequests)
        {
            // Check if request is for this session and not expired
            if (kvp.Value.Request.SessionId == sessionId &&
                kvp.Value.CreatedAt.Add(_config.PermissionRequestTimeout) >= now)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAutoApprovedByMode(AgentPermissionMode mode, PermissionRequirement permission)
    {
        // Read operations are always auto-approved
        if (permission.Class is PermissionClass.ReadProject or PermissionClass.ReadExternal)
            return true;

        // Generation spend and credential access always require an explicit user
        // decision, regardless of agent mode. Paid/asset-writing generation must
        // never be auto-approved (e.g. as the first action of a broad request).
        if (permission.Class is PermissionClass.GenerationSpend or PermissionClass.CredentialAccess)
            return false;

        // Full auto approves most things except critical
        if (mode == AgentPermissionMode.FullAuto && permission.Risk != PermissionRisk.Critical)
            return true;

        // Workspace write mode auto-approves project writes
        if (mode == AgentPermissionMode.WorkspaceWrite &&
            permission.Class == PermissionClass.WriteProject &&
            permission.Risk <= PermissionRisk.Medium)
            return true;

        return false;
    }

    private PermissionRequest CreatePermissionRequest(AgentSession session, ToolCall toolCall, ToolSpec tool)
    {
        return new PermissionRequest(
            PermissionRequestId.New(),
            session.Id,
            toolCall.RequestedBy.TurnId,
            toolCall.Id,
            tool.Permission,
            new PermissionPreview(
                $"{tool.DisplayName}",
                tool.Description,
                null, null, null, null, null),
            new[]
            {
                new PermissionOption("allow_once", "Allow Once", PermissionOutcome.Allowed, PermissionScope.Once),
                new PermissionOption("allow_session", "Allow for Session", PermissionOutcome.Allowed, PermissionScope.Session),
                new PermissionOption("deny_once", "Deny", PermissionOutcome.Denied, PermissionScope.Once),
                new PermissionOption("deny_session", "Deny for Session", PermissionOutcome.Denied, PermissionScope.Session)
            },
            DateTimeOffset.UtcNow.Add(_config.PermissionRequestTimeout));
    }

    public Task<IReadOnlyList<PermissionGrant>> ListGrantsAsync(string sessionId, CancellationToken ct)
    {
        var grants = _sessionGrants.Values
            .Where(g => g.SessionId == sessionId)
            .Where(g => g.ExpiresAt == null || g.ExpiresAt > DateTimeOffset.UtcNow)
            .ToList();

        return Task.FromResult<IReadOnlyList<PermissionGrant>>(grants);
    }

    public Task<bool> RevokeGrantAsync(string grantId, CancellationToken ct)
    {
        var result = _sessionGrants.TryRemove(grantId, out _);
        if (result)
        {
            _logger.LogInformation("Revoked grant {GrantId}", grantId);
        }
        return Task.FromResult(result);
    }

    public Task ExpirePendingRequestsAsync(string sessionId, CancellationToken ct)
    {
        var toRemove = _pendingRequests
            .Where(kvp => kvp.Value.Request.SessionId == sessionId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            if (_pendingRequests.TryRemove(id, out var pending))
            {
                _logger.LogDebug("Expired pending request {RequestId} for session {SessionId}", id, sessionId);
            }
        }

        return Task.CompletedTask;
    }

    public Task SetPolicyAsync(string workspaceId, PermissionPolicy policy, CancellationToken ct)
    {
        _policies[workspaceId] = policy;
        _logger.LogInformation("Set permission policy for workspace {WorkspaceId}", workspaceId);
        return Task.CompletedTask;
    }

    public Task<PermissionPolicy?> GetPolicyAsync(string workspaceId, CancellationToken ct)
    {
        _policies.TryGetValue(workspaceId, out var policy);
        return Task.FromResult(policy);
    }

    public bool IsPathAllowed(string path, PermissionClass permissionClass, PermissionPolicy? policy)
    {
        if (policy == null)
            return true;

        // Normalize path
        var normalizedPath = Path.GetFullPath(path).Replace('\\', '/');

        // Check blocked patterns first
        if (policy.BlockedPathPatterns != null)
        {
            foreach (var pattern in policy.BlockedPathPatterns)
            {
                if (MatchesPattern(normalizedPath, pattern))
                {
                    _logger.LogDebug("Path {Path} blocked by pattern {Pattern}", path, pattern);
                    return false;
                }
            }
        }

        // For write operations, check allowed patterns
        if (permissionClass is PermissionClass.WriteProject or PermissionClass.WriteExternal or PermissionClass.DeleteProject)
        {
            if (policy.AllowedPathPatterns != null && policy.AllowedPathPatterns.Count > 0)
            {
                var allowed = false;
                foreach (var pattern in policy.AllowedPathPatterns)
                {
                    if (MatchesPattern(normalizedPath, pattern))
                    {
                        allowed = true;
                        break;
                    }
                }
                if (!allowed)
                {
                    _logger.LogDebug("Path {Path} not in allowed patterns", path);
                    return false;
                }
            }
        }

        return true;
    }

    public bool IsHostAllowed(string host, PermissionPolicy? policy)
    {
        if (policy == null)
            return true;

        // Check blocked hosts first
        if (policy.BlockedHosts != null && policy.BlockedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // If allowed hosts are specified, check against them
        if (policy.AllowedHosts != null && policy.AllowedHosts.Count > 0)
        {
            return policy.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        }

        // If external network is disabled, only allow provider hosts
        if (!policy.AllowExternalNetwork)
        {
            // Allow known provider hosts
            var providerHosts = new[]
            {
                "api.openai.com",
                "api.anthropic.com",
                "generativelanguage.googleapis.com",
                "openrouter.ai"
            };
            return providerHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        // Simple glob-style matching
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".") + "$";

        return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
    }

    private void OnPermissionRequested(PermissionRequest request)
    {
        PermissionRequested?.Invoke(this, request);
    }

    private void OnPermissionDecided(PermissionDecision decision)
    {
        PermissionDecided?.Invoke(this, decision);
    }

    private sealed record PendingRequest(PermissionRequest Request, DateTimeOffset CreatedAt);
}
