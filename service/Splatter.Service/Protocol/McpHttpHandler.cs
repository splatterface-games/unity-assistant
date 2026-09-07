// MCP (Model Context Protocol) HTTP server endpoint.
//
// Exposes a curated set of Splatter tools to harness CLIs (Claude Code, etc.) over
// the same loopback HttpListener the WebSocket transport uses. Handles the JSON-RPC
// methods a harness needs: initialize, notifications/initialized, tools/list,
// tools/call. Responds with plain application/json (no SSE) - verified against the
// Claude Code MCP client.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Tools;

namespace Splatter.Service.Protocol;

public sealed class McpHttpHandler
{
    private readonly ILogger<McpHttpHandler> _logger;
    private readonly IToolRegistry _tools;
    private readonly ServiceConfiguration _config;
    private readonly Permissions.IPermissionEngine _permissions;
    private readonly InteractiveSessionRegistry _interactiveSessions;

    // The MCP tool Claude Code calls (via --permission-prompt-tool) to ask for permission.
    public const string PermissionPromptTool = "permission_prompt";

    // Fired at the start and end of every tools/call so the editor can render a live
    // activity feed. A C# event (bridged to the transport by ApiMessageHandler) because
    // the transport's ctor takes this handler - injecting ITransportServer here would cycle.
    public event EventHandler<McpToolActivity>? ToolActivity;

    // Per-harness session scoping: a token (sent as the X-Splatter-Session header
    // when Splatter spawns the harness) maps to the originating agent session, so
    // tool calls run in that conversation's context.
    private readonly ConcurrentDictionary<string, AgentSession> _sessionTokens = new();

    // Tools exposed to harnesses. Names are sanitized ('.' -> '_') for MCP; mapped
    // back on call. Start with service-side reads (no Unity dependency); Unity
    // forwarded tools (scene.*) are added once the editor round-trip is verified.
    private static readonly string[] ExposedToolIds =
    {
        // Service-side reads.
        "project.list_files",
        "project.read_file",
        "project.search_text",
        "project.get_overview",
        // Unity reads (forwarded to the editor).
        "scene.get_hierarchy",
        "scene.find_objects",
        "scene.read_object",
        "scene.get_visible_objects",
        "selection.get",
        "console.get_logs",
        // Asset / package / script reads.
        "asset.search",
        "asset.read",
        "asset.get_dependencies",
        "package.list",
        "package.search",
        "package.info",
        "package.get_versions",
        "package.read_manifest",
        "project.search",
        "script.compile_check",
        "script.get_references",
        "csharp.status",
        "csharp.get_diagnostics",
        "csharp.get_symbols",
        "csharp.hover",
        "csharp.find_definition",
        "csharp.find_references",
        "csharp.get_completions",
        "csharp.get_signature_help",
        // Project intelligence / skills / generator queries (all read-only).
        "graph.query",
        "skill.list",
        "skill.read_body",
        "skill.read_resource",
        "generator.quote",
        "generator.status",
        "checkpoint.list",
        // Visual capture: renders a viewport / object to image(s) for the model to see.
        // Pre-approved (looking is free) so the look-> fix -> look loop isn't interrupted.
        "capture.game",
        "capture.scene",
        "capture.multi_angle",
        "capture.region",
        "capture.editor_layout",
        // Mutations (gated): not pre-approved, so the harness asks via permission_prompt
        // before invoking them (unless YOLO/bypass mode).
        "scene.create_gameobject",
        "scene.modify_gameobject",
        "scene.delete_gameobject",
        "scene.add_component",
        "scene.set_component_property",
        "scene.remove_component",
        "scene.duplicate_object",
        "scene.reparent_object",
        "selection.set",
        "prefab.create",
        "prefab.instantiate",
        "prefab.override",
        "asset.create",
        "asset.delete",
        "asset.import",
        // UPM package mutations (install/remove/embed/import-sample/resolve).
        "package.add",
        "package.remove",
        "package.embed",
        "package.import_sample",
        "package.resolve",
        "project.write_file",
        "console.clear",
        "checkpoint.create",
        "checkpoint.restore",
        "generator.submit",
        "generator.apply",
    };

