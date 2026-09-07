// Crash Recovery Manager - M6.2 Implementation
// Handles abandoned sessions, partial writes, and unknown tool state after crash

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Storage;

namespace Splatter.Service.Recovery;

/// <summary>
/// Represents the state of a recoverable item.
/// </summary>
public enum RecoverableItemState
{
    /// <summary>Item is recoverable and can be resumed.</summary>
    Recoverable,
    /// <summary>Item was successfully completed.</summary>
    Completed,
    /// <summary>Item was cancelled by user or system.</summary>
    Cancelled,
    /// <summary>Item failed with error.</summary>
    Failed,
    /// <summary>Item state is unknown, requires user decision.</summary>
    Unknown
}

/// <summary>
/// Represents a recoverable item after crash.
/// </summary>
public sealed record RecoverableItem(
    string Id,
    RecoverableItemType Type,
    RecoverableItemState State,
    string WorkspaceId,
    string? SessionId,
    DateTimeOffset LastActivity,
    string Description,
    IReadOnlyDictionary<string, object?>? Metadata,
    IReadOnlyList<RecoveryAction> AvailableActions);

public enum RecoverableItemType
{
    Session,
    Turn,
    ToolCall,
    Checkpoint,
    GeneratorJob,
    PartialWrite
}

/// <summary>
/// Result of recovery scan.
/// </summary>
public sealed record RecoveryScanResult(
    IReadOnlyList<RecoverableItem> Items,
    int TotalAbandoned,
    int TotalRecoverable,
    int TotalUnknown,
    DateTimeOffset ScannedAt,
    TimeSpan ScanDuration);

/// <summary>
/// Result of a recovery action.
/// </summary>
public sealed record RecoveryActionResult(
    string ItemId,
    RecoveryAction Action,
    bool Success,
    RecoverableItemState NewState,
    string? Message,
    NormalizedError? Error);

/// <summary>
/// Manages crash recovery for abandoned sessions and partial operations.
/// </summary>
public interface ICrashRecoveryManager
{
    /// <summary>
    /// Scans for recoverable items after service restart.
    /// </summary>
    Task<RecoveryScanResult> ScanForRecoverableItemsAsync(CancellationToken ct);

    /// <summary>
    /// Executes a recovery action on an item.
    /// </summary>
    Task<RecoveryActionResult> ExecuteRecoveryActionAsync(string itemId, RecoveryAction action, CancellationToken ct);

    /// <summary>
    /// Gets current recoverable items.
    /// </summary>
    Task<IReadOnlyList<RecoverableItem>> GetRecoverableItemsAsync(string? workspaceId, CancellationToken ct);

    /// <summary>
    /// Marks an item as resolved with user decision.
    /// </summary>
    Task<bool> ResolveUnknownItemAsync(string itemId, RecoverableItemState newState, string? reason, CancellationToken ct);

    /// <summary>
    /// Records a crash marker for the current service instance.
    /// </summary>
    Task WriteCrashMarkerAsync(CancellationToken ct);

    /// <summary>
    /// Clears the crash marker on graceful shutdown.
    /// </summary>
    Task ClearCrashMarkerAsync(CancellationToken ct);
}

public sealed class CrashRecoveryManager : ICrashRecoveryManager
{
    private readonly ILogger<CrashRecoveryManager> _logger;
    private readonly IPersistenceStore _persistence;
    private readonly IEventJournal _eventJournal;
    private readonly ServiceConfiguration _config;
    private readonly ConcurrentDictionary<string, RecoverableItem> _recoverableItems = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // Thresholds for determining abandoned state
    private static readonly TimeSpan AbandonedSessionThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbandonedToolCallThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PartialWriteMaxAge = TimeSpan.FromDays(1);

