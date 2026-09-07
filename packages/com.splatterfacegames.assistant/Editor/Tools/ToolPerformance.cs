// Tool Performance Utilities - Caching, pagination, cancellation, and batch operations

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Splatter.Editor.Tools
{
    #region Paginated Result

    /// <summary>
    /// Represents a paginated result set for large data queries.
    /// </summary>
    /// <typeparam name="T">The type of items in the result.</typeparam>
    public class PaginatedResult<T>
    {
        /// <summary>
        /// The items in the current page.
        /// </summary>
        public List<T> Items { get; }

        /// <summary>
        /// Total count of all items (not just this page).
        /// </summary>
        public int TotalCount { get; }

        /// <summary>
        /// The offset (starting index) of this page.
        /// </summary>
        public int Offset { get; }

        /// <summary>
        /// The maximum number of items per page.
        /// </summary>
        public int Limit { get; }

        /// <summary>
        /// Whether there are more items available after this page.
        /// </summary>
        public bool HasMore { get; }

        /// <summary>
        /// Token for fetching the next page of results.
        /// </summary>
        public string ContinuationToken { get; }

        public PaginatedResult(
            List<T> items,
            int totalCount,
            int offset,
            int limit,
            string continuationToken = null)
        {
            Items = items ?? new List<T>();
            TotalCount = totalCount;
            Offset = offset;
            Limit = limit;
            HasMore = offset + items.Count < totalCount;
            ContinuationToken = continuationToken ?? (HasMore ? GenerateContinuationToken(offset + limit) : null);
        }

        private static string GenerateContinuationToken(int nextOffset)
        {
            // Simple base64-encoded offset for continuation
            return Convert.ToBase64String(BitConverter.GetBytes(nextOffset));
        }

        /// <summary>
        /// Parses a continuation token to get the next offset.
        /// </summary>
        public static int ParseContinuationToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return 0;

            try
            {
                var bytes = Convert.FromBase64String(token);
                return BitConverter.ToInt32(bytes, 0);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Creates an empty paginated result.
        /// </summary>
        public static PaginatedResult<T> Empty(int limit = 100)
        {
            return new PaginatedResult<T>(new List<T>(), 0, 0, limit);
        }

        /// <summary>
        /// Creates a paginated result from an enumerable with automatic pagination.
        /// </summary>
        public static PaginatedResult<T> FromEnumerable(
            IEnumerable<T> source,
            int offset,
            int limit,
            CancellationToken ct = default)
        {
            var list = source.ToList();
            var totalCount = list.Count;
            var items = list.Skip(offset).Take(limit).ToList();

            return new PaginatedResult<T>(items, totalCount, offset, limit);
        }
    }

    #endregion

    #region Tool Cache

    /// <summary>
    /// Cached hierarchy data for a scene.
    /// </summary>
    public class CachedHierarchy
    {
        public Scene Scene { get; }
        public List<HierarchyNode> RootNodes { get; }
        public int TotalObjectCount { get; }
        public DateTime CachedAt { get; }
        public bool IsValid => Scene.IsValid() && Scene.isLoaded;

        public CachedHierarchy(Scene scene, List<HierarchyNode> rootNodes, int totalCount)
        {
            Scene = scene;
            RootNodes = rootNodes;
            TotalObjectCount = totalCount;
            CachedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Represents a node in the hierarchy cache.
    /// </summary>
    public class HierarchyNode
    {
        public string Name { get; set; }
        public int InstanceId { get; set; }
        public bool ActiveSelf { get; set; }
        public string Tag { get; set; }
        public int Layer { get; set; }
        public List<string> ComponentTypes { get; set; }
        public List<HierarchyNode> Children { get; set; }
        public int ChildCount { get; set; }
    }

    /// <summary>
    /// Cached asset dependency information.
    /// </summary>
    public class CachedDependencies
    {
        public string AssetPath { get; }
        public string Guid { get; }
        public List<string> DirectDependencies { get; }
        public List<string> AllDependencies { get; }
        public DateTime CachedAt { get; }

        public CachedDependencies(
            string assetPath,
            string guid,
            List<string> directDependencies,
            List<string> allDependencies)
        {
            AssetPath = assetPath;
            Guid = guid;
            DirectDependencies = directDependencies;
            AllDependencies = allDependencies;
            CachedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Static cache for frequently-accessed Unity data.
    /// Provides caching for scene hierarchies, asset dependencies, and component types.
    /// </summary>
    [InitializeOnLoad]
    public static class ToolCache
    {
        // Cache storage
        private static readonly ConcurrentDictionary<string, CachedHierarchy> _hierarchyCache = new();
        private static readonly ConcurrentDictionary<string, CachedDependencies> _dependencyCache = new();
        private static readonly ConcurrentDictionary<string, Type> _componentTypeCache = new();

        // Cache configuration
        private const int MaxHierarchyCacheEntries = 10;
        private const int MaxDependencyCacheEntries = 100;
        private const int MaxComponentTypeCacheEntries = 500;
        private static readonly TimeSpan HierarchyCacheExpiry = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DependencyCacheExpiry = TimeSpan.FromMinutes(10);

        // Lock for thread-safe operations
        private static readonly object _cacheLock = new();

        static ToolCache()
        {
            // Register for Unity events
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            // Register for asset changes
            AssetDatabase.importPackageCompleted += OnImportPackageCompleted;
            EditorApplication.projectChanged += OnProjectChanged;

            // Register for scene changes
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += OnSceneClosed;

            // Initialize component type cache with common types
            InitializeCommonComponentTypes();
        }

        #region Hierarchy Cache

        /// <summary>
        /// Gets the cached hierarchy for a scene, rebuilding if necessary.
        /// </summary>
        public static CachedHierarchy GetHierarchy(Scene scene, int maxDepth = 10, bool includeComponents = false)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            var cacheKey = scene.path;

            if (_hierarchyCache.TryGetValue(cacheKey, out var cached))
            {
                // Check if cache is still valid
                if (cached.IsValid && (DateTime.UtcNow - cached.CachedAt) < HierarchyCacheExpiry)
                {
                    return cached;
                }
            }

            // Rebuild cache
            var hierarchy = BuildHierarchyCache(scene, maxDepth, includeComponents);

            // Enforce cache size limit
            while (_hierarchyCache.Count >= MaxHierarchyCacheEntries)
            {
                var oldest = _hierarchyCache.OrderBy(kvp => kvp.Value.CachedAt).FirstOrDefault();
                if (!string.IsNullOrEmpty(oldest.Key))
                {
                    _hierarchyCache.TryRemove(oldest.Key, out _);
                }
            }

            _hierarchyCache[cacheKey] = hierarchy;
            return hierarchy;
        }

        /// <summary>
        /// Invalidates the hierarchy cache for a specific scene.
        /// </summary>
        public static void InvalidateHierarchy(Scene scene)
        {
            if (scene.IsValid())
            {
                _hierarchyCache.TryRemove(scene.path, out _);
            }
        }

        /// <summary>
        /// Invalidates all hierarchy caches.
        /// </summary>
        public static void InvalidateAllHierarchies()
        {
            _hierarchyCache.Clear();
        }

        private static CachedHierarchy BuildHierarchyCache(Scene scene, int maxDepth, bool includeComponents)
        {
            var rootObjects = scene.GetRootGameObjects();
            var rootNodes = new List<HierarchyNode>();
            var totalCount = 0;

            foreach (var go in rootObjects)
            {
                var node = BuildHierarchyNode(go, 0, maxDepth, includeComponents, ref totalCount);
                rootNodes.Add(node);
            }

            return new CachedHierarchy(scene, rootNodes, totalCount);
        }

        private static HierarchyNode BuildHierarchyNode(GameObject go, int depth, int maxDepth, bool includeComponents, ref int totalCount)
        {
            totalCount++;

            var node = new HierarchyNode
            {
                Name = go.name,
                InstanceId = go.GetInstanceID(),
                ActiveSelf = go.activeSelf,
                Tag = go.tag,
                Layer = go.layer,
                ChildCount = go.transform.childCount
            };

            if (includeComponents)
            {
                node.ComponentTypes = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList();
            }

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                node.Children = new List<HierarchyNode>();
                for (var i = 0; i < go.transform.childCount; i++)
                {
                    var childNode = BuildHierarchyNode(
                        go.transform.GetChild(i).gameObject,
                        depth + 1,
                        maxDepth,
                        includeComponents,
                        ref totalCount);
                    node.Children.Add(childNode);
                }
            }

            return node;
        }

        #endregion

        #region Asset Dependency Cache

        /// <summary>
        /// Gets cached dependencies for an asset, rebuilding if necessary.
        /// </summary>
        public static CachedDependencies GetDependencies(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            // Normalize path
            assetPath = assetPath.Replace("\\", "/");

            if (_dependencyCache.TryGetValue(assetPath, out var cached))
            {
                // Check if cache is still valid
                if ((DateTime.UtcNow - cached.CachedAt) < DependencyCacheExpiry)
                {
                    return cached;
                }
            }

            // Rebuild cache
            var dependencies = BuildDependencyCache(assetPath);

            if (dependencies != null)
            {
                // Enforce cache size limit
                while (_dependencyCache.Count >= MaxDependencyCacheEntries)
                {
                    var oldest = _dependencyCache.OrderBy(kvp => kvp.Value.CachedAt).FirstOrDefault();
                    if (!string.IsNullOrEmpty(oldest.Key))
                    {
                        _dependencyCache.TryRemove(oldest.Key, out _);
                    }
                }

                _dependencyCache[assetPath] = dependencies;
            }

            return dependencies;
        }

        /// <summary>
        /// Invalidates the dependency cache for a specific asset.
        /// </summary>
        public static void InvalidateDependencies(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                assetPath = assetPath.Replace("\\", "/");
                _dependencyCache.TryRemove(assetPath, out _);

                // Also invalidate any assets that depend on this one
                var keysToRemove = _dependencyCache
                    .Where(kvp => kvp.Value.DirectDependencies.Contains(assetPath) ||
                                  kvp.Value.AllDependencies.Contains(assetPath))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _dependencyCache.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Invalidates all dependency caches.
        /// </summary>
        public static void InvalidateAllDependencies()
        {
            _dependencyCache.Clear();
        }

        private static CachedDependencies BuildDependencyCache(string assetPath)
        {
            if (!AssetDatabase.AssetPathExists(assetPath))
                return null;

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var directDeps = AssetDatabase.GetDependencies(assetPath, false)
                .Where(p => p != assetPath)
                .ToList();
            var allDeps = AssetDatabase.GetDependencies(assetPath, true)
                .Where(p => p != assetPath)
                .ToList();

            return new CachedDependencies(assetPath, guid, directDeps, allDeps);
        }

        #endregion

        #region Component Type Cache

        /// <summary>
        /// Gets a component type by name from the cache.
        /// </summary>
        public static Type GetComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            // Check cache first
            if (_componentTypeCache.TryGetValue(typeName, out var cachedType))
            {
                return cachedType;
            }

            // Try to find the type
            var type = FindComponentType(typeName);

            if (type != null)
            {
                // Enforce cache size limit
                while (_componentTypeCache.Count >= MaxComponentTypeCacheEntries)
                {
                    var firstKey = _componentTypeCache.Keys.FirstOrDefault();
                    if (!string.IsNullOrEmpty(firstKey))
                    {
                        _componentTypeCache.TryRemove(firstKey, out _);
                    }
                }

                _componentTypeCache[typeName] = type;
            }

            return type;
        }

        /// <summary>
        /// Invalidates the component type cache (typically after domain reload).
        /// </summary>
        public static void InvalidateComponentTypeCache()
        {
            _componentTypeCache.Clear();
            InitializeCommonComponentTypes();
        }

        private static void InitializeCommonComponentTypes()
        {
            // Pre-cache common Unity component types for faster lookups
            var commonTypes = new[]
            {
                typeof(Transform), typeof(Rigidbody), typeof(Rigidbody2D),
                typeof(BoxCollider), typeof(SphereCollider), typeof(CapsuleCollider),
                typeof(MeshCollider), typeof(BoxCollider2D), typeof(CircleCollider2D),
                typeof(MeshRenderer), typeof(SkinnedMeshRenderer), typeof(SpriteRenderer),
                typeof(Camera), typeof(Light), typeof(AudioSource), typeof(AudioListener),
                typeof(Animator), typeof(Animation), typeof(ParticleSystem),
                typeof(Canvas), typeof(CanvasRenderer),
                typeof(CharacterController),
                typeof(LineRenderer), typeof(TrailRenderer)
            };

            foreach (var type in commonTypes)
            {
                _componentTypeCache[type.Name] = type;
                _componentTypeCache[type.FullName] = type;
            }
        }

        private static Type FindComponentType(string typeName)
        {
            // Try common Unity modules
            var moduleNames = new[]
            {
                "UnityEngine.CoreModule",
                "UnityEngine.PhysicsModule",
                "UnityEngine.Physics2DModule",
                "UnityEngine.AudioModule",
                "UnityEngine.AnimationModule",
                "UnityEngine.UIModule",
                "UnityEngine.ParticleSystemModule",
                "UnityEngine.AIModule",
                "UnityEngine.UI"
            };

            foreach (var module in moduleNames)
            {
                var type = Type.GetType($"UnityEngine.{typeName}, {module}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Try with UnityEngine namespace
                    var type = assembly.GetType($"UnityEngine.{typeName}");
                    if (type != null && typeof(Component).IsAssignableFrom(type))
                        return type;

                    // Try with UnityEngine.UI namespace
                    type = assembly.GetType($"UnityEngine.UI.{typeName}");
                    if (type != null && typeof(Component).IsAssignableFrom(type))
                        return type;

                    // Try exact name
                    type = assembly.GetType(typeName);
                    if (type != null && typeof(Component).IsAssignableFrom(type))
                        return type;
                }
                catch
                {
                    // Ignore assembly loading errors
                }
            }

            return null;
        }

        #endregion

        #region Cache Statistics

        /// <summary>
        /// Gets current cache statistics for debugging and monitoring.
        /// </summary>
        public static CacheStatistics GetStatistics()
        {
            return new CacheStatistics
            {
                HierarchyCacheCount = _hierarchyCache.Count,
                DependencyCacheCount = _dependencyCache.Count,
                ComponentTypeCacheCount = _componentTypeCache.Count,
                MaxHierarchyCacheEntries = MaxHierarchyCacheEntries,
                MaxDependencyCacheEntries = MaxDependencyCacheEntries,
                MaxComponentTypeCacheEntries = MaxComponentTypeCacheEntries
            };
        }

        public class CacheStatistics
        {
            public int HierarchyCacheCount { get; set; }
            public int DependencyCacheCount { get; set; }
            public int ComponentTypeCacheCount { get; set; }
            public int MaxHierarchyCacheEntries { get; set; }
            public int MaxDependencyCacheEntries { get; set; }
            public int MaxComponentTypeCacheEntries { get; set; }
        }

        /// <summary>
        /// Clears all caches.
        /// </summary>
        public static void ClearAll()
        {
            _hierarchyCache.Clear();
            _dependencyCache.Clear();
            _componentTypeCache.Clear();
            InitializeCommonComponentTypes();
        }

        #endregion

        #region Event Handlers

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Invalidate hierarchy cache when entering or exiting play mode
            if (state == PlayModeStateChange.EnteredEditMode ||
                state == PlayModeStateChange.EnteredPlayMode)
            {
                InvalidateAllHierarchies();
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            // Clear all caches before assembly reload
            ClearAll();
        }

        private static void OnAfterAssemblyReload()
        {
            // Reinitialize component type cache after reload
            InitializeCommonComponentTypes();
        }

        private static void OnImportPackageCompleted(string packageName)
        {
            // New package may have new component types
            InvalidateComponentTypeCache();
            InvalidateAllDependencies();
        }

        private static void OnProjectChanged()
        {
            // Assets may have changed
            InvalidateAllDependencies();
        }

        private static void OnHierarchyChanged()
        {
            // Scene hierarchy changed - invalidate current scene cache
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid())
            {
                InvalidateHierarchy(activeScene);
            }
        }

        private static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, UnityEditor.SceneManagement.OpenSceneMode mode)
        {
            // New scene opened - cache will be built on demand
        }

        private static void OnSceneClosed(UnityEngine.SceneManagement.Scene scene)
        {
            // Scene closed - remove from cache
            InvalidateHierarchy(scene);
        }

        #endregion
    }

    #endregion

    #region Cancellation Helper

    /// <summary>
    /// Utility class for handling cancellation in async operations.
    /// </summary>
    public static class CancellationHelper
    {
        /// <summary>
        /// Throws OperationCanceledException if the token is cancelled.
        /// </summary>
        /// <param name="ct">The cancellation token to check.</param>
        /// <param name="operation">Optional operation name for debugging.</param>
        public static void ThrowIfCancelled(CancellationToken ct, string operation = null)
        {
            if (ct.IsCancellationRequested)
            {
                var message = string.IsNullOrEmpty(operation)
                    ? "Operation was cancelled"
                    : $"Operation '{operation}' was cancelled";

                throw new OperationCanceledException(message, ct);
            }
        }

        /// <summary>
        /// Wraps a task with a timeout.
        /// </summary>
        /// <typeparam name="T">The result type of the task.</typeparam>
        /// <param name="task">The task to wrap.</param>
        /// <param name="timeoutMs">Timeout in milliseconds.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The task result, or throws TimeoutException.</returns>
        public static async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                return await task;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);

            var completedTask = await Task.WhenAny(task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                // Cancel the original task if possible
                throw new TimeoutException($"Operation timed out after {timeoutMs}ms");
            }

            // Cancel the timeout task
            timeoutCts.Cancel();

            // Return the result (will rethrow any exception from the original task)
            return await task;
        }

        /// <summary>
        /// Wraps a task with a timeout (non-generic version).
        /// </summary>
        public static async Task WithTimeout(Task task, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs <= 0)
            {
                await task;
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);

            var completedTask = await Task.WhenAny(task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                throw new TimeoutException($"Operation timed out after {timeoutMs}ms");
            }

            timeoutCts.Cancel();
            await task;
        }

        /// <summary>
        /// Wraps an enumerable with cancellation support.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="source">The source enumerable.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An enumerable that checks for cancellation during iteration.</returns>
        public static IEnumerable<T> WithCancellation<T>(IEnumerable<T> source, CancellationToken ct)
        {
            if (source == null)
                yield break;

            foreach (var item in source)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        /// <summary>
        /// Wraps an enumerable with cancellation support and progress reporting.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="source">The source enumerable.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="checkInterval">How often to check for cancellation (every N items).</param>
        /// <returns>An enumerable that checks for cancellation during iteration.</returns>
        public static IEnumerable<T> WithCancellation<T>(
            IEnumerable<T> source,
            CancellationToken ct,
            int checkInterval)
        {
            if (source == null)
                yield break;

            checkInterval = Math.Max(1, checkInterval);
            var count = 0;

            foreach (var item in source)
            {
                if (++count >= checkInterval)
                {
                    ct.ThrowIfCancellationRequested();
                    count = 0;
                }
                yield return item;
            }
        }

        /// <summary>
        /// Creates a linked cancellation token source with a timeout.
        /// </summary>
        public static CancellationTokenSource CreateTimeoutSource(int timeoutMs, CancellationToken linkedToken = default)
        {
            var cts = linkedToken == default
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(linkedToken);

            if (timeoutMs > 0)
            {
                cts.CancelAfter(timeoutMs);
            }

            return cts;
        }

        /// <summary>
        /// Executes an action with automatic cancellation on exception.
        /// </summary>
        public static async Task<T> ExecuteWithCancellation<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken ct,
            int timeoutMs = 0)
        {
            using var cts = CreateTimeoutSource(timeoutMs, ct);

            try
            {
                return await action(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Operation timed out after {timeoutMs}ms");
            }
        }
    }

    #endregion

    #region Batch Operation

    /// <summary>
    /// Executes operations in batches for better performance and cancellation handling.
    /// </summary>
    /// <typeparam name="TInput">The input type for each operation.</typeparam>
    /// <typeparam name="TResult">The result type for each operation.</typeparam>
    public class BatchOperation<TInput, TResult>
    {
        private readonly Func<TInput, TResult> _operation;
        private readonly int _batchSize;
        private readonly int _delayBetweenBatchesMs;

        /// <summary>
        /// Creates a new batch operation.
        /// </summary>
        /// <param name="operation">The operation to execute for each input.</param>
        /// <param name="batchSize">Number of items to process per batch.</param>
        /// <param name="delayBetweenBatchesMs">Optional delay between batches to prevent UI freezing.</param>
        public BatchOperation(
            Func<TInput, TResult> operation,
            int batchSize = 100,
            int delayBetweenBatchesMs = 0)
        {
            _operation = operation ?? throw new ArgumentNullException(nameof(operation));
            _batchSize = Math.Max(1, batchSize);
            _delayBetweenBatchesMs = Math.Max(0, delayBetweenBatchesMs);
        }

        /// <summary>
        /// Executes the operation on all inputs asynchronously with batching.
        /// </summary>
        /// <param name="inputs">The inputs to process.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of results in the same order as inputs.</returns>
        public async Task<List<TResult>> ExecuteAsync(IEnumerable<TInput> inputs, CancellationToken ct = default)
        {
            var results = new List<TResult>();
            var inputList = inputs.ToList();
            var totalBatches = (int)Math.Ceiling((double)inputList.Count / _batchSize);

            for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var batchStart = batchIndex * _batchSize;
                var batchItems = inputList.Skip(batchStart).Take(_batchSize);

                foreach (var item in batchItems)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = _operation(item);
                    results.Add(result);
                }

                // Delay between batches if specified
                if (_delayBetweenBatchesMs > 0 && batchIndex < totalBatches - 1)
                {
                    await Task.Delay(_delayBetweenBatchesMs, ct);
                }
            }

            return results;
        }

        /// <summary>
        /// Executes the operation on all inputs asynchronously with batching and progress reporting.
        /// </summary>
        /// <param name="inputs">The inputs to process.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="onProgress">Progress callback (0.0 to 1.0).</param>
        /// <returns>List of results in the same order as inputs.</returns>
        public async Task<List<TResult>> ExecuteAsync(
            IEnumerable<TInput> inputs,
            CancellationToken ct,
            Action<float> onProgress)
        {
            var results = new List<TResult>();
            var inputList = inputs.ToList();
            var total = inputList.Count;
            var processed = 0;

            var totalBatches = (int)Math.Ceiling((double)total / _batchSize);

            for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var batchStart = batchIndex * _batchSize;
                var batchItems = inputList.Skip(batchStart).Take(_batchSize);

                foreach (var item in batchItems)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = _operation(item);
                    results.Add(result);
                    processed++;

                    // Report progress
                    onProgress?.Invoke((float)processed / total);
                }

                // Delay between batches if specified
                if (_delayBetweenBatchesMs > 0 && batchIndex < totalBatches - 1)
                {
                    await Task.Delay(_delayBetweenBatchesMs, ct);
                }
            }

            return results;
        }

        /// <summary>
        /// Executes the operation synchronously with batching (for main thread operations).
        /// </summary>
        public List<TResult> Execute(IEnumerable<TInput> inputs, CancellationToken ct = default)
        {
            var results = new List<TResult>();
            var count = 0;

            foreach (var item in inputs)
            {
                if (++count % _batchSize == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var result = _operation(item);
                results.Add(result);
            }

            return results;
        }
    }

    /// <summary>
    /// Extension for creating batch operations with async operations.
    /// </summary>
    public class AsyncBatchOperation<TInput, TResult>
    {
        private readonly Func<TInput, CancellationToken, Task<TResult>> _asyncOperation;
        private readonly int _batchSize;
        private readonly int _maxConcurrency;

        /// <summary>
        /// Creates a new async batch operation.
        /// </summary>
        /// <param name="asyncOperation">The async operation to execute for each input.</param>
        /// <param name="batchSize">Number of items to process per batch.</param>
        /// <param name="maxConcurrency">Maximum concurrent operations within a batch.</param>
        public AsyncBatchOperation(
            Func<TInput, CancellationToken, Task<TResult>> asyncOperation,
            int batchSize = 100,
            int maxConcurrency = 4)
        {
            _asyncOperation = asyncOperation ?? throw new ArgumentNullException(nameof(asyncOperation));
            _batchSize = Math.Max(1, batchSize);
            _maxConcurrency = Math.Max(1, Math.Min(maxConcurrency, Environment.ProcessorCount));
        }

        /// <summary>
        /// Executes the async operation on all inputs with controlled concurrency.
        /// </summary>
        public async Task<List<TResult>> ExecuteAsync(IEnumerable<TInput> inputs, CancellationToken ct = default)
        {
            var results = new ConcurrentBag<(int Index, TResult Result)>();
            var inputList = inputs.ToList();
            var semaphore = new SemaphoreSlim(_maxConcurrency);

            var tasks = inputList.Select(async (input, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await _asyncOperation(input, ct);
                    results.Add((index, result));
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Return results in original order
            return results.OrderBy(r => r.Index).Select(r => r.Result).ToList();
        }
    }

    #endregion

    #region Performance Helpers

    /// <summary>
    /// Additional performance helper utilities.
    /// </summary>
    public static class PerformanceHelpers
    {
        /// <summary>
        /// Measures execution time of an action.
        /// </summary>
        public static TimeSpan MeasureTime(Action action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            action();
            sw.Stop();
            return sw.Elapsed;
        }

        /// <summary>
        /// Measures execution time of an async action.
        /// </summary>
        public static async Task<(T Result, TimeSpan Elapsed)> MeasureTimeAsync<T>(Func<Task<T>> asyncAction)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await asyncAction();
            sw.Stop();
            return (result, sw.Elapsed);
        }

        /// <summary>
        /// Debounces rapid calls to an action.
        /// </summary>
        public static Action Debounce(Action action, int delayMs)
        {
            CancellationTokenSource cts = null;

            return () =>
            {
                cts?.Cancel();
                cts = new CancellationTokenSource();

                Task.Delay(delayMs, cts.Token)
                    .ContinueWith(t =>
                    {
                        if (!t.IsCanceled)
                        {
                            MainThreadDispatcher.Enqueue(action);
                        }
                    });
            };
        }

        /// <summary>
        /// Throttles calls to an action to a maximum rate.
        /// </summary>
        public static Action Throttle(Action action, int intervalMs)
        {
            DateTime lastExecution = DateTime.MinValue;
            var lockObj = new object();

            return () =>
            {
                lock (lockObj)
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastExecution).TotalMilliseconds >= intervalMs)
                    {
                        lastExecution = now;
                        action();
                    }
                }
            };
        }
    }

    #endregion
}
