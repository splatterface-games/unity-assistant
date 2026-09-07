// Integration Test Base - Foundation for integration tests
// Provides common setup, teardown, and helper methods for testing tool execution pipelines

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using Splatter.Editor.Transport;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Splatter.Tests.Editor.Integration
{
    /// <summary>
    /// Base class for integration tests providing common setup, teardown,
    /// and helper methods for testing tool execution pipelines.
    /// </summary>
    public abstract class IntegrationTestBase
    {
        protected UnityToolRegistry Registry;
        protected MockServiceClient MockClient;
        protected string TestScenePath;
        protected string TestDataDirectory;
        protected Scene TestScene;
        protected List<string> CreatedFiles;
        protected List<GameObject> CreatedObjects;
        protected int InitialUndoGroup;

        /// <summary>
        /// Default timeout for async operations in tests.
        /// </summary>
        protected virtual TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);

        [SetUp]
        public virtual void SetUp()
        {
            // Get registry instance
            Registry = UnityToolRegistry.Instance;

            // Create mock client
            MockClient = new MockServiceClient();

            // Track created resources for cleanup
            CreatedFiles = new List<string>();
            CreatedObjects = new List<GameObject>();

            // Create test data directory
            TestDataDirectory = Path.Combine(Application.temporaryCachePath, "SplatterTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TestDataDirectory);

            // Record initial undo group
            InitialUndoGroup = Undo.GetCurrentGroup();

            // Ensure we start with a clean scene
            SetupTestScene();
        }

        [TearDown]
        public virtual void TearDown()
        {
            // Cleanup created GameObjects
            foreach (var go in CreatedObjects)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
            CreatedObjects.Clear();

            // Cleanup created files
            foreach (var file in CreatedFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to delete test file {file}: {ex.Message}");
                }
            }
            CreatedFiles.Clear();

            // Cleanup test data directory
            if (Directory.Exists(TestDataDirectory))
            {
                try
                {
                    Directory.Delete(TestDataDirectory, true);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to delete test directory: {ex.Message}");
                }
            }

            // Cleanup test scene
            CleanupTestScene();

            // Revert any undo operations made during the test
            Undo.RevertAllDownToGroup(InitialUndoGroup);

            // Dispose mock client
            MockClient?.Dispose();
        }

        /// <summary>
        /// Creates a tool execution context for testing.
        /// </summary>
        protected ToolExecutionContext CreateContext(string toolId, Dictionary<string, object> args)
        {
            return new ToolExecutionContext
            {
                SessionId = $"test-session-{Guid.NewGuid():N}",
                TurnId = $"test-turn-{Guid.NewGuid():N}",
                ToolCallId = $"test-call-{Guid.NewGuid():N}",
                ToolId = toolId,
                Arguments = args ?? new Dictionary<string, object>(),
                WorkspacePath = Path.Combine(Application.dataPath, "..")
            };
        }

        /// <summary>
        /// Executes a tool asynchronously and returns the result.
        /// </summary>
        protected async Task<ToolExecutionResult> ExecuteToolAsync(string toolId, Dictionary<string, object> args, CancellationToken ct = default)
        {
            var executor = Registry.GetExecutor(toolId);
            if (executor == null)
            {
                throw new InvalidOperationException($"Tool not found: {toolId}");
            }

            var context = CreateContext(toolId, args);
            var linkedCts = ct == default
                ? new CancellationTokenSource(DefaultTimeout)
                : CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                return await executor.ExecuteAsync(context, linkedCts.Token);
            }
            finally
            {
                if (ct == default)
                {
                    linkedCts.Dispose();
                }
            }
        }

        /// <summary>
        /// Executes a tool and asserts success.
        /// </summary>
        protected async Task<ToolExecutionResult> ExecuteToolSuccessAsync(string toolId, Dictionary<string, object> args, CancellationToken ct = default)
        {
            var result = await ExecuteToolAsync(toolId, args, ct);
            Assert.IsTrue(result.Success, $"Tool {toolId} failed: {result.Error}");
            return result;
        }

        /// <summary>
        /// Creates a test file with the given content.
        /// </summary>
        protected string CreateTestFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(TestDataDirectory, relativePath);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);
            CreatedFiles.Add(fullPath);

            return fullPath;
        }

        /// <summary>
        /// Creates a test file in the Assets folder (will trigger asset import).
        /// </summary>
        protected string CreateAssetFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(Application.dataPath, relativePath);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);
            CreatedFiles.Add(fullPath);
            CreatedFiles.Add(fullPath + ".meta"); // Track meta file too

            AssetDatabase.Refresh();

            return fullPath;
        }

        /// <summary>
        /// Creates a test GameObject in the current scene.
        /// </summary>
        protected GameObject CreateTestGameObject(string name, params Type[] components)
        {
            var go = new GameObject(name);

            foreach (var componentType in components)
            {
                go.AddComponent(componentType);
            }

            CreatedObjects.Add(go);
            return go;
        }

        /// <summary>
        /// Creates a primitive GameObject.
        /// </summary>
        protected GameObject CreateTestPrimitive(string name, PrimitiveType primitiveType)
        {
            var go = GameObject.CreatePrimitive(primitiveType);
            go.name = name;
            CreatedObjects.Add(go);
            return go;
        }

        /// <summary>
        /// Waits for a condition to be true or times out.
        /// </summary>
        protected async Task WaitForConditionAsync(Func<bool> condition, TimeSpan? timeout = null, string timeoutMessage = null)
        {
            var actualTimeout = timeout ?? TimeSpan.FromSeconds(10);
            var startTime = DateTime.UtcNow;

            while (!condition())
            {
                if (DateTime.UtcNow - startTime > actualTimeout)
                {
                    throw new TimeoutException(timeoutMessage ?? "Condition was not met within the timeout period.");
                }

                await Task.Delay(50);
            }
        }

        /// <summary>
        /// Coroutine wrapper for waiting multiple frames.
        /// </summary>
        protected IEnumerator WaitForFrames(int frameCount)
        {
            for (int i = 0; i < frameCount; i++)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Sets up a test scene.
        /// </summary>
        protected virtual void SetupTestScene()
        {
            // Create a new empty test scene
            TestScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            TestScenePath = null; // Not saved to disk

            // Add standard test objects if needed
            var mainCamera = new GameObject("Main Camera");
            mainCamera.AddComponent<Camera>();
            mainCamera.tag = "MainCamera";
            CreatedObjects.Add(mainCamera);

            var directionalLight = new GameObject("Directional Light");
            var light = directionalLight.AddComponent<Light>();
            light.type = LightType.Directional;
            CreatedObjects.Add(directionalLight);
        }

        /// <summary>
        /// Cleans up the test scene.
        /// </summary>
        protected virtual void CleanupTestScene()
        {
            // Scene cleanup is handled by Unity when we load a new scene or the test ends
        }

        /// <summary>
        /// Gets the project root path.
        /// </summary>
        protected string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        /// <summary>
        /// Asserts that a file exists.
        /// </summary>
        protected void AssertFileExists(string path)
        {
            Assert.IsTrue(File.Exists(path), $"Expected file to exist: {path}");
        }

        /// <summary>
        /// Asserts that a file does not exist.
        /// </summary>
        protected void AssertFileNotExists(string path)
        {
            Assert.IsFalse(File.Exists(path), $"Expected file to not exist: {path}");
        }

        /// <summary>
        /// Asserts that a file contains the expected content.
        /// </summary>
        protected void AssertFileContent(string path, string expectedContent)
        {
            AssertFileExists(path);
            var actualContent = File.ReadAllText(path);
            Assert.AreEqual(expectedContent, actualContent, $"File content mismatch for: {path}");
        }

        /// <summary>
        /// Asserts that a file contains a substring.
        /// </summary>
        protected void AssertFileContains(string path, string substring)
        {
            AssertFileExists(path);
            var content = File.ReadAllText(path);
            Assert.IsTrue(content.Contains(substring), $"File {path} does not contain expected substring: {substring}");
        }

        /// <summary>
        /// Finds a GameObject by path in the test scene.
        /// </summary>
        protected GameObject FindGameObject(string path)
        {
            return GameObject.Find(path);
        }

        /// <summary>
        /// Asserts that a GameObject exists in the scene.
        /// </summary>
        protected void AssertGameObjectExists(string path)
        {
            var go = FindGameObject(path);
            Assert.IsNotNull(go, $"Expected GameObject to exist at path: {path}");
        }

        /// <summary>
        /// Asserts that a GameObject does not exist in the scene.
        /// </summary>
        protected void AssertGameObjectNotExists(string path)
        {
            var go = FindGameObject(path);
            Assert.IsNull(go, $"Expected GameObject to not exist at path: {path}");
        }

        /// <summary>
        /// Gets or extracts a value from a tool execution result output.
        /// </summary>
        protected T GetOutputValue<T>(ToolExecutionResult result, string key)
        {
            if (result.Output is IDictionary<string, object> dict)
            {
                if (dict.TryGetValue(key, out var value))
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
            }

            // Try to use reflection for anonymous types
            var outputType = result.Output?.GetType();
            var property = outputType?.GetProperty(key);
            if (property != null)
            {
                return (T)Convert.ChangeType(property.GetValue(result.Output), typeof(T));
            }

            throw new KeyNotFoundException($"Output does not contain key: {key}");
        }
    }
}