    public CrashRecoveryManager(
        ILogger<CrashRecoveryManager> logger,
        IPersistenceStore persistence,
        IEventJournal eventJournal,
        ServiceConfiguration config)
    {
        _logger = logger;
        _persistence = persistence;
        _eventJournal = eventJournal;
        _config = config;
    }

    public async Task<RecoveryScanResult> ScanForRecoverableItemsAsync(CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow;
        var items = new List<RecoverableItem>();

        _logger.LogInformation("Starting crash recovery scan...");

        try
        {
            // Check for crash marker
            var hadCrash = await CheckCrashMarkerAsync(ct);
            if (hadCrash)
            {
                _logger.LogWarning("Previous service instance did not shut down gracefully");
            }

            // 1. Scan for abandoned sessions
            var abandonedSessions = await ScanAbandonedSessionsAsync(ct);
            items.AddRange(abandonedSessions);

            // 2. Scan for partial writes (incomplete file operations)
            var partialWrites = await ScanPartialWritesAsync(ct);
            items.AddRange(partialWrites);

            // 3. Scan for orphaned checkpoints
            var orphanedCheckpoints = await ScanOrphanedCheckpointsAsync(ct);
            items.AddRange(orphanedCheckpoints);

            // 4. Scan for recoverable generator jobs
            var recoverableJobs = await ScanRecoverableJobsAsync(ct);
            items.AddRange(recoverableJobs);

            // 5. Scan for unknown tool states from event journal
            var unknownTools = await ScanUnknownToolStatesAsync(ct);
            items.AddRange(unknownTools);

            // Cache items
            foreach (var item in items)
            {
                _recoverableItems[item.Id] = item;
            }

            var scanDuration = DateTimeOffset.UtcNow - startTime;
            var result = new RecoveryScanResult(
                items,
                items.Count(i => i.State == RecoverableItemState.Recoverable || i.State == RecoverableItemState.Unknown),
                items.Count(i => i.State == RecoverableItemState.Recoverable),
                items.Count(i => i.State == RecoverableItemState.Unknown),
                DateTimeOffset.UtcNow,
                scanDuration);

            _logger.LogInformation(
                "Recovery scan completed in {Duration}ms. Found {Total} items ({Recoverable} recoverable, {Unknown} unknown)",
                scanDuration.TotalMilliseconds, items.Count, result.TotalRecoverable, result.TotalUnknown);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery scan failed");
            throw;
        }
    }