    public McpHttpHandler(ILogger<McpHttpHandler> logger, IToolRegistry tools, ServiceConfiguration config,
        Permissions.IPermissionEngine permissions, InteractiveSessionRegistry interactiveSessions)
    {
        _logger = logger;
        _tools = tools;
        _config = config;
        _permissions = permissions;
        _interactiveSessions = interactiveSessions;
    }

    /// <summary>Registers a session token so the harness's MCP calls run in that session.</summary>
    public void RegisterSession(string token, AgentSession session) => _sessionTokens[token] = session;

    public void UnregisterSession(string token) => _sessionTokens.TryRemove(token, out _);

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;

        if (request.HttpMethod == "GET")
        {
            // No server-initiated SSE stream; force JSON-only mode.
            context.Response.StatusCode = 405;
            context.Response.Close();
            return;
        }

        if (request.HttpMethod != "POST")
        {
            context.Response.StatusCode = 405;
            context.Response.Close();
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch
        {
            await WriteJson(context, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } });
            return;
        }

        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        var id = root.TryGetProperty("id", out var idEl) ? (object?)JsonElementToIdValue(idEl) : null;

        switch (method)
        {
            case "initialize":
                var protocolVersion = root.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : "2025-06-18";
                await WriteJson(context, new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion,
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "splatter-unity", version = ServiceConfiguration.ServiceVersion }
                    }
                });
                break;

            case "notifications/initialized":
                context.Response.StatusCode = 202;
                context.Response.Close();
                break;

            case "ping":
                await WriteJson(context, new { jsonrpc = "2.0", id, result = new { } });
                break;

            case "tools/list":
                await WriteJson(context, new { jsonrpc = "2.0", id, result = new { tools = BuildToolList() } });
                break;

            case "tools/call":
                await HandleToolCall(context, root, id, ct);
                break;

            default:
                await WriteJson(context, new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Method not found: {method}" } });
                break;
        }
    }

    private List<object> BuildToolList()
    {
        var list = new List<object>();

        // The permission-prompt tool Claude Code invokes when it needs approval.
        list.Add(new
        {
            name = PermissionPromptTool,
            description = "Permission prompt handler for the Splatter assistant (invoked by the harness; not for direct use).",
            inputSchema = new
            {
                type = "object",
                properties = new { tool_name = new { type = "string" }, input = new { type = "object" } }
            }
        });

        foreach (var toolId in ExposedToolIds)
        {
            var spec = _tools.GetTool(toolId);
            if (spec == null) continue;
            list.Add(new
            {
                name = McpName(toolId),
                description = spec.Description,
                inputSchema = spec.InputSchema
            });
        }
        return list;
    }

    private async Task HandleToolCall(HttpListenerContext context, JsonElement root, object? id, CancellationToken ct)
    {
        if (!root.TryGetProperty("params", out var prms))
        {
            await WriteJson(context, new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Missing params" } });
            return;
        }

        var mcpName = prms.TryGetProperty("name", out var n) ? n.GetString() : null;

        // Claude Code's --permission-prompt-tool callback: bubble the decision to the
        // editor and return its allow/deny verdict in Claude Code's expected shape.
        if (mcpName == PermissionPromptTool)
        {
            await HandlePermissionPrompt(context, prms, id, ct);
            return;
        }

        var toolId = ExposedToolIds.FirstOrDefault(t => McpName(t) == mcpName);
        if (toolId == null)
        {
            await WriteJson(context, new { jsonrpc = "2.0", id, error = new { code = -32602, message = $"Unknown tool: {mcpName}" } });
            return;
        }

        var argsEl = prms.TryGetProperty("arguments", out var a) ? a.Clone() : default;
        var arguments = argsEl.ValueKind != JsonValueKind.Undefined ? (object?)argsEl : null;
        var session = ResolveSession(context.Request);
        var argsSummary = SummarizeInput(toolId, argsEl);

        var toolCall = new ToolCall(
            $"mcp_{Guid.NewGuid():N}"[..16],
            toolId,
            arguments,
            new ToolCallSource(session.ProviderId, null, session.Id, "mcp"),
            ToolCallStatus.Requested);

        _interactiveSessions.CountToolCall(session.Id);
        EmitActivity(session, toolCall.Id, "started", toolId, argsSummary,
            ok: null, error: null, durationMs: null, gated: false, gateOutcome: null);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Server-side mutation gate: reads/captures and mode-approved calls pass
        // silently; everything else blocks on an editor Allow/Deny prompt. This is the
        // authoritative gate - the CLI pre-approves our MCP tools so users see exactly
        // one prompt, in the editor, with Unity context.
        var decision = await _permissions.GateToolCallAsync(session, toolCall, argsSummary, _config.McpGateTimeout, ct);
        var prompted = decision.DecidedBy is PermissionDecisionSource.User or PermissionDecisionSource.Timeout;
        if (decision.Outcome != PermissionOutcome.Allowed)
        {
            EmitActivity(session, toolCall.Id, "completed", toolId, argsSummary,
                ok: false, error: decision.Reason, durationMs: sw.ElapsedMilliseconds,
                gated: true, gateOutcome: "denied");
            await WriteToolResult(context, id, isError: true,
                text: $"Permission denied by the user in the Unity editor. {decision.Reason ?? ""} " +
                      "Do not retry this operation without asking the user first.");
            return;
        }

        ToolResult result;
        try
        {
            result = await _tools.ExecuteAsync(toolCall, session, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP tool {ToolId} failed", toolId);
            EmitActivity(session, toolCall.Id, "completed", toolId, argsSummary,
                ok: false, error: ex.Message, durationMs: sw.ElapsedMilliseconds,
                gated: prompted, gateOutcome: prompted ? "allowed" : null);
            await WriteToolResult(context, id, isError: true, text: $"Tool error: {ex.Message}");
            return;
        }

        EmitActivity(session, toolCall.Id, "completed", toolId, argsSummary,
            ok: result.Ok, error: result.Ok ? null : result.Error?.Message,
            durationMs: sw.ElapsedMilliseconds, gated: prompted, gateOutcome: prompted ? "allowed" : null);

        if (!result.Ok)
        {
            await WriteToolResult(context, id, isError: true, text: result.Error?.Message ?? "Tool failed");
            return;
        }

        // Capture tools return inline base64 image(s) -> hand them to the model as MCP
        // image content blocks rather than dumping base64 as text. Supports a single
        // image (game/scene/region) and a captures[] array (multi_angle).
        if (result.Value is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var blocks = new List<object>();
            if (je.TryGetProperty("image", out var single) && single.ValueKind == JsonValueKind.String)
                blocks.Add(ImageBlock(single.GetString(), MimeOf(je)));
            if (je.TryGetProperty("captures", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                foreach (var cap in caps.EnumerateArray())
                    if (cap.ValueKind == JsonValueKind.Object &&
                        cap.TryGetProperty("image", out var ci) && ci.ValueKind == JsonValueKind.String)
                        blocks.Add(ImageBlock(ci.GetString(), MimeOf(cap)));
            }
            if (blocks.Count > 0)
            {
                await WriteImageResult(context, id, blocks);
                return;
            }
        }

        var text = result.Value != null
            ? JsonSerializer.Serialize(result.Value)
            : result.Summary ?? "ok";
        await WriteToolResult(context, id, isError: false, text: text);
    }

    private static object ImageBlock(string? base64, string mimeType) =>
        new { type = "image", data = base64 ?? "", mimeType };

    private static string MimeOf(JsonElement el) =>
        el.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String
            ? (m.GetString() ?? "image/png") : "image/png";

    private static Task WriteImageResult(HttpListenerContext context, object? id, List<object> content) =>
        WriteJson(context, new
        {
            jsonrpc = "2.0",
            id,
            result = new { content = content.ToArray(), isError = false }
        });

    // Handles Claude Code's permission-prompt-tool call: { tool_name, input }. Bubbles a
    // permission request to the editor, waits for the user's decision, and returns the
    // {behavior:"allow"|"deny"} contract Claude Code expects (as JSON text content).
    private async Task HandlePermissionPrompt(HttpListenerContext context, JsonElement prms, object? id, CancellationToken ct)
    {
        var args = prms.TryGetProperty("arguments", out var a) ? a : default;
        var toolName = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("tool_name", out var tn)
            ? tn.GetString() : null;
        toolName ??= "tool";
        var input = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("input", out var inp)
            ? inp : default;

        var session = ResolveSession(context.Request);
        var decision = await _permissions.RequestAndAwaitAsync(
            session.Id, null, toolName, $"Allow {toolName}?", SummarizeInput(toolName, input), ct);

        string responseJson;
        if (decision.Outcome == PermissionOutcome.Allowed)
        {
            var inputJson = input.ValueKind == JsonValueKind.Object ? input.GetRawText() : "{}";
            responseJson = "{\"behavior\":\"allow\",\"updatedInput\":" + inputJson + "}";
        }
        else
        {
            responseJson = JsonSerializer.Serialize(new { behavior = "deny", message = decision.Reason ?? "Denied by the user." });
        }

        await WriteToolResult(context, id, isError: false, text: responseJson);
    }

    private static string SummarizeInput(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return $"The agent wants to use {toolName}.";
        foreach (var key in new[] { "command", "file_path", "path", "url", "pattern", "content" })
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString() ?? "";
                return $"{toolName}: {(s.Length > 240 ? s.Substring(0, 240) + "…" : s)}";
            }
        }
        var raw = input.GetRawText();
        return $"{toolName}: " + (raw.Length > 240 ? raw.Substring(0, 240) + "…" : raw);
    }

    private AgentSession ResolveSession(HttpListenerRequest request)
    {
        // Session token: header preferred; ?session= query param supported for CLIs
        // whose MCP config can't carry custom headers cleanly (e.g. Codex -c overrides).
        var token = request.Headers["X-Splatter-Session"];
        if (string.IsNullOrEmpty(token)) token = request.QueryString["session"];
        if (!string.IsNullOrEmpty(token) && _sessionTokens.TryGetValue(token, out var session))
        {
            _interactiveSessions.Touch(session.Id);
            return session;
        }

        // A token-less caller (e.g. a manually launched terminal) can still bind to a
        // specific workspace via a header or ?workspace= param, so Unity-forwarded tools
        // (scene.*, selection.*, console.*) broadcast to that editor and round-trip.
        // Absent both it's a standalone "mcp" workspace - service-side reads only.
        var workspace = request.Headers["X-Splatter-Workspace"];
        if (string.IsNullOrEmpty(workspace)) workspace = request.QueryString["workspace"];
        var ws = string.IsNullOrEmpty(workspace) ? "mcp" : workspace;
        var now = DateTimeOffset.UtcNow;
        return new AgentSession(
            $"mcp_{ws}", ws, ws, "claude-code", null, null,
            AgentPermissionMode.ReadOnly, AgentSessionStatus.Running, now, now, null);
    }

    private void EmitActivity(AgentSession session, string activityId, string phase, string toolId,
        string argsSummary, bool? ok, string? error, long? durationMs, bool gated, string? gateOutcome)
    {
        try
        {
            ToolActivity?.Invoke(this, new McpToolActivity(
                activityId, session.Id, session.WorkspaceId, session.ProviderId, phase, toolId,
                argsSummary, ok, error, durationMs, gated, gateOutcome));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tool.activity emit failed for {ToolId}", toolId);
        }
    }

    private static Task WriteToolResult(HttpListenerContext context, object? id, bool isError, string text) =>
        WriteJson(context, new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                content = new[] { new { type = "text", text } },
                isError
            }
        });

    private static async Task WriteJson(HttpListenerContext context, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static string McpName(string toolId) => toolId.Replace('.', '_');

    private static object JsonElementToIdValue(JsonElement idEl) => idEl.ValueKind switch
    {
        JsonValueKind.Number => idEl.GetInt64(),
        JsonValueKind.String => idEl.GetString() ?? string.Empty,
        _ => idEl.ToString()
    };
}

/// <summary>One start/finish record per MCP tools/call, rendered by the editor's activity feed.</summary>
public sealed record McpToolActivity(
    string ActivityId,
    string SessionId,
    string WorkspaceId,
    string ProviderId,
    string Phase,          // "started" | "completed"
    string ToolId,
    string ArgsSummary,
    bool? Ok,
    string? Error,
    long? DurationMs,
    bool Gated,            // true when an editor prompt (or denial) was involved
    string? GateOutcome);  // "allowed" | "denied" | null (not gated)
