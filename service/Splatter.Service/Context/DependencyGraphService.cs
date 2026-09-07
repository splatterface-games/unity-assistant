// Dependency Graph Implementation

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Context;

public sealed class DependencyGraphService : IDependencyGraph
{
    private readonly ILogger<DependencyGraphService> _logger;
    private readonly ConcurrentDictionary<string, WorkspaceGraph> _graphs = new();

    public DependencyGraphService(ILogger<DependencyGraphService> logger)
    {
        _logger = logger;
    }

    public Task<GraphQueryResult> QueryAsync(string workspaceId, GraphQuery query, CancellationToken ct)
    {
        var graph = GetOrCreateGraph(workspaceId);

        var resultNodes = new List<GraphNode>();
        var resultEdges = new List<GraphEdge>();
        var visited = new HashSet<string>();
        var paths = new List<List<string>>();

        if (!string.IsNullOrEmpty(query.RootNodeId))
        {
            // Traverse from root
            if (graph.Nodes.TryGetValue(query.RootNodeId, out var rootNode))
            {
                TraverseGraph(
                    graph,
                    rootNode,
                    query.Direction,
                    query.MaxDepth,
                    query.EdgeTypes,
                    query.NodeTypes,
                    resultNodes,
                    resultEdges,
                    visited,
                    new List<string>(),
                    paths,
                    ct);
            }
        }
        else
        {
            // Return filtered subset of entire graph
            foreach (var node in graph.Nodes.Values)
            {
                if (ct.IsCancellationRequested) break;
                if (query.NodeTypes?.Count > 0 && !query.NodeTypes.Contains(node.NodeType)) continue;
                if (resultNodes.Count >= 1000) break;
                resultNodes.Add(node);
            }

            foreach (var edge in graph.Edges)
            {
                if (ct.IsCancellationRequested) break;
                if (query.EdgeTypes?.Count > 0 && !query.EdgeTypes.Contains(edge.EdgeType)) continue;
                if (!visited.Contains(edge.SourceId) || !visited.Contains(edge.TargetId)) continue;
                resultEdges.Add(edge);
            }
        }

        var result = new GraphQueryResult(
            new DependencyGraph(resultNodes, resultEdges, graph.GeneratedAt, workspaceId),
            paths.Count > 0 ? paths.Select(p => (IReadOnlyList<string>)p).ToList() : null,
            resultNodes.Count >= 1000,
            resultNodes.Count);

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<GraphNode>> GetDependentsAsync(string workspaceId, string nodeId, int maxDepth, CancellationToken ct)
    {
        var graph = GetOrCreateGraph(workspaceId);
        var result = new List<GraphNode>();
        var visited = new HashSet<string> { nodeId };

        CollectDependents(graph, nodeId, maxDepth, 0, result, visited, ct);

        return Task.FromResult<IReadOnlyList<GraphNode>>(result);
    }

    public Task<IReadOnlyList<GraphNode>> GetDependenciesAsync(string workspaceId, string nodeId, int maxDepth, CancellationToken ct)
    {
        var graph = GetOrCreateGraph(workspaceId);
        var result = new List<GraphNode>();
        var visited = new HashSet<string> { nodeId };

        CollectDependencies(graph, nodeId, maxDepth, 0, result, visited, ct);

        return Task.FromResult<IReadOnlyList<GraphNode>>(result);
    }

    public Task<bool> RefreshAsync(string workspaceId, string? scope, bool fullRebuild, CancellationToken ct)
    {
        var graph = GetOrCreateGraph(workspaceId);

        if (fullRebuild)
        {
            graph.Nodes.Clear();
            graph.Edges.Clear();
            graph.OutgoingEdges.Clear();
            graph.IncomingEdges.Clear();
        }

        // The service cannot access Unity's asset database directly (two-process architecture).
        // Graph data is populated by Unity calling BuildFromAssetData().
        // RefreshAsync clears stale data and marks ready for new data from Unity.
        graph.GeneratedAt = DateTimeOffset.UtcNow;

        _logger.LogDebug("Refreshed dependency graph for workspace {WorkspaceId}", workspaceId);
        return Task.FromResult(true);
    }

    public Task<GraphNode?> GetNodeAsync(string workspaceId, string nodeId, CancellationToken ct)
    {
        var graph = GetOrCreateGraph(workspaceId);
        graph.Nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    public Task<IReadOnlyList<GraphNode>> FindNodesAsync(string workspaceId, string? path, string? guid, GraphNodeType? nodeType, CancellationToken ct)
    {
        var graph = GetOrCreateGraph(workspaceId);
        var results = graph.Nodes.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(path))
        {
            results = results.Where(n => n.Path?.Contains(path, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrEmpty(guid))
        {
            results = results.Where(n => n.Guid?.Equals(guid, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (nodeType.HasValue)
        {
            results = results.Where(n => n.NodeType == nodeType.Value);
        }

        return Task.FromResult<IReadOnlyList<GraphNode>>(results.Take(100).ToList());
    }

    public Task NotifyAssetsChangedAsync(string workspaceId, IReadOnlyList<string> assetGuids, CancellationToken ct)
    {
        var graph = GetOrCreateGraph(workspaceId);

        foreach (var guid in assetGuids)
        {
            graph.DirtyGuids.Add(guid);
        }

        return Task.CompletedTask;
    }

    #region Graph Manipulation

    /// <summary>
    /// Add a node to the graph (called from Unity side).
    /// </summary>
    public void AddNode(string workspaceId, GraphNode node)
    {
        var graph = GetOrCreateGraph(workspaceId);
        graph.Nodes[node.Id] = node;
    }

    /// <summary>
    /// Add an edge to the graph (called from Unity side).
    /// </summary>
    public void AddEdge(string workspaceId, GraphEdge edge)
    {
        var graph = GetOrCreateGraph(workspaceId);

        // Add to main edge list
        lock (graph.Edges)
        {
            graph.Edges.Add(edge);
        }

        // Update adjacency lists
        graph.OutgoingEdges.AddOrUpdate(
            edge.SourceId,
            _ => new List<GraphEdge> { edge },
            (_, list) => { lock (list) { list.Add(edge); } return list; });

        graph.IncomingEdges.AddOrUpdate(
            edge.TargetId,
            _ => new List<GraphEdge> { edge },
            (_, list) => { lock (list) { list.Add(edge); } return list; });
    }

    /// <summary>
    /// Build the graph from Unity asset data.
    /// </summary>
    public void BuildFromAssetData(string workspaceId, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var graph = GetOrCreateGraph(workspaceId);

        graph.Nodes.Clear();
        graph.Edges.Clear();
        graph.OutgoingEdges.Clear();
        graph.IncomingEdges.Clear();
        graph.DirtyGuids.Clear();

        foreach (var node in nodes)
        {
            graph.Nodes[node.Id] = node;
        }

        foreach (var edge in edges)
        {
            AddEdge(workspaceId, edge);
        }

        graph.GeneratedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Built dependency graph with {NodeCount} nodes and {EdgeCount} edges",
            nodes.Count, edges.Count);
    }

    #endregion

    #region Helper Methods

    private WorkspaceGraph GetOrCreateGraph(string workspaceId)
    {
        return _graphs.GetOrAdd(workspaceId, _ => new WorkspaceGraph());
    }

    private void TraverseGraph(
        WorkspaceGraph graph,
        GraphNode startNode,
        string direction,
        int maxDepth,
        IReadOnlyList<GraphEdgeType>? edgeTypes,
        IReadOnlyList<GraphNodeType>? nodeTypes,
        List<GraphNode> resultNodes,
        List<GraphEdge> resultEdges,
        HashSet<string> visited,
        List<string> currentPath,
        List<List<string>> paths,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (currentPath.Count > maxDepth) return;
        if (resultNodes.Count >= 1000) return;
        if (!visited.Add(startNode.Id)) return;

        // Check node type filter
        if (nodeTypes?.Count > 0 && !nodeTypes.Contains(startNode.NodeType))
        {
            visited.Remove(startNode.Id);
            return;
        }

        resultNodes.Add(startNode);
        currentPath.Add(startNode.Id);

        // Get edges based on direction
        var edgesToFollow = direction.ToLowerInvariant() switch
        {
            "outgoing" or "downstream" => graph.OutgoingEdges.GetValueOrDefault(startNode.Id, new List<GraphEdge>()),
            "incoming" or "upstream" => graph.IncomingEdges.GetValueOrDefault(startNode.Id, new List<GraphEdge>()),
            "both" or "bidirectional" => graph.OutgoingEdges.GetValueOrDefault(startNode.Id, new List<GraphEdge>())
                .Concat(graph.IncomingEdges.GetValueOrDefault(startNode.Id, new List<GraphEdge>()))
                .ToList(),
            _ => new List<GraphEdge>()
        };

        foreach (var edge in edgesToFollow)
        {
            if (ct.IsCancellationRequested) break;

            // Check edge type filter
            if (edgeTypes?.Count > 0 && !edgeTypes.Contains(edge.EdgeType)) continue;

            resultEdges.Add(edge);

            // Get the target node
            var targetId = edge.SourceId == startNode.Id ? edge.TargetId : edge.SourceId;
            if (graph.Nodes.TryGetValue(targetId, out var targetNode))
            {
                TraverseGraph(graph, targetNode, direction, maxDepth, edgeTypes, nodeTypes,
                    resultNodes, resultEdges, visited, currentPath, paths, ct);
            }
        }

        // Record path
        if (currentPath.Count > 1)
        {
            paths.Add(new List<string>(currentPath));
        }

        currentPath.RemoveAt(currentPath.Count - 1);
    }

    private void CollectDependents(
        WorkspaceGraph graph,
        string nodeId,
        int maxDepth,
        int currentDepth,
        List<GraphNode> result,
        HashSet<string> visited,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (currentDepth >= maxDepth) return;
        if (result.Count >= 500) return;

        if (!graph.IncomingEdges.TryGetValue(nodeId, out var edges)) return;

        foreach (var edge in edges)
        {
            if (ct.IsCancellationRequested) break;
            if (!visited.Add(edge.SourceId)) continue;

            if (graph.Nodes.TryGetValue(edge.SourceId, out var node))
            {
                result.Add(node);
                CollectDependents(graph, node.Id, maxDepth, currentDepth + 1, result, visited, ct);
            }
        }
    }

    private void CollectDependencies(
        WorkspaceGraph graph,
        string nodeId,
        int maxDepth,
        int currentDepth,
        List<GraphNode> result,
        HashSet<string> visited,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (currentDepth >= maxDepth) return;
        if (result.Count >= 500) return;

        if (!graph.OutgoingEdges.TryGetValue(nodeId, out var edges)) return;

        foreach (var edge in edges)
        {
            if (ct.IsCancellationRequested) break;
            if (!visited.Add(edge.TargetId)) continue;

            if (graph.Nodes.TryGetValue(edge.TargetId, out var node))
            {
                result.Add(node);
                CollectDependencies(graph, node.Id, maxDepth, currentDepth + 1, result, visited, ct);
            }
        }
    }

    #endregion

    #region Internal Types

    private class WorkspaceGraph
    {
        public ConcurrentDictionary<string, GraphNode> Nodes { get; } = new();
        public List<GraphEdge> Edges { get; } = new();
        public ConcurrentDictionary<string, List<GraphEdge>> OutgoingEdges { get; } = new();
        public ConcurrentDictionary<string, List<GraphEdge>> IncomingEdges { get; } = new();
        public HashSet<string> DirtyGuids { get; } = new();
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.MinValue;
    }

    #endregion
}