    public async Task<RecoveryActionResult> ExecuteRecoveryActionAsync(string itemId, RecoveryAction action, CancellationToken ct)
    {
        if (!_recoverableItems.TryGetValue(itemId, out var item))
        {
            return new RecoveryActionResult(
                itemId, action, false, RecoverableItemState.Unknown,
                "Item not found", null);
        }

        _logger.LogInformation("Executing recovery action {Action} on item {ItemId} ({Type})",
            action, itemId, item.Type);

        try
        {
            return item.Type switch
            {
                RecoverableItemType.Session => await RecoverSessionAsync(item, action, ct),
                RecoverableItemType.Turn => await RecoverTurnAsync(item, action, ct),
                RecoverableItemType.ToolCall => await RecoverToolCallAsync(item, action, ct),
                RecoverableItemType.Checkpoint => await RecoverCheckpointAsync(item, action, ct),
                RecoverableItemType.GeneratorJob => await RecoverGeneratorJobAsync(item, action, ct),
                RecoverableItemType.PartialWrite => await RecoverPartialWriteAsync(item, action, ct),
                _ => new RecoveryActionResult(itemId, action, false, item.State, "Unknown item type", null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery action {Action} failed for item {ItemId}", action, itemId);
            return new RecoveryActionResult(
                itemId, action, false, item.State, ex.Message,
                new NormalizedError("recovery_failed", ex.Message, ex.ToString(), true, null, null));
        }
    }

    public Task<IReadOnlyList<RecoverableItem>> GetRecoverableItemsAsync(string? workspaceId, CancellationToken ct)
    {
        var items = _recoverableItems.Values
            .Where(i => workspaceId == null || i.WorkspaceId == workspaceId)
            .OrderByDescending(i => i.LastActivity)
            .ToList();

        return Task.FromResult<IReadOnlyList<RecoverableItem>>(items);
    }

    public Task<bool> ResolveUnknownItemAsync(string itemId, RecoverableItemState newState, string? reason, CancellationToken ct)
    {
        if (!_recoverableItems.TryGetValue(itemId, out var item))
        {
            return Task.FromResult(false);
        }

        var updated = item with { State = newState };
        _recoverableItems[itemId] = updated;

        _logger.LogInformation("Resolved unknown item {ItemId} to state {NewState}: {Reason}",
            itemId, newState, reason ?? "No reason provided");

        return Task.FromResult(true);
    }

    public async Task WriteCrashMarkerAsync(CancellationToken ct)
    {
        var markerPath = GetCrashMarkerPath();
        var marker = new CrashMarker
        {
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
            ServiceVersion = ServiceConfiguration.ServiceVersion
        };

        var json = JsonSerializer.Serialize(marker, JsonOptions);
        var dir = Path.GetDirectoryName(markerPath);
        if (dir != null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(markerPath, json, ct);

        _logger.LogDebug("Crash marker written to {Path}", markerPath);
    }

    public Task ClearCrashMarkerAsync(CancellationToken ct)
    {
        var markerPath = GetCrashMarkerPath();
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
            _logger.LogDebug("Crash marker cleared from {Path}", markerPath);
        }
        return Task.CompletedTask;
    }

    #region Scan Methods

    private async Task<bool> CheckCrashMarkerAsync(CancellationToken ct)
    {
        var markerPath = GetCrashMarkerPath();
        if (!File.Exists(markerPath))
            return false;

        try
        {
            var json = await File.ReadAllTextAsync(markerPath, ct);
            var marker = JsonSerializer.Deserialize<CrashMarker>(json, JsonOptions);

            if (marker != null)
            {
                _logger.LogWarning(
                    "Found crash marker from previous instance (PID: {Pid}, Started: {StartedAt}, Version: {Version})",
                    marker.Pid, marker.StartedAt, marker.ServiceVersion);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read crash marker");
        }

        return false;
    }

    private async Task<IReadOnlyList<RecoverableItem>> ScanAbandonedSessionsAsync(CancellationToken ct)
    {
        var items = new List<RecoverableItem>();
        var eventsDir = Path.Combine(_config.DataDirectory, "events");

        if (!Directory.Exists(eventsDir))
            return items;

        foreach (var workspaceDir in Directory.GetDirectories(eventsDir))
        {
            var workspaceId = Path.GetFileName(workspaceDir);

            foreach (var sessionFile in Directory.GetFiles(workspaceDir, "*.jsonl"))
            {
                ct.ThrowIfCancellationRequested();

                var sessionId = Path.GetFileNameWithoutExtension(sessionFile);
                var lastEvent = await GetLastEventAsync(sessionFile, ct);

                if (lastEvent == null)
                    continue;

                // Check if session ended properly
                var isTerminal = lastEvent.Type is "session.completed" or "session.cancelled" or "session.failed";
                if (isTerminal)
                    continue;

                // Check if session is abandoned (no activity for threshold)
                var timeSinceLastActivity = DateTimeOffset.UtcNow - lastEvent.Timestamp;
                if (timeSinceLastActivity < AbandonedSessionThreshold)
                    continue;

                var state = DetermineSessionState(lastEvent);
                var actions = GetActionsForState(state, RecoverableItemType.Session);

                items.Add(new RecoverableItem(
                    sessionId,
                    RecoverableItemType.Session,
                    state,
                    workspaceId,
                    sessionId,
                    lastEvent.Timestamp,
                    $"Abandoned session (last event: {lastEvent.Type})",
                    new Dictionary<string, object?>
                    {
                        ["lastEventType"] = lastEvent.Type,
                        ["lastEventTimestamp"] = lastEvent.Timestamp
                    },
                    actions));
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<RecoverableItem>> ScanPartialWritesAsync(CancellationToken ct)
    {
        var items = new List<RecoverableItem>();
        var checkpointsDir = Path.Combine(_config.DataDirectory, "checkpoints");

        if (!Directory.Exists(checkpointsDir))
            return items;

        // Look for .partial files (incomplete writes)
        foreach (var partialFile in Directory.GetFiles(checkpointsDir, "*.partial", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(partialFile);
            var age = DateTimeOffset.UtcNow - fileInfo.LastWriteTimeUtc;

            // Skip very recent partial files (might still be in progress)
            if (age < TimeSpan.FromMinutes(1))
                continue;

            var state = age > PartialWriteMaxAge
                ? RecoverableItemState.Failed
                : RecoverableItemState.Recoverable;

            items.Add(new RecoverableItem(
                Path.GetFileNameWithoutExtension(partialFile),
                RecoverableItemType.PartialWrite,
                state,
                "unknown",
                null,
                fileInfo.LastWriteTimeUtc,
                $"Partial write: {partialFile}",
                new Dictionary<string, object?>
                {
                    ["path"] = partialFile,
                    ["size"] = fileInfo.Length
                },
                new[] { RecoveryAction.Delete, RecoveryAction.Skip }));
        }

        // Look for backup files without corresponding originals
        foreach (var backupFile in Directory.GetFiles(_config.DataDirectory, "*.bak", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var originalPath = backupFile[..^4]; // Remove .bak
            if (File.Exists(originalPath))
                continue;

            var fileInfo = new FileInfo(backupFile);

            items.Add(new RecoverableItem(
                Path.GetFileName(backupFile),
                RecoverableItemType.PartialWrite,
                RecoverableItemState.Unknown,
                "unknown",
                null,
                fileInfo.LastWriteTimeUtc,
                $"Orphaned backup: {backupFile}",
                new Dictionary<string, object?>
                {
                    ["path"] = backupFile,
                    ["originalPath"] = originalPath
                },
                new[] { RecoveryAction.Resume, RecoveryAction.Delete, RecoveryAction.Skip }));
        }

        return items;
    }

    private Task<IReadOnlyList<RecoverableItem>> ScanOrphanedCheckpointsAsync(CancellationToken ct)
    {
        var items = new List<RecoverableItem>();
        var checkpointsDir = Path.Combine(_config.DataDirectory, "checkpoints");

        if (!Directory.Exists(checkpointsDir))
            return Task.FromResult<IReadOnlyList<RecoverableItem>>(items);

        // Look for checkpoint directories that are very old
        foreach (var checkpointDir in Directory.GetDirectories(checkpointsDir))
        {
            ct.ThrowIfCancellationRequested();

            var dirInfo = new DirectoryInfo(checkpointDir);
            var age = DateTimeOffset.UtcNow - dirInfo.LastWriteTimeUtc;

            // Only flag very old checkpoints
            if (age < _config.CheckpointMaxAge)
                continue;

            var fileCount = Directory.GetFiles(checkpointDir, "*", SearchOption.AllDirectories).Length;

            items.Add(new RecoverableItem(
                Path.GetFileName(checkpointDir),
                RecoverableItemType.Checkpoint,
                RecoverableItemState.Recoverable,
                "unknown",
                null,
                dirInfo.LastWriteTimeUtc,
                $"Old checkpoint with {fileCount} files",
                new Dictionary<string, object?>
                {
                    ["path"] = checkpointDir,
                    ["fileCount"] = fileCount,
                    ["age"] = age.ToString()
                },
                new[] { RecoveryAction.Delete, RecoveryAction.Skip }));
        }

        return Task.FromResult<IReadOnlyList<RecoverableItem>>(items);
    }

    private async Task<IReadOnlyList<RecoverableItem>> ScanRecoverableJobsAsync(CancellationToken ct)
    {
        var items = new List<RecoverableItem>();

        // Get all workspaces by scanning events directory
        var eventsDir = Path.Combine(_config.DataDirectory, "events");
        if (!Directory.Exists(eventsDir))
            return items;

        foreach (var workspaceDir in Directory.GetDirectories(eventsDir))
        {
            var workspaceId = Path.GetFileName(workspaceDir);
            var jobs = await _persistence.GetRecoverableJobsAsync(workspaceId, ct);

            foreach (var job in jobs)
            {
                items.Add(new RecoverableItem(
                    job.Id,
                    RecoverableItemType.GeneratorJob,
                    RecoverableItemState.Recoverable,
                    job.WorkspaceId,
                    null,
                    job.UpdatedAt,
                    $"Generator job ({job.Modality}): {job.Status}",
                    new Dictionary<string, object?>
                    {
                        ["modality"] = job.Modality.ToString(),
                        ["status"] = job.Status.ToString(),
                        ["targetPath"] = job.TargetAssetPath
                    },
                    new[] { RecoveryAction.Resume, RecoveryAction.Delete, RecoveryAction.Skip }));
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<RecoverableItem>> ScanUnknownToolStatesAsync(CancellationToken ct)
    {
        var items = new List<RecoverableItem>();
        var eventsDir = Path.Combine(_config.DataDirectory, "events");

        if (!Directory.Exists(eventsDir))
            return items;

        var toolStarted = new Dictionary<string, (string sessionId, string workspaceId, DateTimeOffset timestamp)>();
        var toolCompleted = new HashSet<string>();

        foreach (var workspaceDir in Directory.GetDirectories(eventsDir))
        {
            var workspaceId = Path.GetFileName(workspaceDir);

            foreach (var sessionFile in Directory.GetFiles(workspaceDir, "*.jsonl"))
            {
                ct.ThrowIfCancellationRequested();

                var sessionId = Path.GetFileNameWithoutExtension(sessionFile);

                await foreach (var line in File.ReadLinesAsync(sessionFile, ct))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        var eventType = root.GetProperty("eventType").GetString();
                        var timestamp = DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!);

                        if (eventType == "tool.started")
                        {
                            var payload = JsonDocument.Parse(root.GetProperty("payload").GetString()!);
                            var toolCallId = payload.RootElement.GetProperty("toolCallId").GetString()!;
                            toolStarted[toolCallId] = (sessionId, workspaceId, timestamp);
                        }
                        else if (eventType == "tool.completed")
                        {
                            var payload = JsonDocument.Parse(root.GetProperty("payload").GetString()!);
                            var toolCallId = payload.RootElement.GetProperty("toolCallId").GetString()!;
                            toolCompleted.Add(toolCallId);
                        }
                    }
                    catch
                    {
                        // Skip malformed events
                    }
                }
            }
        }

        // Find tools that started but never completed
        foreach (var (toolCallId, info) in toolStarted)
        {
            if (toolCompleted.Contains(toolCallId))
                continue;

            var timeSinceStart = DateTimeOffset.UtcNow - info.timestamp;
            if (timeSinceStart < AbandonedToolCallThreshold)
                continue;

            items.Add(new RecoverableItem(
                toolCallId,
                RecoverableItemType.ToolCall,
                RecoverableItemState.Unknown,
                info.workspaceId,
                info.sessionId,
                info.timestamp,
                "Tool call started but never completed",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = info.sessionId,
                    ["timeSinceStart"] = timeSinceStart.ToString()
                },
                new[] { RecoveryAction.Skip }));
        }

        return items;
    }

    #endregion

    #region Recovery Actions

    private async Task<RecoveryActionResult> RecoverSessionAsync(RecoverableItem item, RecoveryAction action, CancellationToken ct)
    {
        var session = await _persistence.GetSessionAsync(item.Id, ct);

        switch (action)
        {
            case RecoveryAction.Resume:
                if (session == null)
                    return new RecoveryActionResult(item.Id, action, false, item.State, "Session not found in database", null);

                // Resume not supported for sessions - they need user to re-send message
                return new RecoveryActionResult(item.Id, action, false, item.State,
                    "Sessions cannot be automatically resumed. Please start a new turn.", null);

            case RecoveryAction.Delete:
                // Mark session as cancelled
                if (session != null)
                {
                    var updated = session with { Status = AgentSessionStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
                    await _persistence.UpdateSessionAsync(updated, ct);
                }
                _recoverableItems.TryRemove(item.Id, out _);
                return new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Cancelled, "Session marked as cancelled", null);

            case RecoveryAction.Skip:
                // Mark session as failed
                if (session != null)
                {
                    var updated = session with { Status = AgentSessionStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow };
                    await _persistence.UpdateSessionAsync(updated, ct);
                }
                _recoverableItems.TryRemove(item.Id, out _);
                return new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Failed, "Session marked as failed", null);

            default:
                return new RecoveryActionResult(item.Id, action, false, item.State, "Unsupported action", null);
        }
    }

    private Task<RecoveryActionResult> RecoverTurnAsync(RecoverableItem item, RecoveryAction action, CancellationToken ct)
    {
        // Turns are handled as part of session recovery
        _recoverableItems.TryRemove(item.Id, out _);
        return Task.FromResult(new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Completed, "Turn marked as handled", null));
    }

    private Task<RecoveryActionResult> RecoverToolCallAsync(RecoverableItem item, RecoveryAction action, CancellationToken ct)
    {
        // Tool calls with unknown state - mark as resolved
        _recoverableItems.TryRemove(item.Id, out _);
        return Task.FromResult(new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Completed,
            "Tool call marked as unknown outcome", null));
    }

    private Task<RecoveryActionResult> RecoverCheckpointAsync(RecoverableItem item, RecoveryAction action, CancellationToken ct)
    {
        if (item.Metadata == null || !item.Metadata.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return Task.FromResult(new RecoveryActionResult(item.Id, action, false, item.State, "Checkpoint path not found", null));
        }

        switch (action)
        {
            case RecoveryAction.Delete:
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                _recoverableItems.TryRemove(item.Id, out _);
                return Task.FromResult(new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Completed, "Checkpoint deleted", null));

            case RecoveryAction.Skip:
                _recoverableItems.TryRemove(item.Id, out _);
                return Task.FromResult(new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Completed, "Checkpoint skipped", null));

            default:
                return Task.FromResult(new RecoveryActionResult(item.Id, action, false, item.State, "Unsupported action for checkpoint", null));
        }
    }

    private async Task<RecoveryActionResult> RecoverGeneratorJobAsync(RecoverableItem item, RecoveryAction action, CancellationToken ct)
    {
        var job = await _persistence.GetGeneratorJobAsync(item.Id, ct);
        if (job == null)
        {
            _recoverableItems.TryRemove(item.Id, out _);
            return new RecoveryActionResult(item.Id, action, false, item.State, "Job not found", null);
        }

        switch (action)
        {
            case RecoveryAction.Resume:
                // Mark job as ready to resume
                var resumedJob = job with { Status = GeneratorJobStatus.Recoverable, UpdatedAt = DateTimeOffset.UtcNow };
                await _persistence.UpdateGeneratorJobAsync(resumedJob, ct);
                return new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Recoverable, "Job marked for resume", null);

            case RecoveryAction.Delete:
                var cancelledJob = job with { Status = GeneratorJobStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
                await _persistence.UpdateGeneratorJobAsync(cancelledJob, ct);
                _recoverableItems.TryRemove(item.Id, out _);
                return new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Cancelled, "Job cancelled", null);

            case RecoveryAction.Skip:
                var failedJob = job with { Status = GeneratorJobStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow };
                await _persistence.UpdateGeneratorJobAsync(failedJob, ct);
                _recoverableItems.TryRemove(item.Id, out _);
                return new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Failed, "Job marked as failed", null);

            default:
                return new RecoveryActionResult(item.Id, action, false, item.State, "Unsupported action", null);
        }
    }

