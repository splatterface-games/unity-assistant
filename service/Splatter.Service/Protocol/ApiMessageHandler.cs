// API Message Handler - Routes client API requests to appropriate services

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Agents; // HarnessLauncher (CLI path/version resolution)
using Splatter.Service.Context;
using Splatter.Service.Generators;
using Splatter.Service.Permissions;
using Splatter.Service.Storage;
using Splatter.Service.Tools;
using Splatter.Service.Recovery;
using Splatter.Service.Diagnostics;
using Splatter.Service.Security;

namespace Splatter.Service.Protocol;

/// <summary>
/// Registers and handles API message types from Unity client.
/// </summary>
public sealed class ApiMessageHandler
{
    private readonly ILogger<ApiMessageHandler> _logger;
    private readonly IMessageRouter _router;
    private readonly ITransportServer _transport;
    private readonly IPersistenceStore _persistence;
    private readonly IPermissionEngine _permissions;
    private readonly IContextService _context;
    private readonly ISkillRegistry _skills;
    private readonly ICrashRecoveryManager _recovery;
    private readonly IDiagnosticsExporter _diagnostics;
    private readonly ITimingMetrics _timing;
    private readonly ISecurityPolicyManager _security;
    private readonly IGeneratorService _generators;
    private readonly ICredentialStore _credentials;
    private readonly IToolRegistry _tools;
    private readonly ServiceConfiguration _config;
    private readonly McpHttpHandler _mcp;
    private readonly InteractiveSessionRegistry _interactiveSessions;

    // Set when the editor disconnects (domain reload); drives the reconnect resume.
    private volatile bool _editorWasDisconnected;

    // Canonical wire-protocol options (shared with the transport).
    private static readonly JsonSerializerOptions JsonOptions = ProtocolJson.Options;

    public ApiMessageHandler(
        ILogger<ApiMessageHandler> logger,
        IMessageRouter router,
        ServiceConfiguration config,
        ITransportServer transport,
        IPersistenceStore persistence,
        IPermissionEngine permissions,
        IContextService context,
        ISkillRegistry skills,
        ICrashRecoveryManager recovery,
        IDiagnosticsExporter diagnostics,
        ITimingMetrics timing,
        ISecurityPolicyManager security,
        IGeneratorService generators,
        ICredentialStore credentials,
        IToolRegistry tools,
        McpHttpHandler mcp,
        InteractiveSessionRegistry interactiveSessions)
    {
        _logger = logger;
        _router = router;
        _config = config;
        _transport = transport;
        _persistence = persistence;
        _permissions = permissions;
        _context = context;
        _skills = skills;
        _recovery = recovery;
        _diagnostics = diagnostics;
        _timing = timing;
        _security = security;
        _generators = generators;
        _credentials = credentials;
        _tools = tools;
        _mcp = mcp;
        _interactiveSessions = interactiveSessions;
    }

