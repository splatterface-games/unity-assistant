// Script Tool Executors - Code analysis and compilation

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Checks for compilation errors in the project.
    /// </summary>
    public class ScriptCompileCheckExecutor : IToolExecutor
    {
        public string ToolId => "script.compile_check";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // A recompile triggers a domain reload that tears down this call's round-trip.
                // Trigger it and DEFER: ReloadSurvival completes the call with FRESH errors once
                // the reload settles. (Reading errors now would report stale, pre-recompile state,
                // and the reload would orphan the turn.)
                if (context.Arguments.TryGetValue("recompile", out var recompile) && Convert.ToBoolean(recompile))
                {
                    // Record the deferral HERE (on the main thread, before the reload) — SessionState
                    // and EditorApplication are main-thread-only, and the handler's post-await
                    // continuation may run off-thread.
                    Splatter.Editor.Handlers.ReloadSurvival.Defer(context.ToolCallId, ToolId);
                    AssetDatabase.Refresh();
                    CompilationPipeline.RequestScriptCompilation();
                    return Task.FromResult(ToolExecutionResult.Defer());
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(ReadCompileResult()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        // Reads the current C# compiler errors/warnings. Used for an immediate check and, via
        // ReloadSurvival, to complete a deferred recompile after the domain reload.
        public static object ReadCompileResult()
        {
            var messages = new List<object>();
            foreach (var entry in GetCompilationMessages())
            {
                messages.Add(new { type = entry.type, message = entry.message, file = entry.file, line = entry.line });
            }
            return new
            {
                hasErrors = messages.Any(m => ((dynamic)m).type == "error"),
                errorCount = messages.Count(m => ((dynamic)m).type == "error"),
                warningCount = messages.Count(m => ((dynamic)m).type == "warning"),
                messages
            };
        }

        private static List<(string type, string message, string file, int line)> GetCompilationMessages()
        {
            var messages = new List<(string type, string message, string file, int line)>();

            // Use reflection to access internal console log entries
            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
                if (logEntriesType != null)
                {
                    var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public);
                    var startMethod = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public);
                    var endMethod = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public);
                    var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public);

                    if (getCountMethod != null && startMethod != null && endMethod != null)
                    {
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

                            for (var i = 0; i < Math.Min(count, 100); i++)
                            {
                                getEntryMethod.Invoke(null, new[] { i, entry });
                                var mode = (int)modeField.GetValue(entry);
                                var message = (string)messageField.GetValue(entry);
                                var file = (string)fileField.GetValue(entry);
                                var line = (int)lineField.GetValue(entry);

                                // Filter for compilation messages
                                if (message.Contains("error CS") || message.Contains("warning CS"))
                                {
                                    var type = message.Contains("error CS") ? "error" : "warning";
                                    messages.Add((type, message, file, line));
                                }
                            }
                        }
                        finally
                        {
                            endMethod.Invoke(null, null);
                        }
                    }
                }
            }
            catch
            {
                // Fallback - return empty list if reflection fails
            }

            return messages;
        }
    }

    /// <summary>
    /// Gets references to/from a script or type.
    /// </summary>
    public class ScriptGetReferencesExecutor : IToolExecutor
    {
        public string ToolId => "script.get_references";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var typeName = context.Arguments.TryGetValue("type", out var t) ? t?.ToString() : null;
                var scriptPath = context.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;

                Type targetType = null;

                if (!string.IsNullOrEmpty(typeName))
                {
                    targetType = FindType(typeName);
                }
                else if (!string.IsNullOrEmpty(scriptPath))
                {
                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                    if (script != null)
                    {
                        targetType = script.GetClass();
                    }
                }

                if (targetType == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Type not found"));
                }

                var references = new List<object>();
                var referencedBy = new List<object>();

                // Find what this type references
                var fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var field in fields)
                {
                    var fieldType = field.FieldType;
                    if (fieldType.IsArray) fieldType = fieldType.GetElementType();
                    if (fieldType.IsGenericType) fieldType = fieldType.GetGenericArguments().FirstOrDefault() ?? fieldType;

                    if (fieldType != null && !fieldType.IsPrimitive && fieldType != typeof(string))
                    {
                        references.Add(new
                        {
                            field = field.Name,
                            type = fieldType.Name,
                            fullType = fieldType.FullName
                        });
                    }
                }

                // Find scene references (GameObjects using this component)
                if (typeof(Component).IsAssignableFrom(targetType))
                {
                    var sceneRefs = UnityEngine.Object.FindObjectsByType(targetType, FindObjectsSortMode.None);
                    foreach (var obj in sceneRefs.Take(50))
                    {
                        var comp = obj as Component;
                        if (comp != null)
                        {
                            referencedBy.Add(new
                            {
                                gameObject = comp.gameObject.name,
                                instanceId = comp.gameObject.GetInstanceID(),
                                scene = comp.gameObject.scene.name
                            });
                        }
                    }
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    type = targetType.Name,
                    fullName = targetType.FullName,
                    assembly = targetType.Assembly.GetName().Name,
                    baseType = targetType.BaseType?.Name,
                    interfaces = targetType.GetInterfaces().Select(i => i.Name).ToList(),
                    references,
                    referencedBy
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Type FindType(string typeName)
        {
            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName);
                if (type != null) return type;

                // Try with common namespaces
                type = assembly.GetType($"UnityEngine.{typeName}");
                if (type != null) return type;

                type = assembly.GetType($"UnityEditor.{typeName}");
                if (type != null) return type;
            }

            return null;
        }
    }
}
