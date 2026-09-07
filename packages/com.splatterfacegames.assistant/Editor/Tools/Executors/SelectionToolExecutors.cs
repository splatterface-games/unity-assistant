// Selection Tool Executors - Unity Editor selection operations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Gets the current selection in the Editor.
    /// </summary>
    public class SelectionGetExecutor : IToolExecutor
    {
        public string ToolId => "selection.get";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var includeDetails = context.Arguments.TryGetValue("include_details", out var id) && Convert.ToBoolean(id);

                var selection = new
                {
                    activeObject = GetObjectInfo(Selection.activeObject, includeDetails),
                    activeGameObject = GetGameObjectInfo(Selection.activeGameObject, includeDetails),
                    selectedObjects = Selection.objects.Select(o => GetObjectInfo(o, includeDetails)).ToList(),
                    selectedGameObjects = Selection.gameObjects.Select(go => GetGameObjectInfo(go, includeDetails)).ToList(),
                    assetGUIDs = Selection.assetGUIDs.Select(guid => new
                    {
                        guid,
                        path = AssetDatabase.GUIDToAssetPath(guid)
                    }).ToList(),
                    count = Selection.objects.Length
                };

                return Task.FromResult(ToolExecutionResult.Succeeded(selection));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private object GetObjectInfo(Object obj, bool includeDetails)
        {
            if (obj == null) return null;

            var info = new Dictionary<string, object>
            {
                ["name"] = obj.name,
                ["type"] = obj.GetType().Name,
                ["instanceId"] = obj.GetInstanceID()
            };

            var path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path))
            {
                info["assetPath"] = path;
                info["guid"] = AssetDatabase.AssetPathToGUID(path);
            }

            if (includeDetails)
            {
                info["fullType"] = obj.GetType().FullName;

                if (obj is GameObject go)
                {
                    info["components"] = go.GetComponents<Component>()
                        .Where(c => c != null)
                        .Select(c => c.GetType().Name)
                        .ToList();
                }
            }

            return info;
        }

        private object GetGameObjectInfo(GameObject go, bool includeDetails)
        {
            if (go == null) return null;

            var info = new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["scene"] = go.scene.name
            };

            // Check if it's a prefab instance
            var prefabStatus = PrefabUtility.GetPrefabInstanceStatus(go);
            if (prefabStatus != PrefabInstanceStatus.NotAPrefab)
            {
                info["isPrefabInstance"] = true;
                var prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (prefabAsset != null)
                {
                    info["prefabPath"] = AssetDatabase.GetAssetPath(prefabAsset);
                }
            }

            if (includeDetails)
            {
                info["transform"] = new
                {
                    position = new[] { go.transform.position.x, go.transform.position.y, go.transform.position.z },
                    rotation = new[] { go.transform.eulerAngles.x, go.transform.eulerAngles.y, go.transform.eulerAngles.z },
                    scale = new[] { go.transform.localScale.x, go.transform.localScale.y, go.transform.localScale.z }
                };

                info["components"] = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => new
                    {
                        type = c.GetType().Name,
                        enabled = IsComponentEnabled(c)
                    })
                    .ToList();

                info["childCount"] = go.transform.childCount;
            }

            return info;
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
    /// Sets the current selection in the Editor.
    /// </summary>
    public class SelectionSetExecutor : IToolExecutor
    {
        public string ToolId => "selection.set";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var objects = new List<Object>();

                // Select by instance IDs
                if (context.Arguments.TryGetValue("instance_ids", out var ids) && ids is List<object> idList)
                {
                    foreach (var id in idList)
                    {
                        var instanceId = Convert.ToInt32(id);
                        var obj = EditorUtility.InstanceIDToObject(instanceId);
                        if (obj != null)
                            objects.Add(obj);
                    }
                }

                // Select by asset paths
                if (context.Arguments.TryGetValue("paths", out var paths) && paths is List<object> pathList)
                {
                    foreach (var path in pathList)
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<Object>(path.ToString());
                        if (obj != null)
                            objects.Add(obj);
                    }
                }

                // Select by GUIDs
                if (context.Arguments.TryGetValue("guids", out var guids) && guids is List<object> guidList)
                {
                    foreach (var guid in guidList)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid.ToString());
                        if (!string.IsNullOrEmpty(path))
                        {
                            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                            if (obj != null)
                                objects.Add(obj);
                        }
                    }
                }

                // Select by GameObject names/paths in scene
                if (context.Arguments.TryGetValue("scene_paths", out var scenePaths) && scenePaths is List<object> scenePathList)
                {
                    foreach (var scenePath in scenePathList)
                    {
                        var go = GameObject.Find(scenePath.ToString());
                        if (go != null)
                            objects.Add(go);
                    }
                }

                // Apply selection
                if (objects.Count > 0)
                {
                    Selection.objects = objects.ToArray();

                    // Ping the first object
                    if (context.Arguments.TryGetValue("ping", out var ping) && Convert.ToBoolean(ping))
                    {
                        EditorGUIUtility.PingObject(objects[0]);
                    }

                    // Focus on selection
                    if (context.Arguments.TryGetValue("focus", out var focus) && Convert.ToBoolean(focus))
                    {
                        if (objects[0] is GameObject go)
                        {
                            SceneView.lastActiveSceneView?.FrameSelected();
                        }
                        else
                        {
                            EditorUtility.FocusProjectWindow();
                        }
                    }
                }
                else if (context.Arguments.TryGetValue("clear", out var clear) && Convert.ToBoolean(clear))
                {
                    Selection.objects = Array.Empty<Object>();
                }

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    selectedCount = objects.Count,
                    selected = objects.Select(o => new { name = o.name, type = o.GetType().Name }).ToList()
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }
}
