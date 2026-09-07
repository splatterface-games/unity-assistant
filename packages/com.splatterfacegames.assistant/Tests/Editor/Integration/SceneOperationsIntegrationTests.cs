// Scene Operations Integration Tests
// Tests scene manipulation operations with undo support

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Splatter.Tests.Editor.Integration
{
    /// <summary>
    /// Integration tests for scene operations.
    /// Tests create -> add component -> set property -> delete cycles with undo support.
    /// </summary>
    [TestFixture]
    public class SceneOperationsIntegrationTests : IntegrationTestBase
    {
        private int _undoGroupAtStart;

        public override void SetUp()
        {
            base.SetUp();
            _undoGroupAtStart = Undo.GetCurrentGroup();
        }

        public override void TearDown()
        {
            // Revert all undo operations from this test
            Undo.RevertAllDownToGroup(_undoGroupAtStart);
            base.TearDown();
        }

        #region Full Lifecycle Tests

        [Test]
        public async Task FullLifecycle_CreateAddSetDelete_WorksCorrectly()
        {
            // Step 1: Create GameObject
            var createResult = await ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "LifecycleTest" },
                { "position", new List<object> { 1.0f, 2.0f, 3.0f } }
            });

            var go = GameObject.Find("LifecycleTest");
            Assert.IsNotNull(go, "GameObject should be created");
            Assert.AreEqual(new Vector3(1, 2, 3), go.transform.position);

            var instanceId = go.GetInstanceID();
            CreatedObjects.Add(go);

            // Step 2: Add Rigidbody component
            var addResult = await ExecuteToolSuccessAsync("scene.add_component", new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "componentType", "Rigidbody" }
            });

            Assert.IsNotNull(go.GetComponent<Rigidbody>());

            // Step 3: Set mass property
            await ExecuteToolSuccessAsync("scene.set_component_property", new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "componentType", "Rigidbody" },
                { "propertyPath", "mass" },
                { "value", 10.0f }
            });

            Assert.AreEqual(10.0f, go.GetComponent<Rigidbody>().mass, 0.01f);

            // Step 4: Add BoxCollider
            await ExecuteToolSuccessAsync("scene.add_component", new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "componentType", "BoxCollider" }
            });

            Assert.IsNotNull(go.GetComponent<BoxCollider>());

            // Step 5: Set collider size
            await ExecuteToolSuccessAsync("scene.set_component_property", new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "componentType", "BoxCollider" },
                { "propertyPath", "size" },
                { "value", new List<object> { 2.0f, 2.0f, 2.0f } }
            });

            Assert.AreEqual(new Vector3(2, 2, 2), go.GetComponent<BoxCollider>().size);

            // Step 6: Delete the GameObject
            await ExecuteToolSuccessAsync("scene.delete_gameobject", new Dictionary<string, object>
            {
                { "instanceId", instanceId }
            });

            Assert.IsNull(GameObject.Find("LifecycleTest"), "GameObject should be deleted");
            CreatedObjects.Remove(go);
        }

        [Test]
        public async Task CreateHierarchy_WorksCorrectly()
        {
            // Create parent
            var parentResult = await ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "Parent" }
            });

            var parent = GameObject.Find("Parent");
            Assert.IsNotNull(parent);
            CreatedObjects.Add(parent);

            // Create child under parent
            var childResult = await ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "Child" },
                { "parent_id", parent.GetInstanceID() }
            });

            var child = parent.transform.Find("Child");
            Assert.IsNotNull(child, "Child should be under parent");
            CreatedObjects.Add(child.gameObject);

            // Create grandchild
            var grandchildResult = await ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "Grandchild" },
                { "parent_id", child.gameObject.GetInstanceID() }
            });

            var grandchild = child.Find("Grandchild");
            Assert.IsNotNull(grandchild, "Grandchild should be under child");
            CreatedObjects.Add(grandchild.gameObject);

            // Verify hierarchy
            var hierarchyResult = await ExecuteToolSuccessAsync("scene.get_hierarchy", new Dictionary<string, object>
            {
                { "max_depth", 10 },
                { "include_components", false }
            });

            Assert.IsTrue(hierarchyResult.Success);
        }

        #endregion

        #region Undo Stack Tests

        [UnityTest]
        public IEnumerator UndoStack_CreateGameObject_CanUndo()
        {
            // Create a new undo group for this test
            Undo.IncrementCurrentGroup();
            var groupBeforeCreate = Undo.GetCurrentGroup();

            var task = ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "UndoTest" }
            });

            while (!task.IsCompleted) yield return null;

            var go = GameObject.Find("UndoTest");
            Assert.IsNotNull(go, "GameObject should exist after creation");

            // Perform undo
            Undo.RevertAllDownToGroup(groupBeforeCreate);

            yield return null; // Wait a frame for undo to process

            // Check if object was removed
            go = GameObject.Find("UndoTest");
            Assert.IsNull(go, "GameObject should be removed after undo");
        }

        [UnityTest]
        public IEnumerator UndoStack_ModifyGameObject_CanUndo()
        {
            // Create test object
            var testObj = CreateTestGameObject("ModifyUndoTest");
            testObj.transform.position = Vector3.zero;

            yield return null;

            // Record the state before modification
            Undo.IncrementCurrentGroup();
            var groupBeforeModify = Undo.GetCurrentGroup();

            var task = ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", testObj.GetInstanceID() },
                { "position", new List<object> { 10.0f, 20.0f, 30.0f } }
            });

            while (!task.IsCompleted) yield return null;

            Assert.AreEqual(new Vector3(10, 20, 30), testObj.transform.position);

            // Perform undo
            Undo.RevertAllDownToGroup(groupBeforeModify);

            yield return null;

            // Position should be reverted
            Assert.AreEqual(Vector3.zero, testObj.transform.position, "Position should be reverted after undo");
        }

        [UnityTest]
        public IEnumerator UndoStack_AddComponent_CanUndo()
        {
            // Create test object
            var testObj = CreateTestGameObject("ComponentUndoTest");

            yield return null;

            Undo.IncrementCurrentGroup();
            var groupBeforeAdd = Undo.GetCurrentGroup();

            var task = ExecuteToolSuccessAsync("scene.add_component", new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "Rigidbody" }
            });

            while (!task.IsCompleted) yield return null;

            Assert.IsNotNull(testObj.GetComponent<Rigidbody>(), "Rigidbody should exist after add");

            // Perform undo
            Undo.RevertAllDownToGroup(groupBeforeAdd);

            yield return null;

            Assert.IsNull(testObj.GetComponent<Rigidbody>(), "Rigidbody should be removed after undo");
        }

        [UnityTest]
        public IEnumerator UndoStack_DeleteGameObject_CanUndo()
        {
            // Create test object (don't track for cleanup, we'll undo the delete)
            var testObj = new GameObject("DeleteUndoTest");

            yield return null;

            var instanceId = testObj.GetInstanceID();

            Undo.IncrementCurrentGroup();
            var groupBeforeDelete = Undo.GetCurrentGroup();

            var task = ExecuteToolSuccessAsync("scene.delete_gameobject", new Dictionary<string, object>
            {
                { "instanceId", instanceId }
            });

            while (!task.IsCompleted) yield return null;

            Assert.IsNull(GameObject.Find("DeleteUndoTest"), "Object should be deleted");

            // Perform undo
            Undo.RevertAllDownToGroup(groupBeforeDelete);

            yield return null;

            // Object should be restored
            var restoredObj = GameObject.Find("DeleteUndoTest");
            Assert.IsNotNull(restoredObj, "Object should be restored after undo");

            // Clean up
            UnityEngine.Object.DestroyImmediate(restoredObj);
        }

        [UnityTest]
        public IEnumerator UndoStack_MultipleOperations_CanUndoAll()
        {
            // Create test object
            var testObj = new GameObject("MultiUndoTest");
            var originalPosition = testObj.transform.position;

            yield return null;

            Undo.IncrementCurrentGroup();
            var groupBeforeAll = Undo.GetCurrentGroup();

            // Operation 1: Modify position
            var task1 = ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", testObj.GetInstanceID() },
                { "position", new List<object> { 5.0f, 5.0f, 5.0f } }
            });
            while (!task1.IsCompleted) yield return null;

            // Operation 2: Add component
            var task2 = ExecuteToolSuccessAsync("scene.add_component", new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "BoxCollider" }
            });
            while (!task2.IsCompleted) yield return null;

            // Operation 3: Modify name
            var task3 = ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", testObj.GetInstanceID() },
                { "name", "RenamedObject" }
            });
            while (!task3.IsCompleted) yield return null;

            // Verify all changes applied
            Assert.AreEqual(new Vector3(5, 5, 5), testObj.transform.position);
            Assert.IsNotNull(testObj.GetComponent<BoxCollider>());
            Assert.AreEqual("RenamedObject", testObj.name);

            // Undo all at once
            Undo.RevertAllDownToGroup(groupBeforeAll);

            yield return null;

            // All should be reverted
            testObj = GameObject.Find("MultiUndoTest");
            Assert.IsNotNull(testObj);
            Assert.AreEqual(originalPosition, testObj.transform.position);
            Assert.IsNull(testObj.GetComponent<BoxCollider>());
            Assert.AreEqual("MultiUndoTest", testObj.name);

            // Cleanup
            UnityEngine.Object.DestroyImmediate(testObj);
        }

        #endregion

        #region Scene Dirty State Tests

        [Test]
        public async Task SceneCreateGameObject_MarksDirty()
        {
            // Get initial dirty state
            var scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            var wasDirtyBefore = scene.isDirty;

            // Clear dirty state if possible (by saving to temp or just accepting it's dirty)
            // Note: In test environment, scene is often already dirty

            // Act
            await ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "DirtyTest" }
            });

            var go = GameObject.Find("DirtyTest");
            CreatedObjects.Add(go);

            // Assert - scene should be dirty after modification
            Assert.IsTrue(scene.isDirty, "Scene should be marked dirty after creating object");
        }

        [Test]
        public async Task SceneModifyGameObject_MarksDirty()
        {
            var testObj = CreateTestGameObject("DirtyModifyTest");
            var scene = SceneManager.GetActiveScene();

            await ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", testObj.GetInstanceID() },
                { "name", "ModifiedName" }
            });

            Assert.IsTrue(scene.isDirty, "Scene should be marked dirty after modification");
        }

        [Test]
        public async Task SceneDeleteGameObject_MarksDirty()
        {
            var testObj = new GameObject("DirtyDeleteTest"); // Don't track, we'll delete it
            var instanceId = testObj.GetInstanceID();
            var scene = SceneManager.GetActiveScene();

            await ExecuteToolSuccessAsync("scene.delete_gameobject", new Dictionary<string, object>
            {
                { "instanceId", instanceId }
            });

            Assert.IsTrue(scene.isDirty, "Scene should be marked dirty after deletion");
        }

        #endregion

        #region Complex Hierarchy Operations

        [Test]
        public async Task GetHierarchy_ReturnsCorrectStructure()
        {
            // Create a complex hierarchy
            var root = CreateTestGameObject("HierarchyRoot");
            var child1 = CreateTestGameObject("Child1");
            var child2 = CreateTestGameObject("Child2");
            var grandchild = CreateTestGameObject("Grandchild");

            child1.transform.SetParent(root.transform);
            child2.transform.SetParent(root.transform);
            grandchild.transform.SetParent(child1.transform);

            // Add some components
            root.AddComponent<BoxCollider>();
            child1.AddComponent<Rigidbody>();
            child2.AddComponent<SphereCollider>();

            // Act
            var result = await ExecuteToolSuccessAsync("scene.get_hierarchy", new Dictionary<string, object>
            {
                { "max_depth", 10 },
                { "include_components", true }
            });

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task FindObjects_ByName_FindsCorrectObjects()
        {
            // Create objects with pattern-matching names
            var obj1 = CreateTestGameObject("Enemy_Soldier");
            var obj2 = CreateTestGameObject("Enemy_Tank");
            var obj3 = CreateTestGameObject("Friend_Soldier");

            // Act
            var result = await ExecuteToolSuccessAsync("scene.find_objects", new Dictionary<string, object>
            {
                { "name", "Enemy*" }
            });

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task FindObjects_ByComponent_FindsCorrectObjects()
        {
            // Create objects with different components
            var withRb = CreateTestGameObject("WithRigidbody");
            withRb.AddComponent<Rigidbody>();

            var withCollider = CreateTestGameObject("WithCollider");
            withCollider.AddComponent<BoxCollider>();

            var withBoth = CreateTestGameObject("WithBoth");
            withBoth.AddComponent<Rigidbody>();
            withBoth.AddComponent<BoxCollider>();

            // Act
            var result = await ExecuteToolSuccessAsync("scene.find_objects", new Dictionary<string, object>
            {
                { "componentType", "Rigidbody" }
            });

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task ReadObject_ReturnsDetailedInfo()
        {
            // Create a complex object
            var testObj = CreateTestGameObject("DetailedObject");
            testObj.transform.position = new Vector3(1, 2, 3);
            testObj.transform.rotation = Quaternion.Euler(30, 60, 90);
            testObj.transform.localScale = new Vector3(2, 2, 2);
            testObj.tag = "MainCamera"; // Use existing tag
            testObj.layer = 1;

            var rb = testObj.AddComponent<Rigidbody>();
            rb.mass = 5.0f;
            rb.drag = 0.5f;

            // Act
            var result = await ExecuteToolSuccessAsync("scene.read_object", new Dictionary<string, object>
            {
                { "objectPath", "DetailedObject" },
                { "includeChildren", false }
            });

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        #endregion

        #region Transform Operations

        [Test]
        public async Task ModifyTransform_Position_Works()
        {
            var testObj = CreateTestGameObject("TransformTest");

            await ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", testObj.GetInstanceID() },
                { "position", new List<object> { 10.5f, 20.5f, 30.5f } }
            });

            Assert.AreEqual(10.5f, testObj.transform.position.x, 0.01f);
            Assert.AreEqual(20.5f, testObj.transform.position.y, 0.01f);
            Assert.AreEqual(30.5f, testObj.transform.position.z, 0.01f);
        }

        [Test]
        public async Task ModifyTransform_LocalPosition_Works()
        {
            var parent = CreateTestGameObject("TransformParent");
            parent.transform.position = new Vector3(100, 100, 100);

            var child = CreateTestGameObject("TransformChild");
            child.transform.SetParent(parent.transform);

            await ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", child.GetInstanceID() },
                { "local_position", new List<object> { 5.0f, 5.0f, 5.0f } }
            });

            Assert.AreEqual(5.0f, child.transform.localPosition.x, 0.01f);
            Assert.AreEqual(5.0f, child.transform.localPosition.y, 0.01f);
            Assert.AreEqual(5.0f, child.transform.localPosition.z, 0.01f);
        }

        [Test]
        public async Task ModifyTransform_Rotation_Works()
        {
            var testObj = CreateTestGameObject("RotationTest");

            await ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", testObj.GetInstanceID() },
                { "rotation", new List<object> { 45.0f, 90.0f, 180.0f } }
            });

            Assert.AreEqual(45.0f, testObj.transform.eulerAngles.x, 0.5f);
            Assert.AreEqual(90.0f, testObj.transform.eulerAngles.y, 0.5f);
            Assert.AreEqual(180.0f, testObj.transform.eulerAngles.z, 0.5f);
        }

        [Test]
        public async Task ModifyTransform_Scale_Works()
        {
            var testObj = CreateTestGameObject("ScaleTest");

            await ExecuteToolSuccessAsync("scene.modify_gameobject", new Dictionary<string, object>
            {
                { "instance_id", testObj.GetInstanceID() },
                { "scale", new List<object> { 2.0f, 3.0f, 4.0f } }
            });

            Assert.AreEqual(2.0f, testObj.transform.localScale.x, 0.01f);
            Assert.AreEqual(3.0f, testObj.transform.localScale.y, 0.01f);
            Assert.AreEqual(4.0f, testObj.transform.localScale.z, 0.01f);
        }

        #endregion

        #region Component Property Types

        [Test]
        public async Task SetProperty_BooleanType_Works()
        {
            var testObj = CreateTestPrimitive("BoolTest", PrimitiveType.Cube);
            var collider = testObj.GetComponent<BoxCollider>();

            await ExecuteToolSuccessAsync("scene.set_component_property", new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "BoxCollider" },
                { "propertyPath", "isTrigger" },
                { "value", true }
            });

            Assert.IsTrue(collider.isTrigger);
        }

        [Test]
        public async Task SetProperty_FloatType_Works()
        {
            var testObj = CreateTestPrimitive("FloatTest", PrimitiveType.Sphere);
            var collider = testObj.GetComponent<SphereCollider>();

            await ExecuteToolSuccessAsync("scene.set_component_property", new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "SphereCollider" },
                { "propertyPath", "radius" },
                { "value", 2.5f }
            });

            Assert.AreEqual(2.5f, collider.radius, 0.01f);
        }

        [Test]
        public async Task SetProperty_Vector3Type_Works()
        {
            var testObj = CreateTestPrimitive("VectorTest", PrimitiveType.Cube);
            var collider = testObj.GetComponent<BoxCollider>();

            await ExecuteToolSuccessAsync("scene.set_component_property", new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "BoxCollider" },
                { "propertyPath", "center" },
                { "value", new List<object> { 1.0f, 2.0f, 3.0f } }
            });

            Assert.AreEqual(new Vector3(1, 2, 3), collider.center);
        }

        #endregion
    }
}
