// Dependency Graph Interface

using Splatter.Protocol;

namespace Splatter.Service.Context;

public interface IDependencyGraph
{
    /// <summary>
    /// Query the dependency graph.
    /// </summary>
    Task<GraphQueryResult> QueryAsync(string workspaceId, GraphQuery query, CancellationToken ct);

    /// <summary>
    /// Get direct dependents of a node.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> GetDependentsAsync(string workspaceId, string nodeId, int maxDepth, CancellationToken ct);

    /// <summary>
    /// Get direct dependencies of a node.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> GetDependenciesAsync(string workspaceId, string nodeId, int maxDepth, CancellationToken ct);

    /// <summary>
    /// Refresh the dependency graph.
    /// </summary>
    Task<bool> RefreshAsync(string workspaceId, string? scope, bool fullRebuild, CancellationToken ct);

    /// <summary>
    /// Get a specific node by ID.
    /// </summary>
    Task<GraphNode?> GetNodeAsync(string workspaceId, string nodeId, CancellationToken ct);

    /// <summary>
    /// Find nodes by path or GUID.
    /// </summary>
    Task<IReadOnlyList<GraphNode>> FindNodesAsync(string workspaceId, string? path, string? guid, GraphNodeType? nodeType, CancellationToken ct);

    /// <summary>
    /// Notify that assets have changed (for incremental refresh).
    /// </summary>
    Task NotifyAssetsChangedAsync(string workspaceId, IReadOnlyList<string> assetGuids, CancellationToken ct);
}