    /// <summary>
    /// Registers all API message handlers with the router.
    /// </summary>
    public void RegisterHandlers()
    {
        // Permissions
        _router.RegisterHandler("permission.respond", HandlePermissionResponse);

        // Diagnostics
        _router.RegisterHandler("service.health", HandleHealthCheck);
        _router.RegisterHandler("service.version", HandleVersion);

        // Recovery (M6.2)
        _router.RegisterHandler("recovery.scan", HandleRecoveryScan);
        _router.RegisterHandler("recovery.list", HandleRecoveryList);
        _router.RegisterHandler("recovery.action", HandleRecoveryAction);

        // Diagnostics Export (M6.3)
        _router.RegisterHandler("diagnostics.export", HandleDiagnosticsExport);
        _router.RegisterHandler("diagnostics.timing", HandleTimingMetrics);

        // Security (M6.5)
        _router.RegisterHandler("security.violations", HandleSecurityViolations);

        // Generators (M9)
        _router.RegisterHandler("generator.capabilities", HandleGeneratorCapabilities);
        _router.RegisterHandler("generator.quote", HandleGeneratorQuote);
        _router.RegisterHandler("generator.submit", HandleGeneratorSubmit);
        _router.RegisterHandler("generator.status", HandleGeneratorStatus);
        _router.RegisterHandler("generator.cancel", HandleGeneratorCancel);
        _router.RegisterHandler("generator.apply", HandleGeneratorApply);
        _router.RegisterHandler("generator.history", HandleGeneratorHistory);
        _router.RegisterHandler("generator.recoverable", HandleGeneratorRecoverable);

        // Credentials
        _router.RegisterHandler("credentials.refresh", HandleCredentialsRefresh);
        _router.RegisterHandler("credentials.set", HandleCredentialsSet);

        // Harnesses
        _router.RegisterHandler("harness.check", HandleHarnessCheck);

        // Interactive sessions (launcher mode): the editor mints a token-scoped session
        // before opening an interactive CLI terminal pointed at our MCP endpoint.
        _router.RegisterHandler("mcp.session.create", HandleMcpSessionCreate);
        _router.RegisterHandler("mcp.session.close", HandleMcpSessionClose);
        _router.RegisterHandler("mcp.session.list", HandleMcpSessionList);
        _router.RegisterHandler("workspace.attach", HandleWorkspaceAttach);

        // Unity tool delegation: forward Unity tool calls to the editor as
        // tool.execute events, and route the editor's tool.result back to the
        // pending tool call.
        _tools.UnityToolRequested += OnUnityToolRequested;
        _router.RegisterHandler("tool.result", HandleToolResult);
        _router.RegisterHandler("tool.deferred", HandleToolDeferred);

        // Reload barrier. The editor's domain reload shows up as a WebSocket disconnect, then a
        // reconnect. On disconnect, HOLD in-flight calls (don't fail them at 60s). On reconnect,
        // RESUME them: re-deliver idempotent reads to the fresh editor, fail mutations with a
        // re-check error (re-running could double-apply), leave deferred calls to self-complete.
        if (_transport is WebSocketTransportServer ws)
        {
            ws.ClientDisconnected += (_, __) =>
            {
                _editorWasDisconnected = true;
                _tools.HoldPendingUnityCalls();
            };
            ws.ClientConnected += (_, __) =>
            {
                if (!_editorWasDisconnected) return; // initial connect, nothing to resume
                _editorWasDisconnected = false;
                // Give the reloaded editor a moment to initialize before re-delivering.
                _ = Task.Run(async () => { await Task.Delay(750); _tools.ResumePendingUnityCalls(); });
            };
        }

        // Bubble permission requests (our gated tools + harness-delegated prompts) to the
        // editor; the editor's permission.respond is already handled above.
        _permissions.PermissionRequested += OnPermissionRequested;

        // Activity feed: every MCP tools/call from an interactive terminal becomes a
        // tool.activity event targeted at the session's workspace.
        _mcp.ToolActivity += OnMcpToolActivity;

        _logger.LogInformation("Registered {Count} API message handlers", 26);
    }

    private void OnPermissionRequested(object? sender, PermissionRequest req)
    {
        var payload = new
        {
            request_id = req.Id,
            title = req.Preview.Title,
            description = req.Preview.Summary,
            tool_id = req.ToolCallId,
            session_id = req.SessionId,
            provider_id = _interactiveSessions.Get(req.SessionId)?.ProviderId
        };
        // No workspace on the request -> send to all clients (loopback / single editor).
        _ = _transport.BroadcastToAllAsync(MessageEnvelope.Event("permission.requested", payload, ""), CancellationToken.None);
    }

    private void OnMcpToolActivity(object? sender, McpToolActivity activity)
    {
        var payload = new
        {
            activity_id = activity.ActivityId,
            session_id = activity.SessionId,
            provider_id = activity.ProviderId,
            phase = activity.Phase,
            tool_id = activity.ToolId,
            args_summary = activity.ArgsSummary,
            ok = activity.Ok,
            error = activity.Error,
            duration_ms = activity.DurationMs,
            gated = activity.Gated,
            gate_outcome = activity.GateOutcome
        };
        _ = _transport.BroadcastAsync(activity.WorkspaceId,
            MessageEnvelope.Event("tool.activity", payload, activity.WorkspaceId), CancellationToken.None);
    }

    #region Permission Handlers

