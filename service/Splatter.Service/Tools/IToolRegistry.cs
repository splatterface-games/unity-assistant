// Tool Registry Interfaces

using Splatter.Protocol;

namespace Splatter.Service.Tools;

public interface IToolRegistry
{
    /// <summary>
    /// Register a tool with its handler.
    /// </summary>
    void RegisterTool(ToolSpec spec, Func<ToolCall, AgentSession, CancellationToken, Task<ToolResult>> handler);

    /// <summary>
    /// Get a tool specification by ID.
    /// </summary>
    ToolSpec? GetTool(string toolId);

    /// <summary>
    /// Get all tools available in a given permission mode.
    /// </summary>
    IReadOnlyList<ToolSpec> GetToolsForMode(AgentPermissionMode mode);

    /// <summary>
    /// Get all registered tools.
    /// </summary>
    IReadOnlyList<ToolSpec> GetAllTools();

    /// <summary>
    /// Execute a tool call.
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolCall toolCall, AgentSession session, CancellationToken ct);

    /// <summary>
    /// Called by Unity to complete a delegated tool call.
    /// </summary>
    void CompleteUnityToolCall(string callId, ToolResult result);

    /// <summary>Extends a pending Unity tool call's deadline when the editor signals a domain reload.</summary>
    void DeferUnityToolCall(string callId);

    /// <summary>Holds all in-flight Unity calls open when the editor disconnects (e.g. a domain reload).</summary>
    void HoldPendingUnityCalls();

    /// <summary>Resumes in-flight Unity calls after the editor reconnects: re-deliver reads, fail mutations.</summary>
    void ResumePendingUnityCalls();

    /// <summary>
    /// Event fired when a tool requires Unity main thread execution.
    /// </summary>
    event EventHandler<UnityToolRequest>? UnityToolRequested;
}
