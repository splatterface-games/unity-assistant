// Tool Execution Handler - Processes tool execution requests from the service
// Enhanced with batching, cancellation, timeout reporting, and editor-update yielding

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Splatter.Editor.Tools;
using Splatter.Editor.Transport;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Splatter.Protocol;

namespace Splatter.Editor.Handlers
{
    /// <summary>
    /// Handles tool execution requests from the Splatter service.
    /// Executes tools on the Unity main thread with batching, cancellation, and timeout support.
    /// </summary>
    public sealed class ToolExecutionHandler : IDisposable
    {
        private readonly IUnityToolRegistry _toolRegistry;
        private readonly ServiceClient _client;
        private readonly ConcurrentQueue<QueuedToolExecution> _executionQueue = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCancellations = new();
        private readonly object _processingLock = new();

        private bool _isProcessing;
        private bool _disposed;

        // Configuration
        private const int MaxBatchSize = 10;
        private const int MaxExecutionsPerFrame = 5;
        private const double MaxFrameTimeMs = 16.0; // Target ~60fps
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan LongRunningTimeout = TimeSpan.FromMinutes(15);

        public ToolExecutionHandler(IUnityToolRegistry toolRegistry, ServiceClient client)
        {
            _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// Starts listening for tool execution requests.
        /// </summary>
        public IDisposable StartListening()
        {
            EditorApplication.update += ProcessQueuedExecutions;
            return _client.SubscribeToAllEvents(OnEventReceived);
        }

        /// <summary>
        /// Cancels a specific tool execution by its tool call ID.
        /// </summary>
        public bool CancelExecution(string toolCallId)
        {
            if (_activeCancellations.TryRemove(toolCallId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                Debug.Log($"[Splatter] Cancelled tool execution: {toolCallId}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Cancels all active tool executions for a session.
        /// </summary>
        public void CancelSession(string sessionId)
        {
            foreach (var kvp in _activeCancellations)
            {
                if (kvp.Key.StartsWith(sessionId))
                {
                    if (_activeCancellations.TryRemove(kvp.Key, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                }
            }
        }

        private void OnEventReceived(MessageEnvelope envelope)
        {
            if (_disposed) return;

            switch (envelope.Type)
            {
                case "tool.execute":
                    HandleToolExecute(envelope);
                    break;
                case "tool.cancel":
                    HandleToolCancel(envelope);
                    break;
                case "tool.batch":
                    HandleToolBatch(envelope);
                    break;
            }
        }

        private void HandleToolExecute(MessageEnvelope envelope)
        {
            try
            {
                var request = Transport.ServiceClient.PayloadAs<ToolExecuteRequest>(envelope.Payload);
                EnqueueExecution(envelope.Id, request);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Failed to parse tool execute request: {ex.Message}");
            }
        }

        private void HandleToolCancel(MessageEnvelope envelope)
        {
            try
            {
                var request = Transport.ServiceClient.PayloadAs<ToolCancelRequest>(envelope.Payload);
                CancelExecution(request.tool_call_id);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Failed to parse tool cancel request: {ex.Message}");
            }
        }

        private void HandleToolBatch(MessageEnvelope envelope)
        {
            try
            {
                var batch = Transport.ServiceClient.PayloadAs<ToolBatchRequest>(envelope.Payload);
                foreach (var request in batch.requests)
                {
                    EnqueueExecution(envelope.Id, request);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Failed to parse tool batch request: {ex.Message}");
            }
        }

        private void EnqueueExecution(string requestId, ToolExecuteRequest request)
        {
            var queuedExecution = new QueuedToolExecution
            {
                RequestId = requestId,
                Request = request,
                QueuedAt = DateTime.UtcNow
            };

            _executionQueue.Enqueue(queuedExecution);
        }

        private void ProcessQueuedExecutions()
        {
            if (_disposed || _executionQueue.IsEmpty) return;

            // Prevent re-entrancy
            lock (_processingLock)
            {
                if (_isProcessing) return;
                _isProcessing = true;
            }

            try
            {
                var frameStopwatch = Stopwatch.StartNew();
                var executedCount = 0;

                while (!_executionQueue.IsEmpty &&
                       executedCount < MaxExecutionsPerFrame &&
                       frameStopwatch.Elapsed.TotalMilliseconds < MaxFrameTimeMs)
                {
                    if (_executionQueue.TryDequeue(out var queuedExecution))
                    {
                        _ = ExecuteToolAsync(queuedExecution);
                        executedCount++;
                    }
                }

                // Log if we have backlog
                if (_executionQueue.Count > MaxBatchSize)
                {
                    Debug.LogWarning($"[Splatter] Tool execution queue backlog: {_executionQueue.Count} pending");
                }
            }
            finally
            {
                lock (_processingLock)
                {
                    _isProcessing = false;
                }
            }
        }

        private async Task ExecuteToolAsync(QueuedToolExecution queuedExecution)
        {
            var request = queuedExecution.Request;
            var executionStopwatch = Stopwatch.StartNew();

            // Determine timeout based on tool type
            var timeout = GetTimeoutForTool(request.tool_id);

            // Create cancellation token with timeout
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

            // Register for manual cancellation
            _activeCancellations[request.tool_call_id] = linkedCts;

            ToolExecutionResult result;
            var metrics = new ToolExecutionMetrics
            {
                QueuedAt = queuedExecution.QueuedAt,
                StartedAt = DateTime.UtcNow
            };

            try
            {
                result = await ExecuteOnMainThread(async () =>
                {
                    var executor = _toolRegistry.GetExecutor(request.tool_id);
                    if (executor == null)
                    {
                        return ToolExecutionResult.Failed($"Unknown tool: {request.tool_id}");
                    }

                    var context = new ToolExecutionContext
                    {
                        SessionId = request.session_id,
                        TurnId = request.turn_id,
                        ToolCallId = request.tool_call_id,
                        ToolId = request.tool_id,
                        Arguments = ParseArguments(request.arguments),
                        WorkspacePath = request.workspace_path
                    };

                    return await executor.ExecuteAsync(context, linkedCts.Token);
                }, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // Timeout
                result = ToolExecutionResult.Failed($"Tool execution timed out after {timeout.TotalSeconds:F0} seconds");
                metrics.TimedOut = true;
                Debug.LogWarning($"[Splatter] Tool {request.tool_id} timed out after {timeout.TotalSeconds:F0}s");
            }
            catch (OperationCanceledException)
            {
                // Manual cancellation
                result = ToolExecutionResult.Failed("Tool execution was cancelled");
                metrics.Cancelled = true;
            }
            catch (Exception ex)
            {
                result = ToolExecutionResult.Failed($"Tool execution failed: {ex.Message}");
                Debug.LogError($"[Splatter] Tool {request.tool_id} failed: {ex}");
            }
            finally
            {
                _activeCancellations.TryRemove(request.tool_call_id, out _);
                metrics.CompletedAt = DateTime.UtcNow;
                metrics.DurationMs = executionStopwatch.ElapsedMilliseconds;
            }

            // Deferred: the tool triggered a domain reload (e.g. a recompile) and will complete
            // the call after the reload via ReloadSurvival. Record the pending call, tell the
            // service to hold it open, and don't send a result now.
            if (result.Deferred)
            {
                // The marker was already recorded on the main thread inside the executor; here we
                // only tell the service to hold the call open while the editor reloads.
                try
                {
                    await _client.SendNotificationAsync("tool.deferred",
                        new ToolDeferredNotice { tool_call_id = request.tool_call_id });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Splatter] Failed to send tool.deferred: {ex.Message}");
                }
                return;
            }

            // Send result back to service
            var response = new ToolExecuteResponse
            {
                tool_call_id = request.tool_call_id,
                success = result.Success,
                // Newtonsoft, not JsonUtility: tool outputs are anonymous objects /
                // dictionaries / lists, which JsonUtility.ToJson cannot serialize (it
                // only handles concrete [Serializable] types and returns "{}" otherwise,
                // silently discarding the result). The service parses this as JSON.
                output = result.Output != null ? JsonConvert.SerializeObject(result.Output) : null,
                error = result.Error,
                affected_paths = result.AffectedPaths?.ToArray(),
                metrics = new ToolExecuteResponseMetrics
                {
                    duration_ms = metrics.DurationMs,
                    queue_wait_ms = (long)(metrics.StartedAt - metrics.QueuedAt).TotalMilliseconds,
                    timed_out = metrics.TimedOut,
                    cancelled = metrics.Cancelled
                }
            };

            try
            {
                await _client.SendNotificationAsync("tool.result", response);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Failed to send tool result: {ex.Message}");
            }
        }

        private TimeSpan GetTimeoutForTool(string toolId)
        {
            // Long-running tools get extended timeout
            if (toolId.StartsWith("package.") ||
                toolId.StartsWith("generator.") ||
                toolId == "script.compile_check" ||
                toolId == "asset.import")
            {
                return LongRunningTimeout;
            }

            return DefaultTimeout;
        }

        private Dictionary<string, object> ParseArguments(string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson))
                return new Dictionary<string, object>();

            try
            {
                return SimpleJsonParser.ParseObject(argumentsJson);
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static async Task<T> ExecuteOnMainThread<T>(Func<Task<T>> action, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<T>();

            // Register cancellation
            using var registration = ct.Register(() => tcs.TrySetCanceled());

            MainThreadDispatcher.Enqueue(async () =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled();
                    return;
                }

                try
                {
                    var result = await action();
                    tcs.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return await tcs.Task;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            EditorApplication.update -= ProcessQueuedExecutions;

            // Cancel all active executions
            foreach (var kvp in _activeCancellations)
            {
                kvp.Value.Cancel();
                kvp.Value.Dispose();
            }
            _activeCancellations.Clear();
        }
    }

    #region Request/Response Types

    [Serializable]
    public class ToolExecuteRequest
    {
        public string session_id;
        public string turn_id;
        public string tool_call_id;
        public string tool_id;
        public string arguments;
        public string workspace_path;
    }

    [Serializable]
    public class ToolCancelRequest
    {
        public string tool_call_id;
        public string session_id;
    }

    [Serializable]
    public class ToolBatchRequest
    {
        public ToolExecuteRequest[] requests;
    }

    [Serializable]
    public class ToolExecuteResponse
    {
        public string tool_call_id;
        public bool success;
        public string output;
        public string error;
        public string[] affected_paths;
        public ToolExecuteResponseMetrics metrics;
    }

    [Serializable]
    public class ToolDeferredNotice
    {
        public string tool_call_id;
    }

    [Serializable]
    public class ToolExecuteResponseMetrics
    {
        public long duration_ms;
        public long queue_wait_ms;
        public bool timed_out;
        public bool cancelled;
    }

    internal class QueuedToolExecution
    {
        public string RequestId;
        public ToolExecuteRequest Request;
        public DateTime QueuedAt;
    }

    internal class ToolExecutionMetrics
    {
        public DateTime QueuedAt;
        public DateTime StartedAt;
        public DateTime CompletedAt;
        public long DurationMs;
        public bool TimedOut;
        public bool Cancelled;
    }

    #endregion

    #region JSON Parser

    /// <summary>
    /// Simple JSON parser for dictionaries since Unity's JsonUtility doesn't support them.
    /// </summary>
    internal static class SimpleJsonParser
    {
        public static Dictionary<string, object> ParseObject(string json)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(json)) return result;

            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}"))
                return result;

            json = json.Substring(1, json.Length - 2).Trim();
            if (string.IsNullOrEmpty(json)) return result;

            var index = 0;
            while (index < json.Length)
            {
                // Skip whitespace
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index >= json.Length) break;

                // Parse key
                var key = ParseString(json, ref index);
                if (key == null) break;

                // Skip colon
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index >= json.Length || json[index] != ':') break;
                index++;

                // Skip whitespace
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;

                // Parse value
                var value = ParseValue(json, ref index);
                result[key] = value;

                // Skip comma
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index < json.Length && json[index] == ',') index++;
            }

            return result;
        }

        private static string ParseString(string json, ref int index)
        {
            if (index >= json.Length || json[index] != '"') return null;
            index++; // Skip opening quote

            var start = index;
            while (index < json.Length && json[index] != '"')
            {
                if (json[index] == '\\') index++; // Skip escaped char
                index++;
            }

            var result = json.Substring(start, index - start);
            if (index < json.Length) index++; // Skip closing quote

            // Unescape
            return result.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static object ParseValue(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            if (index >= json.Length) return null;

            var ch = json[index];

            // String
            if (ch == '"')
            {
                return ParseString(json, ref index);
            }

            // Object
            if (ch == '{')
            {
                var depth = 1;
                var start = index;
                index++;
                while (index < json.Length && depth > 0)
                {
                    if (json[index] == '{') depth++;
                    else if (json[index] == '}') depth--;
                    else if (json[index] == '"') ParseString(json, ref index);
                    else index++;
                }
                return ParseObject(json.Substring(start, index - start));
            }

            // Array
            if (ch == '[')
            {
                var list = new List<object>();
                index++; // Skip [
                while (index < json.Length)
                {
                    while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                    if (index >= json.Length || json[index] == ']') break;

                    list.Add(ParseValue(json, ref index));

                    while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                    if (index < json.Length && json[index] == ',') index++;
                }
                if (index < json.Length) index++; // Skip ]
                return list;
            }

            // Boolean or null
            if (json.Substring(index).StartsWith("true"))
            {
                index += 4;
                return true;
            }
            if (json.Substring(index).StartsWith("false"))
            {
                index += 5;
                return false;
            }
            if (json.Substring(index).StartsWith("null"))
            {
                index += 4;
                return null;
            }

            // Number
            var numStart = index;
            if (json[index] == '-') index++;
            while (index < json.Length && (char.IsDigit(json[index]) || json[index] == '.' || json[index] == 'e' || json[index] == 'E' || json[index] == '+' || json[index] == '-'))
            {
                index++;
            }

            var numStr = json.Substring(numStart, index - numStart);
            if (numStr.Contains(".") || numStr.Contains("e") || numStr.Contains("E"))
            {
                if (double.TryParse(numStr, out var d)) return d;
            }
            else
            {
                if (long.TryParse(numStr, out var l)) return l;
            }

            return numStr;
        }
    }

    #endregion
}
