// Console Tool Executors - Unity console operations

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Gets log entries from the Unity console.
    /// </summary>
    public class ConsoleGetLogsExecutor : IToolExecutor
    {
        public string ToolId => "console.get_logs";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var maxCount = context.Arguments.TryGetValue("max_count", out var mc) ? Convert.ToInt32(mc) : 100;
                var logType = context.Arguments.TryGetValue("type", out var lt) ? lt?.ToString() : null;
                var filter = context.Arguments.TryGetValue("filter", out var f) ? f?.ToString() : null;

                var logs = GetConsoleLogs(maxCount, logType, filter);

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    count = logs.Count,
                    logs
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private List<object> GetConsoleLogs(int maxCount, string typeFilter, string textFilter)
        {
            var logs = new List<object>();

            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
                if (logEntriesType == null) return logs;

                var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public);
                var startMethod = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var endMethod = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);
                var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);

                if (getCountMethod == null || startMethod == null || endMethod == null || getEntryMethod == null)
                    return logs;

                var count = (int)getCountMethod.Invoke(null, null);
                startMethod.Invoke(null, null);

                try
                {
                    var logEntryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
                    var entry = Activator.CreateInstance(logEntryType);
                    var modeField = logEntryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public);
                    var messageField = logEntryType.GetField("message", BindingFlags.Instance | BindingFlags.Public);
                    var fileField = logEntryType.GetField("file", BindingFlags.Instance | BindingFlags.Public);
                    var lineField = logEntryType.GetField("line", BindingFlags.Instance | BindingFlags.Public);

                    // Process from newest to oldest
                    for (var i = count - 1; i >= 0 && logs.Count < maxCount; i--)
                    {
                        getEntryMethod.Invoke(null, new[] { i, entry });
                        var mode = (int)modeField.GetValue(entry);
                        var message = (string)messageField.GetValue(entry);
                        var file = (string)fileField.GetValue(entry);
                        var line = (int)lineField.GetValue(entry);

                        var entryType = GetLogType(mode);

                        // Apply filters
                        if (!string.IsNullOrEmpty(typeFilter) && !string.Equals(entryType, typeFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!string.IsNullOrEmpty(textFilter) && message.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        logs.Add(new
                        {
                            type = entryType,
                            message = TruncateMessage(message, 500),
                            file,
                            line
                        });
                    }
                }
                finally
                {
                    endMethod.Invoke(null, null);
                }
            }
            catch
            {
                // Return empty list if reflection fails
            }

            return logs;
        }

        private string GetLogType(int mode)
        {
            // Mode flags from Unity internal API
            const int kModeError = 1;
            const int kModeAssert = 2;
            const int kModeWarning = 4;
            const int kModeLog = 8;
            const int kModeException = 16;

            if ((mode & kModeException) != 0) return "exception";
            if ((mode & kModeError) != 0) return "error";
            if ((mode & kModeAssert) != 0) return "assert";
            if ((mode & kModeWarning) != 0) return "warning";
            return "log";
        }

        private string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
                return message;

            return message.Substring(0, maxLength) + "...";
        }
    }

    /// <summary>
    /// Clears the Unity console.
    /// </summary>
    public class ConsoleClearExecutor : IToolExecutor
    {
        public string ToolId => "console.clear";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
                if (logEntriesType != null)
                {
                    var clearMethod = logEntriesType.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public);
                    clearMethod?.Invoke(null, null);
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new { cleared = true }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }
}
