// Test Scene Setup - Utilities for creating and managing test scenes
// Provides helpers for setting up various test scenarios

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Splatter.Tests.Editor.Integration
{
    /// <summary>
    /// Provides utilities for setting up test scenes with various configurations.
    /// </summary>
    public class TestSceneSetup : IDisposable
    {
        private readonly List<GameObject> _createdObjects = new();
        private Scene _scene;
        private bool _disposed;

        /// <summary>
        /// Gets the test scene.
        /// </summary>
        public Scene Scene => _scene;

        /// <summary>
        /// Gets all created GameObjects.
        /// </summary>
        public IReadOnlyList<GameObject> CreatedObjects => _createdObjects;

        /// <summary>
        /// Creates a new empty test scene.
        /// </summary>
        public static TestSceneSetup CreateEmptyScene()
        {
            var setup = new TestSceneSetup();
            setup._scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return setup;
        }

        /// <summary>
        /// Creates a test scene with basic setup (camera and light).
        /// </summary>
        public static TestSceneSetup CreateBasicScene()
        {
            var setup = CreateEmptyScene();
            setup.AddMainCamera();
            setup.AddDirectionalLight();
            return setup;
        }

        /// <summary>
        /// Creates a test scene with a hierarchy of GameObjects.
        /// </summary>
        public static TestSceneSetup CreateHierarchyScene()
        {
            var setup = CreateBasicScene();

            // Create a hierarchy
            var root = setup.CreateGameObject("Root");
            var child1 = setup.CreateGameObject("Child1", root.transform);
            var child2 = setup.CreateGameObject("Child2", root.transform);
            var grandchild = setup.CreateGameObject("Grandchild", child1.transform);

            // Add some components
            root.AddComponent<BoxCollider>();
            child1.AddComponent<Rigidbody>();
            child2.AddComponent<SphereCollider>();
            grandchild.AddComponent<MeshRenderer>();

            return setup;
        }

        /// <summary>
        /// Creates a test scene with primitives.
        /// </summary>
        public static TestSceneSetup CreatePrimitivesScene()
        {
            var setup = CreateBasicScene();

            setup.CreatePrimitive("Cube", PrimitiveType.Cube, new Vector3(0, 0, 0));
            setup.CreatePrimitive("Sphere", PrimitiveType.Sphere, new Vector3(3, 0, 0));
            setup.CreatePrimitive("Cylinder", PrimitiveType.Cylinder, new Vector3(-3, 0, 0));
            setup.CreatePrimitive("Capsule", PrimitiveType.Capsule, new Vector3(0, 0, 3));
            setup.CreatePrimitive("Plane", PrimitiveType.Plane, new Vector3(0, -1, 0));

            return setup;
        }

        /// <summary>
        /// Creates a test scene with UI elements.
        /// </summary>
        public static TestSceneSetup CreateUIScene()
        {
            var setup = CreateBasicScene();

            // Create Canvas
            var canvas = setup.CreateGameObject("Canvas");
            var canvasComponent = canvas.AddComponent<Canvas>();
            canvasComponent.renderMode = RenderMode.ScreenSpaceOverlay;

            // Create UI elements under canvas
            var panel = setup.CreateGameObject("Panel", canvas.transform);
            var button = setup.CreateGameObject("Button", panel.transform);
            var text = setup.CreateGameObject("Text", panel.transform);

            return setup;
        }

        /// <summary>
        /// Adds a main camera to the scene.
        /// </summary>
        public GameObject AddMainCamera(Vector3? position = null)
        {
            var camera = CreateGameObject("Main Camera");
            camera.AddComponent<Camera>();
            camera.tag = "MainCamera";

            if (position.HasValue)
            {
                camera.transform.position = position.Value;
            }
            else
            {
                camera.transform.position = new Vector3(0, 5, -10);
                camera.transform.LookAt(Vector3.zero);
            }

            return camera;
        }

        /// <summary>
        /// Adds a directional light to the scene.
        /// </summary>
        public GameObject AddDirectionalLight(Vector3? rotation = null)
        {
            var lightGo = CreateGameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;

            if (rotation.HasValue)
            {
                lightGo.transform.eulerAngles = rotation.Value;
            }
            else
            {
                lightGo.transform.eulerAngles = new Vector3(50, -30, 0);
            }

            return lightGo;
        }

        /// <summary>
        /// Creates a new GameObject.
        /// </summary>
        public GameObject CreateGameObject(string name, Transform parent = null)
        {
            var go = new GameObject(name);

            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            _createdObjects.Add(go);
            return go;
        }

        /// <summary>
        /// Creates a primitive GameObject.
        /// </summary>
        public GameObject CreatePrimitive(string name, PrimitiveType type, Vector3? position = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;

            if (position.HasValue)
            {
                go.transform.position = position.Value;
            }

            _createdObjects.Add(go);
            return go;
        }

        /// <summary>
        /// Creates an empty GameObject at a specific path in the hierarchy.
        /// </summary>
        public GameObject CreateAtPath(string path)
        {
            var parts = path.Split('/');
            Transform parent = null;

            foreach (var part in parts)
            {
                var existing = parent == null
                    ? GameObject.Find(part)
                    : parent.Find(part)?.gameObject;

                if (existing != null)
                {
                    parent = existing.transform;
                }
                else
                {
                    var go = CreateGameObject(part, parent);
                    parent = go.transform;
                }
            }

            return parent?.gameObject;
        }

        /// <summary>
        /// Finds a GameObject by path.
        /// </summary>
        public GameObject Find(string path)
        {
            return GameObject.Find(path);
        }

        /// <summary>
        /// Gets the path of a GameObject in the hierarchy.
        /// </summary>
        public static string GetPath(GameObject go)
        {
            if (go == null) return null;

            var path = go.name;
            var parent = go.transform.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        /// <summary>
        /// Counts all GameObjects in the scene.
        /// </summary>
        public int CountAllObjects()
        {
            var rootObjects = _scene.GetRootGameObjects();
            var count = 0;

            foreach (var root in rootObjects)
            {
                count += CountObjectsRecursive(root.transform);
            }

            return count;
        }

        private int CountObjectsRecursive(Transform t)
        {
            var count = 1;
            for (int i = 0; i < t.childCount; i++)
            {
                count += CountObjectsRecursive(t.GetChild(i));
            }
            return count;
        }

        /// <summary>
        /// Marks the scene as dirty (modified).
        /// </summary>
        public void MarkDirty()
        {
            EditorSceneManager.MarkSceneDirty(_scene);
        }

        /// <summary>
        /// Checks if the scene is dirty.
        /// </summary>
        public bool IsDirty()
        {
            return _scene.isDirty;
        }

        /// <summary>
        /// Disposes the test scene setup and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var go in _createdObjects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _createdObjects.Clear();
        }
    }

    /// <summary>
    /// Builder pattern for creating test hierarchies.
    /// </summary>
    public class HierarchyBuilder
    {
        private readonly TestSceneSetup _setup;
        private readonly Stack<GameObject> _parentStack = new();
        private GameObject _current;

        public HierarchyBuilder(TestSceneSetup setup)
        {
            _setup = setup;
        }

        /// <summary>
        /// Creates a new GameObject at the current level.
        /// </summary>
        public HierarchyBuilder Add(string name)
        {
            var parent = _parentStack.Count > 0 ? _parentStack.Peek().transform : null;
            _current = _setup.CreateGameObject(name, parent);
            return this;
        }

        /// <summary>
        /// Adds a component to the current GameObject.
        /// </summary>
        public HierarchyBuilder WithComponent<T>() where T : Component
        {
            _current?.AddComponent<T>();
            return this;
        }

        /// <summary>
        /// Sets the position of the current GameObject.
        /// </summary>
        public HierarchyBuilder AtPosition(Vector3 position)
        {
            if (_current != null)
            {
                _current.transform.position = position;
            }
            return this;
        }

        /// <summary>
        /// Sets the local position of the current GameObject.
        /// </summary>
        public HierarchyBuilder AtLocalPosition(Vector3 localPosition)
        {
            if (_current != null)
            {
                _current.transform.localPosition = localPosition;
            }
            return this;
        }

        /// <summary>
        /// Sets the rotation of the current GameObject.
        /// </summary>
        public HierarchyBuilder WithRotation(Vector3 eulerAngles)
        {
            if (_current != null)
            {
                _current.transform.eulerAngles = eulerAngles;
            }
            return this;
        }

        /// <summary>
        /// Sets the scale of the current GameObject.
        /// </summary>
        public HierarchyBuilder WithScale(Vector3 scale)
        {
            if (_current != null)
            {
                _current.transform.localScale = scale;
            }
            return this;
        }

        /// <summary>
        /// Sets the tag of the current GameObject.
        /// </summary>
        public HierarchyBuilder WithTag(string tag)
        {
            if (_current != null)
            {
                _current.tag = tag;
            }
            return this;
        }

        /// <summary>
        /// Sets the layer of the current GameObject.
        /// </summary>
        public HierarchyBuilder OnLayer(int layer)
        {
            if (_current != null)
            {
                _current.layer = layer;
            }
            return this;
        }

        /// <summary>
        /// Begins adding children to the current GameObject.
        /// </summary>
        public HierarchyBuilder BeginChildren()
        {
            if (_current != null)
            {
                _parentStack.Push(_current);
            }
            return this;
        }

        /// <summary>
        /// Ends adding children and returns to the parent level.
        /// </summary>
        public HierarchyBuilder EndChildren()
        {
            if (_parentStack.Count > 0)
            {
                _current = _parentStack.Pop();
            }
            return this;
        }

        /// <summary>
        /// Gets the current GameObject.
        /// </summary>
        public GameObject Current => _current;
    }

    /// <summary>
    /// Extension methods for TestSceneSetup.
    /// </summary>
    public static class TestSceneSetupExtensions
    {
        /// <summary>
        /// Starts building a hierarchy.
        /// </summary>
        public static HierarchyBuilder BuildHierarchy(this TestSceneSetup setup)
        {
            return new HierarchyBuilder(setup);
        }
    }
}
