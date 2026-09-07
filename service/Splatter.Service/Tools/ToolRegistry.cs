// Tool Registry Implementation - Full Implementation with Real Tool Handlers

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Generators;
using Splatter.Service.Language;

namespace Splatter.Service.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ILogger<ToolRegistry> _logger;
    private readonly ServiceConfiguration _config;
    private readonly IGeneratorService _generators;
    private readonly ICSharpLanguageService _language;
    private readonly ConcurrentDictionary<string, RegisteredTool> _tools = new();

    // Tools that require Unity main thread - these get forwarded to Unity
    private static readonly HashSet<string> UnityMainThreadTools = new()
    {
        // Asset tools
        "asset.search", "asset.read", "asset.get_dependencies",
        "asset.create", "asset.delete", "asset.import",
        // Scene tools
        "scene.get_hierarchy", "scene.find_objects", "scene.read_object",
        "scene.create_gameobject", "scene.modify_gameobject", "scene.delete_gameobject",
        "scene.add_component", "scene.set_component_property",
        "scene.remove_component", "scene.duplicate_object", "scene.reparent_object",
        "scene.get_visible_objects",
        // Prefab tools
        "prefab.create", "prefab.instantiate", "prefab.override",
        // Package tools
        "package.list", "package.search", "package.add", "package.remove",
        // Console tools
        "console.get_logs", "console.clear",
        // Capture tools (render viewports to images)
        "capture.game", "capture.scene", "capture.multi_angle", "capture.region",
        // Selection tools
        "selection.get", "selection.set",
        // Capture tools
        "capture.scene", "capture.game",
        // Script tools
        "script.compile_check", "script.get_references",
        // Checkpoint tools
        "checkpoint.list", "checkpoint.create", "checkpoint.restore",
        // Graph tools
        "graph.query",
        // Skill tools
        "skill.list", "skill.read_body", "skill.read_resource"
        // Note: Generator tools (generator.*) are handled by the service, not Unity
    };

    // Pending Unity tool calls
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolResult>> _pendingUnityCalls = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingUnityCallCts = new();
    private readonly ConcurrentDictionary<string, PendingUnityCall> _pendingUnityCallInfo = new();

    // Tracks an in-flight delegated call so it can be resumed if the editor reloads mid-flight.
    private sealed class PendingUnityCall
    {
        public UnityToolRequest Request { get; }
        public bool Deferred { get; set; }   // editor self-completes after the reload (e.g. compile-check)
        public PendingUnityCall(UnityToolRequest request) => Request = request;
    }

    // Pending plan reviews keyed by session id (set by plan.exit_mode).
    private readonly ConcurrentDictionary<string, PendingPlanReview> _pendingPlanReviews = new();

    // Sessions that have surfaced a generation quote, establishing explicit, costed
    // intent. Generation submit requires this (or an explicit confirmGeneration flag)
    // so paid/asset-writing generation cannot run as an unsolicited first action.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _generationQuoted = new();

    private const string PlansRelativeRoot = "Assets/Plans";

    private sealed record PendingPlanReview(string PlanPath, string? Title, DateTimeOffset SubmittedAt);

    public event EventHandler<UnityToolRequest>? UnityToolRequested;

    public ToolRegistry(ILogger<ToolRegistry> logger, ServiceConfiguration config, IGeneratorService generators, ICSharpLanguageService? language = null)
    {
        _logger = logger;
        _config = config;
        _generators = generators;
        _language = language ?? new UnavailableCSharpLanguageService();
        RegisterBuiltInTools();
    }

    public void RegisterTool(ToolSpec spec, Func<ToolCall, AgentSession, CancellationToken, Task<ToolResult>> handler)
    {
        _tools[spec.Id] = new RegisteredTool(spec, handler);
        _logger.LogDebug("Registered tool: {ToolId}", spec.Id);
    }

    public ToolSpec? GetTool(string toolId)
    {
        return _tools.TryGetValue(toolId, out var tool) ? tool.Spec : null;
    }

    public IReadOnlyList<ToolSpec> GetToolsForMode(AgentPermissionMode mode)
    {
        return _tools.Values
            .Where(t => IsToolAllowedInMode(t.Spec, mode))
            .Select(t => t.Spec)
            .ToList();
    }

    public IReadOnlyList<ToolSpec> GetAllTools()
    {
        return _tools.Values.Select(t => t.Spec).ToList();
    }

    private static bool IsToolAllowedInMode(ToolSpec spec, AgentPermissionMode mode)
    {
        if (spec.Category is ToolCategory.ProjectRead or ToolCategory.AssetRead or ToolCategory.SceneRead)
            return true;

        if (mode == AgentPermissionMode.ReadOnly)
            return false;

        return true;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall toolCall, AgentSession session, CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolCall.ToolId, out var tool))
        {
            return new ToolResult(
                toolCall.Id,
                false, null, null,
                new NormalizedError(ErrorCodes.ToolFailed, $"Unknown tool: {toolCall.ToolId}", null, false, null, null),
                null, null);
        }

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(tool.Spec.Execution.TimeoutMs);

            var result = await tool.Handler(toolCall, session, cts.Token);

            _logger.LogDebug("Tool {ToolId} completed in {Duration}ms",
                toolCall.ToolId, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolResult(
                toolCall.Id, false, null, null,
                new NormalizedError(ErrorCodes.ToolTimeout, "Tool execution timed out", null, true, null, null),
                null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolId} failed", toolCall.ToolId);
            return new ToolResult(
                toolCall.Id, false, null, null,
                new NormalizedError(ErrorCodes.ToolFailed, ex.Message, ex.ToString(), false, null, null),
                null, null);
        }
    }

    /// <summary>
    /// Called by Unity to provide results for delegated tool calls.
    /// </summary>
    public void CompleteUnityToolCall(string callId, ToolResult result)
    {
        if (_pendingUnityCalls.TryRemove(callId, out var tcs))
        {
            tcs.TrySetResult(result);
        }
    }

    private void RegisterBuiltInTools()
    {
        // === PROJECT READ TOOLS ===
        RegisterTool(CreateToolSpec(
            "project.list_files", "List Project Files",
            "List files in the Unity project with optional filters",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                path = new { type = "string", description = "Path relative to project root (default: Assets)" },
                pattern = new { type = "string", description = "Glob pattern filter (e.g., *.cs)" },
                maxDepth = new { type = "integer", description = "Maximum directory depth", @default = 10 },
                includeHidden = new { type = "boolean", description = "Include hidden files", @default = false }
            }},
            PermissionClass.ReadProject, PermissionRisk.Low, false),
            ListProjectFilesAsync);

        RegisterTool(CreateToolSpec(
            "project.read_file", "Read File",
            "Read the contents of a file in the project",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                path = new { type = "string", description = "File path relative to project root" },
                startLine = new { type = "integer", description = "Start line (1-based)" },
                endLine = new { type = "integer", description = "End line (inclusive)" },
                maxLines = new { type = "integer", description = "Maximum lines to return", @default = 500 }
            }, required = new[] { "path" }},
            PermissionClass.ReadProject, PermissionRisk.Low, false),
            ReadFileAsync);

        RegisterTool(CreateToolSpec(
            "project.search_text", "Search Text",
            "Search for text patterns in project files using regex",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                pattern = new { type = "string", description = "Search pattern (regex supported)" },
                path = new { type = "string", description = "Directory to search (default: Assets)" },
                filePattern = new { type = "string", description = "File glob pattern (e.g., *.cs)" },
                caseSensitive = new { type = "boolean", description = "Case sensitive search", @default = false },
                maxResults = new { type = "integer", description = "Maximum results", @default = 50 },
                contextLines = new { type = "integer", description = "Lines of context around matches", @default = 2 }
            }, required = new[] { "pattern" }},
            PermissionClass.ReadProject, PermissionRisk.Low, false),
            SearchTextAsync);

        RegisterTool(CreateToolSpec(
            "project.get_overview", "Get Project Overview",
            "Get high-level project overview including folder structure and stats",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                maxDepth = new { type = "integer", description = "Maximum folder depth", @default = 3 },
                includeStats = new { type = "boolean", description = "Include file statistics", @default = true }
            }},
            PermissionClass.ReadProject, PermissionRisk.Low, false),
            GetProjectOverviewAsync);

        // === CODE EDIT TOOLS ===
        RegisterTool(CreateToolSpec(
            "code.propose_patch", "Propose Code Patch",
            "Propose changes to a file using unified diff format",
            ToolCategory.CodeEdit,
            new { type = "object", properties = new {
                path = new { type = "string", description = "File path relative to project root" },
                oldContent = new { type = "string", description = "Exact content to find and replace" },
                newContent = new { type = "string", description = "New content to insert" },
                description = new { type = "string", description = "Description of the change" }
            }, required = new[] { "path", "oldContent", "newContent" }},
            PermissionClass.WriteProject, PermissionRisk.Medium, false),
            ProposePatchAsync);

        RegisterTool(CreateToolSpec(
            "code.apply_patch", "Apply Code Patch",
            "Apply a previously proposed patch",
            ToolCategory.CodeEdit,
            new { type = "object", properties = new {
                patchId = new { type = "string", description = "ID of the patch to apply" },
                createBackup = new { type = "boolean", description = "Create backup before applying", @default = true }
            }, required = new[] { "patchId" }},
            PermissionClass.WriteProject, PermissionRisk.Medium, false),
            ApplyPatchAsync);

        RegisterTool(CreateToolSpec(
            "code.create_file", "Create File",
            "Create a new file in the project",
            ToolCategory.CodeEdit,
            new { type = "object", properties = new {
                path = new { type = "string", description = "File path relative to project root" },
                content = new { type = "string", description = "File content" },
                overwrite = new { type = "boolean", description = "Overwrite if exists", @default = false }
            }, required = new[] { "path", "content" }},
            PermissionClass.WriteProject, PermissionRisk.Medium, false),
            CreateFileAsync);

        RegisterTool(CreateToolSpec(
            "code.delete_file", "Delete File",
            "Delete a file from the project",
            ToolCategory.CodeEdit,
            new { type = "object", properties = new {
                path = new { type = "string", description = "File path relative to project root" }
            }, required = new[] { "path" }},
            PermissionClass.DeleteProject, PermissionRisk.High, false),
            DeleteFileAsync);

        // === PLAN TOOLS ===
        // Plan documents live under Assets/Plans/. WritePlan is creation-only;
        // revisions must go through EditPlan as diff-like edits.
        RegisterTool(CreateToolSpec(
            "plan.write", "Write Plan",
            "Create a new plan document under Assets/Plans/. Creation-only; use plan.edit to revise an existing plan.",
            ToolCategory.CodeEdit,
            new { type = "object", properties = new {
                filePath = new { type = "string", description = "Plan path under Assets/Plans/, ending in .md" },
                content = new { type = "string", description = "Full markdown content of the plan" }
            }, required = new[] { "filePath", "content" }},
            PermissionClass.WriteProject, PermissionRisk.Medium, false),
            PlanWriteAsync);

        RegisterTool(CreateToolSpec(
            "plan.edit", "Edit Plan",
            "Revise an existing plan under Assets/Plans/ via exact string replacement (diff-like edit).",
            ToolCategory.CodeEdit,
            new { type = "object", properties = new {
                filePath = new { type = "string", description = "Existing plan path under Assets/Plans/" },
                oldString = new { type = "string", description = "Exact text to replace (must be non-empty and present)" },
                newString = new { type = "string", description = "Replacement text" },
                expectedOccurrences = new { type = "integer", description = "Required number of matches (default 1)", @default = 1 }
            }, required = new[] { "filePath", "oldString", "newString" }},
            PermissionClass.WriteProject, PermissionRisk.Medium, false),
            PlanEditAsync);

        RegisterTool(CreateToolSpec(
            "plan.exit_mode", "Exit Plan Mode",
            "Submit a plan under Assets/Plans/ for user approval or revision, returning the pending review state.",
            ToolCategory.UserInteraction,
            new { type = "object", properties = new {
                planPath = new { type = "string", description = "Plan path under Assets/Plans/ to submit for review" },
                title = new { type = "string", description = "Optional title shown in the approval prompt" }
            }, required = new[] { "planPath" }},
            PermissionClass.UserInteraction, PermissionRisk.Low, false),
            PlanExitModeAsync);

        RegisterTool(CreateToolSpec(
            "plan.write_todos", "Write Todos",
            "Set the agent's todo list for the active plan. Exactly one todo may be in_progress. Does not write a file.",
            ToolCategory.UserInteraction,
            new { type = "object", properties = new {
                planPath = new { type = "string", description = "Plan path under Assets/Plans/ the todos belong to" },
                todos = new { type = "array", description = "Todo items", items = new { type = "object", properties = new {
                    description = new { type = "string" },
                    status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } }
                }, required = new[] { "description", "status" } } }
            }, required = new[] { "todos" }},
            PermissionClass.UserInteraction, PermissionRisk.Low, false),
            PlanWriteTodosAsync);

        // === SHELL TOOL ===
        RegisterTool(CreateToolSpec(
            "shell.run", "Run Shell Command",
            "Execute a shell command in the project directory",
            ToolCategory.Shell,
            new { type = "object", properties = new {
                command = new { type = "string", description = "Command to execute" },
                args = new { type = "array", items = new { type = "string" }, description = "Command arguments" },
                workingDirectory = new { type = "string", description = "Working directory (relative to project root)" },
                timeout = new { type = "integer", description = "Timeout in seconds", @default = 60 },
                captureOutput = new { type = "boolean", description = "Capture stdout/stderr", @default = true }
            }, required = new[] { "command" }},
            PermissionClass.ExecuteShell, PermissionRisk.High, false),
            RunShellAsync);

        // === GENERATOR TOOLS (handled by service-side GeneratorService) ===
        RegisterTool(CreateToolSpec(
            "generator.quote", "Get Generation Quote",
            "Get a cost and time estimate for AI content generation",
            ToolCategory.Generator,
            new { type = "object", properties = new {
                modality = new { type = "string", description = "Generation type: image, mesh, material, sound, animation" },
                providerId = new { type = "string", description = "Provider ID (e.g., comfyui, kao, meshy)" },
                modelId = new { type = "string", description = "Model ID" },
                mode = new { type = "string", description = "Generation mode (e.g., generate, from_image)" },
                parameters = new { type = "object", description = "Generation parameters" }
            }, required = new[] { "modality" }},
            PermissionClass.GenerationSpend, PermissionRisk.Medium, false, 30000),
            GeneratorQuoteAsync);

        RegisterTool(CreateToolSpec(
            "generator.submit", "Submit Generation Job",
            "Submit an AI content generation job",
            ToolCategory.Generator,
            new { type = "object", properties = new {
                modality = new { type = "string", description = "Generation type: image, mesh, material, sound, animation" },
                providerId = new { type = "string", description = "Provider ID (e.g., comfyui, kao, meshy)" },
                modelId = new { type = "string", description = "Model ID" },
                mode = new { type = "string", description = "Generation mode" },
                targetAssetPath = new { type = "string", description = "Target asset path for result" },
                prompt = new { type = "string", description = "Text prompt for generation" },
                parameters = new { type = "object", description = "Generation parameters" },
                confirmGeneration = new { type = "boolean", description = "Set true only after the user has explicitly confirmed they want to spend on generation. Otherwise call generator.quote first." }
            }, required = new[] { "modality", "prompt" }},
            PermissionClass.GenerationSpend, PermissionRisk.Medium, false, 300000),
            GeneratorSubmitAsync);

        RegisterTool(CreateToolSpec(
            "generator.status", "Get Generation Status",
            "Get the status of a generation job",
            ToolCategory.Generator,
            new { type = "object", properties = new {
                jobId = new { type = "string", description = "Job ID to check" }
            }, required = new[] { "jobId" }},
            PermissionClass.ReadProject, PermissionRisk.Low, false),
            GeneratorStatusAsync);

        RegisterTool(CreateToolSpec(
            "generator.cancel", "Cancel Generation",
            "Cancel a running generation job",
            ToolCategory.Generator,
            new { type = "object", properties = new {
                jobId = new { type = "string", description = "Job ID to cancel" }
            }, required = new[] { "jobId" }},
            PermissionClass.GenerationSpend, PermissionRisk.Low, false),
            GeneratorCancelAsync);

        RegisterTool(CreateToolSpec(
            "generator.apply", "Apply Generation Result",
            "Apply a generation result to a Unity asset",
            ToolCategory.Generator,
            new { type = "object", properties = new {
                jobId = new { type = "string", description = "Job ID" },
                resultId = new { type = "string", description = "Result ID to apply" },
                targetPath = new { type = "string", description = "Target asset path" }
            }, required = new[] { "jobId" }},
            PermissionClass.WriteProject, PermissionRisk.Medium, false),
            GeneratorApplyAsync);

        RegisterLanguageTools();

        // === UNITY DELEGATED TOOLS (require Unity main thread) ===
        foreach (var unityTool in GetUnityDelegatedToolSpecs())
        {
            RegisterTool(unityTool, DelegateToUnityAsync);
        }
    }

    private void RegisterLanguageTools()
    {
        var path = new { type = "string", description = "Unity project-relative .cs path" };
        var line = new { type = "integer", minimum = 1, description = "One-based line number" };
        var column = new { type = "integer", minimum = 1, description = "One-based UTF-16 column" };
        void Register(string id, string name, string description, object schema) =>
            RegisterTool(CreateToolSpec(id, name, description, ToolCategory.ProjectRead, schema,
                PermissionClass.ReadProject, PermissionRisk.Low, false, 30_000), CSharpLanguageToolAsync);

        Register("csharp.status", "C# Language Server Status", "Report C# language-server discovery, connection, workspace, and capabilities.",
            new { type = "object", properties = new { } });
        Register("csharp.get_diagnostics", "Get C# Diagnostics", "Return Roslyn/csharp-ls syntax, semantic, and analyzer diagnostics for a Unity C# file.",
            new { type = "object", properties = new { path, waitMs = new { type = "integer", minimum = 50, maximum = 10000 } }, required = new[] { "path" } });
        Register("csharp.get_symbols", "Get C# Symbols", "Return declared C# types and members in a Unity script.",
            new { type = "object", properties = new { path }, required = new[] { "path" } });
        Register("csharp.hover", "C# Hover", "Return C# type and documentation information at a Unity script position.",
            new { type = "object", properties = new { path, line, column }, required = new[] { "path", "line", "column" } });
        Register("csharp.find_definition", "Find C# Definition", "Find the definition of the C# symbol at a Unity script position.",
            new { type = "object", properties = new { path, line, column }, required = new[] { "path", "line", "column" } });
        Register("csharp.find_references", "Find C# References", "Find Unity-project references to the C# symbol at a source position.",
            new { type = "object", properties = new { path, line, column, includeDeclaration = new { type = "boolean" } }, required = new[] { "path", "line", "column" } });
        Register("csharp.get_completions", "Get C# Completions", "Return context-aware C# completions at a Unity script position.",
            new { type = "object", properties = new { path, line, column }, required = new[] { "path", "line", "column" } });
        Register("csharp.get_signature_help", "Get C# Signature Help", "Return C# call signatures and active parameter information at a Unity script position.",
            new { type = "object", properties = new { path, line, column }, required = new[] { "path", "line", "column" } });
    }

    private static ToolSpec CreateToolSpec(
        string id, string displayName, string description,
        ToolCategory category, object inputSchema,
        PermissionClass permClass, PermissionRisk risk,
        bool requiresUnity, int timeoutMs = 30000)
    {
        return new ToolSpec(
            id, displayName, description, category,
            inputSchema, null,
            new PermissionRequirement(permClass, risk, description, null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(requiresUnity, timeoutMs, true, category == ToolCategory.ProjectRead),
            new ToolConstraints(PathScope.AssetsOrPackages));
    }

    #region Service-Side Tool Implementations

    private async Task<ToolResult> CSharpLanguageToolAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var arguments = call.Arguments is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(call.Arguments ?? new { });
        var value = await _language.InvokeAsync(call.ToolId, arguments, ct);
        return new ToolResult(call.Id, true, value, $"{call.ToolId} completed", null, null, null);
    }

    private async Task<ToolResult> ListProjectFilesAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        var basePath = args.GetValueOrDefault("path", "Assets");
        var pattern = args.GetValueOrDefault("pattern", "*");
        var maxDepth = int.Parse(args.GetValueOrDefault("maxDepth", "10"));
        var includeHidden = bool.Parse(args.GetValueOrDefault("includeHidden", "false"));

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, basePath));

        // Security: Ensure path is within project
        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return CreateErrorResult(call.Id, "Path is outside project root");
        }

        if (!Directory.Exists(fullPath))
        {
            return CreateErrorResult(call.Id, $"Directory not found: {basePath}");
        }

        var files = new List<object>();
        var searchOption = maxDepth > 1 ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, pattern, searchOption))
            {
                var relativePath = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');

                // Skip hidden files unless requested
                if (!includeHidden && Path.GetFileName(file).StartsWith('.'))
                    continue;

                // Skip .meta files and common excluded directories
                if (relativePath.EndsWith(".meta") ||
                    relativePath.Contains("/Library/") ||
                    relativePath.Contains("/Temp/") ||
                    relativePath.Contains("/obj/") ||
                    relativePath.Contains("/bin/"))
                    continue;

                // Check depth
                var depth = relativePath.Count(c => c == '/');
                if (depth > maxDepth) continue;

                var info = new FileInfo(file);
                files.Add(new {
                    path = relativePath,
                    size = info.Length,
                    modified = info.LastWriteTimeUtc,
                    extension = info.Extension
                });

                if (files.Count >= 1000) break; // Limit results
            }

            return new ToolResult(call.Id, true, new { files, count = files.Count, basePath },
                $"Found {files.Count} files in {basePath}", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Error listing files: {ex.Message}");
        }
    }

    private async Task<ToolResult> ReadFileAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("path", out var relativePath))
        {
            return CreateErrorResult(call.Id, "Missing required argument: path");
        }

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

        // Security: Ensure path is within project
        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return CreateErrorResult(call.Id, "Path is outside project root");
        }

        if (!File.Exists(fullPath))
        {
            return CreateErrorResult(call.Id, $"File not found: {relativePath}");
        }

        // Check if binary
        if (IsBinaryFile(fullPath))
        {
            return CreateErrorResult(call.Id, "Cannot read binary file");
        }

        // Check file size
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > _config.MaxIndexFileSizeBytes)
        {
            return CreateErrorResult(call.Id, $"File too large: {fileInfo.Length} bytes (max: {_config.MaxIndexFileSizeBytes})");
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(fullPath, ct);
            var startLine = int.Parse(args.GetValueOrDefault("startLine", "1")) - 1;
            var endLine = int.Parse(args.GetValueOrDefault("endLine", lines.Length.ToString()));
            var maxLines = int.Parse(args.GetValueOrDefault("maxLines", "500"));

            startLine = Math.Max(0, startLine);
            endLine = Math.Min(lines.Length, endLine);
            var lineCount = Math.Min(endLine - startLine, maxLines);

            var content = new StringBuilder();
            for (int i = startLine; i < startLine + lineCount && i < lines.Length; i++)
            {
                content.AppendLine($"{i + 1,6}│ {lines[i]}");
            }

            var truncated = lineCount < (endLine - startLine);

            return new ToolResult(call.Id, true, new {
                path = relativePath,
                content = content.ToString(),
                totalLines = lines.Length,
                startLine = startLine + 1,
                endLine = startLine + lineCount,
                truncated
            }, $"Read {lineCount} lines from {relativePath}", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Error reading file: {ex.Message}");
        }
    }

    private async Task<ToolResult> SearchTextAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("pattern", out var pattern))
        {
            return CreateErrorResult(call.Id, "Missing required argument: pattern");
        }

        var basePath = args.GetValueOrDefault("path", "Assets");
        var filePattern = args.GetValueOrDefault("filePattern", "*.cs");
        var caseSensitive = bool.Parse(args.GetValueOrDefault("caseSensitive", "false"));
        var maxResults = int.Parse(args.GetValueOrDefault("maxResults", "50"));
        var contextLines = int.Parse(args.GetValueOrDefault("contextLines", "2"));

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, basePath));

        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return CreateErrorResult(call.Id, "Path is outside project root");
        }

        if (!Directory.Exists(fullPath))
        {
            return CreateErrorResult(call.Id, $"Directory not found: {basePath}");
        }

        var regexOptions = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        Regex regex;
        try
        {
            regex = new Regex(pattern, regexOptions | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Invalid regex pattern: {ex.Message}");
        }

        var results = new List<object>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, filePattern, SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                if (results.Count >= maxResults) break;

                var relativePath = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                if (relativePath.Contains("/Library/") || relativePath.Contains("/Temp/"))
                    continue;

                if (IsBinaryFile(file)) continue;

                var lines = await File.ReadAllLinesAsync(file, ct);
                for (int i = 0; i < lines.Length && results.Count < maxResults; i++)
                {
                    var match = regex.Match(lines[i]);
                    if (match.Success)
                    {
                        var contextBefore = new List<string>();
                        var contextAfter = new List<string>();

                        for (int j = Math.Max(0, i - contextLines); j < i; j++)
                            contextBefore.Add($"{j + 1}: {lines[j]}");
                        for (int j = i + 1; j <= Math.Min(lines.Length - 1, i + contextLines); j++)
                            contextAfter.Add($"{j + 1}: {lines[j]}");

                        results.Add(new {
                            path = relativePath,
                            line = i + 1,
                            column = match.Index + 1,
                            content = lines[i],
                            match = match.Value,
                            contextBefore,
                            contextAfter
                        });
                    }
                }
            }

            return new ToolResult(call.Id, true, new { results, count = results.Count, pattern, truncated = results.Count >= maxResults },
                $"Found {results.Count} matches for '{pattern}'", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Search error: {ex.Message}");
        }
    }

    private async Task<ToolResult> GetProjectOverviewAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        var maxDepth = int.Parse(args.GetValueOrDefault("maxDepth", "3"));
        var includeStats = bool.Parse(args.GetValueOrDefault("includeStats", "true"));

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var assetsPath = Path.Combine(projectRoot, "Assets");

        var folders = new List<object>();
        var stats = new Dictionary<string, int>();

        if (Directory.Exists(assetsPath))
        {
            await ScanFolderAsync(assetsPath, projectRoot, 0, maxDepth, folders, stats, ct);
        }

        // Read ProjectSettings
        var projectName = Path.GetFileName(projectRoot);
        var unityVersion = "Unknown";
        var projectSettingsPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
        if (File.Exists(projectSettingsPath))
        {
            var versionContent = await File.ReadAllTextAsync(projectSettingsPath, ct);
            var match = Regex.Match(versionContent, @"m_EditorVersion:\s*(.+)");
            if (match.Success) unityVersion = match.Groups[1].Value.Trim();
        }

        return new ToolResult(call.Id, true, new {
            projectName,
            unityVersion,
            projectRoot,
            folders,
            stats = includeStats ? stats : null
        }, $"Project overview for {projectName}", null, null, null);
    }

    private async Task ScanFolderAsync(string path, string projectRoot, int depth, int maxDepth,
        List<object> folders, Dictionary<string, int> stats, CancellationToken ct)
    {
        if (depth > maxDepth || ct.IsCancellationRequested) return;

        var relativePath = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
        var files = Directory.GetFiles(path);
        var subdirs = Directory.GetDirectories(path);

        // Count file types
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".meta") continue;
            stats[ext] = stats.GetValueOrDefault(ext, 0) + 1;
        }

        folders.Add(new {
            path = relativePath,
            depth,
            fileCount = files.Count(f => !f.EndsWith(".meta")),
            subdirCount = subdirs.Length
        });

        foreach (var subdir in subdirs)
        {
            var name = Path.GetFileName(subdir);
            if (name.StartsWith('.')) continue;
            await ScanFolderAsync(subdir, projectRoot, depth + 1, maxDepth, folders, stats, ct);
        }
    }

    private async Task<ToolResult> ProposePatchAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("path", out var relativePath) ||
            !args.TryGetValue("oldContent", out var oldContent) ||
            !args.TryGetValue("newContent", out var newContent))
        {
            return CreateErrorResult(call.Id, "Missing required arguments: path, oldContent, newContent");
        }

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return CreateErrorResult(call.Id, "Path is outside project root");
        }

        if (!File.Exists(fullPath))
        {
            return CreateErrorResult(call.Id, $"File not found: {relativePath}");
        }

        var currentContent = await File.ReadAllTextAsync(fullPath, ct);

        if (!currentContent.Contains(oldContent))
        {
            return CreateErrorResult(call.Id, "Old content not found in file - cannot create patch");
        }

        // Create unified diff
        var patchedContent = currentContent.Replace(oldContent, newContent);
        var diff = CreateUnifiedDiff(relativePath, currentContent, patchedContent);
        var patchId = $"patch_{Guid.NewGuid():N}"[..16];

        // Store patch for later application (in production, would persist this)
        // For now, include the patch details in the result

        return new ToolResult(call.Id, true, new {
            patchId,
            path = relativePath,
            diff,
            oldContent,
            newContent,
            description = args.GetValueOrDefault("description", "Code change")
        }, $"Patch proposed for {relativePath}", null, null, null);
    }

    private async Task<ToolResult> ApplyPatchAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("patchId", out var patchId))
        {
            return CreateErrorResult(call.Id, "Missing required argument: patchId");
        }

        // In a full implementation, we would retrieve the stored patch
        // For now, this requires the patch to include path, oldContent, newContent
        if (!args.TryGetValue("path", out var relativePath) ||
            !args.TryGetValue("oldContent", out var oldContent) ||
            !args.TryGetValue("newContent", out var newContent))
        {
            return CreateErrorResult(call.Id, "Patch data not found - include path, oldContent, newContent");
        }

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return CreateErrorResult(call.Id, "Path is outside project root");
        }

        var createBackup = bool.Parse(args.GetValueOrDefault("createBackup", "true"));

        try
        {
            var currentContent = await File.ReadAllTextAsync(fullPath, ct);

            if (!currentContent.Contains(oldContent))
            {
                return CreateErrorResult(call.Id, "File has changed - patch no longer applies");
            }

            // Create backup
            if (createBackup)
            {
                var backupPath = fullPath + ".bak";
                await File.WriteAllTextAsync(backupPath, currentContent, ct);
            }

            // Apply patch
            var newFileContent = currentContent.Replace(oldContent, newContent);
            await File.WriteAllTextAsync(fullPath, newFileContent, ct);

            return new ToolResult(call.Id, true, new {
                patchId,
                path = relativePath,
                applied = true,
                backupCreated = createBackup
            }, $"Patch {patchId} applied to {relativePath}", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Failed to apply patch: {ex.Message}");
        }
    }

    private async Task<ToolResult> CreateFileAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("path", out var relativePath) ||
            !args.TryGetValue("content", out var content))
        {
            return CreateErrorResult(call.Id, "Missing required arguments: path, content");
        }

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return CreateErrorResult(call.Id, "Path is outside project root");
        }

        var overwrite = bool.Parse(args.GetValueOrDefault("overwrite", "false"));

        if (File.Exists(fullPath) && !overwrite)
        {
            return CreateErrorResult(call.Id, "File already exists and overwrite=false");
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content, ct);

            return new ToolResult(call.Id, true, new {
                path = relativePath,
                created = true,
                size = content.Length
            }, $"Created file: {relativePath}", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Failed to create file: {ex.Message}");
        }
    }

    private async Task<ToolResult> DeleteFileAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("path", out var relativePath))
        {
            return CreateErrorResult(call.Id, "Missing required argument: path");
        }

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return CreateErrorResult(call.Id, "Path is outside project root");
        }

        if (!File.Exists(fullPath))
        {
            return CreateErrorResult(call.Id, $"File not found: {relativePath}");
        }

        try
        {
            File.Delete(fullPath);

            // Also delete .meta file if it exists
            var metaPath = fullPath + ".meta";
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }

            return new ToolResult(call.Id, true, new {
                path = relativePath,
                deleted = true
            }, $"Deleted file: {relativePath}", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Failed to delete file: {ex.Message}");
        }
    }

    // === PLAN TOOL IMPLEMENTATIONS ===

    /// <summary>
    /// Resolves a plan path and enforces containment under Assets/Plans/ with a .md
    /// extension. Accepts paths given relative to the project root or to the plans folder.
    /// </summary>
    private bool TryResolvePlanPath(string requestedPath, out string fullPath, out string relativePath, out string? error)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            error = "Plan path is required";
            return false;
        }

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var plansRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Plans"));

        // Allow callers to pass a path already relative to Assets/Plans (e.g. "my-plan.md").
        var combinedFromProject = Path.GetFullPath(Path.Combine(projectRoot, requestedPath));
        var combined = combinedFromProject.StartsWith(plansRoot, StringComparison.OrdinalIgnoreCase)
            ? combinedFromProject
            : Path.GetFullPath(Path.Combine(plansRoot, requestedPath));

        var plansPrefix = plansRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(plansPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Plan files must be contained under {PlansRelativeRoot}/";
            return false;
        }

        if (!combined.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            error = "Plan files must have a .md extension";
            return false;
        }

        fullPath = combined;
        relativePath = Path.GetRelativePath(projectRoot, combined).Replace('\\', '/');
        return true;
    }

    private async Task<ToolResult> PlanWriteAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("filePath", out var filePath) || !args.TryGetValue("content", out var content))
        {
            return CreateErrorResult(call.Id, "Missing required arguments: filePath, content");
        }

        if (!TryResolvePlanPath(filePath, out var fullPath, out var relativePath, out var error))
        {
            return CreateErrorResult(call.Id, error!);
        }

        // Creation-only: revisions must go through plan.edit.
        if (File.Exists(fullPath))
        {
            return CreateErrorResult(call.Id,
                $"Plan already exists: {relativePath}. Use plan.edit to revise it.");
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Normalize line endings for stable diff-based edits later.
            var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
            await File.WriteAllTextAsync(fullPath, normalized, ct);

            return new ToolResult(call.Id, true, new {
                path = relativePath,
                created = true,
                size = normalized.Length
            }, $"Created plan: {relativePath}", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Failed to write plan: {ex.Message}");
        }
    }

    private async Task<ToolResult> PlanEditAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("filePath", out var filePath) ||
            !args.TryGetValue("oldString", out var oldString) ||
            !args.TryGetValue("newString", out var newString))
        {
            return CreateErrorResult(call.Id, "Missing required arguments: filePath, oldString, newString");
        }

        if (string.IsNullOrEmpty(oldString))
        {
            return CreateErrorResult(call.Id, "oldString must be non-empty");
        }

        if (!TryResolvePlanPath(filePath, out var fullPath, out var relativePath, out var error))
        {
            return CreateErrorResult(call.Id, error!);
        }

        if (!File.Exists(fullPath))
        {
            return CreateErrorResult(call.Id, $"Plan not found: {relativePath}. Use plan.write to create it.");
        }

        var expectedOccurrences = 1;
        if (args.TryGetValue("expectedOccurrences", out var expectedRaw) &&
            int.TryParse(expectedRaw, out var parsedExpected))
        {
            expectedOccurrences = parsedExpected;
        }

        try
        {
            var original = await File.ReadAllTextAsync(fullPath, ct);
            var normalized = original.Replace("\r\n", "\n").Replace("\r", "\n");
            var search = oldString.Replace("\r\n", "\n").Replace("\r", "\n");
            var replacement = newString.Replace("\r\n", "\n").Replace("\r", "\n");

            var occurrences = CountOccurrences(normalized, search);
            if (occurrences == 0)
            {
                return CreateErrorResult(call.Id, "oldString was not found in the plan");
            }
            if (occurrences != expectedOccurrences)
            {
                return CreateErrorResult(call.Id,
                    $"Expected {expectedOccurrences} occurrence(s) of oldString but found {occurrences}");
            }

            var updated = normalized.Replace(search, replacement);
            await File.WriteAllTextAsync(fullPath, updated, ct);

            return new ToolResult(call.Id, true, new {
                path = relativePath,
                replacements = occurrences,
                size = updated.Length
            }, $"Edited plan: {relativePath} ({occurrences} replacement(s))", null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Failed to edit plan: {ex.Message}");
        }
    }

    private async Task<ToolResult> PlanExitModeAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("planPath", out var planPath))
        {
            return CreateErrorResult(call.Id, "Missing required argument: planPath");
        }

        if (!TryResolvePlanPath(planPath, out var fullPath, out var relativePath, out var error))
        {
            return CreateErrorResult(call.Id, error!);
        }

        if (!File.Exists(fullPath))
        {
            return CreateErrorResult(call.Id, $"Plan not found: {relativePath}");
        }

        var title = args.TryGetValue("title", out var t) ? t : null;

        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, ct);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Failed to read plan: {ex.Message}");
        }

        // Persist pending review state for the session so the host/UI can resolve it.
        _pendingPlanReviews[session.Id] = new PendingPlanReview(relativePath, title, DateTimeOffset.UtcNow);

        const int previewLimit = 4000;
        var preview = content.Length > previewLimit ? content[..previewLimit] + "\n…[truncated]" : content;

        return new ToolResult(call.Id, true, new {
            planPath = relativePath,
            title,
            status = "pending_review",
            preview
        }, $"Plan submitted for review: {relativePath}", null, null, null);
    }

    private Task<ToolResult> PlanWriteTodosAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        if (call.Arguments is not JsonElement root || !root.TryGetProperty("todos", out var todosEl) ||
            todosEl.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult(CreateErrorResult(call.Id, "todos must be a non-empty array"));
        }

        var todos = new List<object>();
        var inProgressCount = 0;
        var index = 0;
        foreach (var item in todosEl.EnumerateArray())
        {
            var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;
            var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (string.IsNullOrWhiteSpace(description))
            {
                return Task.FromResult(CreateErrorResult(call.Id, $"todos[{index}].description is required"));
            }
            if (status is not ("pending" or "in_progress" or "completed"))
            {
                return Task.FromResult(CreateErrorResult(call.Id,
                    $"todos[{index}].status must be pending, in_progress, or completed"));
            }
            if (status == "in_progress") inProgressCount++;

            todos.Add(new { description, status });
            index++;
        }

        if (todos.Count == 0)
        {
            return Task.FromResult(CreateErrorResult(call.Id, "todos must contain at least one item"));
        }
        if (inProgressCount > 1)
        {
            return Task.FromResult(CreateErrorResult(call.Id,
                $"At most one todo may be in_progress, found {inProgressCount}"));
        }

        var planPath = call.Arguments is JsonElement je && je.TryGetProperty("planPath", out var pp)
            ? pp.GetString()
            : null;

        return Task.FromResult(new ToolResult(call.Id, true, new {
            planPath,
            count = todos.Count,
            inProgress = inProgressCount,
            todos
        }, $"Updated {todos.Count} todo(s)", null, null, null));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private async Task<ToolResult> RunShellAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        if (!args.TryGetValue("command", out var command))
        {
            return CreateErrorResult(call.Id, "Missing required argument: command");
        }

        var projectRoot = Path.GetFullPath(_config.ProjectRoot ?? Directory.GetCurrentDirectory());
        var workingDir = projectRoot;

        if (args.TryGetValue("workingDirectory", out var relativeWorkDir))
        {
            workingDir = Path.GetFullPath(Path.Combine(projectRoot, relativeWorkDir));
            if (!workingDir.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return CreateErrorResult(call.Id, "Working directory is outside project root");
            }
        }

        var timeout = int.Parse(args.GetValueOrDefault("timeout", "60")) * 1000;
        var captureOutput = bool.Parse(args.GetValueOrDefault("captureOutput", "true"));

        // Parse command and args
        var cmdArgs = args.TryGetValue("args", out var argsJson)
            ? JsonSerializer.Deserialize<string[]>(argsJson) ?? Array.Empty<string>()
            : Array.Empty<string>();

        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows
                ? $"/c {command} {string.Join(" ", cmdArgs.Select(EscapeArg))}"
                : $"-c \"{command} {string.Join(" ", cmdArgs.Select(EscapeArg))}\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Set safe environment
        startInfo.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "USER", "USERPROFILE", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[key] = value;
            }
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return CreateErrorResult(call.Id, "Failed to start process");
            }

            var stdout = "";
            var stderr = "";

            if (captureOutput)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                    stdout = await stdoutTask;
                    stderr = await stderrTask;
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    return CreateErrorResult(call.Id, "Command timed out");
                }
            }
            else
            {
                await process.WaitForExitAsync(ct);
            }

            // Truncate output if too large
            const int maxOutput = 50000;
            var truncated = false;
            if (stdout.Length > maxOutput)
            {
                stdout = stdout[..maxOutput] + "\n... [output truncated]";
                truncated = true;
            }
            if (stderr.Length > maxOutput)
            {
                stderr = stderr[..maxOutput] + "\n... [output truncated]";
                truncated = true;
            }

            return new ToolResult(call.Id, process.ExitCode == 0, new {
                command,
                exitCode = process.ExitCode,
                stdout,
                stderr,
                truncated
            }, process.ExitCode == 0 ? "Command completed" : $"Command failed with exit code {process.ExitCode}",
            null, null, null);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(call.Id, $"Failed to execute command: {ex.Message}");
        }
    }

    #region Generator Tool Handlers

    private async Task<ToolResult> GeneratorQuoteAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        var modality = ParseModality(args.GetValueOrDefault("modality", "image"));
        var providerId = args.GetValueOrDefault("providerId", "default");
        var modelId = args.GetValueOrDefault("modelId", "");
        var mode = args.GetValueOrDefault("mode", "generate");

        Dictionary<string, object?>? parameters = null;
        if (call.Arguments is JsonElement je && je.TryGetProperty("parameters", out var paramsEl))
        {
            parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsEl.GetRawText());
        }

        var request = new GeneratorQuoteRequest(modality, providerId, modelId, mode, parameters);
        var quote = await _generators.GetQuoteAsync(request, ct);

        // Record that this session has surfaced a cost estimate, establishing
        // explicit generation intent for a subsequent generator.submit.
        _generationQuoted[session.Id] = DateTimeOffset.UtcNow;

        return new ToolResult(call.Id, true, new {
            success = quote.Success,
            estimatedCost = quote.EstimatedCost,
            currency = quote.Currency,
            errorMessage = quote.ErrorMessage
        }, "Quote retrieved", null, null, null);
    }

    private async Task<ToolResult> GeneratorSubmitAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        var modality = ParseModality(args.TryGetValue("modality", out var m) ? m : "image");
        var providerId = args.TryGetValue("providerId", out var p) ? p : "default";
        var modelId = args.TryGetValue("modelId", out var mid) ? mid : "";
        var mode = args.TryGetValue("mode", out var md) ? md : "generate";
        var targetAssetPath = args.TryGetValue("targetAssetPath", out var tap) ? tap : null;
        var prompt = args.TryGetValue("prompt", out var pr) ? pr : "";

        // Explicit-intent gate: paid/asset-writing generation requires that the
        // session has surfaced a quote first, or that the caller explicitly
        // confirmed. This is enforced in policy, not via prompt instructions, so a
        // broad request cannot trigger generation as an unsolicited first action.
        var confirmed = args.TryGetValue("confirmGeneration", out var cg) &&
            bool.TryParse(cg, out var cgParsed) && cgParsed;
        if (!confirmed && !_generationQuoted.ContainsKey(session.Id))
        {
            return CreateErrorResult(call.Id,
                "Generation requires explicit intent: call generator.quote first to surface cost, " +
                "or pass confirmGeneration=true once the user has confirmed they want to generate.");
        }

        Dictionary<string, object?>? parameters = null;
        if (call.Arguments is JsonElement je && je.TryGetProperty("parameters", out var paramsEl))
        {
            parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsEl.GetRawText());
        }

        // Add prompt to parameters if not already there
        parameters ??= new Dictionary<string, object?>();
        if (!parameters.ContainsKey("prompt") && !string.IsNullOrEmpty(prompt))
        {
            parameters["prompt"] = prompt;
        }

        var request = new GeneratorSubmitRequest(
            modality, providerId, modelId, mode,
            null, targetAssetPath, parameters, null);

        var job = await _generators.SubmitJobAsync(session.WorkspaceId, request, ct);

        return new ToolResult(call.Id, true, new {
            jobId = job.Id,
            status = job.Status.ToString().ToLowerInvariant(),
            modality = job.Modality.ToString().ToLowerInvariant(),
            providerId = job.ProviderId
        }, $"Generation job submitted: {job.Id}", null, null, null);
    }

    private async Task<ToolResult> GeneratorStatusAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        var jobId = args.GetValueOrDefault("jobId", "");

        if (string.IsNullOrEmpty(jobId))
        {
            return CreateErrorResult(call.Id, "jobId is required");
        }

        var job = await _generators.GetJobAsync(jobId, ct);
        if (job == null)
        {
            return CreateErrorResult(call.Id, $"Job not found: {jobId}");
        }

        return new ToolResult(call.Id, true, new {
            jobId = job.Id,
            status = job.Status.ToString().ToLowerInvariant(),
            modality = job.Modality.ToString().ToLowerInvariant(),
            providerId = job.ProviderId,
            progress = job.Results?.Count > 0 ? 100 : 0,
            resultCount = job.Results?.Count ?? 0,
            error = job.Error?.Message
        }, $"Job status: {job.Status}", null, null, null);
    }

    private async Task<ToolResult> GeneratorCancelAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        var jobId = args.GetValueOrDefault("jobId", "");

        if (string.IsNullOrEmpty(jobId))
        {
            return CreateErrorResult(call.Id, "jobId is required");
        }

        var cancelled = await _generators.CancelJobAsync(jobId, ct);

        return new ToolResult(call.Id, true, new {
            jobId,
            cancelled
        }, cancelled ? "Job cancelled" : "Could not cancel job", null, null, null);
    }

    private async Task<ToolResult> GeneratorApplyAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        var args = ParseArguments(call.Arguments);
        var jobId = args.TryGetValue("jobId", out var jid) ? jid : "";
        var resultId = args.TryGetValue("resultId", out var rid) ? rid : null;
        var targetPath = args.TryGetValue("targetPath", out var tp) ? tp : null;

        if (string.IsNullOrEmpty(jobId))
        {
            return CreateErrorResult(call.Id, "jobId is required");
        }

        var request = new GeneratorApplyRequest(jobId, resultId, targetPath);
        var assetPath = await _generators.ApplyResultAsync(session.WorkspaceId, request, ct);

        if (string.IsNullOrEmpty(assetPath))
        {
            return CreateErrorResult(call.Id, "Failed to apply result");
        }

        return new ToolResult(call.Id, true, new {
            success = true,
            assetPath
        }, $"Applied to: {assetPath}", null, null, null);
    }

    private static GeneratorModality ParseModality(string modality)
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

    private async Task<ToolResult> DelegateToUnityAsync(ToolCall call, AgentSession session, CancellationToken ct)
    {
        // Create a completion source for the Unity response
        var tcs = new TaskCompletionSource<ToolResult>();
        _pendingUnityCalls[call.Id] = tcs;

        // Fire event for Unity to handle (remember the request so the call can be re-delivered
        // if the editor reloads before it answers).
        var req = new UnityToolRequest(call.Id, call.ToolId, call.Arguments, session.WorkspaceId);
        _pendingUnityCallInfo[call.Id] = new PendingUnityCall(req);
        UnityToolRequested?.Invoke(this, req);

        // Wait for Unity response with timeout. Store the cts so DeferUnityToolCall can push
        // the deadline out when the editor signals it's triggering a domain reload (the editor
        // tears down + reconnects, then completes the call from the new domain).
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        _pendingUnityCallCts[call.Id] = cts;

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _pendingUnityCalls.TryRemove(call.Id, out _);
            return CreateErrorResult(call.Id, "Unity tool call timed out - Unity may be unresponsive");
        }
        finally
        {
            if (_pendingUnityCallCts.TryRemove(call.Id, out var done)) done.Dispose();
            _pendingUnityCallInfo.TryRemove(call.Id, out _);
        }
    }

    /// <summary>
    /// Extends a pending Unity tool call's deadline because the editor is about to domain-reload
    /// and will complete the call afterwards (e.g. script.compile_check with a recompile).
    /// </summary>
    public void DeferUnityToolCall(string callId)
    {
        if (_pendingUnityCallInfo.TryGetValue(callId, out var info)) info.Deferred = true;
        if (_pendingUnityCallCts.TryGetValue(callId, out var cts))
        {
            try { cts.CancelAfter(TimeSpan.FromMinutes(5)); } catch { /* may have completed/disposed */ }
            _logger.LogInformation("Unity tool call {CallId} deferred across a domain reload", callId);
        }
    }

    /// <summary>
    /// The editor reconnected after a reload. Resume the in-flight calls it lost: re-deliver
    /// idempotent reads (the fresh editor re-runs them and returns the real result), and fail
    /// mutations with a clear error (re-executing could double-apply). Deferred calls
    /// (compile-check) self-complete editor-side and are left alone.
    /// </summary>
    public void ResumePendingUnityCalls()
    {
        foreach (var kv in _pendingUnityCallInfo)
        {
            var callId = kv.Key;
            var info = kv.Value;
            if (info.Deferred) continue;
            if (!_pendingUnityCalls.ContainsKey(callId)) continue; // already completed

            if (IsIdempotentTool(info.Request.ToolId))
            {
                _logger.LogInformation("Re-delivering Unity read {Tool} ({CallId}) after editor reconnect", info.Request.ToolId, callId);
                UnityToolRequested?.Invoke(this, info.Request);
            }
            else
            {
                _logger.LogWarning("Unity mutation {Tool} ({CallId}) was interrupted by a reload; failing it (re-check state)", info.Request.ToolId, callId);
                CompleteUnityToolCall(callId, CreateErrorResult(callId,
                    $"'{info.Request.ToolId}' was interrupted by a Unity domain reload; its effect is uncertain. " +
                    "Re-check the editor state before retrying."));
            }
        }
    }

    // A tool is safe to re-run after a reload only if it has no side effects (a read).
    private bool IsIdempotentTool(string toolId)
        => _tools.TryGetValue(toolId, out var rt) && rt.Spec.Permission?.Class == PermissionClass.ReadProject;

    /// <summary>
    /// The editor disconnected (most likely a domain reload). Hold ALL in-flight Unity calls open
    /// instead of failing them at the normal 60s timeout — the editor reconnects and either
    /// completes a deferred call (compile-check) or the call is re-issued. This is the safe
    /// "block while reloading" half of the reload barrier (no re-execution).
    /// </summary>
    public void HoldPendingUnityCalls()
    {
        var count = 0;
        foreach (var kv in _pendingUnityCallCts)
        {
            try { kv.Value.CancelAfter(TimeSpan.FromMinutes(5)); count++; } catch { /* completed/disposed */ }
        }
        if (count > 0)
            _logger.LogInformation("Editor disconnected; holding {Count} in-flight Unity call(s) across the reload", count);
    }

    #endregion

    #region Helper Methods

    private static Dictionary<string, string> ParseArguments(object? args)
    {
        if (args == null) return new Dictionary<string, string>();

        var json = args is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(args);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        return dict?.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ValueKind == JsonValueKind.String
                ? kvp.Value.GetString() ?? ""
                : kvp.Value.GetRawText()
        ) ?? new Dictionary<string, string>();
    }

    private static ToolResult CreateErrorResult(string callId, string message)
    {
        return new ToolResult(callId, false, null, null,
            new NormalizedError(ErrorCodes.ToolFailed, message, null, false, null, null),
            null, null);
    }

    private sealed class UnavailableCSharpLanguageService : ICSharpLanguageService
    {
        public Task<object> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The C# language service is not registered in this host.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static bool IsBinaryFile(string path)
    {
        var binaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd",
            ".mp3", ".wav", ".ogg", ".aif", ".aiff",
            ".mp4", ".mov", ".avi", ".wmv",
            ".fbx", ".obj", ".blend", ".max", ".mb",
            ".dll", ".exe", ".so", ".dylib",
            ".zip", ".tar", ".gz", ".rar",
            ".asset", ".prefab", ".unity", ".mat", ".controller", ".anim"
        };

        return binaryExtensions.Contains(Path.GetExtension(path));
    }

    private static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('"'))
        {
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
        return arg;
    }

    private static string CreateUnifiedDiff(string path, string oldContent, string newContent)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{path}");
        sb.AppendLine($"+++ b/{path}");

        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        // Simple diff - in production would use a proper diff algorithm
        sb.AppendLine($"@@ -1,{oldLines.Length} +1,{newLines.Length} @@");

        foreach (var line in oldLines)
            sb.AppendLine($"-{line.TrimEnd('\r')}");
        foreach (var line in newLines)
            sb.AppendLine($"+{line.TrimEnd('\r')}");

        return sb.ToString();
    }

    private static IEnumerable<ToolSpec> GetUnityDelegatedToolSpecs()
    {
        // Asset tools
        yield return new ToolSpec(
            "asset.search", "Search Assets", "Search for assets by name, type, or label",
            ToolCategory.AssetRead,
            new { type = "object", properties = new {
                query = new { type = "string", description = "Search query" },
                type = new { type = "string", description = "Asset type filter" },
                folder = new { type = "string", description = "Folder to search in" },
                maxResults = new { type = "integer", @default = 50 }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Search assets", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOrPackages));

        yield return new ToolSpec(
            "scene.get_hierarchy", "Get Scene Hierarchy", "Get the hierarchy of objects in the current scene",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                rootPath = new { type = "string", description = "Root object path (optional)" },
                maxDepth = new { type = "integer", @default = 5 }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read scene hierarchy", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.find_objects", "Find Objects",
            "Find GameObjects in the open scene by name, tag, layer, or component type - or all objects whose components reference a given asset/object (e.g. which objects use a mesh or material). Each result includes a globalId for linking.",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                name = new { type = "string", description = "Name substring to match" },
                tag = new { type = "string", description = "Tag filter" },
                componentType = new { type = "string", description = "Only objects that have this component type (e.g. MeshRenderer)" },
                references_global_id = new { type = "string", description = "Only objects whose components reference the asset/object with this globalId (use to answer 'what uses this mesh/material/etc.')" },
                maxResults = new { type = "integer", @default = 100 }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Find scene objects", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.read_object", "Read Object",
            "Read a GameObject's components and properties. Identify it by global_id (preferred), instanceId, or objectPath.",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                global_id = new { type = "string", description = "Stable object id from another tool or a context attachment (preferred)" },
                objectPath = new { type = "string", description = "Hierarchy path" },
                instanceId = new { type = "integer", description = "Instance id" },
                includeChildren = new { type = "boolean", @default = false },
                maxDepth = new { type = "integer", @default = 2 }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read object", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.create_gameobject", "Create GameObject",
            "Create a new GameObject in the open scene (optionally a primitive like Cube/Sphere/Capsule/Cylinder/Plane/Quad). Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                name = new { type = "string", description = "Name of the new object" },
                primitive = new { type = "string", description = "Optional primitive: Cube, Sphere, Capsule, Cylinder, Plane, Quad" },
                parent_id = new { type = "integer", description = "Optional parent instanceId" },
                position = new { type = "array", description = "[x,y,z] world position" },
                rotation = new { type = "array", description = "[x,y,z] euler rotation" },
                scale = new { type = "array", description = "[x,y,z] scale" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Create a GameObject", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.modify_gameobject", "Modify GameObject",
            "Modify an existing GameObject's transform/name/active/tag/layer, or add/remove components. Identify it by instance_id or path. Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instance_id = new { type = "integer", description = "Target instanceId (from a read tool)" },
                path = new { type = "string", description = "Or hierarchy path" },
                name = new { type = "string" },
                active = new { type = "boolean" },
                tag = new { type = "string" },
                layer = new { type = "integer" },
                position = new { type = "array", description = "[x,y,z] world position" },
                local_position = new { type = "array" },
                rotation = new { type = "array", description = "[x,y,z] euler" },
                scale = new { type = "array" },
                add_components = new { type = "array", description = "Component type names to add" },
                remove_components = new { type = "array", description = "Component type names to remove" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Modify a GameObject", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.delete_gameobject", "Delete GameObject",
            "Delete a GameObject from the open scene. Identify it by instanceId or objectPath. Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" }
            }},
            null,
            new PermissionRequirement(PermissionClass.DeleteProject, PermissionRisk.High, "Delete a GameObject", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.add_component", "Add Component",
            "Add a component to a GameObject. Identify the object by instanceId or objectPath; componentType is the type name (e.g. Rigidbody). Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                componentType = new { type = "string", description = "Component type name, e.g. Rigidbody, BoxCollider" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Add a component", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.set_component_property", "Set Component Property",
            "Set a serialized property on a component. Identify the object by instanceId/objectPath, the component by componentType, the field by propertyPath, plus the value. Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                componentType = new { type = "string" },
                propertyPath = new { type = "string", description = "Serialized property path, e.g. m_Mass" },
                value = new { description = "The value to set" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Set a component property", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.get_visible_objects", "Get Visible Objects",
            "List the renderers currently visible to the active Scene View camera (what's actually on screen).",
            ToolCategory.SceneRead,
            new { type = "object", properties = new { } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get visible objects", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.remove_component", "Remove Component",
            "Remove a component from a GameObject (by componentType, or componentIndex among that type). Identify the object by instanceId or objectPath. Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                componentType = new { type = "string", description = "Component type name to remove" },
                componentIndex = new { type = "integer", description = "Index among components of that type (optional)" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Remove a component", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.duplicate_object", "Duplicate GameObject",
            "Duplicate a GameObject (optionally count copies with a per-copy position offset). Identify it by instanceId or objectPath. Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                count = new { type = "integer", description = "Number of duplicates (default 1)" },
                offset = new { type = "array", description = "[x,y,z] position offset applied per copy" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Duplicate a GameObject", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "scene.reparent_object", "Reparent GameObject",
            "Move a GameObject under a new parent (or to the scene root). Identify the object by instanceId/objectPath and the parent by newParentId/newParentPath. Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                newParentId = new { type = "integer", description = "New parent instanceId (omit/0 for scene root)" },
                newParentPath = new { type = "string" },
                worldPositionStays = new { type = "boolean", description = "Keep world transform (default true)" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Reparent a GameObject", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "prefab.create", "Create Prefab",
            "Save a scene GameObject as a prefab asset at prefabPath. Identify the object by instanceId or objectPath.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                prefabPath = new { type = "string", description = "Asset path for the new prefab, e.g. Assets/Prefabs/Foo.prefab" }
            }},
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Create a prefab", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "prefab.instantiate", "Instantiate Prefab",
            "Instantiate a prefab into the open scene. Identify the prefab by prefabPath or prefabGuid; optional parent by parentId/parentPath. Undoable.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                prefabPath = new { type = "string" },
                prefabGuid = new { type = "string" },
                parentId = new { type = "integer" },
                parentPath = new { type = "string" }
            }},
            null,
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Instantiate a prefab", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "prefab.override", "Apply/Revert Prefab Override",
            "Apply or revert prefab instance overrides. action is 'apply' (push to the prefab asset) or 'revert'. Identify the instance by instanceId or objectPath.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                action = new { type = "string", description = "'apply' or 'revert'" }
            }},
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Apply/revert prefab overrides", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "asset.create", "Create Asset",
            "Create an asset. type is 'material', 'script', 'shader', or 'scriptableObject'; path is the destination under Assets/ (e.g. Assets/Materials/Foo.mat). Provide content for script/shader, shader name for a material, script_type for a ScriptableObject.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                type = new { type = "string", description = "material | script | shader | scriptableObject" },
                path = new { type = "string", description = "Destination asset path under Assets/" },
                content = new { type = "string", description = "Source text for a script/shader" },
                shader = new { type = "string", description = "Shader name for a material (default Standard)" },
                script_type = new { type = "string", description = "Type name for a ScriptableObject" }
            }},
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Create an asset", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "asset.delete", "Delete Asset",
            "Delete an asset at the given project path (and its .meta).",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                path = new { type = "string", description = "Asset path under Assets/" }
            }},
            null,
            new PermissionRequirement(PermissionClass.DeleteProject, PermissionRisk.High, "Delete an asset", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "asset.import", "Import Asset",
            "Import an external file into the project. source_path is the file on disk; dest_path is the destination under Assets/.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                source_path = new { type = "string", description = "Source file path on disk" },
                dest_path = new { type = "string", description = "Destination under Assets/" }
            }},
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Import an asset", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "asset.read", "Read Asset",
            "Read an asset's metadata (and optionally its serialized data). Identify it by path or guid.",
            ToolCategory.AssetRead,
            new { type = "object", properties = new {
                path = new { type = "string" },
                guid = new { type = "string" },
                includeSerializedData = new { type = "boolean", description = "Include serialized fields (default false)" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read an asset", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "asset.get_dependencies", "Get Asset Dependencies",
            "List what an asset depends on (or what depends on it). Identify it by path or guid.",
            ToolCategory.AssetRead,
            new { type = "object", properties = new {
                path = new { type = "string" },
                guid = new { type = "string" },
                direction = new { type = "string", description = "'dependencies' (default) or 'dependents'" },
                maxDepth = new { type = "integer" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get asset dependencies", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "script.compile_check", "Check Compilation",
            "Report current C# compiler errors/warnings (optionally trigger a recompile first). Use after editing scripts to verify they compile.",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                recompile = new { type = "boolean", description = "Request a recompile before checking (default false)" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Check compilation", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            null);

        yield return new ToolSpec(
            "script.get_references", "Get Script References",
            "Find references to a type or script across the project.",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                type = new { type = "string", description = "Type name to find references to" },
                path = new { type = "string", description = "Or a script path" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get script references", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "selection.set", "Set Selection",
            "Set the editor selection to the given objects (by instance_ids, paths, or asset guids) - e.g. to select an object you just created.",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                instance_ids = new { type = "array", description = "GameObject instanceIds" },
                paths = new { type = "array", description = "Hierarchy paths" },
                guids = new { type = "array", description = "Asset guids" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Set selection", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 5000, true, true),
            null);

        yield return new ToolSpec(
            "capture.game", "Capture Game View",
            "Render the Game view (main camera) to an image so you can SEE the result. Use for VISUAL questions (appearance, lighting, materials, UI layout) - structural questions are answered more cheaply by the scene tools. The image is downscaled to keep token cost low; only capture when you actually need to look.",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                max_dimension = new { type = "integer", description = "Max long-edge pixels (default 1024); lower = fewer tokens" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ScreenCapture, PermissionRisk.Low, "Capture the Game view", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "capture.scene", "Capture Scene View",
            "Render the Scene view (editor camera) to an image so you can SEE the editor viewport. Use for VISUAL questions only; downscaled automatically.",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                max_dimension = new { type = "integer", description = "Max long-edge pixels (default 1024)" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ScreenCapture, PermissionRisk.Low, "Capture the Scene view", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "capture.multi_angle", "Capture Object (Multi-Angle)",
            "Render a specific GameObject from several angles (focus + framing) so you can SEE it in 3D. Identify the object by instanceId or objectPath; choose a preset (e.g. orbit, sides, front) and distance. Returns one image per angle. Great for inspecting a model you just created or changed.",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                instanceId = new { type = "integer" },
                objectPath = new { type = "string" },
                preset = new { type = "string", description = "Angle preset (e.g. orbit, sides, front)" },
                distance = new { type = "number", description = "Camera distance (auto-fit if omitted)" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ScreenCapture, PermissionRisk.Low, "Capture object angles", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            null);

        yield return new ToolSpec(
            "capture.region", "Capture Region",
            "Render a sub-region of the Scene View (orthographic) - e.g. for 2D / UI framing. region* values are normalized 0..1.",
            ToolCategory.SceneRead,
            new { type = "object", properties = new {
                regionX = new { type = "number", description = "Left edge (0..1)" },
                regionY = new { type = "number", description = "Bottom edge (0..1)" },
                regionWidth = new { type = "number", description = "Width (0..1)" },
                regionHeight = new { type = "number", description = "Height (0..1)" },
                superSample = new { type = "integer", description = "Supersample factor (1-4)" }
            }},
            null,
            new PermissionRequirement(PermissionClass.ScreenCapture, PermissionRisk.Low, "Capture region", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "console.get_logs", "Get Console Logs", "Get recent Unity console log entries",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                logType = new { type = "string", description = "Filter by log type (Log, Warning, Error, Exception)" },
                maxEntries = new { type = "integer", @default = 100 },
                includeStackTrace = new { type = "boolean", @default = false }
            }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read console logs", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 10000, true, true),
            null);

        yield return new ToolSpec(
            "package.list", "List Packages", "List installed Unity packages",
            ToolCategory.Package,
            new { type = "object", properties = new { }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List packages", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "package.search", "Search Packages", "Search the Unity package registry",
            ToolCategory.Package,
            new { type = "object", properties = new { query = new { type = "string", description = "Search text (optional)" } } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Search packages", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            null);

        yield return new ToolSpec(
            "package.info", "Package Info", "Get details about an installed package",
            ToolCategory.Package,
            new { type = "object", properties = new { packageId = new { type = "string", description = "Package name, e.g. com.unity.cinemachine" } }, required = new[] { "packageId" } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read package info", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "package.get_versions", "Get Package Versions", "List available versions of a package",
            ToolCategory.Package,
            new { type = "object", properties = new { packageId = new { type = "string", description = "Package name" } }, required = new[] { "packageId" } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List package versions", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            null);

        yield return new ToolSpec(
            "package.read_manifest", "Read Package Manifest", "Read Packages/manifest.json (the project's UPM dependencies)",
            ToolCategory.Package,
            new { type = "object", properties = new { }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read package manifest", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 10000, true, true),
            null);

        yield return new ToolSpec(
            "package.add", "Add Package", "Install a Unity package via UPM. packageId may be a registry name (com.unity.x), a git URL, a tarball, or a 'file:' local path.",
            ToolCategory.Package,
            new { type = "object", properties = new { packageId = new { type = "string", description = "Registry name, git URL, tarball, or file: path" }, version = new { type = "string", description = "Version (optional, registry packages only)" } }, required = new[] { "packageId" } },
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Install a package", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 180000, true, true),
            null);

        yield return new ToolSpec(
            "package.remove", "Remove Package", "Uninstall a Unity package via UPM",
            ToolCategory.Package,
            new { type = "object", properties = new { packageId = new { type = "string", description = "Package name to remove" } }, required = new[] { "packageId" } },
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Remove a package", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 120000, true, true),
            null);

        yield return new ToolSpec(
            "package.embed", "Embed Package", "Copy an installed package into Packages/ so it can be edited in-project",
            ToolCategory.Package,
            new { type = "object", properties = new { packageId = new { type = "string", description = "Package name to embed" } }, required = new[] { "packageId" } },
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Embed a package", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 120000, true, true),
            null);

        yield return new ToolSpec(
            "package.import_sample", "Import Package Sample", "Import a sample from an installed package into Assets/",
            ToolCategory.Package,
            new { type = "object", properties = new { packageId = new { type = "string", description = "Package name" }, sampleName = new { type = "string", description = "Sample display name (optional)" }, sampleIndex = new { type = "integer", description = "Sample index (optional, alternative to sampleName)" } }, required = new[] { "packageId" } },
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Import a package sample", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 120000, true, true),
            null);

        yield return new ToolSpec(
            "package.resolve", "Resolve Packages", "Force UPM to re-resolve and restore packages",
            ToolCategory.Package,
            new { type = "object", properties = new { }},
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Low, "Resolve packages", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 120000, true, true),
            null);

        yield return new ToolSpec(
            "capture.editor_layout", "Capture Editor Layout", "Screenshot the Unity Editor (or a named EditorWindow type) so the model can see the editor UI",
            ToolCategory.SceneRead,
            new { type = "object", properties = new { windowType = new { type = "string", description = "Optional EditorWindow type name to capture (default: the whole editor)" } } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Capture the editor view", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "project.search", "Search Project Files", "Search text across project files (Editor-side), with an optional path, file glob, regex, and result cap",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new {
                query = new { type = "string", description = "Text to search for" },
                path = new { type = "string", description = "Root folder (default: Assets)" },
                file_pattern = new { type = "string", description = "File glob (default: *.cs)" },
                max_results = new { type = "integer", description = "Max matches (default: 50)" },
                regex = new { type = "boolean", description = "Treat the query as a regex" }
            }, required = new[] { "query" } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Search project files", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "project.write_file", "Write Project File", "Create or overwrite a text file in the project",
            ToolCategory.CodeEdit,
            new { type = "object", properties = new { path = new { type = "string", description = "Project-relative file path" }, content = new { type = "string", description = "File contents" } }, required = new[] { "path", "content" } },
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Write a project file", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "graph.query", "Query Asset Graph", "Query the asset dependency graph: what an asset depends on, or what uses it",
            ToolCategory.AssetRead,
            new { type = "object", properties = new {
                startPath = new { type = "string", description = "Asset path to start from" },
                startGuid = new { type = "string", description = "Asset GUID to start from (alternative to startPath)" },
                direction = new { type = "string", description = "'dependencies' (what it uses) or 'usages' (what uses it)" },
                maxDepth = new { type = "integer", description = "Traversal depth (default shallow)" },
                filter = new { type = "string", description = "Asset-type filter (optional)" },
                includeBuiltIn = new { type = "boolean", description = "Include built-in/package assets" }
            } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Query the asset graph", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            null);

        yield return new ToolSpec(
            "skill.list", "List Skills", "List available Splatter skills (reusable procedures), optionally filtered",
            ToolCategory.Skill,
            new { type = "object", properties = new {
                category = new { type = "string", description = "Filter by category (optional)" },
                tag = new { type = "string", description = "Filter by tag (optional)" },
                search = new { type = "string", description = "Search text (optional)" },
                refresh = new { type = "boolean", description = "Force a re-scan (optional)" }
            } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List skills", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 15000, true, true),
            null);

        yield return new ToolSpec(
            "skill.read_body", "Read Skill", "Read a skill's full instructions/body",
            ToolCategory.Skill,
            new { type = "object", properties = new { skillId = new { type = "string", description = "Skill id" } }, required = new[] { "skillId" } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read a skill", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 15000, true, true),
            null);

        yield return new ToolSpec(
            "skill.read_resource", "Read Skill Resource", "Read a named resource file bundled with a skill",
            ToolCategory.Skill,
            new { type = "object", properties = new { skillId = new { type = "string", description = "Skill id" }, resourceName = new { type = "string", description = "Resource file name" } }, required = new[] { "skillId", "resourceName" } },
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read a skill resource", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 15000, true, true),
            null);

        yield return new ToolSpec(
            "checkpoint.list", "List Checkpoints", "List Splatter scene/state checkpoints (restore points)",
            ToolCategory.SceneRead,
            new { type = "object", properties = new { }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List checkpoints", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 15000, true, true),
            null);

        yield return new ToolSpec(
            "checkpoint.create", "Create Checkpoint", "Snapshot current scene/asset state as a restore point (non-destructive)",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                description = new { type = "string", description = "Label for the checkpoint (optional)" },
                paths = new { type = "array", items = new { type = "string" }, description = "Specific asset paths to snapshot (optional; default is the scene)" }
            } },
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Low, "Create a checkpoint", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            null);

        yield return new ToolSpec(
            "checkpoint.restore", "Restore Checkpoint", "Restore scene/asset state from a checkpoint. Reverts changes since the checkpoint.",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new {
                checkpointId = new { type = "string", description = "Checkpoint id to restore" },
                preview = new { type = "boolean", description = "Preview the diff without applying (optional)" }
            }, required = new[] { "checkpointId" } },
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.High, "Restore a checkpoint (reverts changes)", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            null);

        yield return new ToolSpec(
            "console.clear", "Clear Console", "Clear the Unity console log",
            ToolCategory.UnityMutation,
            new { type = "object", properties = new { }},
            null,
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Low, "Clear the console", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 10000, true, true),
            null);

        yield return new ToolSpec(
            "selection.get", "Get Selection", "Get currently selected objects in Unity Editor",
            ToolCategory.SceneRead,
            new { type = "object", properties = new { }},
            null,
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get selection", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 5000, true, true),
            null);
    }

    #endregion

    private sealed record RegisteredTool(ToolSpec Spec, Func<ToolCall, AgentSession, CancellationToken, Task<ToolResult>> Handler);
}

public sealed record UnityToolRequest(string CallId, string ToolId, object? Arguments, string WorkspaceId);
