// Diagnostics Bundle Exporter - M6.3 Implementation
// Exports redacted diagnostic bundles for troubleshooting

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Storage;

namespace Splatter.Service.Diagnostics;

/// <summary>
/// Diagnostic bundle containing system state and logs.
/// </summary>
public sealed record DiagnosticsBundle(
    string BundleId,
    DateTimeOffset GeneratedAt,
    string ServiceVersion,
    DiagnosticsSystemInfo SystemInfo,
    DiagnosticsSessionSummary[] Sessions,
    DiagnosticsProviderSummary[] Providers,
    DiagnosticsTimingMetrics Timing,
    DiagnosticsStorageInfo Storage,
    IReadOnlyList<DiagnosticsLogEntry> RecentLogs,
    IReadOnlyDictionary<string, string>? CustomData);

public sealed record DiagnosticsSystemInfo(
    string OsDescription,
    string OsArchitecture,
    int ProcessorCount,
    long WorkingSetBytes,
    TimeSpan Uptime,
    string RuntimeVersion,
    string DataDirectory,
    bool HasGit);

public sealed record DiagnosticsSessionSummary(
    string SessionId,
    string WorkspaceId,
    AgentSessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivity,
    int TurnCount,
    int ToolCallCount,
    int ErrorCount,
    IReadOnlyDictionary<string, int> ToolUsage);

public sealed record DiagnosticsProviderSummary(
    string ProviderId,
    bool IsConfigured,
    bool IsHealthy,
    int TotalRequests,
    int FailedRequests,
    int? EstimatedInputTokens,
    int? EstimatedOutputTokens,
    decimal? EstimatedCostUsd,
    DateTimeOffset? LastRequestAt);

public sealed record DiagnosticsTimingMetrics(
    double AvgFirstTokenLatencyMs,
    double AvgToolExecutionMs,
    double AvgPermissionPromptLatencyMs,
    double AvgPatchApplyMs,
    double AvgIndexScanMs,
    double AvgEventReplayMs,
    int SampleCount);

public sealed record DiagnosticsStorageInfo(
    string DatabasePath,
    long DatabaseSizeBytes,
    long EventJournalSizeBytes,
    long CheckpointsSizeBytes,
    int ConversationCount,
    int SessionCount,
    int CheckpointCount);

public sealed record DiagnosticsLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>
/// Options for diagnostic bundle export.
/// </summary>
public sealed record DiagnosticsExportOptions(
    bool IncludeLogs = true,
    bool IncludeEventJournal = false,
    int MaxLogEntries = 1000,
    int MaxSessionsToInclude = 10,
    TimeSpan? TimeRange = null,
    string? SpecificSessionId = null,
    IReadOnlyDictionary<string, string>? CustomData = null);

/// <summary>
/// Exports diagnostic bundles with sensitive data redaction.
/// </summary>
public interface IDiagnosticsExporter
{
    /// <summary>
    /// Generates a diagnostic bundle.
    /// </summary>
    Task<DiagnosticsBundle> GenerateBundleAsync(DiagnosticsExportOptions options, CancellationToken ct);

    /// <summary>
    /// Exports a diagnostic bundle to a ZIP file.
    /// </summary>
    Task<string> ExportToZipAsync(DiagnosticsBundle bundle, string? outputPath, CancellationToken ct);

    /// <summary>
    /// Exports a diagnostic bundle to JSON.
    /// </summary>
    Task<string> ExportToJsonAsync(DiagnosticsBundle bundle, CancellationToken ct);

    /// <summary>
    /// Redacts sensitive information from a string.
    /// </summary>
    string Redact(string input);
}

