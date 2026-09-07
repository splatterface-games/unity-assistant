// Tests for Asset Tool Executors
// Tests asset reading, dependency analysis, and asset operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace Splatter.Tests.Editor.Tools
{
    [TestFixture]
    public class AssetToolExecutorsTests
    {
        private string _testDirectory;
        private string _projectRoot;

        // Test assets created during setup
        private string _testMaterialPath;
        private string _testMaterialGuid;
        private string _testScriptPath;
        private string _testScriptGuid;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _testDirectory = Path.Combine(Application.dataPath, "TestData_AssetTools");

            // Create test directory
            if (!Directory.Exists(_testDirectory))
            {
                Directory.CreateDirectory(_testDirectory);
            }

            // Create test material
            _testMaterialPath = "Assets/TestData_AssetTools/TestMaterial.mat";
            var material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, _testMaterialPath);
            _testMaterialGuid = AssetDatabase.AssetPathToGUID(_testMaterialPath);

            // Create test script
            _testScriptPath = "Assets/TestData_AssetTools/TestScript.cs";
            var scriptContent = @"using UnityEngine;
public class TestScript : MonoBehaviour
{
    public Material testMaterial;
}";
            File.WriteAllText(Path.Combine(_projectRoot, _testScriptPath), scriptContent);
            AssetDatabase.Refresh();
            _testScriptGuid = AssetDatabase.AssetPathToGUID(_testScriptPath);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // Clean up test assets
            if (AssetDatabase.IsValidFolder("Assets/TestData_AssetTools"))
            {
                AssetDatabase.DeleteAsset("Assets/TestData_AssetTools");
            }

            // Also clean up the directory manually if needed
            if (Directory.Exists(_testDirectory))
            {
                try
                {
                    Directory.Delete(_testDirectory, true);
                    var metaFile = _testDirectory + ".meta";
                    if (File.Exists(metaFile))
                    {
                        File.Delete(metaFile);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to clean up test directory: {ex.Message}");
                }
            }
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

        #region AssetReadExecutor Tests

        [Test]
        public async Task AssetRead_ByPath_ReturnsAssetInfo()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task AssetRead_ByGuid_ReturnsAssetInfo()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "guid", _testMaterialGuid }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task AssetRead_InvalidPath_ReturnsError()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/NonExistent/Asset.mat" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task AssetRead_InvalidGuid_ReturnsError()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "guid", "00000000000000000000000000000000" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
        }

        [Test]
        public async Task AssetRead_NoPathOrGuid_ReturnsError()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>());

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task AssetRead_WithSerializedData_ReturnsProperties()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath },
                { "includeSerializedData", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task AssetRead_PathOutsideProject_ReturnsError()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "C:/Windows/System32/somefile.dll" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Access denied") || result.Error.Contains("outside project") || result.Error.Contains("not found"));
        }

        [Test]
        public async Task AssetRead_Script_ReturnsScriptInfo()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testScriptPath }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task AssetRead_EmptyGuid_ReturnsError()
        {
            // Arrange
            var executor = new AssetReadExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "guid", "" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
        }

        #endregion

        #region AssetGetDependenciesExecutor Tests

        [Test]
        public async Task GetDependencies_ValidAsset_ReturnsDependencies()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath },
                { "direction", "dependencies" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task GetDependencies_ByGuid_ReturnsDependencies()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "guid", _testMaterialGuid },
                { "direction", "dependencies" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task GetDependencies_DirectionBoth_ReturnsBothDirections()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath },
                { "direction", "both" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task GetDependencies_DirectionDependents_ReturnsDependents()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath },
                { "direction", "dependents" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task GetDependencies_InvalidPath_ReturnsError()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/NonExistent/Asset.mat" },
                { "direction", "dependencies" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task GetDependencies_InvalidGuid_ReturnsError()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "guid", "00000000000000000000000000000000" },
                { "direction", "dependencies" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
        }

        [Test]
        public async Task GetDependencies_NoPathOrGuid_ReturnsError()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "direction", "dependencies" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task GetDependencies_MaxDepthClamped_ClampsValue()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath },
                { "direction", "dependencies" },
                { "maxDepth", 100 } // Should be clamped to 10
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task GetDependencies_MaxDepthNegative_ClampsToMinimum()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath },
                { "direction", "dependencies" },
                { "maxDepth", -5 } // Should be clamped to 1
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        [Test]
        public async Task GetDependencies_PathOutsideProject_ReturnsError()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "C:/Windows/System32/kernel32.dll" },
                { "direction", "dependencies" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
        }

        [Test]
        public async Task GetDependencies_DefaultDirection_UsesBoth()
        {
            // Arrange
            var executor = new AssetGetDependenciesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", _testMaterialPath }
                // direction not specified, should default to "both"
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
        }

        #endregion

        #region AssetImportExecutor Tests

        [Test]
        public async Task AssetImport_MissingSourcePath_ReturnsError()
        {
            // Arrange
            var executor = new AssetImportExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "dest_path", "Assets/TestData_AssetTools/imported.txt" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task AssetImport_MissingDestPath_ReturnsError()
        {
            // Arrange
            var executor = new AssetImportExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "source_path", "some/source.txt" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task AssetImport_NonExistentSource_ReturnsError()
        {
            // Arrange
            var executor = new AssetImportExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "source_path", "NonExistent/File.txt" },
                { "dest_path", "Assets/TestData_AssetTools/imported.txt" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        #endregion

        #region AssetCreateExecutor Tests

        [Test]
        public async Task AssetCreate_Material_CreatesMaterial()
        {
            // Arrange
            var executor = new AssetCreateExecutor();
            var newMatPath = "Assets/TestData_AssetTools/NewMaterial.mat";
            var context = CreateContext(new Dictionary<string, object>
            {
                { "type", "material" },
                { "path", newMatPath }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");

            // Cleanup
            AssetDatabase.DeleteAsset(newMatPath);
        }

        [Test]
        public async Task AssetCreate_MissingType_ReturnsError()
        {
            // Arrange
            var executor = new AssetCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/TestData_AssetTools/test.mat" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task AssetCreate_MissingPath_ReturnsError()
        {
            // Arrange
            var executor = new AssetCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "type", "material" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task AssetCreate_UnsupportedType_ReturnsError()
        {
            // Arrange
            var executor = new AssetCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "type", "unsupported_type" },
                { "path", "Assets/TestData_AssetTools/test.unknown" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Unsupported"));
        }

        [Test]
        public async Task AssetCreate_WithoutAssetsPrefix_AddsPrefix()
        {
            // Arrange
            var executor = new AssetCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "type", "material" },
                { "path", "TestData_AssetTools/AutoPrefixMaterial.mat" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Cleanup
            AssetDatabase.DeleteAsset("Assets/TestData_AssetTools/AutoPrefixMaterial.mat");
        }

        #endregion

        #region AssetDeleteExecutor Tests

        [Test]
        public async Task AssetDelete_MissingPath_ReturnsError()
        {
            // Arrange
            var executor = new AssetDeleteExecutor();
            var context = CreateContext(new Dictionary<string, object>());

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task AssetDelete_PathOutsideAssets_ReturnsError()
        {
            // Arrange
            var executor = new AssetDeleteExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Packages/com.unity.something/test.cs" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Assets folder"));
        }

        [Test]
        public async Task AssetDelete_NonExistentAsset_ReturnsError()
        {
            // Arrange
            var executor = new AssetDeleteExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/NonExistent/Asset.mat" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
        }

        [Test]
        public async Task AssetDelete_ValidAsset_DeletesAsset()
        {
            // Arrange
            var deletePath = "Assets/TestData_AssetTools/ToDelete.mat";
            var deleteableMat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(deleteableMat, deletePath);
            AssetDatabase.SaveAssets();

            var executor = new AssetDeleteExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", deletePath }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsFalse(File.Exists(Path.Combine(_projectRoot, deletePath)));
        }

        #endregion
    }
}
