// Project Tool Executors - File and project operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Lists files in the project matching a pattern.
    /// </summary>
    public class ProjectListFilesExecutor : IToolExecutor
    {
        public string ToolId => "project.list_files";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var path = context.Arguments.TryGetValue("path", out var p) ? p?.ToString() : "Assets";
                var pattern = context.Arguments.TryGetValue("pattern", out var pat) ? pat?.ToString() : "*";
                var recursive = context.Arguments.TryGetValue("recursive", out var rec) && Convert.ToBoolean(rec);

                var fullPath = Path.Combine(Application.dataPath, "..", path);
                if (!Directory.Exists(fullPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Directory not found: {path}"));
                }

                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.GetFiles(fullPath, pattern, searchOption)
                    .Select(f => Path.GetRelativePath(Path.Combine(Application.dataPath, ".."), f))
                    .Where(f => !f.EndsWith(".meta"))
                    .OrderBy(f => f)
                    .ToList();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    path,
                    pattern,
                    count = files.Count,
                    files
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Reads a file from the project.
    /// </summary>
    public class ProjectReadFileExecutor : IToolExecutor
    {
        public string ToolId => "project.read_file";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var path = context.Arguments["path"]?.ToString();
                if (string.IsNullOrEmpty(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Path is required"));
                }

                var fullPath = Path.Combine(Application.dataPath, "..", path);
                if (!File.Exists(fullPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"File not found: {path}"));
                }

                // Security check - ensure path is within project
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var normalizedPath = Path.GetFullPath(fullPath);
                if (!normalizedPath.StartsWith(projectRoot))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Access denied: Path is outside project"));
                }

                var content = File.ReadAllText(fullPath);
                var maxLength = 100000; // Limit content size

                if (content.Length > maxLength)
                {
                    content = content.Substring(0, maxLength) + "\n\n[TRUNCATED]";
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    path,
                    content,
                    size = new FileInfo(fullPath).Length,
                    truncated = content.Length > maxLength
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Writes a file to the project.
    /// </summary>
    public class ProjectWriteFileExecutor : IToolExecutor
    {
        public string ToolId => "project.write_file";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var path = context.Arguments["path"]?.ToString();
                var content = context.Arguments["content"]?.ToString();

                if (string.IsNullOrEmpty(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Path is required"));
                }

                var fullPath = Path.Combine(Application.dataPath, "..", path);

                // Security check
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var normalizedPath = Path.GetFullPath(fullPath);
                if (!normalizedPath.StartsWith(projectRoot))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Access denied: Path is outside project"));
                }

                // Create directory if needed
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var isNew = !File.Exists(fullPath);
                File.WriteAllText(fullPath, content ?? "");

                // Refresh AssetDatabase if in Assets folder
                if (path.StartsWith("Assets/") || path.StartsWith("Assets\\"))
                {
                    AssetDatabase.Refresh();
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    path,
                    created = isNew,
                    size = content?.Length ?? 0
                }, new List<string> { path }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Searches for text in project files.
    /// </summary>
    public class ProjectSearchExecutor : IToolExecutor
    {
        public string ToolId => "project.search";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var query = context.Arguments["query"]?.ToString();
                var path = context.Arguments.TryGetValue("path", out var p) ? p?.ToString() : "Assets";
                var filePattern = context.Arguments.TryGetValue("file_pattern", out var fp) ? fp?.ToString() : "*.cs";
                var maxResults = context.Arguments.TryGetValue("max_results", out var mr) ? Convert.ToInt32(mr) : 50;
                var isRegex = context.Arguments.TryGetValue("regex", out var rx) && Convert.ToBoolean(rx);

                if (string.IsNullOrEmpty(query))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Query is required"));
                }

                var fullPath = Path.Combine(Application.dataPath, "..", path);
                if (!Directory.Exists(fullPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Directory not found: {path}"));
                }

                var results = new List<object>();
                var regex = isRegex ? new Regex(query, RegexOptions.IgnoreCase) : null;

                foreach (var file in Directory.GetFiles(fullPath, filePattern, SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested || results.Count >= maxResults)
                        break;

                    try
                    {
                        var content = File.ReadAllText(file);
                        var lines = content.Split('\n');
                        var relativePath = Path.GetRelativePath(Path.Combine(Application.dataPath, ".."), file);

                        for (var i = 0; i < lines.Length && results.Count < maxResults; i++)
                        {
                            var line = lines[i];
                            var matches = isRegex
                                ? regex.IsMatch(line)
                                : line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (matches)
                            {
                                results.Add(new
                                {
                                    file = relativePath,
                                    line = i + 1,
                                    content = line.Trim(),
                                    context = GetLineContext(lines, i, 1)
                                });
                            }
                        }
                    }
                    catch
                    {
                        // Skip files that can't be read
                    }
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    query,
                    path,
                    count = results.Count,
                    results
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetLineContext(string[] lines, int lineIndex, int contextLines)
        {
            var start = Math.Max(0, lineIndex - contextLines);
            var end = Math.Min(lines.Length - 1, lineIndex + contextLines);

            var contextParts = new List<string>();
            for (var i = start; i <= end; i++)
            {
                var prefix = i == lineIndex ? ">>> " : "    ";
                contextParts.Add($"{prefix}{i + 1}: {lines[i]}");
            }

            return string.Join("\n", contextParts);
        }
    }
}
