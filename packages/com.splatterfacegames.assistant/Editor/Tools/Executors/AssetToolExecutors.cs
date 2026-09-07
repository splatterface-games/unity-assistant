// Asset Tool Executors - Unity asset operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Imports an asset into the project.
    /// </summary>
    public class AssetImportExecutor : IToolExecutor
    {
        public string ToolId => "asset.import";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var sourcePath = context.Arguments["source_path"]?.ToString();
                var destPath = context.Arguments["dest_path"]?.ToString();

                if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("source_path and dest_path are required"));
                }

                // Ensure destination is within Assets
                if (!destPath.StartsWith("Assets/") && !destPath.StartsWith("Assets\\"))
                {
                    destPath = "Assets/" + destPath;
                }

                var fullSourcePath = Path.IsPathRooted(sourcePath)
                    ? sourcePath
                    : Path.Combine(Application.dataPath, "..", sourcePath);

                if (!File.Exists(fullSourcePath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Source file not found: {sourcePath}"));
                }

                var fullDestPath = Path.Combine(Application.dataPath, "..", destPath);
                var destDir = Path.GetDirectoryName(fullDestPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(fullSourcePath, fullDestPath, true);
                AssetDatabase.Refresh();

                var asset = AssetDatabase.LoadAssetAtPath<Object>(destPath);
                var assetType = asset != null ? asset.GetType().Name : "Unknown";

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    path = destPath,
                    type = assetType,
                    guid = AssetDatabase.AssetPathToGUID(destPath)
                }, new List<string> { destPath }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Creates a new asset.
    /// </summary>
    public class AssetCreateExecutor : IToolExecutor
    {
        public string ToolId => "asset.create";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var assetType = context.Arguments["type"]?.ToString();
                var path = context.Arguments["path"]?.ToString();

                if (string.IsNullOrEmpty(assetType) || string.IsNullOrEmpty(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("type and path are required"));
                }

                // Ensure path is within Assets
                if (!path.StartsWith("Assets/") && !path.StartsWith("Assets\\"))
                {
                    path = "Assets/" + path;
                }

                Object asset = assetType.ToLowerInvariant() switch
                {
                    "material" => CreateMaterial(context),
                    "script" => CreateScript(context, path),
                    "shader" => CreateShader(context, path),
                    "animator" => CreateAnimatorController(context),
                    "scriptableobject" => CreateScriptableObject(context),
                    "prefab" => CreatePrefab(context, path),
                    "scene" => CreateScene(context, path),
                    _ => throw new ArgumentException($"Unsupported asset type: {assetType}")
                };

                if (asset == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Failed to create {assetType}"));
                }

                // For assets that need explicit saving
                if (assetType != "script" && assetType != "shader" && assetType != "scene")
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                    {
                        CreateFolderRecursive(dir);
                    }

                    AssetDatabase.CreateAsset(asset, path);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    path,
                    type = assetType,
                    guid = AssetDatabase.AssetPathToGUID(path)
                }, new List<string> { path }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Material CreateMaterial(ToolExecutionContext context)
        {
            var shaderName = context.Arguments.TryGetValue("shader", out var s) ? s?.ToString() : "Standard";
            var shader = Shader.Find(shaderName) ?? Shader.Find("Standard");
            return new Material(shader);
        }

        private Object CreateScript(ToolExecutionContext context, string path)
        {
            var content = context.Arguments.TryGetValue("content", out var c) ? c?.ToString() : GetDefaultScriptContent(path);
            var fullPath = Path.Combine(Application.dataPath, "..", path);

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return AssetDatabase.LoadAssetAtPath<MonoScript>(path);
        }

        private string GetDefaultScriptContent(string path)
        {
            var className = Path.GetFileNameWithoutExtension(path);
            return $@"using UnityEngine;

public class {className} : MonoBehaviour
{{
    void Start()
    {{

    }}

    void Update()
    {{

    }}
}}
";
        }

        private Object CreateShader(ToolExecutionContext context, string path)
        {
            var shaderName = Path.GetFileNameWithoutExtension(path);
            var content = context.Arguments.TryGetValue("content", out var c) ? c?.ToString() : GetDefaultShaderContent(shaderName);
            var fullPath = Path.Combine(Application.dataPath, "..", path);

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return AssetDatabase.LoadAssetAtPath<Shader>(path);
        }

        private string GetDefaultShaderContent(string name)
        {
            return $@"Shader ""Custom/{name}""
{{
    Properties
    {{
        _Color (""Color"", Color) = (1,1,1,1)
        _MainTex (""Albedo (RGB)"", 2D) = ""white"" {{}}
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" }}
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;

        struct Input
        {{
            float2 uv_MainTex;
        }};

        void surf (Input IN, inout SurfaceOutputStandard o)
        {{
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Alpha = c.a;
        }}
        ENDCG
    }}
    FallBack ""Diffuse""
}}
";
        }

        private Object CreateAnimatorController(ToolExecutionContext context)
        {
            return UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath("");
        }

        private Object CreateScriptableObject(ToolExecutionContext context)
        {
            var typeName = context.Arguments.TryGetValue("script_type", out var t) ? t?.ToString() : null;
            if (string.IsNullOrEmpty(typeName))
            {
                return ScriptableObject.CreateInstance<ScriptableObject>();
            }

            var type = Type.GetType(typeName);
            if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
            {
                return ScriptableObject.CreateInstance(type);
            }

            return ScriptableObject.CreateInstance<ScriptableObject>();
        }

        private Object CreatePrefab(ToolExecutionContext context, string path)
        {
            var go = new GameObject(Path.GetFileNameWithoutExtension(path));
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        private Object CreateScene(ToolExecutionContext context, string path)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, path);
            return AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
        }

        private void CreateFolderRecursive(string path)
        {
            var parts = path.Split('/', '\\');
            var current = "";

            foreach (var part in parts)
            {
                var parent = string.IsNullOrEmpty(current) ? "" : current;
                var next = string.IsNullOrEmpty(current) ? part : current + "/" + part;

                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(parent, part);
                }

                current = next;
            }
        }
    }

    /// <summary>
    /// Deletes an asset from the project.
    /// </summary>
    public class AssetDeleteExecutor : IToolExecutor
    {
        public string ToolId => "asset.delete";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var path = context.Arguments["path"]?.ToString();

                if (string.IsNullOrEmpty(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("path is required"));
                }

                // Security check - only allow deleting from Assets folder
                if (!path.StartsWith("Assets/") && !path.StartsWith("Assets\\"))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Can only delete assets from Assets folder"));
                }

                if (!AssetDatabase.DeleteAsset(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Failed to delete: {path}"));
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    path,
                    deleted = true
                }, new List<string> { path }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Searches for Unity assets by name, type, labels, or path.
    /// </summary>
    public class AssetSearchExecutor : IToolExecutor
    {
        public string ToolId => "asset.search";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Extract arguments
                var query = context.Arguments.TryGetValue("query", out var q) ? q?.ToString() : "";
                var typeFilter = context.Arguments.TryGetValue("type", out var t) ? t?.ToString() : null;
                var pathFilter = context.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
                var maxResults = context.Arguments.TryGetValue("maxResults", out var mr) ? Convert.ToInt32(mr) : 50;
                maxResults = Math.Clamp(maxResults, 1, 200);

                // Extract labels array
                List<string> labels = new List<string>();
                if (context.Arguments.TryGetValue("labels", out var labelsObj) && labelsObj is List<object> labelsList)
                {
                    foreach (var label in labelsList)
                    {
                        if (label != null)
                            labels.Add(label.ToString());
                    }
                }

                // Build search filter for AssetDatabase
                string searchFilter = "";

                // Add type filter (Unity search syntax: t:TypeName)
                if (!string.IsNullOrEmpty(typeFilter))
                {
                    searchFilter += $"t:{typeFilter} ";
                }

                // Add label filters (Unity search syntax: l:LabelName)
                foreach (var label in labels)
                {
                    searchFilter += $"l:{label} ";
                }

                // Add name/query filter
                if (!string.IsNullOrEmpty(query))
                {
                    searchFilter += query;
                }

                // Determine search paths
                string[] searchInFolders = null;
                if (!string.IsNullOrEmpty(pathFilter))
                {
                    if (pathFilter.StartsWith("Assets/") || pathFilter.StartsWith("Assets\\") || pathFilter == "Assets")
                    {
                        searchInFolders = new[] { pathFilter };
                    }
                    else
                    {
                        searchInFolders = new[] { "Assets/" + pathFilter.TrimStart('/').TrimStart('\\') };
                    }
                }

                // Execute search
                string[] guids;
                if (searchInFolders != null)
                {
                    guids = AssetDatabase.FindAssets(searchFilter.Trim(), searchInFolders);
                }
                else
                {
                    guids = AssetDatabase.FindAssets(searchFilter.Trim());
                }

                // Check cancellation
                ct.ThrowIfCancellationRequested();

                // Build results
                var assets = new List<object>();
                bool truncated = guids.Length > maxResults;
                int count = Math.Min(guids.Length, maxResults);

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    string guid = guids[i];
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);

                    // Skip if path filter specified and doesn't match
                    if (!string.IsNullOrEmpty(pathFilter) && !assetPath.Contains(pathFilter))
                        continue;

                    var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (asset == null)
                        continue;

                    var assetLabels = AssetDatabase.GetLabels(asset);
                    var assetType = asset.GetType();

                    assets.Add(new
                    {
                        guid = guid,
                        path = assetPath,
                        name = asset.name,
                        type = assetType.Name,
                        fullType = assetType.FullName,
                        labels = assetLabels,
                        isMainAsset = AssetDatabase.IsMainAsset(asset),
                        instanceId = asset.GetInstanceID()
                    });

                    if (assets.Count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    assets = assets,
                    count = assets.Count,
                    totalFound = guids.Length,
                    truncated = truncated,
                    query = query,
                    filter = searchFilter.Trim()
                }));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(ToolExecutionResult.Failed("Operation was cancelled"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed($"Asset search failed: {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Reads asset information including metadata and optionally serialized properties.
    /// </summary>
    public class AssetReadExecutor : IToolExecutor
    {
        public string ToolId => "asset.read";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Get path from either path or guid argument
                var path = context.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
                var guid = context.Arguments.TryGetValue("guid", out var g) ? g?.ToString() : null;
                var includeSerializedData = context.Arguments.TryGetValue("includeSerializedData", out var isd) && Convert.ToBoolean(isd);

                // Resolve path from guid if provided
                if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(guid))
                {
                    path = AssetDatabase.GUIDToAssetPath(guid);
                }

                if (string.IsNullOrEmpty(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Either path or guid is required"));
                }

                // Security check - ensure path is within project bounds
                if (!IsPathWithinProject(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Access denied: Path is outside project"));
                }

                // Verify asset exists
                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Asset not found at path: {path}"));
                }

                // Get the GUID if not already provided
                if (string.IsNullOrEmpty(guid))
                {
                    guid = AssetDatabase.AssetPathToGUID(path);
                }

                // Build basic asset info
                var assetInfo = new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["guid"] = guid,
                    ["name"] = asset.name,
                    ["type"] = asset.GetType().Name,
                    ["fullType"] = asset.GetType().FullName,
                    ["instanceId"] = asset.GetInstanceID()
                };

                // Add meta file info
                var metaPath = path + ".meta";
                var fullMetaPath = Path.Combine(Application.dataPath, "..", metaPath);
                if (File.Exists(fullMetaPath))
                {
                    var metaInfo = new Dictionary<string, object>
                    {
                        ["exists"] = true,
                        ["lastModified"] = File.GetLastWriteTimeUtc(fullMetaPath).ToString("o")
                    };

                    // Try to extract importer type from meta
                    var importer = AssetImporter.GetAtPath(path);
                    if (importer != null)
                    {
                        metaInfo["importerType"] = importer.GetType().Name;
                        metaInfo["assetBundleName"] = importer.assetBundleName;
                        metaInfo["assetBundleVariant"] = importer.assetBundleVariant;
                    }

                    assetInfo["meta"] = metaInfo;
                }

                // Add file info
                var fullPath = Path.Combine(Application.dataPath, "..", path);
                if (File.Exists(fullPath))
                {
                    var fileInfo = new FileInfo(fullPath);
                    assetInfo["fileInfo"] = new Dictionary<string, object>
                    {
                        ["size"] = fileInfo.Length,
                        ["extension"] = fileInfo.Extension,
                        ["lastModified"] = fileInfo.LastWriteTimeUtc.ToString("o"),
                        ["created"] = fileInfo.CreationTimeUtc.ToString("o")
                    };
                }

                // Add labels and asset bundle info
                var labels = AssetDatabase.GetLabels(asset);
                if (labels.Length > 0)
                {
                    assetInfo["labels"] = labels;
                }

                // Check if it's a main asset or sub-asset
                assetInfo["isMainAsset"] = AssetDatabase.IsMainAsset(asset);
                assetInfo["isSubAsset"] = AssetDatabase.IsSubAsset(asset);

                // Get sub-assets if this is a main asset
                if (AssetDatabase.IsMainAsset(asset))
                {
                    var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
                    if (subAssets.Length > 0)
                    {
                        assetInfo["subAssets"] = subAssets.Select(sa => new
                        {
                            name = sa.name,
                            type = sa.GetType().Name,
                            instanceId = sa.GetInstanceID()
                        }).ToList();
                    }
                }

                // Include serialized data if requested
                if (includeSerializedData)
                {
                    assetInfo["serializedData"] = GetSerializedProperties(asset);
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new { asset = assetInfo }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private bool IsPathWithinProject(string path)
        {
            // Allow Assets and Packages folders
            if (path.StartsWith("Assets/") || path.StartsWith("Assets\\") ||
                path.StartsWith("Packages/") || path.StartsWith("Packages\\"))
            {
                return true;
            }

            // Additional check for absolute paths
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            return fullPath.StartsWith(projectRoot);
        }

        private Dictionary<string, object> GetSerializedProperties(Object asset)
        {
            var properties = new Dictionary<string, object>();
            var serializedObject = new SerializedObject(asset);
            var iterator = serializedObject.GetIterator();

            // Limit properties to avoid huge outputs
            var maxProperties = 100;
            var propertyCount = 0;

            if (iterator.NextVisible(true))
            {
                do
                {
                    if (propertyCount >= maxProperties)
                    {
                        properties["_truncated"] = true;
                        break;
                    }

                    var propValue = GetSerializedPropertyValue(iterator);
                    if (propValue != null)
                    {
                        properties[iterator.propertyPath] = propValue;
                        propertyCount++;
                    }
                }
                while (iterator.NextVisible(false));
            }

            serializedObject.Dispose();
            return properties;
        }

        private object GetSerializedPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return prop.floatValue;
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    var color = prop.colorValue;
                    return new { r = color.r, g = color.g, b = color.b, a = color.a };
                case SerializedPropertyType.ObjectReference:
                    var obj = prop.objectReferenceValue;
                    if (obj != null)
                    {
                        return new
                        {
                            name = obj.name,
                            type = obj.GetType().Name,
                            instanceId = obj.GetInstanceID()
                        };
                    }
                    return null;
                case SerializedPropertyType.Enum:
                    return prop.enumNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                        ? prop.enumNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return new { x = v2.x, y = v2.y };
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return new { x = v3.x, y = v3.y, z = v3.z };
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };
                case SerializedPropertyType.Rect:
                    var rect = prop.rectValue;
                    return new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };
                case SerializedPropertyType.Bounds:
                    var bounds = prop.boundsValue;
                    return new
                    {
                        center = new { x = bounds.center.x, y = bounds.center.y, z = bounds.center.z },
                        size = new { x = bounds.size.x, y = bounds.size.y, z = bounds.size.z }
                    };
                case SerializedPropertyType.Quaternion:
                    var quat = prop.quaternionValue;
                    return new { x = quat.x, y = quat.y, z = quat.z, w = quat.w };
                case SerializedPropertyType.Vector2Int:
                    var v2i = prop.vector2IntValue;
                    return new { x = v2i.x, y = v2i.y };
                case SerializedPropertyType.Vector3Int:
                    var v3i = prop.vector3IntValue;
                    return new { x = v3i.x, y = v3i.y, z = v3i.z };
                case SerializedPropertyType.RectInt:
                    var rectInt = prop.rectIntValue;
                    return new { x = rectInt.x, y = rectInt.y, width = rectInt.width, height = rectInt.height };
                case SerializedPropertyType.BoundsInt:
                    var boundsInt = prop.boundsIntValue;
                    return new
                    {
                        position = new { x = boundsInt.position.x, y = boundsInt.position.y, z = boundsInt.position.z },
                        size = new { x = boundsInt.size.x, y = boundsInt.size.y, z = boundsInt.size.z }
                    };
                case SerializedPropertyType.ArraySize:
                    return prop.intValue;
                case SerializedPropertyType.Character:
                    return ((char)prop.intValue).ToString();
                case SerializedPropertyType.LayerMask:
                    return prop.intValue;
                case SerializedPropertyType.AnimationCurve:
                    var curve = prop.animationCurveValue;
                    return new
                    {
                        keys = curve.keys.Select(k => new
                        {
                            time = k.time,
                            value = k.value,
                            inTangent = k.inTangent,
                            outTangent = k.outTangent
                        }).ToList(),
                        preWrapMode = curve.preWrapMode.ToString(),
                        postWrapMode = curve.postWrapMode.ToString()
                    };
                case SerializedPropertyType.Gradient:
                    // Gradient is complex, return a simplified representation
                    return "[Gradient]";
                case SerializedPropertyType.ExposedReference:
                    return prop.exposedReferenceValue?.name;
                case SerializedPropertyType.Hash128:
                    return prop.hash128Value.ToString();
                default:
                    // For complex types like Generic, ManagedReference, FixedBufferSize, skip
                    return null;
            }
        }
    }

    /// <summary>
    /// Gets dependencies and dependents of an asset.
    /// </summary>
    public class AssetGetDependenciesExecutor : IToolExecutor
    {
        public string ToolId => "asset.get_dependencies";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Get path from either path or guid argument
                var path = context.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
                var guid = context.Arguments.TryGetValue("guid", out var g) ? g?.ToString() : null;
                var direction = context.Arguments.TryGetValue("direction", out var d) ? d?.ToString()?.ToLowerInvariant() : "both";
                var maxDepth = context.Arguments.TryGetValue("maxDepth", out var md) ? Convert.ToInt32(md) : 1;

                // Clamp maxDepth to valid range
                maxDepth = Math.Max(1, Math.Min(10, maxDepth));

                // Resolve path from guid if provided
                if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(guid))
                {
                    path = AssetDatabase.GUIDToAssetPath(guid);
                }

                if (string.IsNullOrEmpty(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Either path or guid is required"));
                }

                // Security check - ensure path is within project bounds
                if (!IsPathWithinProject(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Access denied: Path is outside project"));
                }

                // Verify asset exists
                if (!AssetDatabase.AssetPathExists(path))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Asset not found at path: {path}"));
                }

                // Get the GUID if not already provided
                if (string.IsNullOrEmpty(guid))
                {
                    guid = AssetDatabase.AssetPathToGUID(path);
                }

                var result = new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["guid"] = guid
                };

                // Get dependencies (what this asset depends on)
                if (direction == "dependencies" || direction == "both")
                {
                    var dependencies = GetDependenciesRecursive(path, maxDepth, ct);
                    result["dependencies"] = dependencies;
                }

                // Get dependents (what depends on this asset)
                if (direction == "dependents" || direction == "both")
                {
                    var dependents = GetDependents(path, guid, maxDepth, ct);
                    result["dependents"] = dependents;
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(result));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private bool IsPathWithinProject(string path)
        {
            // Allow Assets and Packages folders
            if (path.StartsWith("Assets/") || path.StartsWith("Assets\\") ||
                path.StartsWith("Packages/") || path.StartsWith("Packages\\"))
            {
                return true;
            }

            // Additional check for absolute paths
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            return fullPath.StartsWith(projectRoot);
        }

        private List<object> GetDependenciesRecursive(string rootPath, int maxDepth, CancellationToken ct)
        {
            var result = new List<object>();
            var visited = new HashSet<string> { rootPath };
            var queue = new Queue<(string path, int depth)>();

            // Get direct dependencies
            var directDeps = AssetDatabase.GetDependencies(rootPath, false);
            foreach (var dep in directDeps)
            {
                if (dep != rootPath && !visited.Contains(dep))
                {
                    visited.Add(dep);
                    queue.Enqueue((dep, 1));
                }
            }

            while (queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var (currentPath, depth) = queue.Dequeue();

                var depInfo = CreateAssetInfo(currentPath, depth);
                result.Add(depInfo);

                // If we haven't reached max depth, continue traversing
                if (depth < maxDepth)
                {
                    var childDeps = AssetDatabase.GetDependencies(currentPath, false);
                    foreach (var childDep in childDeps)
                    {
                        if (!visited.Contains(childDep))
                        {
                            visited.Add(childDep);
                            queue.Enqueue((childDep, depth + 1));
                        }
                    }
                }
            }

            return result;
        }

        private List<object> GetDependents(string targetPath, string targetGuid, int maxDepth, CancellationToken ct)
        {
            var result = new List<object>();
            var visited = new HashSet<string> { targetPath };

            // Find all assets that reference this asset
            // We search through all asset paths in the project
            var allAssetPaths = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/") || p.StartsWith("Packages/"))
                .ToList();

            var directDependents = new List<string>();

            foreach (var assetPath in allAssetPaths)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (assetPath == targetPath)
                    continue;

                // Get dependencies of this asset
                var deps = AssetDatabase.GetDependencies(assetPath, false);
                if (deps.Contains(targetPath))
                {
                    directDependents.Add(assetPath);
                }
            }

            // Build result with depth info
            var queue = new Queue<(string path, int depth)>();
            foreach (var dep in directDependents)
            {
                if (!visited.Contains(dep))
                {
                    visited.Add(dep);
                    queue.Enqueue((dep, 1));
                }
            }

            while (queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var (currentPath, depth) = queue.Dequeue();

                var depInfo = CreateAssetInfo(currentPath, depth);
                result.Add(depInfo);

                // If we haven't reached max depth, find assets that depend on this dependent
                if (depth < maxDepth)
                {
                    foreach (var assetPath in allAssetPaths)
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        if (visited.Contains(assetPath))
                            continue;

                        var deps = AssetDatabase.GetDependencies(assetPath, false);
                        if (deps.Contains(currentPath))
                        {
                            visited.Add(assetPath);
                            queue.Enqueue((assetPath, depth + 1));
                        }
                    }
                }
            }

            return result;
        }

        private object CreateAssetInfo(string path, int depth)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
            var guid = AssetDatabase.AssetPathToGUID(path);

            return new
            {
                path,
                guid,
                name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path),
                type = asset != null ? asset.GetType().Name : "Unknown",
                depth
            };
        }
    }
}
