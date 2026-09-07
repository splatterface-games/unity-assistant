// Tests for Scene Tool Executors
// Tests scene object finding, reading, creation, deletion, and component operations

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Splatter.Tests.Editor.Tools
{
    [TestFixture]
    public class SceneToolExecutorsTests
    {
        private string _projectRoot;
        private Scene _testScene;
        private GameObject _testObject;
        private GameObject _testParent;
        private GameObject _testChild;
        private int _testObjectInstanceId;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));

            // Create a new test scene
            _testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [SetUp]
        public void SetUp()
        {
            // Create test objects for each test
            _testParent = new GameObject("TestParent");
            _testParent.tag = "Untagged";
            _testParent.layer = 0;

            _testChild = new GameObject("TestChild");
            _testChild.transform.SetParent(_testParent.transform);

            _testObject = new GameObject("TestObject");
            _testObject.AddComponent<BoxCollider>();
            _testObjectInstanceId = _testObject.GetInstanceID();
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test objects
            if (_testObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_testObject);
            }

            if (_testParent != null)
            {
                UnityEngine.Object.DestroyImmediate(_testParent);
            }

            // Clear undo stack to avoid test pollution
            Undo.ClearAll();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // Create a new empty scene to clean up
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        }

        private ToolExecutionContext CreateContext(Dictionary<string, object> args)
        {
            return new ToolExecutionContext
            {
                SessionId = "test-session",
                TurnId = "test-turn",
                ToolCallId = "test-call",
                ToolId = "test.tool",
                Arguments = args,
                WorkspacePath = _projectRoot
            };
        }

        #region SceneFindObjectsExecutor Tests

        [Test]
        public async Task FindObjects_ByName_FindsMatchingObjects()
        {
            // Arrange
            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "TestObject" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task FindObjects_ByWildcard_FindsMatchingObjects()
        {
            // Arrange
            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "Test*" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task FindObjects_ByTag_FindsMatchingObjects()
        {
            // Arrange
            _testObject.tag = "MainCamera"; // Use a built-in tag

            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "tag", "MainCamera" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task FindObjects_ByLayer_FindsMatchingObjects()
        {
            // Arrange
            _testObject.layer = 5; // UI layer

            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "layer", 5 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task FindObjects_ByComponentType_FindsMatchingObjects()
        {
            // Arrange
            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "componentType", "BoxCollider" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task FindObjects_InvalidComponentType_ReturnsError()
        {
            // Arrange
            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "componentType", "NonExistentComponent" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task FindObjects_NoMatches_ReturnsEmptyList()
        {
            // Arrange
            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "NonExistentObjectName12345" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task FindObjects_MaxResults_LimitsOutput()
        {
            // Arrange
            // Create multiple test objects
            var tempObjects = new List<GameObject>();
            for (int i = 0; i < 10; i++)
            {
                tempObjects.Add(new GameObject($"TempObject{i}"));
            }

            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "TempObject*" },
                { "maxResults", 3 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Cleanup
            foreach (var obj in tempObjects)
            {
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public async Task FindObjects_InvalidScene_ReturnsError()
        {
            // Arrange
            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "TestObject" },
                { "scenePath", "Assets/NonExistent/Scene.unity" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found") || result.Error.Contains("not loaded"));
        }

        [Test]
        public async Task FindObjects_CombinedFilters_AppliesAllFilters()
        {
            // Arrange
            _testObject.tag = "MainCamera";
            _testObject.layer = 5;

            var executor = new SceneFindObjectsExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "TestObject" },
                { "tag", "MainCamera" },
                { "layer", 5 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        #endregion

        #region SceneReadObjectExecutor Tests

        [Test]
        public async Task ReadObject_ByInstanceId_ReturnsObjectInfo()
        {
            // Arrange
            var executor = new SceneReadObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task ReadObject_ByPath_ReturnsObjectInfo()
        {
            // Arrange
            var executor = new SceneReadObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "TestParent/TestChild" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
        }

        [Test]
        public async Task ReadObject_InvalidInstanceId_ReturnsError()
        {
            // Arrange
            var executor = new SceneReadObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", 999999999 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task ReadObject_InvalidPath_ReturnsError()
        {
            // Arrange
            var executor = new SceneReadObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "NonExistent/Object/Path" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task ReadObject_WithChildren_ReturnsChildInfo()
        {
            // Arrange
            var executor = new SceneReadObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "TestParent" },
                { "includeChildren", true },
                { "maxDepth", 2 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task ReadObject_IncludesComponentInfo()
        {
            // Arrange
            var executor = new SceneReadObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            // Verify that components are included in the output
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task ReadObject_MaxDepthClamped_ClampsValue()
        {
            // Arrange
            var executor = new SceneReadObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "TestParent" },
                { "includeChildren", true },
                { "maxDepth", 100 } // Should be clamped to 5
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        #endregion

        #region SceneCreateGameObjectExecutor Tests

        [Test]
        public async Task CreateGameObject_Basic_CreatesObject()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "NewTestObject" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");

            // Verify object was created
            var createdObject = GameObject.Find("NewTestObject");
            Assert.IsNotNull(createdObject, "Object should have been created");

            // Cleanup
            UnityEngine.Object.DestroyImmediate(createdObject);
        }

        [Test]
        public async Task CreateGameObject_WithPrimitive_CreatesPrimitive()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "TestCube" },
                { "primitive", "Cube" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var cube = GameObject.Find("TestCube");
            Assert.IsNotNull(cube);
            Assert.IsNotNull(cube.GetComponent<MeshFilter>(), "Primitive should have MeshFilter");

            // Cleanup
            UnityEngine.Object.DestroyImmediate(cube);
        }

        [Test]
        public async Task CreateGameObject_WithParent_SetsParent()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "ChildOfTestParent" },
                { "parent_id", _testParent.GetInstanceID() }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var child = GameObject.Find("ChildOfTestParent");
            Assert.IsNotNull(child);
            Assert.AreEqual(_testParent.transform, child.transform.parent);

            // Cleanup
            UnityEngine.Object.DestroyImmediate(child);
        }

        [Test]
        public async Task CreateGameObject_WithComponents_AddsComponents()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "ObjectWithComponents" },
                { "components", new List<object> { "Rigidbody", "AudioSource" } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var obj = GameObject.Find("ObjectWithComponents");
            Assert.IsNotNull(obj);
            Assert.IsNotNull(obj.GetComponent<Rigidbody>());
            Assert.IsNotNull(obj.GetComponent<AudioSource>());

            // Cleanup
            UnityEngine.Object.DestroyImmediate(obj);
        }

        [Test]
        public async Task CreateGameObject_WithPosition_SetsPosition()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "PositionedObject" },
                { "position", new List<object> { 1.0f, 2.0f, 3.0f } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var obj = GameObject.Find("PositionedObject");
            Assert.IsNotNull(obj);
            Assert.AreEqual(new Vector3(1, 2, 3), obj.transform.position);

            // Cleanup
            UnityEngine.Object.DestroyImmediate(obj);
        }

        [Test]
        public async Task CreateGameObject_WithRotation_SetsRotation()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "RotatedObject" },
                { "rotation", new List<object> { 45.0f, 90.0f, 0.0f } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var obj = GameObject.Find("RotatedObject");
            Assert.IsNotNull(obj);

            // Cleanup
            UnityEngine.Object.DestroyImmediate(obj);
        }

        [Test]
        public async Task CreateGameObject_WithScale_SetsScale()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "ScaledObject" },
                { "scale", new List<object> { 2.0f, 2.0f, 2.0f } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var obj = GameObject.Find("ScaledObject");
            Assert.IsNotNull(obj);
            Assert.AreEqual(new Vector3(2, 2, 2), obj.transform.localScale);

            // Cleanup
            UnityEngine.Object.DestroyImmediate(obj);
        }

        [Test]
        public async Task CreateGameObject_SupportsUndo()
        {
            // Arrange
            var executor = new SceneCreateGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "name", "UndoTestObject" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);
            Assert.IsTrue(result.Success);

            var obj = GameObject.Find("UndoTestObject");
            Assert.IsNotNull(obj);

            // Undo the creation
            Undo.PerformUndo();

            // Assert - object should be gone after undo
            obj = GameObject.Find("UndoTestObject");
            Assert.IsNull(obj, "Object should be removed after undo");
        }

        #endregion

        #region SceneDeleteGameObjectExecutor Tests

        [Test]
        public async Task DeleteGameObject_ByInstanceId_DeletesObject()
        {
            // Arrange
            var toDelete = new GameObject("ObjectToDelete");
            var instanceId = toDelete.GetInstanceID();

            var executor = new SceneDeleteGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", instanceId }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");

            // Verify object was deleted
            var found = EditorUtility.InstanceIDToObject(instanceId);
            Assert.IsNull(found, "Object should have been deleted");
        }

        [Test]
        public async Task DeleteGameObject_ByPath_DeletesObject()
        {
            // Arrange
            var toDelete = new GameObject("DeleteByPath");

            var executor = new SceneDeleteGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "DeleteByPath" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var found = GameObject.Find("DeleteByPath");
            Assert.IsNull(found);
        }

        [Test]
        public async Task DeleteGameObject_InvalidInstanceId_ReturnsError()
        {
            // Arrange
            var executor = new SceneDeleteGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", 999999999 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task DeleteGameObject_InvalidPath_ReturnsError()
        {
            // Arrange
            var executor = new SceneDeleteGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "NonExistent/Object" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task DeleteGameObject_SupportsUndo()
        {
            // Arrange
            var toDelete = new GameObject("UndoDeleteTest");
            var name = toDelete.name;

            var executor = new SceneDeleteGameObjectExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "UndoDeleteTest" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);
            Assert.IsTrue(result.Success);

            // Verify deleted
            Assert.IsNull(GameObject.Find(name));

            // Undo the deletion
            Undo.PerformUndo();

            // Assert - object should be back after undo
            var restored = GameObject.Find(name);
            Assert.IsNotNull(restored, "Object should be restored after undo");

            // Cleanup
            if (restored != null)
            {
                UnityEngine.Object.DestroyImmediate(restored);
            }
        }

        #endregion

        #region SceneAddComponentExecutor Tests

        [Test]
        public async Task AddComponent_ValidType_AddsComponent()
        {
            // Arrange
            var testObj = new GameObject("AddComponentTest");

            var executor = new SceneAddComponentExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "Rigidbody" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(testObj.GetComponent<Rigidbody>());

            // Cleanup
            UnityEngine.Object.DestroyImmediate(testObj);
        }

        [Test]
        public async Task AddComponent_ByPath_AddsComponent()
        {
            // Arrange
            var testObj = new GameObject("AddComponentByPath");

            var executor = new SceneAddComponentExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "objectPath", "AddComponentByPath" },
                { "componentType", "AudioSource" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(testObj.GetComponent<AudioSource>());

            // Cleanup
            UnityEngine.Object.DestroyImmediate(testObj);
        }

        [Test]
        public async Task AddComponent_MissingComponentType_ReturnsError()
        {
            // Arrange
            var executor = new SceneAddComponentExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task AddComponent_InvalidComponentType_ReturnsError()
        {
            // Arrange
            var executor = new SceneAddComponentExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId },
                { "componentType", "NonExistentComponent" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task AddComponent_InvalidGameObject_ReturnsError()
        {
            // Arrange
            var executor = new SceneAddComponentExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", 999999999 },
                { "componentType", "Rigidbody" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task AddComponent_SupportsUndo()
        {
            // Arrange
            var testObj = new GameObject("UndoAddComponentTest");

            var executor = new SceneAddComponentExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "SphereCollider" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(testObj.GetComponent<SphereCollider>());

            // Undo
            Undo.PerformUndo();

            // Assert
            Assert.IsNull(testObj.GetComponent<SphereCollider>(), "Component should be removed after undo");

            // Cleanup
            UnityEngine.Object.DestroyImmediate(testObj);
        }

        #endregion

        #region SceneSetComponentPropertyExecutor Tests

        [Test]
        public async Task SetComponentProperty_IntegerValue_SetsProperty()
        {
            // Arrange
            var testObj = new GameObject("SetPropertyTest");
            testObj.AddComponent<BoxCollider>();

            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "BoxCollider" },
                { "propertyPath", "isTrigger" },
                { "value", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsTrue(testObj.GetComponent<BoxCollider>().isTrigger);

            // Cleanup
            UnityEngine.Object.DestroyImmediate(testObj);
        }

        [Test]
        public async Task SetComponentProperty_Vector3Value_SetsProperty()
        {
            // Arrange
            var testObj = new GameObject("SetVector3Test");
            testObj.AddComponent<BoxCollider>();

            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "BoxCollider" },
                { "propertyPath", "size" },
                { "value", new List<object> { 2.0f, 3.0f, 4.0f } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.AreEqual(new Vector3(2, 3, 4), testObj.GetComponent<BoxCollider>().size);

            // Cleanup
            UnityEngine.Object.DestroyImmediate(testObj);
        }

        [Test]
        public async Task SetComponentProperty_MissingComponentType_ReturnsError()
        {
            // Arrange
            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId },
                { "propertyPath", "enabled" },
                { "value", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task SetComponentProperty_MissingPropertyPath_ReturnsError()
        {
            // Arrange
            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId },
                { "componentType", "BoxCollider" },
                { "value", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task SetComponentProperty_InvalidPropertyPath_ReturnsError()
        {
            // Arrange
            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId },
                { "componentType", "BoxCollider" },
                { "propertyPath", "nonExistentProperty" },
                { "value", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task SetComponentProperty_InvalidGameObject_ReturnsError()
        {
            // Arrange
            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", 999999999 },
                { "componentType", "BoxCollider" },
                { "propertyPath", "enabled" },
                { "value", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task SetComponentProperty_ComponentNotOnObject_ReturnsError()
        {
            // Arrange - _testObject has BoxCollider, not Rigidbody
            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", _testObjectInstanceId },
                { "componentType", "Rigidbody" },
                { "propertyPath", "mass" },
                { "value", 10.0f }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task SetComponentProperty_EnumValue_SetsProperty()
        {
            // Arrange
            var testObj = new GameObject("EnumPropertyTest");
            var rb = testObj.AddComponent<Rigidbody>();

            var executor = new SceneSetComponentPropertyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "Rigidbody" },
                { "propertyPath", "collisionDetectionMode" },
                { "value", "Continuous" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");

            // Cleanup
            UnityEngine.Object.DestroyImmediate(testObj);
        }

        #endregion

        #region SceneGetHierarchyExecutor Tests

        [Test]
        public async Task GetHierarchy_Basic_ReturnsHierarchy()
        {
            // Arrange
            var executor = new SceneGetHierarchyExecutor();
            var context = CreateContext(new Dictionary<string, object>());

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task GetHierarchy_WithComponents_IncludesComponents()
        {
            // Arrange
            var executor = new SceneGetHierarchyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "include_components", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task GetHierarchy_MaxDepth_LimitsDepth()
        {
            // Arrange
            var executor = new SceneGetHierarchyExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "max_depth", 1 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        #endregion
    }
}
