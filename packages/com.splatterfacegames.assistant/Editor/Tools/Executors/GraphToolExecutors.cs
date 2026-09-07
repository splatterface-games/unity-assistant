// Graph Tool Executors - Asset dependency graph operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Queries the asset dependency graph with flexible traversal options.
    /// Supports incoming (dependents), outgoing (dependencies), or bidirectional traversal.
    /// </summary>
    public class GraphQueryExecutor : IToolExecutor
    {
        public string ToolId => "graph.query";

        // Static cache for graph data to improve performance
        private static readonly Dictionary<string, CachedGraphData> _graphCache = new();
        private static bool _cacheInvalidated = true;
        private static int _lastAssetImportCount = 0;

        // Asset type categories for filtering
        private static readonly Dictionary<string, HashSet<string>> _assetTypeCategories = new()
        {
            ["scripts"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".js", ".boo"
            },
            ["textures"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tiff", ".tif", ".gif", ".bmp", ".exr", ".hdr"
            },
            ["materials"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mat"
            },
            ["shaders"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".shader", ".shadergraph", ".shadersubgraph", ".hlsl", ".cginc", ".compute"
            },
            ["prefabs"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".prefab"
            },
            ["scenes"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".unity"
            },
            ["animations"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".anim", ".controller", ".overrideController"
            },
            ["audio"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".wav", ".mp3", ".ogg", ".aiff", ".aif", ".flac"
            },
            ["models"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".fbx", ".obj", ".dae", ".3ds", ".blend", ".ma", ".mb"
            },
            ["fonts"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".ttf", ".otf", ".fnt", ".fontsettings"
            },
            ["scriptableobjects"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".asset"
            }
        };

        static GraphQueryExecutor()
        {
            // Subscribe to asset import events to invalidate cache
            EditorApplication.projectChanged += OnProjectChanged;
        }

        private static void OnProjectChanged()
        {
            _cacheInvalidated = true;
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Parse input arguments
                var startPath = context.Arguments.TryGetValue("startPath", out var sp) ? sp?.ToString() : null;
                var startGuid = context.Arguments.TryGetValue("startGuid", out var sg) ? sg?.ToString() : null;
                var directionStr = context.Arguments.TryGetValue("direction", out var d) ? d?.ToString()?.ToLowerInvariant() : "both";
                var maxDepth = context.Arguments.TryGetValue("maxDepth", out var md) ? Convert.ToInt32(md) : 3;
                var includeBuiltIn = context.Arguments.TryGetValue("includeBuiltIn", out var ib) && Convert.ToBoolean(ib);

                // Parse filter options
                GraphFilter filter = null;
                if (context.Arguments.TryGetValue("filter", out var filterObj) && filterObj != null)
                {
                    filter = ParseFilter(filterObj);
                }

                // Clamp maxDepth to valid range (1-10)
                maxDepth = Math.Max(1, Math.Min(10, maxDepth));

                // Parse direction
                var direction = directionStr switch
                {
                    "incoming" => TraversalDirection.Incoming,
                    "outgoing" => TraversalDirection.Outgoing,
                    "both" => TraversalDirection.Both,
                    _ => TraversalDirection.Both
                };

                // Resolve path from guid if provided
                if (string.IsNullOrEmpty(startPath) && !string.IsNullOrEmpty(startGuid))
                {
                    startPath = AssetDatabase.GUIDToAssetPath(startGuid);
                }

                if (string.IsNullOrEmpty(startPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Either startPath or startGuid is required"));
                }

                // Validate the path exists
                if (!AssetDatabase.AssetPathExists(startPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Asset not found at path: {startPath}"));
                }

                // Security check - ensure path is within project bounds
                if (!IsPathWithinProject(startPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Access denied: Path is outside project"));
                }

                // Get or resolve the GUID
                if (string.IsNullOrEmpty(startGuid))
                {
                    startGuid = AssetDatabase.AssetPathToGUID(startPath);
                }

                // Build the graph
                var graphResult = BuildGraph(startPath, startGuid, direction, maxDepth, includeBuiltIn, filter, ct);

                return Task.FromResult(ToolExecutionResult.Succeeded(graphResult));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(ToolExecutionResult.Failed("Operation was cancelled"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed($"Graph query failed: {ex.Message}"));
            }
        }

        private GraphFilter ParseFilter(object filterObj)
        {
            var filter = new GraphFilter();

            if (filterObj is Dictionary<string, object> filterDict)
            {
                if (filterDict.TryGetValue("assetType", out var assetType))
                {
                    filter.AssetType = assetType?.ToString();
                }

                if (filterDict.TryGetValue("extension", out var extension))
                {
                    var extStr = extension?.ToString();
                    if (!string.IsNullOrEmpty(extStr))
                    {
                        // Handle both single extension and comma-separated list
                        filter.Extensions = extStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(e => e.Trim())
                            .Where(e => !string.IsNullOrEmpty(e))
                            .Select(e => e.StartsWith(".") ? e : "." + e)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                }

                if (filterDict.TryGetValue("pathPattern", out var pathPattern))
                {
                    filter.PathPattern = pathPattern?.ToString();
                }
            }

            return filter;
        }

        private object BuildGraph(
            string startPath,
            string startGuid,
            TraversalDirection direction,
            int maxDepth,
            bool includeBuiltIn,
            GraphFilter filter,
            CancellationToken ct)
        {
            var nodes = new List<GraphNode>();
            var edges = new List<GraphEdge>();
            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startPath };
            var pathToNode = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
            var maxDepthReached = 0;

            // Create root node
            var rootNode = CreateNode(startPath, startGuid, 0);
            nodes.Add(rootNode);
            pathToNode[startPath] = rootNode;

            // BFS queue: (path, depth)
            var queue = new Queue<(string path, int depth)>();
            queue.Enqueue((startPath, 0));

            // Get cached incoming edges if we need them
            Dictionary<string, HashSet<string>> incomingEdgesMap = null;
            if (direction == TraversalDirection.Incoming || direction == TraversalDirection.Both)
            {
                incomingEdgesMap = GetOrBuildIncomingEdgesCache(includeBuiltIn, ct);
            }

            while (queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var (currentPath, depth) = queue.Dequeue();
                maxDepthReached = Math.Max(maxDepthReached, depth);

                if (depth >= maxDepth)
                    continue;

                // Get outgoing edges (dependencies)
                if (direction == TraversalDirection.Outgoing || direction == TraversalDirection.Both)
                {
                    var dependencies = AssetDatabase.GetDependencies(currentPath, false);
                    foreach (var depPath in dependencies)
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        // Skip self-references
                        if (depPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Skip built-in assets unless requested
                        if (!includeBuiltIn && IsBuiltInAsset(depPath))
                            continue;

                        // Apply filter
                        if (!PassesFilter(depPath, filter))
                            continue;

                        // Create or get the node
                        if (!pathToNode.TryGetValue(depPath, out var depNode))
                        {
                            var depGuid = AssetDatabase.AssetPathToGUID(depPath);
                            depNode = CreateNode(depPath, depGuid, depth + 1);
                            nodes.Add(depNode);
                            pathToNode[depPath] = depNode;
                        }

                        // Create edge (current -> dependency)
                        var edge = new GraphEdge
                        {
                            From = currentPath,
                            To = depPath,
                            Type = "depends_on"
                        };

                        // Avoid duplicate edges
                        if (!edges.Any(e => e.From == edge.From && e.To == edge.To))
                        {
                            edges.Add(edge);
                        }

                        // Add to queue if not visited
                        if (!visitedPaths.Contains(depPath))
                        {
                            visitedPaths.Add(depPath);
                            queue.Enqueue((depPath, depth + 1));
                        }
                    }
                }

                // Get incoming edges (dependents)
                if ((direction == TraversalDirection.Incoming || direction == TraversalDirection.Both)
                    && incomingEdgesMap != null)
                {
                    if (incomingEdgesMap.TryGetValue(currentPath, out var dependents))
                    {
                        foreach (var dependentPath in dependents)
                        {
                            if (ct.IsCancellationRequested)
                                break;

                            // Skip self-references
                            if (dependentPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Skip built-in assets unless requested
                            if (!includeBuiltIn && IsBuiltInAsset(dependentPath))
                                continue;

                            // Apply filter
                            if (!PassesFilter(dependentPath, filter))
                                continue;

                            // Create or get the node
                            if (!pathToNode.TryGetValue(dependentPath, out var dependentNode))
                            {
                                var dependentGuid = AssetDatabase.AssetPathToGUID(dependentPath);
                                dependentNode = CreateNode(dependentPath, dependentGuid, depth + 1);
                                nodes.Add(dependentNode);
                                pathToNode[dependentPath] = dependentNode;
                            }

                            // Create edge (dependent -> current)
                            var edge = new GraphEdge
                            {
                                From = dependentPath,
                                To = currentPath,
                                Type = "depends_on"
                            };

                            // Avoid duplicate edges
                            if (!edges.Any(e => e.From == edge.From && e.To == edge.To))
                            {
                                edges.Add(edge);
                            }

                            // Add to queue if not visited
                            if (!visitedPaths.Contains(dependentPath))
                            {
                                visitedPaths.Add(dependentPath);
                                queue.Enqueue((dependentPath, depth + 1));
                            }
                        }
                    }
                }
            }

            // Build result object
            return new
            {
                nodes = nodes.Select(n => new
                {
                    path = n.Path,
                    guid = n.Guid,
                    type = n.Type,
                    depth = n.Depth
                }).ToList(),
                edges = edges.Select(e => new
                {
                    from = e.From,
                    to = e.To,
                    type = e.Type
                }).ToList(),
                stats = new
                {
                    nodeCount = nodes.Count,
                    edgeCount = edges.Count,
                    maxDepthReached
                }
            };
        }

        private Dictionary<string, HashSet<string>> GetOrBuildIncomingEdgesCache(bool includeBuiltIn, CancellationToken ct)
        {
            // Check if cache needs invalidation
            var currentImportCount = AssetDatabase.GetAllAssetPaths().Length;
            if (_cacheInvalidated || currentImportCount != _lastAssetImportCount)
            {
                _graphCache.Clear();
                _cacheInvalidated = false;
                _lastAssetImportCount = currentImportCount;
            }

            var cacheKey = includeBuiltIn ? "incoming_with_builtin" : "incoming_no_builtin";

            if (_graphCache.TryGetValue(cacheKey, out var cachedData))
            {
                return cachedData.IncomingEdges;
            }

            // Build the incoming edges map
            var incomingEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            var allAssetPaths = AssetDatabase.GetAllAssetPaths()
                .Where(p => includeBuiltIn || !IsBuiltInAsset(p))
                .ToList();

            foreach (var assetPath in allAssetPaths)
            {
                if (ct.IsCancellationRequested)
                    break;

                var dependencies = AssetDatabase.GetDependencies(assetPath, false);
                foreach (var depPath in dependencies)
                {
                    // Skip self-references
                    if (depPath.Equals(assetPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip built-in if not included
                    if (!includeBuiltIn && IsBuiltInAsset(depPath))
                        continue;

                    // Add the reverse edge (depPath is depended on by assetPath)
                    if (!incomingEdges.TryGetValue(depPath, out var dependents))
                    {
                        dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        incomingEdges[depPath] = dependents;
                    }
                    dependents.Add(assetPath);
                }
            }

            // Cache the result
            _graphCache[cacheKey] = new CachedGraphData
            {
                IncomingEdges = incomingEdges,
                CreatedAt = DateTime.UtcNow
            };

            return incomingEdges;
        }

        private GraphNode CreateNode(string path, string guid, int depth)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
            var assetType = asset != null ? asset.GetType().Name : GetTypeFromExtension(path);

            return new GraphNode
            {
                Path = path,
                Guid = guid,
                Type = assetType,
                Depth = depth
            };
        }

        private string GetTypeFromExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".cs" => "MonoScript",
                ".mat" => "Material",
                ".shader" => "Shader",
                ".prefab" => "Prefab",
                ".unity" => "Scene",
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".psd" => "Texture2D",
                ".fbx" or ".obj" or ".dae" => "Model",
                ".anim" => "AnimationClip",
                ".controller" => "AnimatorController",
                ".asset" => "ScriptableObject",
                ".wav" or ".mp3" or ".ogg" => "AudioClip",
                ".ttf" or ".otf" => "Font",
                _ => "Unknown"
            };
        }

        private bool IsBuiltInAsset(string path)
        {
            // Built-in assets start with these prefixes
            return path.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Packages/com.unity.", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/Editor/")
                || path.StartsWith("Built-in", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path));
        }

        private bool PassesFilter(string path, GraphFilter filter)
        {
            if (filter == null)
                return true;

            // Filter by asset type category
            if (!string.IsNullOrEmpty(filter.AssetType))
            {
                var categoryKey = filter.AssetType.ToLowerInvariant();
                if (_assetTypeCategories.TryGetValue(categoryKey, out var extensions))
                {
                    var ext = Path.GetExtension(path);
                    if (!extensions.Contains(ext))
                        return false;
                }
            }

            // Filter by specific extensions
            if (filter.Extensions != null && filter.Extensions.Count > 0)
            {
                var ext = Path.GetExtension(path);
                if (!filter.Extensions.Contains(ext))
                    return false;
            }

            // Filter by path pattern (regex)
            if (!string.IsNullOrEmpty(filter.PathPattern))
            {
                try
                {
                    if (!Regex.IsMatch(path, filter.PathPattern, RegexOptions.IgnoreCase))
                        return false;
                }
                catch (ArgumentException)
                {
                    // Invalid regex pattern, skip this filter
                }
            }

            return true;
        }

        private bool IsPathWithinProject(string path)
        {
            // Allow Assets and Packages folders
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Packages\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Additional check for absolute paths
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            return fullPath.StartsWith(projectRoot);
        }

        /// <summary>
        /// Clears the graph cache. Call this when you need to force a refresh.
        /// </summary>
        public static void InvalidateCache()
        {
            _graphCache.Clear();
            _cacheInvalidated = true;
        }

        #region Internal Types

        private enum TraversalDirection
        {
            Incoming,
            Outgoing,
            Both
        }

        private class GraphNode
        {
            public string Path { get; set; }
            public string Guid { get; set; }
            public string Type { get; set; }
            public int Depth { get; set; }
        }

        private class GraphEdge
        {
            public string From { get; set; }
            public string To { get; set; }
            public string Type { get; set; }
        }

        private class GraphFilter
        {
            public string AssetType { get; set; }
            public HashSet<string> Extensions { get; set; }
            public string PathPattern { get; set; }
        }

        private class CachedGraphData
        {
            public Dictionary<string, HashSet<string>> IncomingEdges { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        #endregion
    }
}
