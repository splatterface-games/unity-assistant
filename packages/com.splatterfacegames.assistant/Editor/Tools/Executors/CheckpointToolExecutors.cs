// Checkpoint Tool Executors - File checkpoint and restore operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Lists all available checkpoints.
    /// </summary>
    public class CheckpointListExecutor : IToolExecutor
    {
        public string ToolId => "checkpoint.list";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var checkpointsDir = GetCheckpointsDirectory();
                var checkpoints = new List<object>();

                if (Directory.Exists(checkpointsDir))
                {
                    foreach (var checkpointDir in Directory.GetDirectories(checkpointsDir))
                    {
                        var metadataPath = Path.Combine(checkpointDir, "metadata.json");
                        if (File.Exists(metadataPath))
                        {
                            try
                            {
                                var metadataJson = File.ReadAllText(metadataPath);
                                var metadata = JsonUtility.FromJson<CheckpointMetadata>(metadataJson);

                                if (metadata != null)
                                {
                                    checkpoints.Add(new
                                    {
                                        id = metadata.id,
                                        description = metadata.description,
                                        createdAt = metadata.createdAt,
                                        fileCount = metadata.files?.Length ?? 0,
                                        paths = metadata.files?.Select(f => f.originalPath).ToArray() ?? Array.Empty<string>()
                                    });
                                }
                            }
                            catch
                            {
                                // Skip malformed checkpoint metadata
                            }
                        }
                    }
                }

                // Sort by creation date descending (newest first)
                checkpoints = checkpoints
                    .OrderByDescending(c => ((dynamic)c).createdAt)
                    .ToList();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    checkpoints
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetCheckpointsDirectory()
        {
            return Path.Combine(Application.dataPath, "..", "Library", "SplatterAI", "Checkpoints");
        }
    }

    /// <summary>
    /// Creates a checkpoint of specified files.
    /// </summary>
    public class CheckpointCreateExecutor : IToolExecutor
    {
        public string ToolId => "checkpoint.create";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Parse paths argument
                var pathsArg = context.Arguments.TryGetValue("paths", out var p) ? p : null;
                var description = context.Arguments.TryGetValue("description", out var d) ? d?.ToString() : null;

                List<string> paths;
                if (pathsArg is IEnumerable<object> pathList)
                {
                    paths = pathList.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                }
                else if (pathsArg is string pathString)
                {
                    // Handle single path or comma-separated paths
                    paths = pathString.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                }
                else
                {
                    return Task.FromResult(ToolExecutionResult.Failed("paths argument is required (array of file paths)"));
                }

                if (paths.Count == 0)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("At least one path is required"));
                }

                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

                // Validate all paths are within project
                foreach (var path in paths)
                {
                    var fullPath = Path.IsPathRooted(path)
                        ? Path.GetFullPath(path)
                        : Path.GetFullPath(Path.Combine(projectRoot, path));

                    if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(ToolExecutionResult.Failed($"Path is outside project: {path}"));
                    }

                    if (!File.Exists(fullPath))
                    {
                        return Task.FromResult(ToolExecutionResult.Failed($"File not found: {path}"));
                    }
                }

                // Generate unique checkpoint ID
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var guidSuffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                var checkpointId = $"{timestamp}-{guidSuffix}";

                // Create checkpoint directory structure
                var checkpointsDir = GetCheckpointsDirectory();
                var checkpointDir = Path.Combine(checkpointsDir, checkpointId);
                var filesDir = Path.Combine(checkpointDir, "files");
                Directory.CreateDirectory(filesDir);

                var fileEntries = new List<CheckpointFileEntry>();
                var fileCount = 0;

                // Copy files to checkpoint
                foreach (var path in paths)
                {
                    var fullPath = Path.IsPathRooted(path)
                        ? Path.GetFullPath(path)
                        : Path.GetFullPath(Path.Combine(projectRoot, path));

                    // Compute relative path for storage
                    var relativePath = Path.GetRelativePath(projectRoot, fullPath);
                    var destPath = Path.Combine(filesDir, relativePath);
                    var destDir = Path.GetDirectoryName(destPath);

                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    File.Copy(fullPath, destPath, true);

                    fileEntries.Add(new CheckpointFileEntry
                    {
                        path = relativePath,
                        originalPath = relativePath
                    });

                    fileCount++;
                }

                // Create metadata file
                var metadata = new CheckpointMetadata
                {
                    id = checkpointId,
                    description = description ?? $"Checkpoint with {fileCount} files",
                    createdAt = DateTime.UtcNow.ToString("o"),
                    files = fileEntries.ToArray()
                };

                var metadataPath = Path.Combine(checkpointDir, "metadata.json");
                File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true));

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    checkpointId,
                    fileCount,
                    description = metadata.description,
                    paths = paths
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetCheckpointsDirectory()
        {
            return Path.Combine(Application.dataPath, "..", "Library", "SplatterAI", "Checkpoints");
        }
    }

    /// <summary>
    /// Restores files from a checkpoint.
    /// </summary>
    public class CheckpointRestoreExecutor : IToolExecutor
    {
        public string ToolId => "checkpoint.restore";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var checkpointId = context.Arguments.TryGetValue("checkpointId", out var id) ? id?.ToString() : null;
                var preview = true;

                if (context.Arguments.TryGetValue("preview", out var previewArg))
                {
                    if (previewArg is bool previewBool)
                    {
                        preview = previewBool;
                    }
                    else if (previewArg != null)
                    {
                        bool.TryParse(previewArg.ToString(), out preview);
                    }
                }

                if (string.IsNullOrEmpty(checkpointId))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("checkpointId is required"));
                }

                var checkpointsDir = GetCheckpointsDirectory();
                var checkpointDir = Path.Combine(checkpointsDir, checkpointId);
                var metadataPath = Path.Combine(checkpointDir, "metadata.json");
                var filesDir = Path.Combine(checkpointDir, "files");

                if (!File.Exists(metadataPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Checkpoint not found: {checkpointId}"));
                }

                var metadataJson = File.ReadAllText(metadataPath);
                var metadata = JsonUtility.FromJson<CheckpointMetadata>(metadataJson);

                if (metadata == null || metadata.files == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Invalid checkpoint metadata"));
                }

                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var plan = new RestorePlan();
                var affectedPaths = new List<string>();

                foreach (var fileEntry in metadata.files)
                {
                    var checkpointFilePath = Path.Combine(filesDir, fileEntry.path);
                    var targetPath = Path.Combine(projectRoot, fileEntry.originalPath);
                    var targetExists = File.Exists(targetPath);

                    var fileChange = new FileChangeInfo
                    {
                        path = fileEntry.originalPath,
                        action = targetExists ? "modify" : "create",
                        checkpointPath = fileEntry.path
                    };

                    // Check if file differs from checkpoint
                    if (targetExists && File.Exists(checkpointFilePath))
                    {
                        var currentContent = File.ReadAllBytes(targetPath);
                        var checkpointContent = File.ReadAllBytes(checkpointFilePath);

                        if (currentContent.SequenceEqual(checkpointContent))
                        {
                            fileChange.action = "unchanged";
                        }
                        else
                        {
                            fileChange.action = "modify";
                            fileChange.currentSize = currentContent.Length;
                            fileChange.checkpointSize = checkpointContent.Length;
                        }
                    }
                    else if (!targetExists)
                    {
                        fileChange.action = "create";
                    }

                    plan.files.Add(fileChange);

                    if (fileChange.action != "unchanged")
                    {
                        affectedPaths.Add(fileEntry.originalPath);
                    }
                }

                // If preview mode, just return the plan
                if (preview)
                {
                    return Task.FromResult(ToolExecutionResult.Succeeded(new
                    {
                        checkpointId,
                        preview = true,
                        restored = false,
                        plan = new
                        {
                            description = metadata.description,
                            createdAt = metadata.createdAt,
                            totalFiles = plan.files.Count,
                            filesToModify = plan.files.Count(f => f.action == "modify"),
                            filesToCreate = plan.files.Count(f => f.action == "create"),
                            unchangedFiles = plan.files.Count(f => f.action == "unchanged"),
                            files = plan.files.Select(f => new
                            {
                                path = f.path,
                                action = f.action,
                                currentSize = f.currentSize,
                                checkpointSize = f.checkpointSize
                            }).ToList()
                        }
                    }));
                }

                // Restore mode - actually copy the files back
                var restoredCount = 0;
                var errors = new List<string>();

                foreach (var fileEntry in metadata.files)
                {
                    try
                    {
                        var checkpointFilePath = Path.Combine(filesDir, fileEntry.path);
                        var targetPath = Path.Combine(projectRoot, fileEntry.originalPath);

                        if (!File.Exists(checkpointFilePath))
                        {
                            errors.Add($"Checkpoint file missing: {fileEntry.path}");
                            continue;
                        }

                        // Security check - ensure target is within project
                        var fullTargetPath = Path.GetFullPath(targetPath);
                        if (!fullTargetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Target path outside project: {fileEntry.originalPath}");
                            continue;
                        }

                        // Create directory if needed
                        var targetDir = Path.GetDirectoryName(fullTargetPath);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        File.Copy(checkpointFilePath, fullTargetPath, true);
                        restoredCount++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to restore {fileEntry.path}: {ex.Message}");
                    }
                }

                // Refresh AssetDatabase to pick up changes
                AssetDatabase.Refresh();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    checkpointId,
                    preview = false,
                    restored = true,
                    restoredCount,
                    totalFiles = metadata.files.Length,
                    errors = errors.Count > 0 ? errors : null
                }, affectedPaths));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetCheckpointsDirectory()
        {
            return Path.Combine(Application.dataPath, "..", "Library", "SplatterAI", "Checkpoints");
        }
    }

    #region Internal Data Structures

    /// <summary>
    /// Metadata stored with each checkpoint.
    /// </summary>
    [Serializable]
    internal class CheckpointMetadata
    {
        public string id;
        public string description;
        public string createdAt;
        public CheckpointFileEntry[] files;
    }

    /// <summary>
    /// Entry for each file in a checkpoint.
    /// </summary>
    [Serializable]
    internal class CheckpointFileEntry
    {
        public string path;
        public string originalPath;
    }

    /// <summary>
    /// Plan for restoring a checkpoint.
    /// </summary>
    internal class RestorePlan
    {
        public List<FileChangeInfo> files = new List<FileChangeInfo>();
    }

    /// <summary>
    /// Information about a file change during restore.
    /// </summary>
    internal class FileChangeInfo
    {
        public string path;
        public string action; // "create", "modify", "unchanged"
        public string checkpointPath;
        public long currentSize;
        public long checkpointSize;
    }

    #endregion
}
