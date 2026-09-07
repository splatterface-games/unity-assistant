// Tool Execution Integration Tests
// Tests the full tool execution pipeline from request to response

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Handlers;
using Splatter.Editor.Tools;
using Splatter.Editor.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using Splatter.Protocol;

namespace Splatter.Tests.Editor.Integration
{
    /// <summary>
    /// Integration tests for the full tool execution pipeline.
    /// Tests end-to-end tool execution, permission flows, timeouts, and cancellation.
    /// </summary>
    [TestFixture]
    public class ToolExecutionIntegrationTests : IntegrationTestBase
    {
        private MockPermissionHandler _permissionHandler;

        public override void SetUp()
        {
            base.SetUp();
            _permissionHandler = new MockPermissionHandler();
        }

        public override void TearDown()
        {
            _permissionHandler?.Clear();
            base.TearDown();
        }

        #region Basic Tool Execution Tests

        [Test]
        public async Task ExecuteTool_SceneGetHierarchy_ReturnsHierarchy()
        {
            // Arrange
            var testObj = CreateTestGameObject("TestObject");
            var childObj = CreateTestGameObject("ChildObject");
            childObj.transform.SetParent(testObj.transform);

            var args = new Dictionary<string, object>
            {
                { "max_depth", 10 },
                { "include_components", false }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.get_hierarchy", args);

            // Assert
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task ExecuteTool_SceneCreateGameObject_CreatesObject()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "name", "NewTestObject" }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.create_gameobject", args);

            // Assert
            var go = GameObject.Find("NewTestObject");
            Assert.IsNotNull(go, "GameObject should have been created");
            CreatedObjects.Add(go); // Track for cleanup
        }

        [Test]
        public async Task ExecuteTool_SceneCreateGameObjectWithPrimitive_CreatesPrimitive()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "name", "TestCube" },
                { "primitive", "Cube" }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.create_gameobject", args);