    private Task<RecoveryActionResult> RecoverPartialWriteAsync(RecoverableItem item, RecoveryAction action, CancellationToken ct)
    {
        if (item.Metadata == null || !item.Metadata.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return Task.FromResult(new RecoveryActionResult(item.Id, action, false, item.State, "File path not found", null));
        }

        switch (action)
        {
            case RecoveryAction.Resume:
                // For backup files, restore them
                if (path.EndsWith(".bak") && item.Metadata.TryGetValue("originalPath", out var origObj) && origObj is string origPath)
                {
                    if (File.Exists(path))
                    {
                        File.Move(path, origPath, overwrite: true);
                    }
                    _recoverableItems.TryRemove(item.Id, out _);
                    return Task.FromResult(new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Completed, "Backup restored", null));
                }
                return Task.FromResult(new RecoveryActionResult(item.Id, action, false, item.State, "Cannot resume this partial write", null));

            case RecoveryAction.Delete:
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                _recoverableItems.TryRemove(item.Id, out _);
                return Task.FromResult(new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Completed, "Partial file deleted", null));

            case RecoveryAction.Skip:
                _recoverableItems.TryRemove(item.Id, out _);
                return Task.FromResult(new RecoveryActionResult(item.Id, action, true, RecoverableItemState.Completed, "Partial file skipped", null));

