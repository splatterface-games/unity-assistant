// Scene Tool Executors - Unity scene and hierarchy operations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Gets the hierarchy of the current scene.
    /// </summary>
    public class SceneGetHierarchyExecutor : IToolExecutor
    {
        public string ToolId => "scene.get_hierarchy";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var maxDepth = context.Arguments.TryGetValue("max_depth", out var md) ? Convert.ToInt32(md) : 10;
                var includeComponents = context.Arguments.TryGetValue("include_components", out var ic) && Convert.ToBoolean(ic);

                var scene = SceneManager.GetActiveScene();
                var rootObjects = scene.GetRootGameObjects();

                var hierarchy = rootObjects.Select(go => BuildHierarchyNode(go, 0, maxDepth, includeComponents)).ToList();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    scene = scene.name,
                    path = scene.path,
                    rootCount = rootObjects.Length,
                    hierarchy
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private object BuildHierarchyNode(GameObject go, int depth, int maxDepth, bool includeComponents)
        {
            var node = new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["active"] = go.activeSelf,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["instanceId"] = go.GetInstanceID(),
                // Stable handle for chat links (splatter://obj/<globalId>) and for
                // addressing this object in later tool calls (scene.read_object global_id).
                ["globalId"] = SceneObjectRef.GlobalId(go)
            };

            if (includeComponents)
            {
                var components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => new { type = c.GetType().Name, enabled = IsComponentEnabled(c) })
                    .ToList();
                node["components"] = components;
            }

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = new List<object>();
                for (var i = 0; i < go.transform.childCount; i++)
                {
                    children.Add(BuildHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth, includeComponents));
                }
                node["children"] = children;
            }
            else if (go.transform.childCount > 0)
            {
                node["childCount"] = go.transform.childCount;
            }

            return node;
        }

        private bool IsComponentEnabled(Component c)
        {
            if (c is Behaviour b) return b.enabled;
            if (c is Renderer r) return r.enabled;
            if (c is Collider col) return col.enabled;
            return true;
        }
    }

    /// <summary>
    /// Creates a new GameObject in the scene.
    /// </summary>
    public class SceneCreateGameObjectExecutor : IToolExecutor
    {
        public string ToolId => "scene.create_gameobject";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var name = context.Arguments.TryGetValue("name", out var n) ? n?.ToString() : "New GameObject";
                var parentId = context.Arguments.TryGetValue("parent_id", out var pid) ? Convert.ToInt32(pid) : 0;
                var primitiveType = context.Arguments.TryGetValue("primitive", out var pt) ? pt?.ToString() : null;
                var components = context.Arguments.TryGetValue("components", out var comps) ? comps as List<object> : null;

                GameObject go;

                if (!string.IsNullOrEmpty(primitiveType) && Enum.TryParse<PrimitiveType>(primitiveType, true, out var primitive))
                {
                    go = GameObject.CreatePrimitive(primitive);
                    go.name = name;
                }
                else
                {
                    go = new GameObject(name);
                }

                // Set parent if specified
                if (parentId != 0)
                {
                    var parent = EditorUtility.InstanceIDToObject(parentId) as GameObject;
                    if (parent != null)
                    {
                        go.transform.SetParent(parent.transform, false);
                    }
                }

                // Add requested components
                if (components != null)
                {
                    foreach (var comp in components)
                    {
                        var typeName = comp?.ToString();
                        if (!string.IsNullOrEmpty(typeName))
                        {
                            var type = FindComponentType(typeName);
                            if (type != null)
                            {
                                go.AddComponent(type);
                            }
                        }
                    }
                }

                // Set transform properties
                if (context.Arguments.TryGetValue("position", out var pos) && pos is List<object> posList && posList.Count >= 3)
                {
                    go.transform.position = new Vector3(
                        Convert.ToSingle(posList[0]),
                        Convert.ToSingle(posList[1]),
                        Convert.ToSingle(posList[2]));
                }

                if (context.Arguments.TryGetValue("rotation", out var rot) && rot is List<object> rotList && rotList.Count >= 3)
                {
                    go.transform.eulerAngles = new Vector3(
                        Convert.ToSingle(rotList[0]),
                        Convert.ToSingle(rotList[1]),
                        Convert.ToSingle(rotList[2]));
                }

                if (context.Arguments.TryGetValue("scale", out var scale) && scale is List<object> scaleList && scaleList.Count >= 3)
                {
                    go.transform.localScale = new Vector3(
                        Convert.ToSingle(scaleList[0]),
                        Convert.ToSingle(scaleList[1]),
                        Convert.ToSingle(scaleList[2]));
                }

                Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    name = go.name,
                    instanceId = go.GetInstanceID(),
                    globalId = SceneObjectRef.GlobalId(go),
                    path = GetGameObjectPath(go)
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Type FindComponentType(string typeName)
        {
            // Try common Unity types first
            var unityType = Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule");
            if (unityType != null) return unityType;

            // Try all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName) ?? assembly.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }

    /// <summary>
    /// Modifies an existing GameObject in the scene.
    /// </summary>
    public class SceneModifyGameObjectExecutor : IToolExecutor
    {
        public string ToolId => "scene.modify_gameobject";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instance_id", out var id) ? Convert.ToInt32(id) : 0;
                var path = context.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;

                GameObject go = null;

                if (instanceId != 0)
                {
                    go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }
                else if (!string.IsNullOrEmpty(path))
                {
                    go = GameObject.Find(path);
                }

                if (go == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                Undo.RecordObject(go, "Modify GameObject");

                // Apply modifications
                if (context.Arguments.TryGetValue("name", out var name))
                {
                    go.name = name.ToString();
                }

                if (context.Arguments.TryGetValue("active", out var active))
                {
                    go.SetActive(Convert.ToBoolean(active));
                }

                if (context.Arguments.TryGetValue("tag", out var tag))
                {
                    go.tag = tag.ToString();
                }

                if (context.Arguments.TryGetValue("layer", out var layer))
                {
                    go.layer = LayerMask.NameToLayer(layer.ToString());
                }

                // Transform modifications
                Undo.RecordObject(go.transform, "Modify Transform");

                if (context.Arguments.TryGetValue("position", out var pos) && pos is List<object> posList && posList.Count >= 3)
                {
                    go.transform.position = new Vector3(
                        Convert.ToSingle(posList[0]),
                        Convert.ToSingle(posList[1]),
                        Convert.ToSingle(posList[2]));
                }

                if (context.Arguments.TryGetValue("local_position", out var lpos) && lpos is List<object> lposList && lposList.Count >= 3)
                {
                    go.transform.localPosition = new Vector3(
                        Convert.ToSingle(lposList[0]),
                        Convert.ToSingle(lposList[1]),
                        Convert.ToSingle(lposList[2]));
                }

                if (context.Arguments.TryGetValue("rotation", out var rot) && rot is List<object> rotList && rotList.Count >= 3)
                {
                    go.transform.eulerAngles = new Vector3(
                        Convert.ToSingle(rotList[0]),
                        Convert.ToSingle(rotList[1]),
                        Convert.ToSingle(rotList[2]));
                }

                if (context.Arguments.TryGetValue("scale", out var scale) && scale is List<object> scaleList && scaleList.Count >= 3)
                {
                    go.transform.localScale = new Vector3(
                        Convert.ToSingle(scaleList[0]),
                        Convert.ToSingle(scaleList[1]),
                        Convert.ToSingle(scaleList[2]));
                }

                // Component modifications
                if (context.Arguments.TryGetValue("add_components", out var addComps) && addComps is List<object> addList)
                {
                    foreach (var comp in addList)
                    {
                        var typeName = comp?.ToString();
                        var type = FindComponentType(typeName);
                        if (type != null)
                        {
                            Undo.AddComponent(go, type);
                        }
                    }
                }

                if (context.Arguments.TryGetValue("remove_components", out var removeComps) && removeComps is List<object> removeList)
                {
                    foreach (var comp in removeList)
                    {
                        var typeName = comp?.ToString();
                        var type = FindComponentType(typeName);
                        if (type != null)
                        {
                            var component = go.GetComponent(type);
                            if (component != null)
                            {
                                Undo.DestroyObjectImmediate(component);
                            }
                        }
                    }
                }

                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    instanceId = go.GetInstanceID(),
                    globalId = SceneObjectRef.GlobalId(go),
                    name = go.name,
                    modified = true
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Type FindComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            var unityType = Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule");
            if (unityType != null) return unityType;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName) ?? assembly.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }
    }

    /// <summary>
    /// Finds GameObjects in the scene by name, tag, layer, or component.
    /// Supports wildcard matching for names (* and ?).
    /// </summary>
    public class SceneFindObjectsExecutor : IToolExecutor
    {
        public string ToolId => "scene.find_objects";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Parse input arguments
                var namePattern = context.Arguments.TryGetValue("name", out var n) ? n?.ToString() : null;
                var tag = context.Arguments.TryGetValue("tag", out var t) ? t?.ToString() : null;
                var layer = context.Arguments.TryGetValue("layer", out var l) ? Convert.ToInt32(l) : -1;
                var componentType = context.Arguments.TryGetValue("componentType", out var ct2) ? ct2?.ToString() : null;
                var scenePath = context.Arguments.TryGetValue("scenePath", out var sp) ? sp?.ToString() : null;
                var maxResults = context.Arguments.TryGetValue("maxResults", out var mr) ? Convert.ToInt32(mr) : 100;
                var referencesGlobalId = context.Arguments.TryGetValue("references_global_id", out var rg) ? rg?.ToString() : null;

                // Clamp maxResults to valid range
                maxResults = Math.Clamp(maxResults, 1, 500);

                // Resolve the asset/object we're looking for usages of (e.g. a mesh).
                Object referencedObject = null;
                if (!string.IsNullOrEmpty(referencesGlobalId))
                {
                    referencedObject = SceneObjectRef.Resolve(referencesGlobalId);
                    if (referencedObject == null)
                        return Task.FromResult(ToolExecutionResult.Failed($"Referenced object not found: {referencesGlobalId}"));
                }

                // Get the target scene
                Scene targetScene;
                if (!string.IsNullOrEmpty(scenePath))
                {
                    targetScene = SceneManager.GetSceneByPath(scenePath);
                    if (!targetScene.IsValid() || !targetScene.isLoaded)
                    {
                        return Task.FromResult(ToolExecutionResult.Failed($"Scene not found or not loaded: {scenePath}"));
                    }
                }
                else
                {
                    targetScene = SceneManager.GetActiveScene();
                }

                // Find the component type if specified
                Type requiredComponentType = null;
                if (!string.IsNullOrEmpty(componentType))
                {
                    requiredComponentType = FindComponentType(componentType);
                    if (requiredComponentType == null)
                    {
                        return Task.FromResult(ToolExecutionResult.Failed($"Component type not found: {componentType}"));
                    }
                }

                // Build the regex pattern for wildcard matching
                Regex nameRegex = null;
                if (!string.IsNullOrEmpty(namePattern))
                {
                    var regexPattern = WildcardToRegex(namePattern);
                    nameRegex = new Regex(regexPattern, RegexOptions.IgnoreCase);
                }

                // Collect all GameObjects to search
                IEnumerable<GameObject> allObjects;
                if (requiredComponentType != null)
                {
                    // Use FindObjectsByType when filtering by component (more efficient)
                    allObjects = Object.FindObjectsByType(requiredComponentType, FindObjectsSortMode.None)
                        .Cast<Component>()
                        .Where(c => c != null && c.gameObject != null)
                        .Select(c => c.gameObject)
                        .Distinct();
                }
                else
                {
                    // Get all GameObjects including inactive ones
                    allObjects = Resources.FindObjectsOfTypeAll<GameObject>()
                        .Where(go => go != null && !EditorUtility.IsPersistent(go) && go.hideFlags == HideFlags.None);
                }

                // Filter by scene
                allObjects = allObjects.Where(go => go.scene == targetScene);

                // Apply filters
                var filteredObjects = allObjects.Where(go =>
                {
                    // Name filter with wildcard support
                    if (nameRegex != null && !nameRegex.IsMatch(go.name))
                        return false;

                    // Tag filter
                    if (!string.IsNullOrEmpty(tag) && !go.CompareTag(tag))
                        return false;

                    // Layer filter
                    if (layer >= 0 && go.layer != layer)
                        return false;

                    // Component filter (already filtered if requiredComponentType was used)
                    if (requiredComponentType != null && go.GetComponent(requiredComponentType) == null)
                        return false;

                    // "Uses this asset" filter: keep objects with any component that
                    // references the target object (e.g. a MeshFilter whose mesh is it).
                    if (referencedObject != null && !GameObjectReferences(go, referencedObject))
                        return false;

                    return true;
                })
                .Take(maxResults)
                .ToList();

                // Build result objects
                var resultObjects = filteredObjects.Select(go => new
                {
                    name = go.name,
                    path = GetGameObjectPath(go),
                    instanceId = go.GetInstanceID(),
                    globalId = SceneObjectRef.GlobalId(go),
                    components = go.GetComponents<Component>()
                        .Where(c => c != null)
                        .Select(c => c.GetType().Name)
                        .ToArray(),
                    tag = go.tag,
                    layer = go.layer,
                    layerName = LayerMask.LayerToName(go.layer)
                }).ToList();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    objects = resultObjects,
                    count = resultObjects.Count,
                    totalFound = filteredObjects.Count,
                    maxResults,
                    scene = targetScene.name
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string WildcardToRegex(string pattern)
        {
            // Escape regex special characters except * and ?
            var escaped = Regex.Escape(pattern);
            // Replace escaped wildcards with regex equivalents
            escaped = escaped.Replace("\\*", ".*").Replace("\\?", ".");
            // Anchor the pattern to match the whole string
            return "^" + escaped + "$";
        }

        // True if any component on the GameObject has a serialized object-reference field
        // pointing at the target (e.g. a MeshFilter whose sharedMesh is the given mesh,
        // or a Renderer referencing a material). Iterates serialized properties so it
        // covers arrays (materials[]) and nested references.
        private static bool GameObjectReferences(GameObject go, Object target)
        {
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue;
                using var so = new SerializedObject(component);
                var prop = so.GetIterator();
                while (prop.NextVisible(true))
                {
                    if (prop.propertyType == SerializedPropertyType.ObjectReference &&
                        prop.objectReferenceValue == target)
                        return true;
                }
            }
            return false;
        }

        private Type FindComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // Try UnityEngine namespace first
            var unityType = Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule");
            if (unityType != null) return unityType;

            // Try UnityEngine.UI namespace
            unityType = Type.GetType($"UnityEngine.UI.{typeName}, UnityEngine.UI");
            if (unityType != null) return unityType;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Try direct type name
                var type = assembly.GetType(typeName);
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;

                // Try with UnityEngine prefix
                type = assembly.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }

    /// <summary>
    /// Reads detailed information about a specific GameObject including its components and properties.
    /// </summary>
    public class SceneReadObjectExecutor : IToolExecutor
    {
        public string ToolId => "scene.read_object";

        // Types to skip when serializing properties (to avoid infinite recursion or large outputs)
        private static readonly HashSet<Type> SkipPropertyTypes = new HashSet<Type>
        {
            typeof(Mesh),
            typeof(Material[]),
            typeof(Texture),
            typeof(Texture2D),
            typeof(RenderTexture),
            typeof(Sprite),
            typeof(AnimationClip),
            typeof(AudioClip),
            typeof(GameObject),
            typeof(Transform)
        };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Parse input arguments
                var globalId = context.Arguments.TryGetValue("global_id", out var gid) ? gid?.ToString()
                    : context.Arguments.TryGetValue("globalId", out var gid2) ? gid2?.ToString() : null;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var op) ? op?.ToString() : null;
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var includeChildren = context.Arguments.TryGetValue("includeChildren", out var ic) && Convert.ToBoolean(ic);
                var maxDepth = context.Arguments.TryGetValue("maxDepth", out var md) ? Convert.ToInt32(md) : 2;

                // Clamp maxDepth to valid range
                maxDepth = Math.Clamp(maxDepth, 1, 5);

                // Find the GameObject. global_id (the durable handle from get_hierarchy /
                // a context attachment) takes precedence, then instanceId, then path.
                GameObject targetObject = null;

                if (!string.IsNullOrEmpty(globalId))
                {
                    targetObject = SceneObjectRef.Resolve(globalId) as GameObject;
                }
                else if (instanceId != 0)
                {
                    targetObject = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }
                else if (!string.IsNullOrEmpty(objectPath))
                {
                    targetObject = GameObject.Find(objectPath);

                    // If not found by path, try searching through all objects
                    if (targetObject == null)
                    {
                        targetObject = FindObjectByPath(objectPath);
                    }
                }

                if (targetObject == null)
                {
                    var identifier = !string.IsNullOrEmpty(globalId) ? $"global_id '{globalId}'"
                        : instanceId != 0 ? $"instanceId {instanceId}" : $"path '{objectPath}'";
                    return Task.FromResult(ToolExecutionResult.Failed($"GameObject not found: {identifier}"));
                }

                // Build detailed object info
                var objectInfo = BuildObjectInfo(targetObject, includeChildren, 0, maxDepth);

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    @object = objectInfo
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private GameObject FindObjectByPath(string path)
        {
            // Try to find the object by traversing the hierarchy
            var parts = path.Split('/');
            if (parts.Length == 0) return null;

            // Find root objects
            var rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
            GameObject current = null;

            foreach (var rootGo in rootObjects)
            {
                if (rootGo.name == parts[0])
                {
                    current = rootGo;
                    break;
                }
            }

            if (current == null) return null;

            // Traverse path
            for (int i = 1; i < parts.Length; i++)
            {
                var child = current.transform.Find(parts[i]);
                if (child == null) return null;
                current = child.gameObject;
            }

            return current;
        }

        private object BuildObjectInfo(GameObject go, bool includeChildren, int currentDepth, int maxDepth)
        {
            var transform = go.transform;

            var info = new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["path"] = GetGameObjectPath(go),
                ["instanceId"] = go.GetInstanceID(),
                ["globalId"] = SceneObjectRef.GlobalId(go),
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["isStatic"] = go.isStatic,
                ["tag"] = go.tag,
                ["layer"] = go.layer,
                ["layerName"] = LayerMask.LayerToName(go.layer),
                ["transform"] = new
                {
                    position = Vector3ToDict(transform.position),
                    localPosition = Vector3ToDict(transform.localPosition),
                    rotation = Vector3ToDict(transform.eulerAngles),
                    localRotation = Vector3ToDict(transform.localEulerAngles),
                    scale = Vector3ToDict(transform.localScale),
                    lossyScale = Vector3ToDict(transform.lossyScale)
                },
                ["components"] = GetComponentsInfo(go)
            };

            // Include children if requested and within depth limit
            if (includeChildren && currentDepth < maxDepth && transform.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < transform.childCount; i++)
                {
                    var child = transform.GetChild(i).gameObject;
                    children.Add(BuildObjectInfo(child, true, currentDepth + 1, maxDepth));
                }
                info["children"] = children;
            }
            else if (transform.childCount > 0)
            {
                info["childCount"] = transform.childCount;
            }

            return info;
        }

        private List<object> GetComponentsInfo(GameObject go)
        {
            var components = go.GetComponents<Component>();
            var result = new List<object>();

            foreach (var component in components)
            {
                if (component == null) continue;

                // Skip Transform as it's already included separately
                if (component is Transform) continue;

                var componentInfo = new Dictionary<string, object>
                {
                    ["type"] = component.GetType().Name,
                    ["fullType"] = component.GetType().FullName,
                    ["enabled"] = IsComponentEnabled(component),
                    ["instanceId"] = component.GetInstanceID()
                };

                // Get serialized properties using SerializedObject
                try
                {
                    var serializedProperties = GetSerializedProperties(component);
                    if (serializedProperties.Count > 0)
                    {
                        componentInfo["properties"] = serializedProperties;
                    }
                }
                catch
                {
                    // If serialization fails, try reflection fallback
                    var reflectionProperties = GetReflectionProperties(component);
                    if (reflectionProperties.Count > 0)
                    {
                        componentInfo["properties"] = reflectionProperties;
                    }
                }

                result.Add(componentInfo);
            }

            return result;
        }

        private Dictionary<string, object> GetSerializedProperties(Component component)
        {
            var result = new Dictionary<string, object>();
            var serializedObject = new SerializedObject(component);
            var property = serializedObject.GetIterator();

            // Enter the first child
            if (property.NextVisible(true))
            {
                do
                {
                    // Skip script reference
                    if (property.name == "m_Script") continue;

                    var value = GetSerializedPropertyValue(property);
                    if (value != null)
                    {
                        result[property.name] = value;
                    }
                }
                while (property.NextVisible(false));
            }

            return result;
        }

        private object GetSerializedPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue;

                case SerializedPropertyType.Boolean:
                    return property.boolValue;

                case SerializedPropertyType.Float:
                    return property.floatValue;

                case SerializedPropertyType.String:
                    return property.stringValue;

                case SerializedPropertyType.Color:
                    var color = property.colorValue;
                    return new { r = color.r, g = color.g, b = color.b, a = color.a };

                case SerializedPropertyType.Vector2:
                    return Vector2ToDict(property.vector2Value);

                case SerializedPropertyType.Vector3:
                    return Vector3ToDict(property.vector3Value);

                case SerializedPropertyType.Vector4:
                    var v4 = property.vector4Value;
                    return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };

                case SerializedPropertyType.Rect:
                    var rect = property.rectValue;
                    return new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };

                case SerializedPropertyType.Bounds:
                    var bounds = property.boundsValue;
                    return new
                    {
                        center = Vector3ToDict(bounds.center),
                        size = Vector3ToDict(bounds.size)
                    };

                case SerializedPropertyType.Quaternion:
                    var quat = property.quaternionValue;
                    return new { x = quat.x, y = quat.y, z = quat.z, w = quat.w };

                case SerializedPropertyType.Enum:
                    return property.enumDisplayNames.Length > property.enumValueIndex && property.enumValueIndex >= 0
                        ? property.enumDisplayNames[property.enumValueIndex]
                        : property.enumValueIndex.ToString();

                case SerializedPropertyType.ObjectReference:
                    var obj = property.objectReferenceValue;
                    if (obj == null) return null;
                    return new
                    {
                        name = obj.name,
                        type = obj.GetType().Name,
                        instanceId = obj.GetInstanceID()
                    };

                case SerializedPropertyType.LayerMask:
                    return property.intValue;

                case SerializedPropertyType.ArraySize:
                    return property.intValue;

                case SerializedPropertyType.AnimationCurve:
                    var curve = property.animationCurveValue;
                    return new { length = curve.length, preWrapMode = curve.preWrapMode.ToString(), postWrapMode = curve.postWrapMode.ToString() };

                default:
                    // For complex types, just return the type name
                    return $"[{property.propertyType}]";
            }
        }

        private Dictionary<string, object> GetReflectionProperties(Component component)
        {
            var result = new Dictionary<string, object>();
            var type = component.GetType();

            // Get public fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                try
                {
                    if (SkipPropertyTypes.Contains(field.FieldType)) continue;
                    if (field.FieldType.IsSubclassOf(typeof(Object))) continue;

                    var value = field.GetValue(component);
                    var serializedValue = SerializeValue(value);
                    if (serializedValue != null)
                    {
                        result[field.Name] = serializedValue;
                    }
                }
                catch
                {
                    // Skip fields that can't be read
                }
            }

            // Get public properties with getters
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                try
                {
                    if (!prop.CanRead) continue;
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (SkipPropertyTypes.Contains(prop.PropertyType)) continue;
                    if (prop.PropertyType.IsSubclassOf(typeof(Object))) continue;

                    var value = prop.GetValue(component);
                    var serializedValue = SerializeValue(value);
                    if (serializedValue != null)
                    {
                        result[prop.Name] = serializedValue;
                    }
                }
                catch
                {
                    // Skip properties that can't be read
                }
            }

            return result;
        }

        private object SerializeValue(object value)
        {
            if (value == null) return null;

            var type = value.GetType();

            if (type.IsPrimitive || value is string)
                return value;

            if (value is Vector2 v2)
                return Vector2ToDict(v2);

            if (value is Vector3 v3)
                return Vector3ToDict(v3);

            if (value is Vector4 v4)
                return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };

            if (value is Quaternion q)
                return new { x = q.x, y = q.y, z = q.z, w = q.w };

            if (value is Color c)
                return new { r = c.r, g = c.g, b = c.b, a = c.a };

            if (value is Color32 c32)
                return new { r = c32.r, g = c32.g, b = c32.b, a = c32.a };

            if (value is Rect rect)
                return new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };

            if (value is Bounds bounds)
                return new { center = Vector3ToDict(bounds.center), size = Vector3ToDict(bounds.size) };

            if (type.IsEnum)
                return value.ToString();

            // For other types, just return type name
            return $"[{type.Name}]";
        }

        private bool IsComponentEnabled(Component c)
        {
            if (c is Behaviour b) return b.enabled;
            if (c is Renderer r) return r.enabled;
            if (c is Collider col) return col.enabled;
            if (c is LODGroup lod) return lod.enabled;
            return true;
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private object Vector2ToDict(Vector2 v)
        {
            return new { x = v.x, y = v.y };
        }

        private object Vector3ToDict(Vector3 v)
        {
            return new { x = v.x, y = v.y, z = v.z };
        }
    }

    /// <summary>
    /// Deletes a GameObject from the scene.
    /// </summary>
    public class SceneDeleteGameObjectExecutor : IToolExecutor
    {
        public string ToolId => "scene.delete_gameobject";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var p) ? p?.ToString() : null;

                GameObject go = null;

                // Try to find by instanceId first
                if (instanceId != 0)
                {
                    go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                // Fall back to path if instanceId didn't work
                if (go == null && !string.IsNullOrEmpty(objectPath))
                {
                    go = GameObject.Find(objectPath);
                }

                if (go == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                var deletedName = go.name;
                var deletedPath = GetGameObjectPath(go);

                // Use Undo.DestroyObjectImmediate for undo support
                Undo.DestroyObjectImmediate(go);

                // Mark scene dirty after deletion
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    success = true,
                    deletedName,
                    deletedPath
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }

    /// <summary>
    /// Adds a component to a GameObject in the scene.
    /// </summary>
    public class SceneAddComponentExecutor : IToolExecutor
    {
        public string ToolId => "scene.add_component";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var p) ? p?.ToString() : null;
                var componentType = context.Arguments.TryGetValue("componentType", out var compType) ? compType?.ToString() : null;

                if (string.IsNullOrEmpty(componentType))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("componentType is required"));
                }

                GameObject go = null;

                // Try to find by instanceId first
                if (instanceId != 0)
                {
                    go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                // Fall back to path if instanceId didn't work
                if (go == null && !string.IsNullOrEmpty(objectPath))
                {
                    go = GameObject.Find(objectPath);
                }

                if (go == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                // Find the component type
                var type = FindComponentType(componentType);
                if (type == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Component type '{componentType}' not found"));
                }

                // Use Undo.AddComponent for undo support
                var component = Undo.AddComponent(go, type);

                if (component == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Failed to add component '{componentType}'"));
                }

                // Mark scene dirty
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    success = true,
                    componentId = component.GetInstanceID(),
                    componentType = component.GetType().Name,
                    gameObjectName = go.name
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Type FindComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

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
                var unityType = Type.GetType($"UnityEngine.{typeName}, {module}");
                if (unityType != null && typeof(Component).IsAssignableFrom(unityType))
                    return unityType;
            }

            // Try all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

            return null;
        }
    }

    /// <summary>
    /// Sets a property on a component attached to a GameObject.
    /// </summary>
    public class SceneSetComponentPropertyExecutor : IToolExecutor
    {
        public string ToolId => "scene.set_component_property";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var p) ? p?.ToString() : null;
                var componentType = context.Arguments.TryGetValue("componentType", out var compType) ? compType?.ToString() : null;
                var propertyPath = context.Arguments.TryGetValue("propertyPath", out var pp) ? pp?.ToString() : null;
                context.Arguments.TryGetValue("value", out var value);

                if (string.IsNullOrEmpty(componentType))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("componentType is required"));
                }

                if (string.IsNullOrEmpty(propertyPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("propertyPath is required"));
                }

                GameObject go = null;

                // Try to find by instanceId first
                if (instanceId != 0)
                {
                    go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                // Fall back to path if instanceId didn't work
                if (go == null && !string.IsNullOrEmpty(objectPath))
                {
                    go = GameObject.Find(objectPath);
                }

                if (go == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                // Find the component type
                var type = FindComponentType(componentType);
                if (type == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Component type '{componentType}' not found"));
                }

                // Get the component
                var component = go.GetComponent(type);
                if (component == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Component '{componentType}' not found on GameObject"));
                }

                // Use Undo.RecordObject before changes
                Undo.RecordObject(component, $"Set {componentType}.{propertyPath}");

                // Use SerializedObject/SerializedProperty for property access
                var serializedObject = new SerializedObject(component);

                // Handle nested property paths (e.g., "material.color" -> "m_Material.m_Color")
                var serializedProperty = FindSerializedProperty(serializedObject, propertyPath);

                if (serializedProperty == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Property '{propertyPath}' not found on component '{componentType}'"));
                }

                // Set the property value based on type
                if (!SetPropertyValue(serializedProperty, value))
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Failed to set property value for '{propertyPath}'"));
                }

                // Apply changes
                serializedObject.ApplyModifiedProperties();

                // Mark scene dirty
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    success = true,
                    gameObjectName = go.name,
                    componentType = component.GetType().Name,
                    propertyPath,
                    newValue = GetPropertyDisplayValue(serializedProperty)
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private SerializedProperty FindSerializedProperty(SerializedObject serializedObject, string propertyPath)
        {
            // First try direct path
            var property = serializedObject.FindProperty(propertyPath);
            if (property != null) return property;

            // Try with "m_" prefix (Unity's internal naming convention)
            if (propertyPath.Length > 0)
            {
                property = serializedObject.FindProperty("m_" + char.ToUpper(propertyPath[0]) + propertyPath.Substring(1));
                if (property != null) return property;
            }

            // Handle nested paths like "size" for BoxCollider -> "m_Size"
            var commonMappings = new Dictionary<string, string>
            {
                { "size", "m_Size" },
                { "center", "m_Center" },
                { "radius", "m_Radius" },
                { "height", "m_Height" },
                { "direction", "m_Direction" },
                { "isTrigger", "m_IsTrigger" },
                { "material", "m_Material" },
                { "mass", "m_Mass" },
                { "drag", "m_Drag" },
                { "angularDrag", "m_AngularDrag" },
                { "useGravity", "m_UseGravity" },
                { "isKinematic", "m_IsKinematic" },
                { "interpolate", "m_Interpolate" },
                { "collisionDetectionMode", "m_CollisionDetection" },
                { "constraints", "m_Constraints" },
                { "enabled", "m_Enabled" },
                { "color", "m_Color" },
                { "intensity", "m_Intensity" },
                { "range", "m_Range" },
                { "spotAngle", "m_SpotAngle" },
                { "castShadows", "m_Shadows.m_Type" },
                { "shadowStrength", "m_Shadows.m_Strength" }
            };

            // Handle nested property paths (e.g., "material.color")
            var parts = propertyPath.Split('.');
            if (parts.Length > 1)
            {
                var currentPath = "";
                foreach (var part in parts)
                {
                    var mappedPart = commonMappings.TryGetValue(part, out var mapped) ? mapped : part;
                    currentPath = string.IsNullOrEmpty(currentPath) ? mappedPart : currentPath + "." + mappedPart;
                }
                property = serializedObject.FindProperty(currentPath);
                if (property != null) return property;
            }

            // Try the mapped property name
            if (commonMappings.TryGetValue(propertyPath, out var mappedPath))
            {
                property = serializedObject.FindProperty(mappedPath);
                if (property != null) return property;
            }

            // Try lowercase with m_ prefix
            property = serializedObject.FindProperty("m_" + propertyPath);
            if (property != null) return property;

            return null;
        }

        private bool SetPropertyValue(SerializedProperty property, object value)
        {
            if (value == null) return false;

            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        property.intValue = Convert.ToInt32(value);
                        return true;

                    case SerializedPropertyType.Boolean:
                        property.boolValue = Convert.ToBoolean(value);
                        return true;

                    case SerializedPropertyType.Float:
                        property.floatValue = Convert.ToSingle(value);
                        return true;

                    case SerializedPropertyType.String:
                        property.stringValue = value.ToString();
                        return true;

                    case SerializedPropertyType.Color:
                        if (value is List<object> colorList && colorList.Count >= 3)
                        {
                            property.colorValue = new Color(
                                Convert.ToSingle(colorList[0]),
                                Convert.ToSingle(colorList[1]),
                                Convert.ToSingle(colorList[2]),
                                colorList.Count >= 4 ? Convert.ToSingle(colorList[3]) : 1f);
                            return true;
                        }
                        return false;

                    case SerializedPropertyType.Vector2:
                        if (value is List<object> v2List && v2List.Count >= 2)
                        {
                            property.vector2Value = new Vector2(
                                Convert.ToSingle(v2List[0]),
                                Convert.ToSingle(v2List[1]));
                            return true;
                        }
                        return false;

                    case SerializedPropertyType.Vector3:
                        if (value is List<object> v3List && v3List.Count >= 3)
                        {
                            property.vector3Value = new Vector3(
                                Convert.ToSingle(v3List[0]),
                                Convert.ToSingle(v3List[1]),
                                Convert.ToSingle(v3List[2]));
                            return true;
                        }
                        return false;

                    case SerializedPropertyType.Vector4:
                        if (value is List<object> v4List && v4List.Count >= 4)
                        {
                            property.vector4Value = new Vector4(
                                Convert.ToSingle(v4List[0]),
                                Convert.ToSingle(v4List[1]),
                                Convert.ToSingle(v4List[2]),
                                Convert.ToSingle(v4List[3]));
                            return true;
                        }
                        return false;

                    case SerializedPropertyType.Rect:
                        if (value is List<object> rectList && rectList.Count >= 4)
                        {
                            property.rectValue = new Rect(
                                Convert.ToSingle(rectList[0]),
                                Convert.ToSingle(rectList[1]),
                                Convert.ToSingle(rectList[2]),
                                Convert.ToSingle(rectList[3]));
                            return true;
                        }
                        return false;

                    case SerializedPropertyType.Bounds:
                        if (value is List<object> boundsList && boundsList.Count >= 6)
                        {
                            property.boundsValue = new Bounds(
                                new Vector3(
                                    Convert.ToSingle(boundsList[0]),
                                    Convert.ToSingle(boundsList[1]),
                                    Convert.ToSingle(boundsList[2])),
                                new Vector3(
                                    Convert.ToSingle(boundsList[3]),
                                    Convert.ToSingle(boundsList[4]),
                                    Convert.ToSingle(boundsList[5])));
                            return true;
                        }
                        return false;

                    case SerializedPropertyType.Quaternion:
                        if (value is List<object> quatList && quatList.Count >= 4)
                        {
                            property.quaternionValue = new Quaternion(
                                Convert.ToSingle(quatList[0]),
                                Convert.ToSingle(quatList[1]),
                                Convert.ToSingle(quatList[2]),
                                Convert.ToSingle(quatList[3]));
                            return true;
                        }
                        return false;

                    case SerializedPropertyType.Enum:
                        if (value is int intVal)
                        {
                            property.enumValueIndex = intVal;
                            return true;
                        }
                        else if (value is string strVal)
                        {
                            var enumNames = property.enumNames;
                            for (var i = 0; i < enumNames.Length; i++)
                            {
                                if (string.Equals(enumNames[i], strVal, StringComparison.OrdinalIgnoreCase))
                                {
                                    property.enumValueIndex = i;
                                    return true;
                                }
                            }
                        }
                        return false;

                    case SerializedPropertyType.LayerMask:
                        property.intValue = Convert.ToInt32(value);
                        return true;

                    case SerializedPropertyType.AnimationCurve:
                        // AnimationCurve requires more complex handling
                        return false;

                    case SerializedPropertyType.ObjectReference:
                        // Object references require finding the object first
                        if (value is int objId && objId != 0)
                        {
                            var obj = EditorUtility.InstanceIDToObject(objId);
                            if (obj != null)
                            {
                                property.objectReferenceValue = obj;
                                return true;
                            }
                        }
                        return false;

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private object GetPropertyDisplayValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue;
                case SerializedPropertyType.Boolean:
                    return property.boolValue;
                case SerializedPropertyType.Float:
                    return property.floatValue;
                case SerializedPropertyType.String:
                    return property.stringValue;
                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    return new[] { c.r, c.g, c.b, c.a };
                case SerializedPropertyType.Vector2:
                    var v2 = property.vector2Value;
                    return new[] { v2.x, v2.y };
                case SerializedPropertyType.Vector3:
                    var v3 = property.vector3Value;
                    return new[] { v3.x, v3.y, v3.z };
                case SerializedPropertyType.Vector4:
                    var v4 = property.vector4Value;
                    return new[] { v4.x, v4.y, v4.z, v4.w };
                case SerializedPropertyType.Enum:
                    return property.enumNames[property.enumValueIndex];
                default:
                    return property.propertyType.ToString();
            }
        }

        private Type FindComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

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
                var unityType = Type.GetType($"UnityEngine.{typeName}, {module}");
                if (unityType != null && typeof(Component).IsAssignableFrom(unityType))
                    return unityType;
            }

            // Try all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

            return null;
        }
    }

    /// <summary>
    /// Removes a component from a GameObject in the scene.
    /// </summary>
    public class SceneRemoveComponentExecutor : IToolExecutor
    {
        public string ToolId => "scene.remove_component";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var p) ? p?.ToString() : null;
                var componentType = context.Arguments.TryGetValue("componentType", out var compType) ? compType?.ToString() : null;
                var componentIndex = context.Arguments.TryGetValue("componentIndex", out var ci) ? Convert.ToInt32(ci) : -1;

                if (string.IsNullOrEmpty(componentType) && componentIndex < 0)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Either componentType or componentIndex is required"));
                }

                GameObject go = null;

                if (instanceId != 0)
                {
                    go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                if (go == null && !string.IsNullOrEmpty(objectPath))
                {
                    go = GameObject.Find(objectPath);
                }

                if (go == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                Component componentToRemove = null;

                if (componentIndex >= 0)
                {
                    var components = go.GetComponents<Component>().Where(c => c != null && !(c is Transform)).ToArray();
                    if (componentIndex < components.Length)
                    {
                        componentToRemove = components[componentIndex];
                    }
                }
                else if (!string.IsNullOrEmpty(componentType))
                {
                    var type = FindComponentType(componentType);
                    if (type == null)
                    {
                        return Task.FromResult(ToolExecutionResult.Failed($"Component type '{componentType}' not found"));
                    }
                    componentToRemove = go.GetComponent(type);
                }

                if (componentToRemove == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Component not found on GameObject"));
                }

                if (componentToRemove is Transform)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Cannot remove Transform component"));
                }

                var removedType = componentToRemove.GetType().Name;

                // Use Undo for removal
                Undo.DestroyObjectImmediate(componentToRemove);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    success = true,
                    removedComponentType = removedType,
                    gameObjectName = go.name
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Type FindComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

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
                var unityType = Type.GetType($"UnityEngine.{typeName}, {module}");
                if (unityType != null && typeof(Component).IsAssignableFrom(unityType))
                    return unityType;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;

                type = assembly.GetType($"UnityEngine.UI.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;

                type = assembly.GetType(typeName);
                if (type != null && typeof(Component).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }
    }

    /// <summary>
    /// Gets visible objects from the scene view (objects visible to scene camera).
    /// </summary>
    public class SceneGetVisibleObjectsExecutor : IToolExecutor
    {
        public string ToolId => "scene.get_visible_objects";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var maxResults = context.Arguments.TryGetValue("maxResults", out var mr) ? Convert.ToInt32(mr) : 100;
                var includeInactive = context.Arguments.TryGetValue("includeInactive", out var ii) && Convert.ToBoolean(ii);
                var layerMask = context.Arguments.TryGetValue("layerMask", out var lm) ? Convert.ToInt32(lm) : -1;

                maxResults = Math.Clamp(maxResults, 1, 500);

                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("No active Scene View found"));
                }

                var camera = sceneView.camera;
                if (camera == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Scene View camera not available"));
                }

                var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(camera);
                var visibleObjects = new List<object>();

                // Get all renderers in the scene
                var renderers = includeInactive
                    ? Resources.FindObjectsOfTypeAll<Renderer>()
                    : Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);

                foreach (var renderer in renderers)
                {
                    if (ct.IsCancellationRequested) break;
                    if (visibleObjects.Count >= maxResults) break;

                    if (renderer == null || renderer.gameObject == null) continue;

                    // Skip prefab assets
                    if (EditorUtility.IsPersistent(renderer.gameObject)) continue;

                    // Skip hidden objects
                    if (renderer.gameObject.hideFlags != HideFlags.None) continue;

                    // Layer mask filter
                    if (layerMask >= 0 && ((1 << renderer.gameObject.layer) & layerMask) == 0) continue;

                    // Check if bounds are in frustum
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, renderer.bounds)) continue;

                    var go = renderer.gameObject;
                    visibleObjects.Add(new
                    {
                        name = go.name,
                        path = GetGameObjectPath(go),
                        instanceId = go.GetInstanceID(),
                        globalId = SceneObjectRef.GlobalId(go),
                        rendererType = renderer.GetType().Name,
                        bounds = new
                        {
                            center = new { x = renderer.bounds.center.x, y = renderer.bounds.center.y, z = renderer.bounds.center.z },
                            size = new { x = renderer.bounds.size.x, y = renderer.bounds.size.y, z = renderer.bounds.size.z }
                        },
                        distanceToCamera = Vector3.Distance(camera.transform.position, renderer.bounds.center),
                        layer = LayerMask.LayerToName(go.layer),
                        tag = go.tag
                    });
                }

                // Sort by distance to camera
                var sortedObjects = visibleObjects
                    .OrderBy(o => ((dynamic)o).distanceToCamera)
                    .ToList();

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    objects = sortedObjects,
                    count = sortedObjects.Count,
                    cameraPosition = new
                    {
                        x = camera.transform.position.x,
                        y = camera.transform.position.y,
                        z = camera.transform.position.z
                    },
                    cameraRotation = new
                    {
                        x = camera.transform.eulerAngles.x,
                        y = camera.transform.eulerAngles.y,
                        z = camera.transform.eulerAngles.z
                    }
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }

    /// <summary>
    /// Creates a prefab from a GameObject in the scene.
    /// </summary>
    public class PrefabCreateExecutor : IToolExecutor
    {
        public string ToolId => "prefab.create";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var op) ? op?.ToString() : null;
                var prefabPath = context.Arguments.TryGetValue("prefabPath", out var pp) ? pp?.ToString() : null;

                if (string.IsNullOrEmpty(prefabPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("prefabPath is required"));
                }

                // Ensure path starts with Assets/
                if (!prefabPath.StartsWith("Assets/") && !prefabPath.StartsWith("Assets\\"))
                {
                    prefabPath = "Assets/" + prefabPath;
                }

                // Ensure .prefab extension
                if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabPath += ".prefab";
                }

                GameObject sourceGo = null;

                if (instanceId != 0)
                {
                    sourceGo = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                if (sourceGo == null && !string.IsNullOrEmpty(objectPath))
                {
                    sourceGo = GameObject.Find(objectPath);
                }

                if (sourceGo == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                // Ensure directory exists
                var directory = System.IO.Path.GetDirectoryName(prefabPath);
                if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
                {
                    CreateFolderRecursive(directory);
                }

                // Create the prefab
                var prefab = PrefabUtility.SaveAsPrefabAsset(sourceGo, prefabPath, out var success);

                if (!success || prefab == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Failed to create prefab"));
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    prefabPath,
                    guid = AssetDatabase.AssetPathToGUID(prefabPath),
                    sourceObjectName = sourceGo.name,
                    success = true
                }, new List<string> { prefabPath }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
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
    /// Instantiates a prefab in the scene.
    /// </summary>
    public class PrefabInstantiateExecutor : IToolExecutor
    {
        public string ToolId => "prefab.instantiate";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var prefabPath = context.Arguments.TryGetValue("prefabPath", out var pp) ? pp?.ToString() : null;
                var prefabGuid = context.Arguments.TryGetValue("prefabGuid", out var pg) ? pg?.ToString() : null;
                var parentId = context.Arguments.TryGetValue("parentId", out var pid) ? Convert.ToInt32(pid) : 0;
                var parentPath = context.Arguments.TryGetValue("parentPath", out var ppath) ? ppath?.ToString() : null;

                // Resolve prefab path from guid if needed
                if (string.IsNullOrEmpty(prefabPath) && !string.IsNullOrEmpty(prefabGuid))
                {
                    prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
                }

                if (string.IsNullOrEmpty(prefabPath))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("prefabPath or prefabGuid is required"));
                }

                // Load the prefab
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed($"Prefab not found at path: {prefabPath}"));
                }

                // Find parent if specified
                Transform parent = null;
                if (parentId != 0)
                {
                    var parentGo = EditorUtility.InstanceIDToObject(parentId) as GameObject;
                    if (parentGo != null) parent = parentGo.transform;
                }
                else if (!string.IsNullOrEmpty(parentPath))
                {
                    var parentGo = GameObject.Find(parentPath);
                    if (parentGo != null) parent = parentGo.transform;
                }

                // Instantiate the prefab
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

                if (instance == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Failed to instantiate prefab"));
                }

                // Set parent
                if (parent != null)
                {
                    instance.transform.SetParent(parent, false);
                }

                // Apply position
                if (context.Arguments.TryGetValue("position", out var pos) && pos is List<object> posList && posList.Count >= 3)
                {
                    instance.transform.position = new Vector3(
                        Convert.ToSingle(posList[0]),
                        Convert.ToSingle(posList[1]),
                        Convert.ToSingle(posList[2]));
                }

                // Apply rotation
                if (context.Arguments.TryGetValue("rotation", out var rot) && rot is List<object> rotList && rotList.Count >= 3)
                {
                    instance.transform.eulerAngles = new Vector3(
                        Convert.ToSingle(rotList[0]),
                        Convert.ToSingle(rotList[1]),
                        Convert.ToSingle(rotList[2]));
                }

                // Apply scale
                if (context.Arguments.TryGetValue("scale", out var scale) && scale is List<object> scaleList && scaleList.Count >= 3)
                {
                    instance.transform.localScale = new Vector3(
                        Convert.ToSingle(scaleList[0]),
                        Convert.ToSingle(scaleList[1]),
                        Convert.ToSingle(scaleList[2]));
                }

                // Register undo
                Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {prefab.name}");
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    instanceId = instance.GetInstanceID(),
                    name = instance.name,
                    path = GetGameObjectPath(instance),
                    prefabPath,
                    isPrefabInstance = true
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }

    /// <summary>
    /// Applies or reverts prefab overrides.
    /// </summary>
    public class PrefabOverrideExecutor : IToolExecutor
    {
        public string ToolId => "prefab.override";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var op) ? op?.ToString() : null;
                var action = context.Arguments.TryGetValue("action", out var act) ? act?.ToString()?.ToLowerInvariant() : "apply";

                GameObject instance = null;

                if (instanceId != 0)
                {
                    instance = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                if (instance == null && !string.IsNullOrEmpty(objectPath))
                {
                    instance = GameObject.Find(objectPath);
                }

                if (instance == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                // Check if it's a prefab instance
                if (!PrefabUtility.IsPartOfPrefabInstance(instance))
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject is not a prefab instance"));
                }

                var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(instance);
                var prefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);

                switch (action)
                {
                    case "apply":
                        // Apply all overrides to the prefab asset
                        PrefabUtility.ApplyPrefabInstance(prefabRoot, InteractionMode.UserAction);
                        AssetDatabase.SaveAssets();

                        return Task.FromResult(ToolExecutionResult.Succeeded(new
                        {
                            action = "applied",
                            prefabPath = prefabAssetPath,
                            instanceName = instance.name
                        }, new List<string> { prefabAssetPath }));

                    case "revert":
                        // Revert all overrides
                        PrefabUtility.RevertPrefabInstance(prefabRoot, InteractionMode.UserAction);
                        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                        return Task.FromResult(ToolExecutionResult.Succeeded(new
                        {
                            action = "reverted",
                            prefabPath = prefabAssetPath,
                            instanceName = instance.name
                        }));

                    case "unpack":
                        // Unpack the prefab instance
                        var unpackMode = context.Arguments.TryGetValue("unpackMode", out var um) && um?.ToString() == "completely"
                            ? PrefabUnpackMode.Completely
                            : PrefabUnpackMode.OutermostRoot;

                        PrefabUtility.UnpackPrefabInstance(prefabRoot, unpackMode, InteractionMode.UserAction);
                        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                        return Task.FromResult(ToolExecutionResult.Succeeded(new
                        {
                            action = "unpacked",
                            unpackMode = unpackMode.ToString(),
                            instanceName = instance.name
                        }));

                    case "get_overrides":
                        // Get list of overrides
                        var objectOverrides = PrefabUtility.GetObjectOverrides(prefabRoot, true);
                        var propertyOverrides = PrefabUtility.GetPropertyModifications(prefabRoot);
                        var addedComponents = PrefabUtility.GetAddedComponents(prefabRoot);
                        var removedComponents = PrefabUtility.GetRemovedComponents(prefabRoot);
                        var addedGameObjects = PrefabUtility.GetAddedGameObjects(prefabRoot);

                        return Task.FromResult(ToolExecutionResult.Succeeded(new
                        {
                            prefabPath = prefabAssetPath,
                            instanceName = instance.name,
                            overrides = new
                            {
                                objectOverrideCount = objectOverrides.Count,
                                propertyModificationCount = propertyOverrides?.Length ?? 0,
                                addedComponentCount = addedComponents.Count,
                                removedComponentCount = removedComponents.Count,
                                addedGameObjectCount = addedGameObjects.Count,
                                propertyModifications = propertyOverrides?.Take(50).Select(pm => new
                                {
                                    target = pm.target?.name,
                                    propertyPath = pm.propertyPath,
                                    value = pm.value
                                }).ToList()
                            }
                        }));

                    default:
                        return Task.FromResult(ToolExecutionResult.Failed($"Unknown action: {action}. Valid actions: apply, revert, unpack, get_overrides"));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Duplicates a GameObject in the scene.
    /// </summary>
    public class SceneDuplicateObjectExecutor : IToolExecutor
    {
        public string ToolId => "scene.duplicate_object";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var op) ? op?.ToString() : null;
                var count = context.Arguments.TryGetValue("count", out var c) ? Convert.ToInt32(c) : 1;

                count = Math.Clamp(count, 1, 100);

                GameObject source = null;

                if (instanceId != 0)
                {
                    source = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                if (source == null && !string.IsNullOrEmpty(objectPath))
                {
                    source = GameObject.Find(objectPath);
                }

                if (source == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("GameObject not found"));
                }

                var duplicates = new List<object>();

                for (int i = 0; i < count; i++)
                {
                    var duplicate = Object.Instantiate(source, source.transform.parent);
                    duplicate.name = source.name; // Instantiate adds (Clone), this removes it but Unity will add (1), (2), etc.

                    // Apply offset if specified
                    if (context.Arguments.TryGetValue("offset", out var offset) && offset is List<object> offsetList && offsetList.Count >= 3)
                    {
                        var offsetVec = new Vector3(
                            Convert.ToSingle(offsetList[0]) * (i + 1),
                            Convert.ToSingle(offsetList[1]) * (i + 1),
                            Convert.ToSingle(offsetList[2]) * (i + 1));
                        duplicate.transform.position = source.transform.position + offsetVec;
                    }

                    Undo.RegisterCreatedObjectUndo(duplicate, $"Duplicate {source.name}");

                    duplicates.Add(new
                    {
                        instanceId = duplicate.GetInstanceID(),
                        name = duplicate.name,
                        path = GetGameObjectPath(duplicate)
                    });
                }

                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    sourceName = source.name,
                    duplicateCount = duplicates.Count,
                    duplicates
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }

    /// <summary>
    /// Reparents a GameObject in the scene hierarchy.
    /// </summary>
    public class SceneReparentObjectExecutor : IToolExecutor
    {
        public string ToolId => "scene.reparent_object";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var op) ? op?.ToString() : null;
                var newParentId = context.Arguments.TryGetValue("newParentId", out var npid) ? Convert.ToInt32(npid) : 0;
                var newParentPath = context.Arguments.TryGetValue("newParentPath", out var npp) ? npp?.ToString() : null;
                var worldPositionStays = !context.Arguments.TryGetValue("worldPositionStays", out var wps) || Convert.ToBoolean(wps);

                GameObject target = null;

                if (instanceId != 0)
                {
                    target = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                if (target == null && !string.IsNullOrEmpty(objectPath))
                {
                    target = GameObject.Find(objectPath);
                }

                if (target == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Target GameObject not found"));
                }

                Transform newParent = null;

                // null parent means root
                if (newParentId != 0)
                {
                    var parentGo = EditorUtility.InstanceIDToObject(newParentId) as GameObject;
                    if (parentGo != null) newParent = parentGo.transform;
                }
                else if (!string.IsNullOrEmpty(newParentPath))
                {
                    var parentGo = GameObject.Find(newParentPath);
                    if (parentGo != null) newParent = parentGo.transform;
                }

                var oldParentName = target.transform.parent?.name ?? "(root)";

                // Record undo
                Undo.SetTransformParent(target.transform, newParent, worldPositionStays, $"Reparent {target.name}");
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    objectName = target.name,
                    oldParent = oldParentName,
                    newParent = newParent?.name ?? "(root)",
                    newPath = GetGameObjectPath(target)
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