public sealed class DiagnosticsExporter : IDiagnosticsExporter
{
    private readonly ILogger<DiagnosticsExporter> _logger;
    private readonly IPersistenceStore _persistence;
    private readonly IEventJournal _eventJournal;
    private readonly ServiceConfiguration _config;
    private readonly ITimingMetrics _timing;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // Patterns for sensitive data redaction
    private static readonly Regex[] SensitivePatterns = new[]
    {
        // API keys (various formats)
        new Regex(@"(sk-[a-zA-Z0-9]{20,})", RegexOptions.Compiled),
        new Regex(@"(api[_-]?key['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-_]{16,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(bearer\s+)([a-zA-Z0-9\-_\.]{20,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(authorization['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-_\.]{20,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Anthropic keys
        new Regex(@"(anthropic[_-]?api[_-]?key['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-]{20,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(ANTHROPIC_API_KEY\s*=\s*)([^\s]+)", RegexOptions.Compiled),

        // OpenAI keys
        new Regex(@"(openai[_-]?api[_-]?key['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-]{20,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(OPENAI_API_KEY\s*=\s*)([^\s]+)", RegexOptions.Compiled),

        // Google API keys
        new Regex(@"(AIza[a-zA-Z0-9\-_]{35})", RegexOptions.Compiled),
        new Regex(@"(GOOGLE_API_KEY\s*=\s*)([^\s]+)", RegexOptions.Compiled),

        // Generic secrets
        new Regex(@"(secret[_-]?key['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-_]{16,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(password['""]?\s*[:=]\s*['""]?)([^\s'"",]{6,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(token['""]?\s*[:=]\s*['""]?)([a-zA-Z0-9\-_\.]{20,})", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Email addresses (partial redaction)
        new Regex(@"([a-zA-Z0-9._%+-]+)@([a-zA-Z0-9.-]+\.[a-zA-Z]{2,})", RegexOptions.Compiled),

        // File paths that might contain usernames
        new Regex(@"(/Users/|/home/|C:\\Users\\)([^/\\]+)", RegexOptions.Compiled),

        // IP addresses (non-localhost)
        new Regex(@"\b(?!127\.0\.0\.1)(?!0\.0\.0\.0)(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b", RegexOptions.Compiled),

        // Base64 encoded data that might be secrets (long strings)
        new Regex(@"([A-Za-z0-9+/=]{100,})", RegexOptions.Compiled)
    };

    public DiagnosticsExporter(
        ILogger<DiagnosticsExporter> logger,
        IPersistenceStore persistence,
        IEventJournal eventJournal,
        ServiceConfiguration config,
        ITimingMetrics timing)
    {
        _logger = logger;
        _persistence = persistence;
        _eventJournal = eventJournal;
        _config = config;
        _timing = timing;
    }

    public async Task<DiagnosticsBundle> GenerateBundleAsync(DiagnosticsExportOptions options, CancellationToken ct)
    {
        _logger.LogInformation("Generating diagnostics bundle...");

        var bundleId = $"diag_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..32];

        // Collect system info
        var systemInfo = CollectSystemInfo();

        // Collect session summaries
        var sessions = await CollectSessionSummariesAsync(options, ct);

        // Collect provider summaries
        var providers = await CollectProviderSummariesAsync(ct);

        // Collect timing metrics
        var timing = _timing.GetMetrics();

        // Collect storage info
        var storage = CollectStorageInfo();

        // Collect recent logs
        var logs = options.IncludeLogs
            ? await CollectRecentLogsAsync(options.MaxLogEntries, options.TimeRange, ct)
            : Array.Empty<DiagnosticsLogEntry>();

        var bundle = new DiagnosticsBundle(
            bundleId,
            DateTimeOffset.UtcNow,
            ServiceConfiguration.ServiceVersion,
            systemInfo,
            sessions.ToArray(),
            providers.ToArray(),
            timing,
            storage,
            logs,
            options.CustomData);

        _logger.LogInformation("Generated diagnostics bundle {BundleId} with {SessionCount} sessions, {LogCount} log entries",
            bundleId, sessions.Count, logs.Count);

        return bundle;
    }

    public async Task<string> ExportToZipAsync(DiagnosticsBundle bundle, string? outputPath, CancellationToken ct)
    {
        outputPath ??= Path.Combine(_config.DataDirectory, "diagnostics", $"{bundle.BundleId}.zip");
        var dir = Path.GetDirectoryName(outputPath);
        if (dir != null) Directory.CreateDirectory(dir);

        using var zipStream = File.Create(outputPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // Add main bundle JSON
        var mainEntry = archive.CreateEntry("bundle.json");
        await using (var entryStream = mainEntry.Open())
        {
            var json = JsonSerializer.Serialize(bundle, JsonOptions);
            var redactedJson = Redact(json);
            await entryStream.WriteAsync(Encoding.UTF8.GetBytes(redactedJson), ct);
        }

        // Add session details
        foreach (var session in bundle.Sessions)
        {
            var sessionEntry = archive.CreateEntry($"sessions/{session.SessionId}.json");
            await using var entryStream = sessionEntry.Open();
            var sessionJson = JsonSerializer.Serialize(session, JsonOptions);
            await entryStream.WriteAsync(Encoding.UTF8.GetBytes(Redact(sessionJson)), ct);
        }

        // Add timing breakdown
        var timingEntry = archive.CreateEntry("timing.json");
        await using (var entryStream = timingEntry.Open())
        {
            var timingJson = JsonSerializer.Serialize(bundle.Timing, JsonOptions);
            await entryStream.WriteAsync(Encoding.UTF8.GetBytes(timingJson), ct);
        }

        // Add README
        var readmeEntry = archive.CreateEntry("README.txt");
        await using (var entryStream = readmeEntry.Open())
        {
            var readme = GenerateReadme(bundle);
            await entryStream.WriteAsync(Encoding.UTF8.GetBytes(readme), ct);
        }

        _logger.LogInformation("Exported diagnostics bundle to {Path}", outputPath);
        return outputPath;
    }

    public Task<string> ExportToJsonAsync(DiagnosticsBundle bundle, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        var redactedJson = Redact(json);
        return Task.FromResult(redactedJson);
    }

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = input;

        foreach (var pattern in SensitivePatterns)
        {
            result = pattern.Replace(result, match =>
            {
                if (match.Groups.Count > 2)
                {
                    // Preserve the prefix (e.g., "api_key="), redact the value
                    return match.Groups[1].Value + "[REDACTED]";
                }
                else if (match.Value.Contains("@"))
                {
                    // Email: keep domain, redact local part
                    var atIndex = match.Value.IndexOf('@');
                    return "[REDACTED]" + match.Value[atIndex..];
                }
                else
                {
                    return "[REDACTED]";
                }
            });
        }

        return result;
    }

    #region Collectors

    private DiagnosticsSystemInfo CollectSystemInfo()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();

        return new DiagnosticsSystemInfo(
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            process.WorkingSet64,
            DateTimeOffset.UtcNow - process.StartTime,
            Environment.Version.ToString(),
            Redact(_config.DataDirectory),
            CheckGitAvailable());
    }

    private async Task<IReadOnlyList<DiagnosticsSessionSummary>> CollectSessionSummariesAsync(
        DiagnosticsExportOptions options, CancellationToken ct)
    {
        var summaries = new List<DiagnosticsSessionSummary>();
        var eventsDir = Path.Combine(_config.DataDirectory, "events");

        if (!Directory.Exists(eventsDir))
            return summaries;

        var sessionFiles = new List<(string workspaceId, string sessionId, FileInfo fileInfo)>();

        foreach (var workspaceDir in Directory.GetDirectories(eventsDir))
        {
            var workspaceId = Path.GetFileName(workspaceDir);

            foreach (var sessionFile in Directory.GetFiles(workspaceDir, "*.jsonl"))
            {
                var sessionId = Path.GetFileNameWithoutExtension(sessionFile);

                // Filter by specific session if requested
                if (options.SpecificSessionId != null && sessionId != options.SpecificSessionId)
                    continue;

                sessionFiles.Add((workspaceId, sessionId, new FileInfo(sessionFile)));
            }
        }

        // Sort by modification time and take most recent
        var recentSessions = sessionFiles
            .OrderByDescending(s => s.fileInfo.LastWriteTimeUtc)
            .Take(options.MaxSessionsToInclude)
            .ToList();

        foreach (var (workspaceId, sessionId, fileInfo) in recentSessions)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var summary = await BuildSessionSummaryAsync(workspaceId, sessionId, fileInfo.FullName, ct);
                summaries.Add(summary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build summary for session {SessionId}", sessionId);
            }
        }

        return summaries;
    }

