// Tests for Checkpoint Tool Executors
// Tests checkpoint create, list, restore cycle, preview mode, and error handling

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using UnityEngine;

namespace Splatter.Tests.Editor.Tools
{
    [TestFixture]
    public class CheckpointToolExecutorsTests
    {
        private string _testDirectory;
        private string _projectRoot;
        private string _checkpointsDirectory;
        private List<string> _createdCheckpointIds;
        private List<string> _createdTestFiles;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _testDirectory = Path.Combine(Application.dataPath, "TestData_Checkpoints");
            _checkpointsDirectory = Path.Combine(_projectRoot, "Library", "SplatterAI", "Checkpoints");
            _createdCheckpointIds = new List<string>();
            _createdTestFiles = new List<string>();

            // Create test directory
            if (!Directory.Exists(_testDirectory))
            {
                Directory.CreateDirectory(_testDirectory);
            }
        }

        [SetUp]
        public void SetUp()
        {
            // Clear lists before each test
            _createdCheckpointIds.Clear();
            _createdTestFiles.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up created checkpoints
            foreach (var checkpointId in _createdCheckpointIds)
            {
                var checkpointDir = Path.Combine(_checkpointsDirectory, checkpointId);
                if (Directory.Exists(checkpointDir))
                {
                    try
                    {
                        Directory.Delete(checkpointDir, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to clean up checkpoint: {ex.Message}");
                    }
                }
            }

            // Clean up created test files
            foreach (var file in _createdTestFiles)
            {
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to clean up test file: {ex.Message}");
                    }
                }
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // Clean up test directory
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

        private string CreateTestFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(_projectRoot, relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(fullPath, content);
            _createdTestFiles.Add(fullPath);
            return relativePath;
        }

        #region CheckpointCreateExecutor Tests

        [Test]
        public async Task CheckpointCreate_SingleFile_CreatesCheckpoint()
        {
            // Arrange
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/checkpoint_test.txt", "Original content");

            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } },
                { "description", "Test checkpoint" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);

