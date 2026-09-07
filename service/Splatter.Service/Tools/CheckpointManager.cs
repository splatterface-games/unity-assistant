// Checkpoint Manager

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Storage;

namespace Splatter.Service.Tools;

/// <summary>
/// Manages checkpoints for workspace file safety.
/// </summary>
public interface ICheckpointManager
{
    Task<Checkpoint> CreateAsync(string workspaceId, string? sessionId, string? turnId, IReadOnlyList<string> paths, string? description, CancellationToken ct);
    Task<RestorePlan> PreviewRestoreAsync(string checkpointId, CancellationToken ct);
    Task<bool> RestoreAsync(string checkpointId, CancellationToken ct);
}

public sealed class CheckpointManager : ICheckpointManager
{
    private readonly ILogger<CheckpointManager> _logger;
    private readonly IPersistenceStore _persistence;
    private readonly ServiceConfiguration _config;

    public CheckpointManager(
        ILogger<CheckpointManager> logger,
        IPersistenceStore persistence,
        ServiceConfiguration config)
    {
        _logger = logger;
        _persistence = persistence;
        _config = config;
    }

    public async Task<Checkpoint> CreateAsync(string workspaceId, string? sessionId, string? turnId, IReadOnlyList<string> paths, string? description, CancellationToken ct)
    {
        // Determine checkpoint provider
        var providerType = await DetermineProviderTypeAsync(workspaceId, ct);
        string reference;

        if (providerType == CheckpointProviderType.Git)
        {
            reference = await CreateGitCheckpointAsync(workspaceId, paths, description, ct);
        }
        else
        {
            reference = await CreateSnapshotCheckpointAsync(workspaceId, paths, ct);
        }

        var checkpoint = new Checkpoint(
            CheckpointId.New(),
            workspaceId,
            sessionId,
            turnId,
            null,
            providerType,
            reference,
            paths,
            DateTimeOffset.UtcNow,
            description);

        await _persistence.CreateCheckpointAsync(checkpoint, ct);
        _logger.LogInformation("Created checkpoint {CheckpointId} using {Provider}", checkpoint.Id, providerType);

        return checkpoint;
    }

    public async Task<RestorePlan> PreviewRestoreAsync(string checkpointId, CancellationToken ct)
    {
        var checkpoint = await _persistence.GetCheckpointAsync(checkpointId, ct);
        if (checkpoint == null)
        {
            return new RestorePlan(checkpointId, Array.Empty<FileRestoreInfo>(),
                new[] { new RestoreConflict("", "Checkpoint not found", false) }, false);
        }

        var filesToRestore = new List<FileRestoreInfo>();
        var conflicts = new List<RestoreConflict>();

        foreach (var path in checkpoint.Paths)
        {
            // Check if file has been modified since checkpoint
            // This would need workspace path resolution
            filesToRestore.Add(new FileRestoreInfo(path, "restore", null));
        }

        return new RestorePlan(checkpointId, filesToRestore, conflicts, conflicts.Count == 0);
    }

    public async Task<bool> RestoreAsync(string checkpointId, CancellationToken ct)
    {
        var checkpoint = await _persistence.GetCheckpointAsync(checkpointId, ct);
        if (checkpoint == null)
        {
            _logger.LogWarning("Checkpoint {CheckpointId} not found for restore", checkpointId);
            return false;
        }

        try
        {
            if (checkpoint.ProviderType == CheckpointProviderType.Git)
            {
                return await RestoreGitCheckpointAsync(checkpoint, ct);
            }
            else
            {
                return await RestoreSnapshotCheckpointAsync(checkpoint, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore checkpoint {CheckpointId}", checkpointId);
            return false;
        }
    }

    private async Task<CheckpointProviderType> DetermineProviderTypeAsync(string workspaceId, CancellationToken ct)
    {
        // Check if workspace is a git repository
        // This would need workspace path resolution
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --is-inside-work-tree")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0)
                {
                    return CheckpointProviderType.Git;
                }
            }
        }
        catch
        {
            // Git not available
        }

        return CheckpointProviderType.Snapshot;
    }