    private async Task<DiagnosticsSessionSummary> BuildSessionSummaryAsync(
        string workspaceId, string sessionId, string eventFilePath, CancellationToken ct)
    {
        var session = await _persistence.GetSessionAsync(sessionId, ct);

        var turnCount = 0;
        var toolCallCount = 0;
        var errorCount = 0;
        var lastActivity = DateTimeOffset.MinValue;
        var toolUsage = new Dictionary<string, int>();

        await foreach (var line in File.ReadLinesAsync(eventFilePath, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var eventType = root.GetProperty("eventType").GetString() ?? "";
                var timestamp = DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!);

                if (timestamp > lastActivity)
                    lastActivity = timestamp;

                switch (eventType)
                {
                    case "message.delta":
                        // Count complete messages as turns
                        var payload = root.GetProperty("payload").GetString();
                        if (payload?.Contains("\"isComplete\":true") == true)
                            turnCount++;
                        break;

                    case "tool.started":
                        toolCallCount++;
                        var toolPayload = JsonDocument.Parse(root.GetProperty("payload").GetString()!);
                        var toolId = toolPayload.RootElement.GetProperty("toolId").GetString() ?? "unknown";
                        toolUsage[toolId] = toolUsage.GetValueOrDefault(toolId, 0) + 1;
                        break;

                    case "session.failed":
                        errorCount++;
                        break;
                }
            }
            catch
            {
                // Skip malformed events
            }
        }