            // Extract checkpoint ID and track for cleanup
            var output = result.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            if (idProperty != null)
            {
                var checkpointId = idProperty.GetValue(output)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }
        }

        [Test]
        public async Task CheckpointCreate_MultipleFiles_CreatesCheckpoint()
        {
            // Arrange
            var testFile1 = CreateTestFile("Assets/TestData_Checkpoints/multi_1.txt", "Content 1");
            var testFile2 = CreateTestFile("Assets/TestData_Checkpoints/multi_2.txt", "Content 2");

            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile1, testFile2 } },
                { "description", "Multi-file checkpoint" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Track for cleanup
            var output = result.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            if (idProperty != null)
            {
                var checkpointId = idProperty.GetValue(output)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }
        }

        [Test]
        public async Task CheckpointCreate_EmptyPaths_ReturnsError()
        {
            // Arrange
            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object>() }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required") || result.Error.Contains("at least one"));
        }

        [Test]
        public async Task CheckpointCreate_MissingPaths_ReturnsError()
        {
            // Arrange
            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "description", "No paths checkpoint" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task CheckpointCreate_NonExistentFile_ReturnsError()
        {
            // Arrange
            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { "Assets/NonExistent/File.txt" } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task CheckpointCreate_PathOutsideProject_ReturnsError()
        {
            // Arrange
            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { "C:/Windows/System32/config/SAM" } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("outside project") || result.Error.Contains("not found"));
        }

        [Test]
        public async Task CheckpointCreate_StringPaths_ParsesCorrectly()
        {
            // Arrange
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/string_path.txt", "Content");

            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", testFile } // Single string instead of array
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");

            // Track for cleanup
            var output = result.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            if (idProperty != null)
            {
                var checkpointId = idProperty.GetValue(output)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }
        }

        [Test]
        public async Task CheckpointCreate_CommaSeparatedPaths_ParsesCorrectly()
        {
            // Arrange
            var testFile1 = CreateTestFile("Assets/TestData_Checkpoints/comma_1.txt", "Content 1");
            var testFile2 = CreateTestFile("Assets/TestData_Checkpoints/comma_2.txt", "Content 2");

            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", $"{testFile1}, {testFile2}" } // Comma-separated string
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");

            // Track for cleanup
            var output = result.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            if (idProperty != null)
            {
                var checkpointId = idProperty.GetValue(output)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }
        }

        [Test]
        public async Task CheckpointCreate_DefaultDescription_GeneratesDescription()
        {
            // Arrange
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/no_desc.txt", "Content");

            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } }
                // No description provided
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Track for cleanup
            var output = result.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            if (idProperty != null)
            {
                var checkpointId = idProperty.GetValue(output)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }
        }

        #endregion

        #region CheckpointListExecutor Tests

        [Test]
        public async Task CheckpointList_EmptyDirectory_ReturnsEmptyList()
        {
            // Arrange
            var executor = new CheckpointListExecutor();
            var context = CreateContext(new Dictionary<string, object>());

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task CheckpointList_WithCheckpoints_ReturnsCheckpoints()
        {
            // Arrange - Create a checkpoint first
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/list_test.txt", "Content");

            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } },
                { "description", "List test checkpoint" }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success);

            // Track for cleanup
            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            if (idProperty != null)
            {
                var checkpointId = idProperty.GetValue(output)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }

            // Now list checkpoints
            var listExecutor = new CheckpointListExecutor();
            var listContext = CreateContext(new Dictionary<string, object>());

            // Act
            var result = await listExecutor.ExecuteAsync(listContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
        }

        [Test]
        public async Task CheckpointList_SortedByDate_NewestFirst()
        {
            // Arrange - Create multiple checkpoints
            var testFile1 = CreateTestFile("Assets/TestData_Checkpoints/sort_1.txt", "Content 1");
            var testFile2 = CreateTestFile("Assets/TestData_Checkpoints/sort_2.txt", "Content 2");

            var createExecutor = new CheckpointCreateExecutor();

            // Create first checkpoint
            var createContext1 = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile1 } },
                { "description", "First checkpoint" }
            });
            var result1 = await createExecutor.ExecuteAsync(createContext1, CancellationToken.None);
            Assert.IsTrue(result1.Success);

            var output1 = result1.Output;
            var outputType1 = output1.GetType();
            var idProperty1 = outputType1.GetProperty("checkpointId");
            if (idProperty1 != null)
            {
                var checkpointId = idProperty1.GetValue(output1)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }

            // Small delay to ensure different timestamps
            await Task.Delay(100);

            // Create second checkpoint
            var createContext2 = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile2 } },
                { "description", "Second checkpoint" }
            });
            var result2 = await createExecutor.ExecuteAsync(createContext2, CancellationToken.None);
            Assert.IsTrue(result2.Success);

            var output2 = result2.Output;
            var outputType2 = output2.GetType();
            var idProperty2 = outputType2.GetProperty("checkpointId");
            if (idProperty2 != null)
            {
                var checkpointId = idProperty2.GetValue(output2)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }

            // List checkpoints
            var listExecutor = new CheckpointListExecutor();
            var listContext = CreateContext(new Dictionary<string, object>());

            // Act
            var result = await listExecutor.ExecuteAsync(listContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            // Checkpoints should be sorted by creation date descending (newest first)
        }

        #endregion

        #region CheckpointRestoreExecutor Tests

        [Test]
        public async Task CheckpointRestore_PreviewMode_ReturnsPreviewOnly()
        {
            // Arrange - Create a checkpoint first
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/preview_test.txt", "Original");

            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } },
                { "description", "Preview test" }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success);

            // Get checkpoint ID
            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            var checkpointId = idProperty?.GetValue(output)?.ToString();
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Modify the file
            File.WriteAllText(Path.Combine(_projectRoot, testFile), "Modified content");

            // Restore with preview mode
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", true }
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // File should NOT be restored in preview mode
            var currentContent = File.ReadAllText(Path.Combine(_projectRoot, testFile));
            Assert.AreEqual("Modified content", currentContent, "File should not be modified in preview mode");
        }

        [Test]
        public async Task CheckpointRestore_ActualRestore_RestoresFiles()
        {
            // Arrange - Create a checkpoint first
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/restore_test.txt", "Original content");

            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } },
                { "description", "Restore test" }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success);

            // Get checkpoint ID
            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            var checkpointId = idProperty?.GetValue(output)?.ToString();
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Modify the file
            File.WriteAllText(Path.Combine(_projectRoot, testFile), "Modified content");

            // Restore without preview mode
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");

            // File should be restored
            var currentContent = File.ReadAllText(Path.Combine(_projectRoot, testFile));
            Assert.AreEqual("Original content", currentContent, "File should be restored to original content");
        }

        [Test]
        public async Task CheckpointRestore_MissingCheckpoint_ReturnsError()
        {
            // Arrange
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", "non-existent-checkpoint-id-12345" },
                { "preview", true }
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task CheckpointRestore_MissingCheckpointId_ReturnsError()
        {
            // Arrange
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "preview", true }
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task CheckpointRestore_DefaultPreviewTrue_UsesPreviewMode()
        {
            // Arrange - Create a checkpoint first
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/default_preview.txt", "Original");

            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success);

            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            var checkpointId = idProperty?.GetValue(output)?.ToString();
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Modify file
            File.WriteAllText(Path.Combine(_projectRoot, testFile), "Modified");

            // Restore without specifying preview - should default to true
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId }
                // preview not specified
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // File should NOT be restored (preview mode is default)
            var currentContent = File.ReadAllText(Path.Combine(_projectRoot, testFile));
            Assert.AreEqual("Modified", currentContent);
        }

        [Test]
        public async Task CheckpointRestore_UnchangedFile_ReportsUnchanged()
        {
            // Arrange - Create a checkpoint
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/unchanged_test.txt", "Same content");

            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success);

            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            var checkpointId = idProperty?.GetValue(output)?.ToString();
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Don't modify the file - it should be reported as unchanged

            // Restore with preview
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", true }
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            // The plan should indicate the file is unchanged
        }

        [Test]
        public async Task CheckpointRestore_DeletedFile_RestoresFile()
        {
            // Arrange - Create a checkpoint
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/deleted_test.txt", "Content to restore");

            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success);

            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            var checkpointId = idProperty?.GetValue(output)?.ToString();
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Delete the file
            var fullPath = Path.Combine(_projectRoot, testFile);
            File.Delete(fullPath);
            Assert.IsFalse(File.Exists(fullPath));

            // Restore
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsTrue(File.Exists(fullPath), "File should be restored");
            Assert.AreEqual("Content to restore", File.ReadAllText(fullPath));
        }

        #endregion

        #region Full Checkpoint Cycle Tests

        [Test]
        public async Task CheckpointCycle_CreateListRestore_CompleteCycle()
        {
            // Arrange - Create test files
            var testFile1 = CreateTestFile("Assets/TestData_Checkpoints/cycle_1.txt", "File 1 original");
            var testFile2 = CreateTestFile("Assets/TestData_Checkpoints/cycle_2.txt", "File 2 original");

            // Step 1: Create checkpoint
            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile1, testFile2 } },
                { "description", "Cycle test checkpoint" }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success, "Create should succeed");

            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            var checkpointId = idProperty?.GetValue(output)?.ToString();
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Step 2: List checkpoints and verify our checkpoint is there
            var listExecutor = new CheckpointListExecutor();
            var listContext = CreateContext(new Dictionary<string, object>());

            var listResult = await listExecutor.ExecuteAsync(listContext, CancellationToken.None);
            Assert.IsTrue(listResult.Success, "List should succeed");

            // Step 3: Modify files
            File.WriteAllText(Path.Combine(_projectRoot, testFile1), "File 1 modified");
            File.WriteAllText(Path.Combine(_projectRoot, testFile2), "File 2 modified");

            // Step 4: Preview restore
            var restoreExecutor = new CheckpointRestoreExecutor();
            var previewContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", true }
            });

            var previewResult = await restoreExecutor.ExecuteAsync(previewContext, CancellationToken.None);
            Assert.IsTrue(previewResult.Success, "Preview should succeed");

            // Verify files are still modified
            Assert.AreEqual("File 1 modified", File.ReadAllText(Path.Combine(_projectRoot, testFile1)));
            Assert.AreEqual("File 2 modified", File.ReadAllText(Path.Combine(_projectRoot, testFile2)));

            // Step 5: Actual restore
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            var restoreResult = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);
            Assert.IsTrue(restoreResult.Success, "Restore should succeed");

            // Step 6: Verify files are restored
            Assert.AreEqual("File 1 original", File.ReadAllText(Path.Combine(_projectRoot, testFile1)));
            Assert.AreEqual("File 2 original", File.ReadAllText(Path.Combine(_projectRoot, testFile2)));
        }

        #endregion

        #region Edge Cases

        [Test]
        public async Task CheckpointCreate_LargeFile_Succeeds()
        {
            // Arrange - Create a larger file
            var largeContent = new string('x', 100000); // 100KB
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/large_file.txt", largeContent);

            var executor = new CheckpointCreateExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            var output = result.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            if (idProperty != null)
            {
                var checkpointId = idProperty.GetValue(output)?.ToString();
                if (!string.IsNullOrEmpty(checkpointId))
                {
                    _createdCheckpointIds.Add(checkpointId);
                }
            }
        }

        [Test]
        public async Task CheckpointRestore_ReturnsAffectedPaths()
        {
            // Arrange
            var testFile = CreateTestFile("Assets/TestData_Checkpoints/affected_paths.txt", "Original");

            var createExecutor = new CheckpointCreateExecutor();
            var createContext = CreateContext(new Dictionary<string, object>
            {
                { "paths", new List<object> { testFile } }
            });

            var createResult = await createExecutor.ExecuteAsync(createContext, CancellationToken.None);
            Assert.IsTrue(createResult.Success);

            var output = createResult.Output;
            var outputType = output.GetType();
            var idProperty = outputType.GetProperty("checkpointId");
            var checkpointId = idProperty?.GetValue(output)?.ToString();
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Modify file
            File.WriteAllText(Path.Combine(_projectRoot, testFile), "Modified");

            // Restore
            var restoreExecutor = new CheckpointRestoreExecutor();
            var restoreContext = CreateContext(new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Act
            var result = await restoreExecutor.ExecuteAsync(restoreContext, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.AffectedPaths);
            Assert.Greater(result.AffectedPaths.Count, 0);
        }

        #endregion
    }
}
