// Tool Executor Interfaces

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Interface for executing tools on the Unity side.
    /// </summary>
    public interface IToolExecutor
    {
        string ToolId { get; }
        Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct);
    }

    /// <summary>
    /// Context for tool execution.
    /// </summary>
    public class ToolExecutionContext
    {
        public string SessionId { get; set; }
        public string TurnId { get; set; }
        public string ToolCallId { get; set; }
        public string ToolId { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
        public string WorkspacePath { get; set; }
    }

    /// <summary>
    /// Result of tool execution.
    /// </summary>
    public class ToolExecutionResult
    {
        public bool Success { get; set; }
        public object Output { get; set; }
        public string Error { get; set; }
        public List<string> AffectedPaths { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        // The executor triggered a domain reload and will complete the call AFTER the reload
        // (via ReloadSurvival). The handler must not send a tool.result now.
        public bool Deferred { get; set; }

        // Signals that this call is deferred across a domain reload. ReloadSurvival re-runs
        // ComputeResult(toolId) once the reload settles and sends the real result.
        public static ToolExecutionResult Defer() => new ToolExecutionResult { Success = true, Deferred = true };

        public static ToolExecutionResult Succeeded(object output, List<string> affectedPaths = null)
        {
            return new ToolExecutionResult
            {
                Success = true,
                Output = output,
                AffectedPaths = affectedPaths ?? new List<string>()
            };
        }

        public static ToolExecutionResult Failed(string error)
        {
            return new ToolExecutionResult
            {
                Success = false,
                Error = error
            };
        }
    }

    /// <summary>
    /// Registry of tool executors available in Unity.
    /// </summary>
    public interface IUnityToolRegistry
    {
        void Register(IToolExecutor executor);
        void Unregister(string toolId);
        IToolExecutor GetExecutor(string toolId);
        IEnumerable<IToolExecutor> GetAllExecutors();
    }
}