    private async Task<MessageEnvelope?> HandlePermissionResponse(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<PermissionResponseRequest>(message.Payload);
            if (request == null || string.IsNullOrEmpty(request.RequestId))
            {
                return ErrorResponse(message.Id, "Invalid request: request_id is required");
            }

            var outcome = Enum.TryParse<PermissionOutcome>(request.Outcome, true, out var o) ? o : PermissionOutcome.Denied;
            var scope = Enum.TryParse<PermissionScope>(request.Scope, true, out var s) ? s : PermissionScope.Once;

            await _permissions.RecordDecisionAsync(request.RequestId, outcome, scope, request.Reason, ct);

            return MessageEnvelope.Response(message.Id, "permission.recorded", new
            {
                recorded = true
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record permission response");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    #endregion

    #region Diagnostic Handlers

    private Task<MessageEnvelope?> HandleHealthCheck(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        var health = new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            uptime_seconds = (DateTimeOffset.UtcNow - Process.StartTime).TotalSeconds
        };

        return Task.FromResult<MessageEnvelope?>(
            MessageEnvelope.Response(message.Id, "service.health", health, message.WorkspaceId));
    }

    private Task<MessageEnvelope?> HandleVersion(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        var version = new
        {
            protocol = MessageEnvelope.ProtocolVersion,
            service = ServiceConfiguration.ServiceVersion,
            runtime = Environment.Version.ToString()
        };

        return Task.FromResult<MessageEnvelope?>(
            MessageEnvelope.Response(message.Id, "service.version", version, message.WorkspaceId));
    }

    private static class Process
    {
        public static DateTimeOffset StartTime { get; } = DateTimeOffset.UtcNow;
    }

    #endregion

    #region Credential Handlers

    private Task<MessageEnvelope?> HandleCredentialsRefresh(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<CredentialRefreshRequest>(message.Payload);

            // Log the refresh request
            _logger.LogInformation("Credential refresh requested for provider: {ProviderId}", request?.ProviderId ?? "all");

            // The credential store will automatically fetch updated credentials
            // from the OS keychain on the next request, so we just acknowledge the refresh
            return Task.FromResult<MessageEnvelope?>(
                MessageEnvelope.Response(message.Id, "credentials.refresh.result", new
                {
                    success = true,
                    provider_id = request?.ProviderId
                }, message.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process credential refresh");
            return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, ex.Message));
        }
    }

    private sealed record CredentialRefreshRequest(string? ProviderId);

    // Validates that the service can actually launch a harness CLI by resolving it and
    // running `<cli> --version`. This is the authoritative check: it uses the same path
    // resolution (HarnessLauncher) that spawns the harness for real conversations.
    private async Task<MessageEnvelope?> HandleHarnessCheck(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<HarnessCheckRequest>(message.Payload);
            var providerId = request?.ProviderId;
            // An explicit path override (from the editor's Preferences) wins over the
            // service default for that provider.
            var configured = !string.IsNullOrWhiteSpace(request?.Path)
                ? request.Path
                : providerId switch
                {
                    "claude-code" => _config.ClaudeCodePath,
                    "codex" => _config.CodexPath,
                    "grok-build" => _config.GrokBuildPath,
                    _ => null
                };

            if (configured == null)
                return MessageEnvelope.Response(message.Id, "harness.check.result", new
                {
                    provider_id = providerId, ok = false, version = (string?)null, path = (string?)null,
                    message = $"Unknown harness '{providerId}'."
                }, message.WorkspaceId);

            var resolved = HarnessLauncher.ResolveOnPath(configured);
            if (resolved == null)
                return MessageEnvelope.Response(message.Id, "harness.check.result", new
                {
                    provider_id = providerId, ok = false, version = (string?)null, path = (string?)null,
                    message = $"'{configured}' was not found on the service's PATH."
                }, message.WorkspaceId);

            var (file, prefix) = HarnessLauncher.ResolveLaunch(configured);
            var (exit, stdout, stderr) = await RunVersionAsync(file, prefix, ct);
            var version = FirstLine(stdout);
            var ok = exit == 0 && !string.IsNullOrWhiteSpace(version);

            _logger.LogInformation("harness.check {Provider}: ok={Ok} version={Version} path={Path}",
                providerId, ok, version, resolved);

            return MessageEnvelope.Response(message.Id, "harness.check.result", new
            {
                provider_id = providerId,
                ok,
                version,
                path = resolved,
                message = ok ? $"OK · {version}" : (FirstLine(stderr) ?? $"Exited with code {exit}.")
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "harness.check failed");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunVersionAsync(
        string file, List<string> prefix, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var p in prefix) psi.ArgumentList.Add(p);
        psi.ArgumentList.Add("--version");

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return (-1, "", "Could not start process.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var soTask = proc.StandardOutput.ReadToEndAsync();
            var seTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(timeoutCts.Token);
            return (proc.ExitCode, await soTask, await seTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, "", "Timed out.");
        }
    }

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return (i >= 0 ? s.Substring(0, i) : s).Trim();
    }

    private sealed record HarnessCheckRequest(string? ProviderId, string? Path);

    #region Interactive Sessions (launcher mode)

    // Mints a token-scoped session for an interactive CLI terminal. The editor injects
    // the token into the CLI's MCP config (X-Splatter-Session header or ?session= query
    // param) so the terminal's tool calls carry the permission mode chosen at launch.
    private Task<MessageEnvelope?> HandleMcpSessionCreate(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<McpSessionCreateRequest>(message.Payload);
            if (request == null || string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.ProviderId))
                return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, "workspace_id and provider_id are required"));

            var now = DateTimeOffset.UtcNow;
            var sessionId = $"isess_{Guid.NewGuid():N}"[..16];
            var token = Guid.NewGuid().ToString("N");
            var session = new AgentSession(
                sessionId, request.WorkspaceId, sessionId, request.ProviderId, null, null,
                ParseMode(request.Mode), AgentSessionStatus.Running, now, now, null);

            _mcp.RegisterSession(token, session);
            _interactiveSessions.Register(new InteractiveSessionRegistry.Entry
            {
                SessionId = sessionId,
                Token = token,
                Session = session,
                ProviderId = request.ProviderId,
                Label = request.Label,
                CreatedAt = now,
                LastActivity = now,
            });

            // Bind this editor to the workspace so tool.execute / activity broadcasts
            // target it (previously only conversation.create did this).
            if (_transport is WebSocketTransportServer wsTransport)
                wsTransport.AssociateClientWithWorkspace(clientId, request.WorkspaceId);

            _logger.LogInformation("Minted interactive session {SessionId} ({Provider}, {Mode}) for workspace {Workspace}",
                sessionId, request.ProviderId, session.Mode, request.WorkspaceId);

            return Task.FromResult<MessageEnvelope?>(MessageEnvelope.Response(message.Id, "mcp.session.created", new
            {
                session_id = sessionId,
                token,
                mcp_url = _config.McpUrl,
                created_at_ms = now.ToUnixTimeMilliseconds()
            }, message.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "mcp.session.create failed");
            return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, ex.Message));
        }
    }

    private Task<MessageEnvelope?> HandleMcpSessionClose(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<McpSessionCloseRequest>(message.Payload);
            if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
                return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, "session_id is required"));

            // Idempotent: closing an unknown/already-closed session is fine. A stale
            // terminal's calls then fall back to the synthetic read-only MCP session.
            var entry = _interactiveSessions.Close(request.SessionId);
            if (entry != null)
            {
                _mcp.UnregisterSession(entry.Token);
                _logger.LogInformation("Closed interactive session {SessionId}", request.SessionId);
            }

            return Task.FromResult<MessageEnvelope?>(MessageEnvelope.Response(message.Id, "mcp.session.closed", new
            {
                ok = true,
                session_id = request.SessionId
            }, message.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "mcp.session.close failed");
            return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, ex.Message));
        }
    }

    private Task<MessageEnvelope?> HandleMcpSessionList(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<McpSessionListRequest>(message.Payload);
            var sessions = _interactiveSessions.ListForWorkspace(request?.WorkspaceId ?? "")
                .Select(e => new
                {
                    session_id = e.SessionId,
                    provider_id = e.ProviderId,
                    mode = e.Session.Mode.ToString(),
                    label = e.Label,
                    created_at_ms = e.CreatedAt.ToUnixTimeMilliseconds(),
                    last_activity_ms = e.LastActivity.ToUnixTimeMilliseconds(),
                    tool_call_count = e.ToolCallCount
                })
                .ToArray();

            return Task.FromResult<MessageEnvelope?>(MessageEnvelope.Response(message.Id, "mcp.session.list.result", new
            {
                sessions
            }, message.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "mcp.session.list failed");
            return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, ex.Message));
        }
    }

    // Binds the connected editor to its workspace for event routing. Sent by the editor
    // bridge after every connect; replaces the association conversation.create used to do.
    private Task<MessageEnvelope?> HandleWorkspaceAttach(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<WorkspaceAttachRequest>(message.Payload);
            if (request == null || string.IsNullOrWhiteSpace(request.WorkspaceId))
                return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, "workspace_id is required"));

            if (_transport is WebSocketTransportServer wsTransport)
                wsTransport.AssociateClientWithWorkspace(clientId, request.WorkspaceId);

            return Task.FromResult<MessageEnvelope?>(MessageEnvelope.Response(message.Id, "workspace.attached", new
            {
                ok = true,
                workspace_id = request.WorkspaceId
            }, message.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workspace.attach failed");
            return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, ex.Message));
        }
    }

    private sealed record McpSessionCreateRequest(string? WorkspaceId, string? ProviderId, string? Mode, string? Label);
    private sealed record McpSessionCloseRequest(string? SessionId);
    private sealed record McpSessionListRequest(string? WorkspaceId);
    private sealed record WorkspaceAttachRequest(string? WorkspaceId);

    #endregion

    /// <summary>
    /// Stores a provider API key in the service's credential store. The key is sent
    /// from the editor over the authenticated loopback connection because the service
    /// process (not the editor) makes the provider calls.
    /// </summary>
    private async Task<MessageEnvelope?> HandleCredentialsSet(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<CredentialSetRequest>(message.Payload);
            if (request == null || string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return ErrorResponse(message.Id, "provider_id is required");
            }

            if (string.IsNullOrEmpty(request.ApiKey))
            {
                // Empty key = clear the stored credential.
                await _credentials.DeleteAsync(request.ProviderId, ct);
                _logger.LogInformation("Cleared credential for provider {ProviderId}", request.ProviderId);
            }
            else
            {
                await _credentials.SetAsync(request.ProviderId, request.ApiKey, ct);
                _logger.LogInformation("Stored credential for provider {ProviderId}", request.ProviderId);
            }

            return MessageEnvelope.Response(message.Id, "credentials.set.result", new
            {
                success = true,
                provider_id = request.ProviderId
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set credential");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private sealed record CredentialSetRequest(string? ProviderId, string? ApiKey);

    #endregion

    #region Unity Tool Delegation

    /// <summary>
    /// Forwards a Unity tool call to the editor(s) in the workspace as a tool.execute
    /// event. The editor executes it on the main thread and replies with tool.result.
    /// </summary>
    private void OnUnityToolRequested(object? sender, UnityToolRequest req)
    {
        var argsJson = req.Arguments switch
        {
            null => "{}",
            JsonElement je => je.GetRawText(),
            string s => s,
            _ => JsonSerializer.Serialize(req.Arguments, JsonOptions)
        };

        var payload = new
        {
            tool_call_id = req.CallId,
            tool_id = req.ToolId,
            arguments = argsJson,
            workspace_path = (string?)null
        };

        var envelope = MessageEnvelope.Event("tool.execute", payload, req.WorkspaceId);
        _ = _transport.BroadcastAsync(req.WorkspaceId, envelope, CancellationToken.None);
    }

    private Task<MessageEnvelope?> HandleToolResult(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var resp = DeserializePayload<ToolResultPayload>(message.Payload);
            if (resp == null || string.IsNullOrEmpty(resp.ToolCallId))
            {
                return Task.FromResult<MessageEnvelope?>(null);
            }

            object? value = null;
            if (!string.IsNullOrEmpty(resp.Output))
            {
                try { value = JsonSerializer.Deserialize<JsonElement>(resp.Output!); }
                catch { value = resp.Output; }
            }

            var result = resp.Success
                ? new ToolResult(resp.ToolCallId!, true, value, null, null, null, null)
                : new ToolResult(resp.ToolCallId!, false, null, null,
                    new NormalizedError(ErrorCodes.ToolFailed, resp.Error ?? "Unity tool failed", null, false, null, null),
                    null, null);

            _tools.CompleteUnityToolCall(resp.ToolCallId!, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process tool.result");
        }

        // Notification - no response.
        return Task.FromResult<MessageEnvelope?>(null);
    }

    private sealed record ToolResultPayload(string? ToolCallId, bool Success, string? Output, string? Error);

    // The editor sends this before a domain reload so the pending tool call isn't timed out
    // while the editor reloads and reconnects; the real tool.result arrives afterwards.
    private Task<MessageEnvelope?> HandleToolDeferred(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var resp = DeserializePayload<ToolDeferredPayload>(message.Payload);
            if (!string.IsNullOrEmpty(resp?.ToolCallId))
                _tools.DeferUnityToolCall(resp!.ToolCallId!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process tool.deferred");
        }
        return Task.FromResult<MessageEnvelope?>(null);
    }

    private sealed record ToolDeferredPayload(string? ToolCallId);

    #endregion

    #region Recovery Handlers (M6.2)

    private async Task<MessageEnvelope?> HandleRecoveryScan(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            using var scope = _timing.StartScope(TimingOperation.RecoveryScan);
            var result = await _recovery.ScanForRecoverableItemsAsync(ct);

            return MessageEnvelope.Response(message.Id, "recovery.scan.result", new
            {
                total = result.TotalAbandoned,
                recoverable = result.TotalRecoverable,
                unknown = result.TotalUnknown,
                scanned_at = result.ScannedAt,
                duration_ms = result.ScanDuration.TotalMilliseconds,
                items = result.Items.Select(i => new
                {
                    id = i.Id,
                    type = i.Type.ToString(),
                    state = i.State.ToString(),
                    workspace_id = i.WorkspaceId,
                    session_id = i.SessionId,
                    last_activity = i.LastActivity,
                    description = i.Description,
                    available_actions = i.AvailableActions.Select(a => a.ToString())
                }).ToArray()
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery scan failed");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleRecoveryList(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<RecoveryListRequest>(message.Payload);
            var items = await _recovery.GetRecoverableItemsAsync(request?.WorkspaceId, ct);

            return MessageEnvelope.Response(message.Id, "recovery.list.result", new
            {
                count = items.Count,
                items = items.Select(i => new
                {
                    id = i.Id,
                    type = i.Type.ToString(),
                    state = i.State.ToString(),
                    workspace_id = i.WorkspaceId,
                    session_id = i.SessionId,
                    last_activity = i.LastActivity,
                    description = i.Description,
                    available_actions = i.AvailableActions.Select(a => a.ToString())
                }).ToArray()
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery list failed");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleRecoveryAction(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<RecoveryActionRequest>(message.Payload);
            if (request == null || string.IsNullOrEmpty(request.ItemId))
            {
                return ErrorResponse(message.Id, "Invalid request: item_id is required");
            }

            var action = Enum.TryParse<RecoveryAction>(request.Action, true, out var a)
                ? a
                : RecoveryAction.Skip;

            var result = await _recovery.ExecuteRecoveryActionAsync(request.ItemId, action, ct);

            return MessageEnvelope.Response(message.Id, "recovery.action.result", new
            {
                item_id = result.ItemId,
                action = result.Action.ToString(),
                success = result.Success,
                new_state = result.NewState.ToString(),
                message = result.Message
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery action failed");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    #endregion

    #region Diagnostics Export Handlers (M6.3)

    private async Task<MessageEnvelope?> HandleDiagnosticsExport(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            using var scope = _timing.StartScope(TimingOperation.DiagnosticsExport);

            var request = DeserializePayload<DiagnosticsExportRequest>(message.Payload);
            var options = new DiagnosticsExportOptions(
                IncludeLogs: request?.IncludeLogs ?? true,
                IncludeEventJournal: request?.IncludeEventJournal ?? false,
                MaxLogEntries: request?.MaxLogEntries ?? 1000,
                MaxSessionsToInclude: request?.MaxSessions ?? 10,
                SpecificSessionId: request?.SessionId);

            var bundle = await _diagnostics.GenerateBundleAsync(options, ct);

            // Export to ZIP
            var zipPath = await _diagnostics.ExportToZipAsync(bundle, null, ct);

            return MessageEnvelope.Response(message.Id, "diagnostics.export.result", new
            {
                bundle_id = bundle.BundleId,
                generated_at = bundle.GeneratedAt,
                zip_path = _diagnostics.Redact(zipPath),
                session_count = bundle.Sessions.Length,
                log_count = bundle.RecentLogs.Count,
                service_version = bundle.ServiceVersion
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostics export failed");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private Task<MessageEnvelope?> HandleTimingMetrics(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var metrics = _timing.GetMetrics();
            var violations = _timing.GetRecentViolations(20);

            return Task.FromResult<MessageEnvelope?>(MessageEnvelope.Response(message.Id, "diagnostics.timing.result", new
            {
                avg_first_token_ms = metrics.AvgFirstTokenLatencyMs,
                avg_tool_execution_ms = metrics.AvgToolExecutionMs,
                avg_permission_prompt_ms = metrics.AvgPermissionPromptLatencyMs,
                avg_patch_apply_ms = metrics.AvgPatchApplyMs,
                avg_index_scan_ms = metrics.AvgIndexScanMs,
                avg_event_replay_ms = metrics.AvgEventReplayMs,
                sample_count = metrics.SampleCount,
                recent_violations = violations.Select(v => new
                {
                    operation = v.Operation.ToString(),
                    actual_ms = v.ActualMs,
                    budget_ms = v.BudgetMs,
                    severity = v.Severity.ToString(),
                    timestamp = v.Timestamp
                }).ToArray()
            }, message.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Timing metrics retrieval failed");
            return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, ex.Message));
        }
    }

    #endregion

    #region Security Handlers (M6.5)

    private Task<MessageEnvelope?> HandleSecurityViolations(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<SecurityViolationsRequest>(message.Payload);
            var maxCount = request?.MaxCount ?? 50;

            var violations = _security.GetRecentViolations(maxCount);

            return Task.FromResult<MessageEnvelope?>(MessageEnvelope.Response(message.Id, "security.violations.result", new
            {
                count = violations.Count,
                violations = violations.Select(v => new
                {
                    id = v.ViolationId,
                    type = v.Type.ToString(),
                    description = _diagnostics.Redact(v.Description),
                    severity = v.Severity.ToString(),
                    timestamp = v.Timestamp
                }).ToArray()
            }, message.WorkspaceId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Security violations retrieval failed");
            return Task.FromResult<MessageEnvelope?>(ErrorResponse(message.Id, ex.Message));
        }
    }

    #endregion

    #region Generator Handlers (M9)

    private async Task<MessageEnvelope?> HandleGeneratorCapabilities(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorCapabilitiesRequestDto>(message.Payload);
            var modality = ParseModality(request?.Modality);
            var providerId = request?.ProviderId;

            var capabilities = await _generators.GetCapabilitiesAsync(modality, providerId, ct);

            return MessageEnvelope.Response(message.Id, "generator.capabilities.result", new
            {
                capabilities = capabilities.Select(c => new
                {
                    modality = c.Modality.ToString().ToLowerInvariant(),
                    modes = c.Modes.Select(m => new { m.Id, displayName = m.DisplayName, m.Description, m.RequiredInputs, m.OptionalInputs }),
                    supportedFormats = c.SupportedFormats,
                    maxResolution = c.MaxResolution,
                    maxDurationSeconds = c.MaxDurationSeconds,
                    supportsNegativePrompt = c.SupportsNegativePrompt,
                    supportsSeed = c.SupportsSeed,
                    supportsReferences = c.SupportsReferences
                }).ToArray()
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get generator capabilities");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleGeneratorQuote(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorQuoteRequestDto>(message.Payload);
            if (request == null)
            {
                return ErrorResponse(message.Id, "Invalid request payload");
            }

            var quoteRequest = new GeneratorQuoteRequest(
                ParseModality(request.Modality),
                request.ProviderId ?? "default",
                request.ModelId ?? "",
                request.Mode ?? "generate",
                request.Parameters);

            var quote = await _generators.GetQuoteAsync(quoteRequest, ct);

            return MessageEnvelope.Response(message.Id, "generator.quote.result", new
            {
                success = quote.Success,
                estimatedCost = quote.EstimatedCost,
                currency = quote.Currency,
                errorMessage = quote.ErrorMessage
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get generator quote");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleGeneratorSubmit(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorSubmitRequestDto>(message.Payload);
            if (request == null)
            {
                return ErrorResponse(message.Id, "Invalid request payload");
            }

            var references = request.References?.Select(r => new ArtifactReference(
                Guid.NewGuid().ToString("N")[..8],
                r.Type ?? "image",
                r.AssetPath,
                null, // Size
                r.MimeType,
                r.MimeType,
                r.Base64Data
            )).ToList();

            var submitRequest = new GeneratorSubmitRequest(
                ParseModality(request.Modality),
                request.ProviderId ?? "default",
                request.ModelId ?? "",
                request.Mode ?? "generate",
                request.TargetAssetGuid,
                request.TargetAssetPath,
                request.Parameters,
                references);

            var job = await _generators.SubmitJobAsync(request.WorkspaceId ?? message.WorkspaceId ?? "", submitRequest, ct);

            return MessageEnvelope.Response(message.Id, "generator.submit.result", new
            {
                jobId = job.Id,
                status = job.Status.ToString().ToLowerInvariant(),
                modality = job.Modality.ToString().ToLowerInvariant(),
                providerId = job.ProviderId,
                createdAt = job.CreatedAt
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit generator job");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleGeneratorStatus(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorStatusRequestDto>(message.Payload);
            if (string.IsNullOrEmpty(request?.JobId))
            {
                return ErrorResponse(message.Id, "job_id is required");
            }

            var job = await _generators.GetJobAsync(request.JobId, ct);
            if (job == null)
            {
                return ErrorResponse(message.Id, $"Job not found: {request.JobId}");
            }

            return MessageEnvelope.Response(message.Id, "generator.status.result", new
            {
                jobId = job.Id,
                status = job.Status.ToString().ToLowerInvariant(),
                modality = job.Modality.ToString().ToLowerInvariant(),
                providerId = job.ProviderId,
                progress = job.Results?.Count > 0 ? 100 : 0,
                results = job.Results?.Select(r => new
                {
                    id = r.Id,
                    sourceUrl = r.SourceUrl,
                    contentType = r.ContentType,
                    size = r.Size,
                    seed = r.Seed,
                    variationIndex = r.VariationIndex
                }).ToArray(),
                error = job.Error != null ? new { job.Error.Code, job.Error.Message } : null,
                createdAt = job.CreatedAt,
                updatedAt = job.UpdatedAt
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get generator job status");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleGeneratorCancel(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorCancelRequestDto>(message.Payload);
            if (string.IsNullOrEmpty(request?.JobId))
            {
                return ErrorResponse(message.Id, "job_id is required");
            }

            var cancelled = await _generators.CancelJobAsync(request.JobId, ct);

            return MessageEnvelope.Response(message.Id, "generator.cancel.result", new
            {
                jobId = request.JobId,
                cancelled
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel generator job");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleGeneratorApply(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorApplyRequestDto>(message.Payload);
            if (string.IsNullOrEmpty(request?.JobId))
            {
                return ErrorResponse(message.Id, "job_id is required");
            }

            var applyRequest = new GeneratorApplyRequest(
                request.JobId,
                request.ResultId,
                request.TargetPath);

            var assetPath = await _generators.ApplyResultAsync(request.WorkspaceId ?? message.WorkspaceId ?? "", applyRequest, ct);

            return MessageEnvelope.Response(message.Id, "generator.apply.result", new
            {
                success = !string.IsNullOrEmpty(assetPath),
                assetPath
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply generator result");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleGeneratorHistory(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorHistoryRequestDto>(message.Payload);
            if (string.IsNullOrEmpty(request?.AssetGuid))
            {
                return ErrorResponse(message.Id, "asset_guid is required");
            }

            var history = await _generators.GetHistoryAsync(request.WorkspaceId ?? message.WorkspaceId ?? "", request.AssetGuid, ct);

            return MessageEnvelope.Response(message.Id, "generator.history.result", new
            {
                assetGuid = request.AssetGuid,
                jobs = history?.Jobs.Select(j => new
                {
                    id = j.Id,
                    modality = j.Modality.ToString().ToLowerInvariant(),
                    prompt = j.Prompt,
                    status = j.Status.ToString().ToLowerInvariant(),
                    createdAt = j.CreatedAt
                }).ToArray() ?? Array.Empty<object>()
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get generator history");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private async Task<MessageEnvelope?> HandleGeneratorRecoverable(string clientId, MessageEnvelope message, CancellationToken ct)
    {
        try
        {
            var request = DeserializePayload<GeneratorRecoverableRequestDto>(message.Payload);
            var workspaceId = request?.WorkspaceId ?? message.WorkspaceId ?? "";

            var recoverableJobs = await _generators.GetRecoverableJobsAsync(workspaceId, ct);

            return MessageEnvelope.Response(message.Id, "generator.recoverable.result", new
            {
                jobs = recoverableJobs.Select(r => new
                {
                    jobId = r.JobId,
                    status = r.Status.ToString().ToLowerInvariant(),
                    recoveredResults = r.RecoveredResults,
                    failedResults = r.FailedResults,
                    availableActions = r.AvailableActions.Select(a => a.ToString().ToLowerInvariant()).ToArray()
                }).ToArray()
            }, message.WorkspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recoverable generator jobs");
            return ErrorResponse(message.Id, ex.Message);
        }
    }

    private static GeneratorModality ParseModality(string? modality)
    {
        return modality?.ToLowerInvariant() switch
        {
            "image" => GeneratorModality.Image,
            "material" or "materialpbr" or "pbr" => GeneratorModality.MaterialPbr,
            "mesh" or "3d" or "model" => GeneratorModality.Mesh,
            "sound" or "audio" => GeneratorModality.Sound,
            "animation" or "motion" => GeneratorModality.Animation,
            _ => GeneratorModality.Image
        };
    }

    #endregion

    #region Helpers

    private static T? DeserializePayload<T>(object? payload) where T : class
    {
        if (payload == null) return null;

        if (payload is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
        }

        if (payload is T typed)
        {
            return typed;
        }

        // Try to serialize and deserialize
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static MessageEnvelope ErrorResponse(string correlationId, string message)
    {
        return MessageEnvelope.Response(correlationId, "error", new NormalizedError(
            ErrorCodes.ToolFailed,
            message,
            null,
            false,
            null,
            null));
    }

    private static AgentPermissionMode ParseMode(string? mode)
    {
        return mode?.ToLowerInvariant() switch
        {
            "readonly" => AgentPermissionMode.ReadOnly,
            "askbeforewrite" => AgentPermissionMode.AskBeforeWrite,
            "workspacewrite" => AgentPermissionMode.WorkspaceWrite,
            "fullauto" => AgentPermissionMode.FullAuto,
            _ => AgentPermissionMode.AskBeforeWrite
        };
    }

    #endregion
}

#region Request Types

internal sealed class PermissionResponseRequest
{
    public string RequestId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? Reason { get; set; }
}

// M6.2 Recovery request types
internal sealed class RecoveryListRequest
{
    public string? WorkspaceId { get; set; }
}

internal sealed class RecoveryActionRequest
{
    public string ItemId { get; set; } = "";
    public string Action { get; set; } = "";
}

// M6.3 Diagnostics request types
internal sealed class DiagnosticsExportRequest
{
    public bool? IncludeLogs { get; set; }
    public bool? IncludeEventJournal { get; set; }
    public int? MaxLogEntries { get; set; }
    public int? MaxSessions { get; set; }
    public string? SessionId { get; set; }
}

// M6.5 Security request types
internal sealed class SecurityViolationsRequest
{
    public int? MaxCount { get; set; }
}

// M9 Generator request types
internal sealed class GeneratorCapabilitiesRequestDto
{
    public string? Modality { get; set; }
    public string? ProviderId { get; set; }
}

internal sealed class GeneratorQuoteRequestDto
{
    public string? Modality { get; set; }
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public string? Mode { get; set; }
    public IReadOnlyDictionary<string, object?>? Parameters { get; set; }
}

internal sealed class GeneratorSubmitRequestDto
{
    public string? WorkspaceId { get; set; }
    public string? Modality { get; set; }
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public string? TargetAssetGuid { get; set; }
    public string? TargetAssetPath { get; set; }
    public string? Mode { get; set; }
    public IReadOnlyDictionary<string, object?>? Parameters { get; set; }
    public ArtifactReferenceDto[]? References { get; set; }
}

internal sealed class ArtifactReferenceDto
{
    public string? Type { get; set; }
    public string? Url { get; set; }
    public string? Base64Data { get; set; }
    public string? MimeType { get; set; }
    public string? AssetPath { get; set; }
    public string? AssetGuid { get; set; }
}

internal sealed class GeneratorStatusRequestDto
{
    public string? JobId { get; set; }
}

internal sealed class GeneratorCancelRequestDto
{
    public string? JobId { get; set; }
}

internal sealed class GeneratorApplyRequestDto
{
    public string? WorkspaceId { get; set; }
    public string? JobId { get; set; }
    public string? ResultId { get; set; }
    public string? TargetPath { get; set; }
}

internal sealed class GeneratorHistoryRequestDto
{
    public string? WorkspaceId { get; set; }
    public string? AssetGuid { get; set; }
}

internal sealed class GeneratorRecoverableRequestDto
{
    public string? WorkspaceId { get; set; }
}

#endregion
