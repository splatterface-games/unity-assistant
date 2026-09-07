// Context Indexer Interface

using Splatter.Protocol;

namespace Splatter.Service.Context;

public interface IContextIndexer
{
    /// <summary>
    /// Get the current status of all indexes.
    /// </summary>
    Task<IReadOnlyList<IndexFreshness>> GetIndexStatusAsync(string workspaceId, CancellationToken ct);

    /// <summary>
    /// Start a refresh of the specified index type.
    /// </summary>
    Task<bool> RefreshIndexAsync(string workspaceId, string? indexType, bool fullRebuild, CancellationToken ct);

    /// <summary>
    /// Get file inventory with optional filters.
    /// </summary>
    Task<(IReadOnlyList<FileInventoryEntry> Files, bool Truncated, int TotalCount)> GetFileInventoryAsync(
        string workspaceId, string? path, string? pattern, int maxDepth, bool includeHidden, CancellationToken ct);

    /// <summary>
    /// Search for text in indexed files.
    /// </summary>
    Task<(IReadOnlyList<LexicalSearchResult> Results, IndexFreshness Freshness)> SearchTextAsync(
        string workspaceId, string query, string? scope, int maxResults, bool includeContent, CancellationToken ct);

    /// <summary>
    /// Get asset inventory with optional filters.
    /// </summary>
    Task<(IReadOnlyList<AssetInventoryEntry> Assets, bool Truncated, int TotalCount)> GetAssetInventoryAsync(
        string workspaceId, string? query, string? type, IReadOnlyList<string>? labels, string? path, int maxResults, CancellationToken ct);

    /// <summary>
    /// Get scene snapshot.
    /// </summary>
    Task<SceneSnapshot?> GetSceneSnapshotAsync(string workspaceId, string? scenePath, int maxDepth, CancellationToken ct);

    /// <summary>
    /// Notify that files have changed (for incremental refresh).
    /// </summary>
    Task NotifyFilesChangedAsync(string workspaceId, IReadOnlyList<(string Path, FileChangeKind Change)> changes, CancellationToken ct);
}
