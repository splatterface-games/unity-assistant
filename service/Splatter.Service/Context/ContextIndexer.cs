// Context Indexer Implementation

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Context;

public sealed class ContextIndexer : IContextIndexer
{
    private readonly ILogger<ContextIndexer> _logger;
    private readonly ServiceConfiguration _config;
    private readonly ConcurrentDictionary<string, WorkspaceIndex> _indexes = new();

    private const int MaxFileSize = 10 * 1024 * 1024; // 10 MiB
    private const int MaxSearchResults = 1000;
    private const int SchemaVersion = 1;

    // File patterns to exclude
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Temp", "Logs", "obj", "bin", ".git", ".vs", ".idea",
        "Build", "Builds", "node_modules", "__pycache__"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".meta", ".asset", ".prefab", ".unity", ".anim", ".controller",
        ".dll", ".exe", ".so", ".dylib", ".pdb", ".mdb",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd", ".tif", ".tiff",
        ".wav", ".mp3", ".ogg", ".aiff", ".flac",
        ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max",
        ".zip", ".rar", ".7z", ".tar", ".gz"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".so", ".dylib", ".pdb", ".mdb",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tga", ".psd", ".tif", ".tiff",
        ".wav", ".mp3", ".ogg", ".aiff", ".flac",
        ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max",
        ".zip", ".rar", ".7z", ".tar", ".gz",
        ".asset", ".prefab", ".unity"
    };

    public ContextIndexer(ILogger<ContextIndexer> logger, ServiceConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task<IReadOnlyList<IndexFreshness>> GetIndexStatusAsync(string workspaceId, CancellationToken ct)
    {
        var index = GetOrCreateWorkspaceIndex(workspaceId);
        var statuses = new List<IndexFreshness>
        {
            new(
                "file",
                index.FileIndexState,
                index.FileInventory.Count,
                index.PendingFileChanges.Count,
                index.LastFullScan,
                index.LastIncrementalRefresh,
                SchemaVersion,
                index.StalePaths.Take(100).ToList()),
            new(
                "lexical",
                index.LexicalIndexState,
                index.LexicalIndex.Count,
                0,
                index.LastFullScan,
                index.LastIncrementalRefresh,
                SchemaVersion,
                null),
            new(
                "asset",
                index.AssetIndexState,
                index.AssetInventory.Count,
                0,
                index.LastAssetScan,
                null,
                SchemaVersion,
                null)
        };

        return Task.FromResult<IReadOnlyList<IndexFreshness>>(statuses);
    }

    public async Task<bool> RefreshIndexAsync(string workspaceId, string? indexType, bool fullRebuild, CancellationToken ct)
    {
        var index = GetOrCreateWorkspaceIndex(workspaceId);

        if (fullRebuild || index.FileIndexState == IndexState.Missing)
        {
            // Full rebuild
            index.FileIndexState = IndexState.Building;
            index.LexicalIndexState = IndexState.Building;

            try
            {
                await FullScanAsync(index, ct);
                index.FileIndexState = IndexState.Fresh;
                index.LexicalIndexState = IndexState.Fresh;
                index.LastFullScan = DateTimeOffset.UtcNow;
                _logger.LogInformation("Full index rebuild completed for workspace {WorkspaceId}", workspaceId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full index rebuild failed for workspace {WorkspaceId}", workspaceId);
                index.FileIndexState = IndexState.Partial;
                index.LexicalIndexState = IndexState.Partial;
                return false;
            }
        }
        else
        {
            // Incremental refresh
            index.FileIndexState = IndexState.Refreshing;
            index.LexicalIndexState = IndexState.Refreshing;

            try
            {
                await IncrementalRefreshAsync(index, ct);
                index.FileIndexState = IndexState.Fresh;
                index.LexicalIndexState = IndexState.Fresh;
                index.LastIncrementalRefresh = DateTimeOffset.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Incremental refresh failed for workspace {WorkspaceId}", workspaceId);
                index.FileIndexState = IndexState.Stale;
                index.LexicalIndexState = IndexState.Stale;
                return false;
            }
        }
    }

    public async Task<(IReadOnlyList<FileInventoryEntry> Files, bool Truncated, int TotalCount)> GetFileInventoryAsync(
        string workspaceId, string? path, string? pattern, int maxDepth, bool includeHidden, CancellationToken ct)
    {
        var index = GetOrCreateWorkspaceIndex(workspaceId);

        // Ensure index exists
        if (index.FileIndexState == IndexState.Missing)
        {
            await RefreshIndexAsync(workspaceId, "file", true, ct);
        }

        var query = index.FileInventory.Values.AsEnumerable();

        // Filter by path
        if (!string.IsNullOrEmpty(path))
        {
            query = query.Where(f => f.RelativePath.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by pattern (glob)
        if (!string.IsNullOrEmpty(pattern))
        {
            var regex = GlobToRegex(pattern);
            query = query.Where(f => regex.IsMatch(f.RelativePath));
        }

        // Filter by depth
        if (maxDepth > 0)
        {
            query = query.Where(f => f.RelativePath.Count(c => c == '/' || c == '\\') <= maxDepth);
        }

        // Filter hidden files
        if (!includeHidden)
        {
            query = query.Where(f => !f.RelativePath.Split('/', '\\').Any(p => p.StartsWith(".")));
        }

        var all = query.OrderBy(f => f.RelativePath).ToList();
        var truncated = all.Count > 1000;
        var result = all.Take(1000).ToList();

        return (result, truncated, all.Count);
    }

    public async Task<(IReadOnlyList<LexicalSearchResult> Results, IndexFreshness Freshness)> SearchTextAsync(
        string workspaceId, string query, string? scope, int maxResults, bool includeContent, CancellationToken ct)
    {
        var index = GetOrCreateWorkspaceIndex(workspaceId);

        // Ensure index exists
        if (index.LexicalIndexState == IndexState.Missing)
        {
            await RefreshIndexAsync(workspaceId, "lexical", true, ct);
        }

        var results = new List<LexicalSearchResult>();
        var freshness = new IndexFreshness(
            "lexical",
            index.LexicalIndexState,
            index.LexicalIndex.Count,
            0,
            index.LastFullScan,
            index.LastIncrementalRefresh,
            SchemaVersion,
            null);

        try
        {
            var regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));

            foreach (var (path, lines) in index.LexicalIndex)
            {
                if (ct.IsCancellationRequested) break;
                if (results.Count >= maxResults) break;

                // Apply scope filter
                if (!string.IsNullOrEmpty(scope) && !path.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
                    continue;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var match = regex.Match(line);

                    if (match.Success)
                    {
                        var contextBefore = includeContent && i > 0
                            ? lines.Skip(Math.Max(0, i - 2)).Take(Math.Min(i, 2)).ToList()
                            : null;
                        var contextAfter = includeContent && i < lines.Count - 1
                            ? lines.Skip(i + 1).Take(2).ToList()
                            : null;

                        results.Add(new LexicalSearchResult(
                            path,
                            i + 1,
                            match.Index + 1,
                            match.Index + match.Length,
                            line,
                            contextBefore,
                            contextAfter,
                            1.0));

                        if (results.Count >= maxResults) break;
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogWarning("Search query timed out: {Query}", query);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid search pattern: {Query}", query);
        }

        return (results, freshness);
    }

    public Task<(IReadOnlyList<AssetInventoryEntry> Assets, bool Truncated, int TotalCount)> GetAssetInventoryAsync(
        string workspaceId, string? query, string? type, IReadOnlyList<string>? labels, string? path, int maxResults, CancellationToken ct)
    {
        var index = GetOrCreateWorkspaceIndex(workspaceId);

        var assets = index.AssetInventory.Values.AsEnumerable();

        // Filter by query (name match)
        if (!string.IsNullOrEmpty(query))
        {
            assets = assets.Where(a =>
                a.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by type
        if (!string.IsNullOrEmpty(type))
        {
            assets = assets.Where(a => a.MainType.Equals(type, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by labels
        if (labels?.Count > 0)
        {
            assets = assets.Where(a => a.Labels != null && labels.All(l => a.Labels.Contains(l)));
        }

        // Filter by path
        if (!string.IsNullOrEmpty(path))
        {
            assets = assets.Where(a => a.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        }

        var all = assets.OrderBy(a => a.Path).ToList();
        var truncated = all.Count > maxResults;
        var result = all.Take(maxResults).ToList();

        return Task.FromResult<(IReadOnlyList<AssetInventoryEntry>, bool, int)>((result, truncated, all.Count));
    }

    public Task<SceneSnapshot?> GetSceneSnapshotAsync(string workspaceId, string? scenePath, int maxDepth, CancellationToken ct)
    {
        var index = GetOrCreateWorkspaceIndex(workspaceId);

        // Scene snapshots are typically populated from Unity side
        // Return cached snapshot if available
        if (scenePath != null && index.SceneSnapshots.TryGetValue(scenePath, out var snapshot))
        {
            return Task.FromResult<SceneSnapshot?>(snapshot);
        }

        // Return first available snapshot if no specific scene requested
        if (scenePath == null && index.SceneSnapshots.Count > 0)
        {
            return Task.FromResult<SceneSnapshot?>(index.SceneSnapshots.Values.First());
        }

        return Task.FromResult<SceneSnapshot?>(null);
    }

    public Task NotifyFilesChangedAsync(string workspaceId, IReadOnlyList<(string Path, FileChangeKind Change)> changes, CancellationToken ct)
    {
        var index = GetOrCreateWorkspaceIndex(workspaceId);

        foreach (var (path, change) in changes)
        {
            index.PendingFileChanges.Enqueue((path, change));
            index.StalePaths.Add(path);
        }

        // Mark as stale if we have pending changes
        if (index.FileIndexState == IndexState.Fresh && index.PendingFileChanges.Count > 0)
        {
            index.FileIndexState = IndexState.Stale;
            index.LexicalIndexState = IndexState.Stale;
        }

        return Task.CompletedTask;
    }

    #region Internal Methods

    private WorkspaceIndex GetOrCreateWorkspaceIndex(string workspaceId)
    {
        return _indexes.GetOrAdd(workspaceId, _ => new WorkspaceIndex
        {
            WorkspaceId = workspaceId,
            FileIndexState = IndexState.Missing,
            LexicalIndexState = IndexState.Missing,
            AssetIndexState = IndexState.Missing
        });
    }

    private async Task FullScanAsync(WorkspaceIndex index, CancellationToken ct)
    {
        index.FileInventory.Clear();
        index.LexicalIndex.Clear();
        index.StalePaths.Clear();
        while (index.PendingFileChanges.TryDequeue(out _)) { }

        // Get workspace root from configuration
        var workspaceRoot = Path.Combine(_config.DataDirectory, "Projects", index.WorkspaceId, "project");
        if (!Directory.Exists(workspaceRoot))
        {
            // Try to use project path from attached workspace info
            workspaceRoot = index.ProjectRoot ?? workspaceRoot;
        }

        if (!Directory.Exists(workspaceRoot))
        {
            _logger.LogWarning("Workspace root not found: {Path}", workspaceRoot);
            return;
        }

        await ScanDirectoryAsync(index, workspaceRoot, "", ct);
    }

    private async Task ScanDirectoryAsync(WorkspaceIndex index, string baseDir, string relativePath, CancellationToken ct)
    {
        var fullPath = string.IsNullOrEmpty(relativePath) ? baseDir : Path.Combine(baseDir, relativePath);

        if (!Directory.Exists(fullPath)) return;

        // Check if directory should be excluded
        var dirName = Path.GetFileName(fullPath);
        if (ExcludedDirectories.Contains(dirName)) return;

        try
        {
            // Scan files
            foreach (var file in Directory.GetFiles(fullPath))
            {
                if (ct.IsCancellationRequested) return;

                var fileRelativePath = string.IsNullOrEmpty(relativePath)
                    ? Path.GetFileName(file)
                    : Path.Combine(relativePath, Path.GetFileName(file));

                await IndexFileAsync(index, baseDir, fileRelativePath, ct);
            }

            // Recurse into subdirectories
            foreach (var dir in Directory.GetDirectories(fullPath))
            {
                if (ct.IsCancellationRequested) return;

                var subRelativePath = string.IsNullOrEmpty(relativePath)
                    ? Path.GetFileName(dir)
                    : Path.Combine(relativePath, Path.GetFileName(dir));

                await ScanDirectoryAsync(index, baseDir, subRelativePath, ct);
            }
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Access denied to directory: {Path}", fullPath);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "IO error scanning directory: {Path}", fullPath);
        }
    }

    private async Task IndexFileAsync(WorkspaceIndex index, string baseDir, string relativePath, CancellationToken ct)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        var ext = Path.GetExtension(relativePath);

        // Skip excluded extensions for lexical indexing
        var shouldIndexContent = !ExcludedExtensions.Contains(ext);
        var isBinary = BinaryExtensions.Contains(ext);

        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists) return;
            if (fileInfo.Length > MaxFileSize) return; // Skip very large files

            string? contentHash = null;
            int? lineCount = null;
            var isGenerated = IsGeneratedFile(relativePath);

            if (shouldIndexContent && !isBinary && fileInfo.Length < MaxFileSize)
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                var lines = content.Split('\n');
                lineCount = lines.Length;

                // Compute hash
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                contentHash = Convert.ToBase64String(hash)[..16];

                // Add to lexical index
                index.LexicalIndex[relativePath] = lines.ToList();
            }

            var entry = new FileInventoryEntry(
                fullPath,
                relativePath.Replace('\\', '/'),
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                contentHash,
                ext,
                isBinary,
                isGenerated,
                lineCount);

            index.FileInventory[relativePath] = entry;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to index file: {Path}", relativePath);
        }
    }

    private async Task IncrementalRefreshAsync(WorkspaceIndex index, CancellationToken ct)
    {
        var baseDir = index.ProjectRoot;
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            // Fall back to full scan
            await FullScanAsync(index, ct);
            return;
        }

        var processedPaths = new HashSet<string>();

        while (index.PendingFileChanges.TryDequeue(out var change))
        {
            if (ct.IsCancellationRequested) break;
            if (processedPaths.Contains(change.Path)) continue;
            processedPaths.Add(change.Path);

            switch (change.Change)
            {
                case FileChangeKind.Created:
                case FileChangeKind.Modified:
                    await IndexFileAsync(index, baseDir, change.Path, ct);
                    break;

                case FileChangeKind.Deleted:
                    index.FileInventory.TryRemove(change.Path, out _);
                    index.LexicalIndex.TryRemove(change.Path, out _);
                    break;

                case FileChangeKind.Moved:
                    // Move events only provide the new path; re-index at new location (old entry cleanup handled separately)
                    await IndexFileAsync(index, baseDir, change.Path, ct);
                    break;
            }

            index.StalePaths.Remove(change.Path);
        }
    }

    private static bool IsGeneratedFile(string path)
    {
        return path.Contains("Generated", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(".g.", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/\\\\]*")
            .Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    #endregion

    #region Internal Types

    private class WorkspaceIndex
    {
        public required string WorkspaceId { get; init; }
        public string? ProjectRoot { get; set; }

        public IndexState FileIndexState { get; set; }
        public IndexState LexicalIndexState { get; set; }
        public IndexState AssetIndexState { get; set; }

        public DateTimeOffset? LastFullScan { get; set; }
        public DateTimeOffset? LastIncrementalRefresh { get; set; }
        public DateTimeOffset? LastAssetScan { get; set; }

        public ConcurrentDictionary<string, FileInventoryEntry> FileInventory { get; } = new();
        public ConcurrentDictionary<string, List<string>> LexicalIndex { get; } = new();
        public ConcurrentDictionary<string, AssetInventoryEntry> AssetInventory { get; } = new();
        public ConcurrentDictionary<string, SceneSnapshot> SceneSnapshots { get; } = new();

        public ConcurrentQueue<(string Path, FileChangeKind Change)> PendingFileChanges { get; } = new();
        public HashSet<string> StalePaths { get; } = new();
    }

    #endregion
}