    private async Task<string> CreateGitCheckpointAsync(string workspaceId, IReadOnlyList<string> paths, string? description, CancellationToken ct)
    {
        // Create a git stash or commit
        var commitMessage = $"Splatter checkpoint: {description ?? "Before agent changes"}";

        // Stage files
        var addArgs = string.Join(" ", paths.Select(p => $"\"{p}\""));
        await RunGitAsync($"add {addArgs}", ct);

        // Create commit
        await RunGitAsync($"commit -m \"{commitMessage}\" --allow-empty", ct);

        // Get commit hash
        var hash = await RunGitAsync("rev-parse HEAD", ct);
        return hash.Trim();
    }

    private async Task<string> CreateSnapshotCheckpointAsync(string workspaceId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshotDir = Path.Combine(_config.DataDirectory, "checkpoints", snapshotId);
        Directory.CreateDirectory(snapshotDir);

        var projectRoot = _config.ProjectRoot ?? Directory.GetCurrentDirectory();

        // Copy files to snapshot directory
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            // Resolve source path from workspace
            var sourcePath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(projectRoot, path);

            if (!File.Exists(sourcePath))
            {
                _logger.LogWarning("Cannot snapshot file - not found: {Path}", sourcePath);
                continue;
            }

            // Security: Ensure source is within project root
            var fullSourcePath = Path.GetFullPath(sourcePath);
            var fullProjectRoot = Path.GetFullPath(projectRoot);
            if (!fullSourcePath.StartsWith(fullProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Cannot snapshot file outside project: {Path}", path);
                continue;
            }

            // Compute relative path for snapshot storage
            var relativePath = Path.GetRelativePath(projectRoot, fullSourcePath);
            var destPath = Path.Combine(snapshotDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath);

            if (destDir != null)
            {
                Directory.CreateDirectory(destDir);
            }

            // Copy the file
            await using var sourceStream = File.OpenRead(sourcePath);
            await using var destStream = File.Create(destPath);
            await sourceStream.CopyToAsync(destStream, ct);

            _logger.LogDebug("Snapshot copied: {Source} -> {Dest}", relativePath, destPath);
        }

        _logger.LogInformation("Created snapshot {SnapshotId} with {Count} files", snapshotId, paths.Count);
        return snapshotId;
    }

    private async Task<bool> RestoreGitCheckpointAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        // Restore from git commit
        await RunGitAsync($"checkout {checkpoint.Reference} -- {string.Join(" ", checkpoint.Paths.Select(p => $"\"{p}\""))}", ct);
        return true;
    }

    private async Task<bool> RestoreSnapshotCheckpointAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        var snapshotDir = Path.Combine(_config.DataDirectory, "checkpoints", checkpoint.Reference);
        if (!Directory.Exists(snapshotDir))
        {
            _logger.LogWarning("Snapshot directory not found: {Dir}", snapshotDir);
            return false;
        }

        var projectRoot = _config.ProjectRoot ?? Directory.GetCurrentDirectory();
        var restoredCount = 0;

        // Copy files back from snapshot to workspace
        foreach (var path in checkpoint.Paths)
        {
            ct.ThrowIfCancellationRequested();

            // Compute relative path
            var relativePath = Path.IsPathRooted(path)
                ? Path.GetRelativePath(projectRoot, path)
                : path;

            var sourcePath = Path.Combine(snapshotDir, relativePath);
            if (!File.Exists(sourcePath))
            {
                _logger.LogWarning("Snapshot file not found: {Path}", sourcePath);
                continue;
            }

            var destPath = Path.Combine(projectRoot, relativePath);

            // Security: Ensure destination is within project root
            var fullDestPath = Path.GetFullPath(destPath);
            var fullProjectRoot = Path.GetFullPath(projectRoot);
            if (!fullDestPath.StartsWith(fullProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Cannot restore file outside project: {Path}", path);
                continue;
            }

            // Create destination directory if needed
            var destDir = Path.GetDirectoryName(fullDestPath);
            if (destDir != null && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // Copy the file back
            await using var sourceStream = File.OpenRead(sourcePath);
            await using var destStream = File.Create(fullDestPath);
            await sourceStream.CopyToAsync(destStream, ct);

            restoredCount++;
            _logger.LogDebug("Restored: {Path}", relativePath);
        }

        _logger.LogInformation("Restored {Count} files from snapshot {SnapshotId}", restoredCount, checkpoint.Reference);
        return restoredCount > 0;
    }

    private async Task<string> RunGitAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start git");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Git command failed: {error}");
        }

        return output;
    }
}