        return new DiagnosticsSessionSummary(
            sessionId,
            workspaceId,
            session?.Status ?? AgentSessionStatus.Failed,
            session?.CreatedAt ?? DateTimeOffset.MinValue,
            lastActivity,
            turnCount,
            toolCallCount,
            errorCount,
            toolUsage);
    }

    private Task<IReadOnlyList<DiagnosticsProviderSummary>> CollectProviderSummariesAsync(CancellationToken ct)
    {
        // Provider summaries would come from the provider registry
        // For now, return placeholders based on configuration
        var summaries = new List<DiagnosticsProviderSummary>
        {
            new("anthropic", true, true, 0, 0, null, null, null, null),
            new("openai", false, false, 0, 0, null, null, null, null),
            new("gemini", false, false, 0, 0, null, null, null, null)
        };

        return Task.FromResult<IReadOnlyList<DiagnosticsProviderSummary>>(summaries);
    }

    private DiagnosticsStorageInfo CollectStorageInfo()
    {
        var dbPath = _config.DatabasePath;
        var dbSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;

        var eventsDir = Path.Combine(_config.DataDirectory, "events");
        var eventsSize = Directory.Exists(eventsDir)
            ? Directory.GetFiles(eventsDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

        var checkpointsDir = Path.Combine(_config.DataDirectory, "checkpoints");
        var checkpointsSize = Directory.Exists(checkpointsDir)
            ? Directory.GetFiles(checkpointsDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

        var checkpointCount = Directory.Exists(checkpointsDir)
            ? Directory.GetDirectories(checkpointsDir).Length
            : 0;

        // Conversation and session counts would come from database
        // For now, estimate from event files
        var sessionCount = Directory.Exists(eventsDir)
            ? Directory.GetFiles(eventsDir, "*.jsonl", SearchOption.AllDirectories).Length
            : 0;

        return new DiagnosticsStorageInfo(
            Redact(dbPath),
            dbSize,
            eventsSize,
            checkpointsSize,
            sessionCount, // Approximate
            sessionCount,
            checkpointCount);
    }

    private async Task<IReadOnlyList<DiagnosticsLogEntry>> CollectRecentLogsAsync(
        int maxEntries, TimeSpan? timeRange, CancellationToken ct)
    {
        var logs = new List<DiagnosticsLogEntry>();
        var logsDir = Path.Combine(_config.DataDirectory, "logs");

        if (!Directory.Exists(logsDir))
            return logs;

        var cutoff = timeRange.HasValue
            ? DateTimeOffset.UtcNow - timeRange.Value
            : DateTimeOffset.MinValue;

        // Find recent log files
        var logFiles = Directory.GetFiles(logsDir, "*.log")
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .Take(5)
            .ToList();

        foreach (var logFile in logFiles)
        {
            if (logs.Count >= maxEntries)
                break;

            try
            {
                await foreach (var line in File.ReadLinesAsync(logFile, ct))
                {
                    if (logs.Count >= maxEntries)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var entry = ParseLogEntry(line);
                    if (entry != null && entry.Timestamp >= cutoff)
                    {
                        // Redact sensitive data
                        logs.Add(entry with { Message = Redact(entry.Message) });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read log file {Path}", logFile);
            }
        }

        return logs.OrderByDescending(l => l.Timestamp).ToList();
    }

    private static DiagnosticsLogEntry? ParseLogEntry(string line)
    {
        // Simple log parsing - assumes format: [timestamp] [level] category: message
        try
        {
            var timestampMatch = Regex.Match(line, @"^\[(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}[^\]]*)\]");
            if (!timestampMatch.Success)
                return null;

            var timestamp = DateTimeOffset.Parse(timestampMatch.Groups[1].Value);
            var remaining = line[(timestampMatch.Length)..].TrimStart();

            var levelMatch = Regex.Match(remaining, @"^\[?(\w+)\]?\s*");
            var level = levelMatch.Success ? levelMatch.Groups[1].Value : "INFO";
            remaining = remaining[(levelMatch.Length)..].TrimStart();

            var categoryEnd = remaining.IndexOf(':');
            var category = categoryEnd > 0 ? remaining[..categoryEnd].Trim() : "General";
            var message = categoryEnd > 0 ? remaining[(categoryEnd + 1)..].Trim() : remaining;

            return new DiagnosticsLogEntry(timestamp, level.ToUpperInvariant(), category, message, null);
        }
        catch
        {
            return null;
        }
    }

    private static bool CheckGitAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            return process?.WaitForExit(1000) == true && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private string GenerateReadme(DiagnosticsBundle bundle)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SPLATTER AI DIAGNOSTICS BUNDLE");
        sb.AppendLine("==============================");
        sb.AppendLine();
        sb.AppendLine($"Bundle ID: {bundle.BundleId}");
        sb.AppendLine($"Generated: {bundle.GeneratedAt:O}");
        sb.AppendLine($"Service Version: {bundle.ServiceVersion}");
        sb.AppendLine();
        sb.AppendLine("CONTENTS:");
        sb.AppendLine("---------");
        sb.AppendLine("- bundle.json: Main diagnostic data (redacted)");
        sb.AppendLine("- sessions/: Individual session summaries");
        sb.AppendLine("- timing.json: Performance timing metrics");
        sb.AppendLine();
        sb.AppendLine("REDACTION NOTICE:");
        sb.AppendLine("-----------------");
        sb.AppendLine("This bundle has been automatically redacted to remove:");
        sb.AppendLine("- API keys and tokens");
        sb.AppendLine("- Passwords and secrets");
        sb.AppendLine("- Email addresses (partial)");
        sb.AppendLine("- User home directory paths");
        sb.AppendLine("- External IP addresses");
        sb.AppendLine();
        sb.AppendLine("Please review before sharing with support.");
        sb.AppendLine();
        sb.AppendLine("SYSTEM INFO:");
        sb.AppendLine("------------");
        sb.AppendLine($"OS: {bundle.SystemInfo.OsDescription}");
        sb.AppendLine($"Architecture: {bundle.SystemInfo.OsArchitecture}");
        sb.AppendLine($"Processors: {bundle.SystemInfo.ProcessorCount}");
        sb.AppendLine($"Runtime: .NET {bundle.SystemInfo.RuntimeVersion}");
        sb.AppendLine($"Uptime: {bundle.SystemInfo.Uptime}");
        sb.AppendLine();
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("--------");
        sb.AppendLine($"Sessions: {bundle.Sessions.Length}");
        sb.AppendLine($"Providers: {bundle.Providers.Length}");
        sb.AppendLine($"Log Entries: {bundle.RecentLogs.Count}");
        sb.AppendLine($"Database Size: {bundle.Storage.DatabaseSizeBytes / 1024.0:F1} KB");

        return sb.ToString();
    }

    #endregion
}