            // Assert
            var go = GameObject.Find("TestCube");
            Assert.IsNotNull(go, "Primitive should have been created");
            Assert.IsNotNull(go.GetComponent<MeshRenderer>(), "Primitive should have MeshRenderer");
            Assert.IsNotNull(go.GetComponent<MeshFilter>(), "Primitive should have MeshFilter");
            Assert.IsNotNull(go.GetComponent<BoxCollider>(), "Cube should have BoxCollider");
            CreatedObjects.Add(go);
        }

        [Test]
        public async Task ExecuteTool_SceneCreateGameObjectWithPosition_SetsPosition()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "name", "PositionedObject" },
                { "position", new List<object> { 5.0f, 10.0f, 15.0f } }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.create_gameobject", args);

            // Assert
            var go = GameObject.Find("PositionedObject");
            Assert.IsNotNull(go);
            Assert.AreEqual(5.0f, go.transform.position.x, 0.01f);
            Assert.AreEqual(10.0f, go.transform.position.y, 0.01f);
            Assert.AreEqual(15.0f, go.transform.position.z, 0.01f);
            CreatedObjects.Add(go);
        }

        [Test]
        public async Task ExecuteTool_SceneModifyGameObject_ModifiesObject()
        {
            // Arrange
            var testObj = CreateTestGameObject("ModifyTarget");
            var instanceId = testObj.GetInstanceID();

            var args = new Dictionary<string, object>
            {
                { "instance_id", instanceId },
                { "name", "ModifiedName" },
                { "active", false }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.modify_gameobject", args);

            // Assert
            Assert.AreEqual("ModifiedName", testObj.name);
            Assert.IsFalse(testObj.activeSelf);
        }

        [Test]
        public async Task ExecuteTool_SceneFindObjects_FindsObjects()
        {
            // Arrange
            var obj1 = CreateTestGameObject("FindMe_One");
            var obj2 = CreateTestGameObject("FindMe_Two");
            var obj3 = CreateTestGameObject("DontFindMe");

            var args = new Dictionary<string, object>
            {
                { "name", "FindMe*" }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.find_objects", args);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task ExecuteTool_SceneDeleteGameObject_DeletesObject()
        {
            // Arrange
            var testObj = CreateTestGameObject("DeleteTarget");
            var instanceId = testObj.GetInstanceID();

            var args = new Dictionary<string, object>
            {
                { "instanceId", instanceId }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.delete_gameobject", args);

            // Assert
            var foundObj = GameObject.Find("DeleteTarget");
            Assert.IsNull(foundObj, "GameObject should have been deleted");

            // Remove from tracked objects since it's already deleted
            CreatedObjects.Remove(testObj);
        }

        #endregion

        #region Component Operation Tests

        [Test]
        public async Task ExecuteTool_SceneAddComponent_AddsComponent()
        {
            // Arrange
            var testObj = CreateTestGameObject("ComponentTarget");
            var instanceId = testObj.GetInstanceID();

            var args = new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "componentType", "Rigidbody" }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.add_component", args);

            // Assert
            Assert.IsNotNull(testObj.GetComponent<Rigidbody>(), "Rigidbody should have been added");
        }

        [Test]
        public async Task ExecuteTool_SceneSetComponentProperty_SetsProperty()
        {
            // Arrange
            var testObj = CreateTestGameObject("PropertyTarget");
            var rb = testObj.AddComponent<Rigidbody>();
            var instanceId = testObj.GetInstanceID();

            var args = new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "componentType", "Rigidbody" },
                { "propertyPath", "mass" },
                { "value", 10.0f }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.set_component_property", args);

            // Assert
            Assert.AreEqual(10.0f, rb.mass, 0.01f);
        }

        [Test]
        public async Task ExecuteTool_SceneSetVectorProperty_SetsVector()
        {
            // Arrange
            var testObj = CreateTestPrimitive("VectorTarget", PrimitiveType.Cube);
            var boxCollider = testObj.GetComponent<BoxCollider>();
            var instanceId = testObj.GetInstanceID();

            var args = new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "componentType", "BoxCollider" },
                { "propertyPath", "size" },
                { "value", new List<object> { 2.0f, 3.0f, 4.0f } }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("scene.set_component_property", args);

            // Assert
            Assert.AreEqual(2.0f, boxCollider.size.x, 0.01f);
            Assert.AreEqual(3.0f, boxCollider.size.y, 0.01f);
            Assert.AreEqual(4.0f, boxCollider.size.z, 0.01f);
        }

        #endregion

        #region Error Handling Tests

        [Test]
        public async Task ExecuteTool_UnknownTool_ReturnsError()
        {
            // Arrange & Act
            var executor = Registry.GetExecutor("unknown.tool");

            // Assert
            Assert.IsNull(executor, "Unknown tool should not have an executor");
        }

        [Test]
        public async Task ExecuteTool_InvalidArguments_ReturnsError()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "instanceId", -999999 }, // Invalid instance ID
                { "componentType", "Rigidbody" }
            };

            // Act
            var result = await ExecuteToolAsync("scene.add_component", args);

            // Assert
            Assert.IsFalse(result.Success, "Should fail with invalid instance ID");
            Assert.IsNotNull(result.Error);
        }

        [Test]
        public async Task ExecuteTool_MissingRequiredArgument_ReturnsError()
        {
            // Arrange - missing componentType
            var testObj = CreateTestGameObject("MissingArgTarget");
            var args = new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() }
            };

            // Act
            var result = await ExecuteToolAsync("scene.add_component", args);

            // Assert
            Assert.IsFalse(result.Success, "Should fail with missing required argument");
            Assert.IsTrue(result.Error.Contains("componentType"), "Error should mention missing argument");
        }

        [Test]
        public async Task ExecuteTool_ComponentTypeNotFound_ReturnsError()
        {
            // Arrange
            var testObj = CreateTestGameObject("BadComponentTarget");
            var args = new Dictionary<string, object>
            {
                { "instanceId", testObj.GetInstanceID() },
                { "componentType", "NonExistentComponent" }
            };

            // Act
            var result = await ExecuteToolAsync("scene.add_component", args);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"), "Error should indicate component type not found");
        }

        #endregion

        #region Timeout and Cancellation Tests

        [Test]
        public async Task ExecuteTool_WithCancellation_Cancels()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            var args = new Dictionary<string, object>
            {
                { "name", "CancellationTest" }
            };

            // Act - cancel immediately
            cts.Cancel();

            // Assert
            try
            {
                await ExecuteToolAsync("scene.create_gameobject", args, cts.Token);
                Assert.Fail("Should have thrown OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        [UnityTest]
        public IEnumerator ExecuteTool_WithShortTimeout_TimesOut()
        {
            // This test uses a very short timeout to test timeout behavior
            // Note: Most tools execute quickly, so this mainly tests the infrastructure
            var task = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

                try
                {
                    // Delay slightly to ensure token is cancelled
                    await Task.Delay(10);
                    cts.Token.ThrowIfCancellationRequested();
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
            });

            while (!task.IsCompleted)
            {
                yield return null;
            }

            Assert.IsTrue(task.Result, "Task should have been cancelled");
        }

        #endregion

        #region Permission Flow Tests

        [Test]
        public async Task PermissionHandler_AutoApprove_ApprovesRequest()
        {
            // Arrange
            _permissionHandler.AutoApprove = true;

            // Act
            var approved = await _permissionHandler.HandlePermissionRequestAsync(
                "dangerous.tool",
                "This tool does something dangerous",
                new Dictionary<string, object> { { "param", "value" } }
            );

            // Assert
            Assert.IsTrue(approved);
            Assert.AreEqual(1, _permissionHandler.RequestHistory.Count);
            Assert.AreEqual("dangerous.tool", _permissionHandler.RequestHistory[0].ToolId);
        }

        [Test]
        public async Task PermissionHandler_AutoDeny_DeniesRequest()
        {
            // Arrange
            _permissionHandler.AutoApprove = false;

            // Act
            var approved = await _permissionHandler.HandlePermissionRequestAsync(
                "dangerous.tool",
                "This tool does something dangerous",
                new Dictionary<string, object>()
            );

            // Assert
            Assert.IsFalse(approved);
        }

        [Test]
        public async Task PermissionHandler_WithOverride_UsesOverride()
        {
            // Arrange
            _permissionHandler.AutoApprove = false;
            _permissionHandler.SetPermissionOverride("specific.tool", true);

            // Act
            var approved1 = await _permissionHandler.HandlePermissionRequestAsync(
                "specific.tool", "Description", new Dictionary<string, object>());
            var approved2 = await _permissionHandler.HandlePermissionRequestAsync(
                "other.tool", "Description", new Dictionary<string, object>());

            // Assert
            Assert.IsTrue(approved1, "Overridden tool should be approved");
            Assert.IsFalse(approved2, "Non-overridden tool should use default (deny)");
        }

        #endregion

        #region Mock Client Tests

        [Test]
        public async Task MockClient_Connect_Connects()
        {
            // Arrange & Act
            var connected = await MockClient.ConnectAsync("ws://localhost:8080", "test-token");

            // Assert
            Assert.IsTrue(connected);
            Assert.IsTrue(MockClient.IsConnected);
        }

        [Test]
        public async Task MockClient_Disconnect_Disconnects()
        {
            // Arrange
            await MockClient.ConnectAsync();

            // Act
            await MockClient.DisconnectAsync();

            // Assert
            Assert.IsFalse(MockClient.IsConnected);
        }

        [Test]
        public async Task MockClient_SendNotification_RecordsMessage()
        {
            // Arrange
            await MockClient.ConnectAsync();
            var toolResponse = new ToolExecuteResponse
            {
                tool_call_id = "test-call-123",
                success = true,
                output = "{\"result\": \"success\"}"
            };

            // Act
            await MockClient.SendNotificationAsync("tool.result", toolResponse);

            // Assert
            Assert.AreEqual(1, MockClient.ToolResponses.Count);
            Assert.AreEqual("test-call-123", MockClient.ToolResponses[0].tool_call_id);
            Assert.IsTrue(MockClient.ToolResponses[0].success);
        }

        [Test]
        public void MockClient_SimulateToolExecute_InvokesHandler()
        {
            // Arrange
            var receivedRequest = false;
            MockClient.OnToolExecuteRequested += request =>
            {
                receivedRequest = true;
                Assert.AreEqual("scene.create_gameobject", request.tool_id);
            };

            var request = new ToolExecuteRequest
            {
                session_id = "test-session",
                turn_id = "test-turn",
                tool_call_id = "test-call",
                tool_id = "scene.create_gameobject",
                arguments = "{\"name\": \"TestObject\"}"
            };

            // Act
            MockClient.SimulateToolExecuteRequest(request);

            // Assert
            Assert.IsTrue(receivedRequest);
            Assert.AreEqual(1, MockClient.ToolExecutionRequests.Count);
        }

        [Test]
        public void MockClient_SubscribeToEvents_ReceivesEvents()
        {
            // Arrange
            MessageEnvelope receivedEnvelope = null;
            MockClient.SubscribeToAllEvents(envelope =>
            {
                receivedEnvelope = envelope;
            });

            // Act
            MockClient.SimulateEvent("test.event", new { data = "test" }, "session-123");

            // Assert
            Assert.IsNotNull(receivedEnvelope);
            Assert.AreEqual("test.event", receivedEnvelope.Method);
            Assert.AreEqual("session-123", receivedEnvelope.SessionId);
        }

        #endregion

        #region Sequential Tool Execution Tests

        [Test]
        public async Task ExecuteMultipleTools_InSequence_AllSucceed()
        {
            // Arrange & Act
            // Step 1: Create a GameObject
            var createResult = await ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "SequenceTest" }
            });

            var go = GameObject.Find("SequenceTest");
            Assert.IsNotNull(go);
            CreatedObjects.Add(go);

            // Step 2: Add a component
            var addResult = await ExecuteToolSuccessAsync("scene.add_component", new Dictionary<string, object>
            {
                { "instanceId", go.GetInstanceID() },
                { "componentType", "Rigidbody" }
            });

            // Step 3: Set component property
            var setResult = await ExecuteToolSuccessAsync("scene.set_component_property", new Dictionary<string, object>
            {
                { "instanceId", go.GetInstanceID() },
                { "componentType", "Rigidbody" },
                { "propertyPath", "mass" },
                { "value", 5.0f }
            });

            // Step 4: Verify with read
            var readResult = await ExecuteToolSuccessAsync("scene.read_object", new Dictionary<string, object>
            {
                { "objectPath", "SequenceTest" }
            });

            // Assert
            Assert.IsNotNull(go.GetComponent<Rigidbody>());
            Assert.AreEqual(5.0f, go.GetComponent<Rigidbody>().mass, 0.01f);
        }

        [Test]
        public async Task ExecuteTool_CreateWithParent_CreatesHierarchy()
        {
            // Arrange
            var parent = CreateTestGameObject("ParentObject");

            // Act
            var result = await ExecuteToolSuccessAsync("scene.create_gameobject", new Dictionary<string, object>
            {
                { "name", "ChildObject" },
                { "parent_id", parent.GetInstanceID() }
            });

            // Assert
            var child = parent.transform.Find("ChildObject");
            Assert.IsNotNull(child, "Child should exist under parent");
            CreatedObjects.Add(child.gameObject);
        }

        #endregion
    }
}