            default:
                return Task.FromResult(new RecoveryActionResult(item.Id, action, false, item.State, "Unsupported action", null));
        }
    }

    #endregion

    #region Helpers

    private string GetCrashMarkerPath() => Path.Combine(_config.DataDirectory, ".crash_marker");

    private async Task<StoredEventInfo?> GetLastEventAsync(string sessionFile, CancellationToken ct)
    {
        StoredEventInfo? lastEvent = null;

        await foreach (var line in File.ReadLinesAsync(sessionFile, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var eventType = root.GetProperty("eventType").GetString() ?? "";
                var timestamp = DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!);

                lastEvent = new StoredEventInfo(eventType, timestamp);
            }
            catch
            {
                // Skip malformed events
            }
        }

        return lastEvent;
    }

    private static RecoverableItemState DetermineSessionState(StoredEventInfo lastEvent)
    {
        return lastEvent.Type switch
        {
            "tool.started" => RecoverableItemState.Unknown, // Tool might have side effects
            "permission.requested" => RecoverableItemState.Recoverable, // Can re-prompt
            "message.delta" => RecoverableItemState.Recoverable, // Can re-stream
            "thought.delta" => RecoverableItemState.Recoverable,
            _ => RecoverableItemState.Unknown
        };
    }

    private static IReadOnlyList<RecoveryAction> GetActionsForState(RecoverableItemState state, RecoverableItemType type)
    {
        return state switch
        {
            RecoverableItemState.Recoverable => type == RecoverableItemType.Session
                ? new[] { RecoveryAction.Delete, RecoveryAction.Skip }
                : new[] { RecoveryAction.Resume, RecoveryAction.Delete, RecoveryAction.Skip },
            RecoverableItemState.Unknown => new[] { RecoveryAction.Delete, RecoveryAction.Skip },
            _ => new[] { RecoveryAction.Skip }
        };
    }

    private sealed record StoredEventInfo(string Type, DateTimeOffset Timestamp);

    private sealed class CrashMarker
    {
        public int Pid { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public string ServiceVersion { get; set; } = "";
    }

    #endregion
}
