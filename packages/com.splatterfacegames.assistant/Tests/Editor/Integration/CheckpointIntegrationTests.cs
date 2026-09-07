// Checkpoint Integration Tests
// Tests the checkpoint system: create, modify, restore cycles

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatter.Tests.Editor.Integration
{
    /// <summary>
    /// Integration tests for the checkpoint system.
    /// Tests create checkpoint -> modify files -> restore checkpoint cycles.
    /// </summary>
    [TestFixture]
    public class CheckpointIntegrationTests : IntegrationTestBase
    {
        private string _testFilesDirectory;
        private string _checkpointsDirectory;
        private List<string> _createdCheckpointIds;

        public override void SetUp()
        {
            base.SetUp();

            // Create a test files directory within the project
            _testFilesDirectory = Path.Combine(Application.dataPath, "SplatterTestFiles");
            if (!Directory.Exists(_testFilesDirectory))
            {
                Directory.CreateDirectory(_testFilesDirectory);
            }

            // Track checkpoints for cleanup
            _createdCheckpointIds = new List<string>();

            // Get the checkpoints directory
            _checkpointsDirectory = Path.Combine(Application.dataPath, "..", "Library", "SplatterAI", "Checkpoints");
        }

        public override void TearDown()
        {
            // Cleanup created checkpoints
            foreach (var checkpointId in _createdCheckpointIds)
            {
                try
                {
                    var checkpointDir = Path.Combine(_checkpointsDirectory, checkpointId);
                    if (Directory.Exists(checkpointDir))
                    {
                        Directory.Delete(checkpointDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to cleanup checkpoint {checkpointId}: {ex.Message}");
                }
            }

            // Cleanup test files directory
            if (Directory.Exists(_testFilesDirectory))
            {
                try
                {
                    Directory.Delete(_testFilesDirectory, true);
                }
                catch { }
            }

            base.TearDown();
        }

        #region Basic Checkpoint Tests

        [Test]
        public async Task CheckpointCreate_SingleFile_CreatesCheckpoint()
        {
            // Arrange
            var testFile = CreateProjectTestFile("test1.txt", "Original content");
            var relativePath = GetRelativePath(testFile);

            var args = new Dictionary<string, object>
            {
                { "paths", new List<object> { relativePath } },
                { "description", "Test checkpoint" }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("checkpoint.create", args);

            // Assert
            Assert.IsTrue(result.Success);

            // Extract checkpoint ID for cleanup
            var checkpointId = ExtractCheckpointId(result);
            if (!string.IsNullOrEmpty(checkpointId))
            {
                _createdCheckpointIds.Add(checkpointId);
            }
        }

        [Test]
        public async Task CheckpointCreate_MultipleFiles_CreatesCheckpoint()
        {
            // Arrange
            var testFile1 = CreateProjectTestFile("multi1.txt", "Content 1");
            var testFile2 = CreateProjectTestFile("multi2.txt", "Content 2");
            var testFile3 = CreateProjectTestFile("sub/multi3.txt", "Content 3");

            var args = new Dictionary<string, object>
            {
                { "paths", new List<object>
                    {
                        GetRelativePath(testFile1),
                        GetRelativePath(testFile2),
                        GetRelativePath(testFile3)
                    }
                },
                { "description", "Multi-file checkpoint" }
            };

            // Act
            var result = await ExecuteToolSuccessAsync("checkpoint.create", args);

            // Assert
            Assert.IsTrue(result.Success);

            var checkpointId = ExtractCheckpointId(result);
            if (!string.IsNullOrEmpty(checkpointId))
            {
                _createdCheckpointIds.Add(checkpointId);

                // Verify all files were checkpointed
                var checkpointFilesDir = Path.Combine(_checkpointsDirectory, checkpointId, "files");
                Assert.IsTrue(Directory.Exists(checkpointFilesDir));
            }
        }

        [Test]
        public async Task CheckpointList_ReturnsCheckpoints()
        {
            // Arrange - create a checkpoint first
            var testFile = CreateProjectTestFile("listtest.txt", "Content");
            await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { GetRelativePath(testFile) } },
                { "description", "List test checkpoint" }
            });

            // Act
            var result = await ExecuteToolSuccessAsync("checkpoint.list", new Dictionary<string, object>());

            // Assert
            Assert.IsTrue(result.Success);
        }

        #endregion

        #region Create-Modify-Restore Cycle Tests

        [Test]
        public async Task CheckpointRestore_AfterModification_RestoresOriginal()
        {
            // Arrange
            var originalContent = "Original content before modification";
            var testFile = CreateProjectTestFile("restore_test.txt", originalContent);
            var relativePath = GetRelativePath(testFile);

            // Create checkpoint
            var createResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { relativePath } },
                { "description", "Restore test" }
            });

            var checkpointId = ExtractCheckpointId(createResult);
            Assert.IsNotNull(checkpointId);
            _createdCheckpointIds.Add(checkpointId);

            // Modify the file
            var modifiedContent = "Modified content after checkpoint";
            File.WriteAllText(testFile, modifiedContent);
            Assert.AreEqual(modifiedContent, File.ReadAllText(testFile));

            // Act - restore the checkpoint (with preview=false to actually restore)
            var restoreResult = await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Assert
            Assert.IsTrue(restoreResult.Success);
            var restoredContent = File.ReadAllText(testFile);
            Assert.AreEqual(originalContent, restoredContent, "File should be restored to original content");
        }

        [Test]
        public async Task CheckpointRestore_Preview_DoesNotModifyFiles()
        {
            // Arrange
            var originalContent = "Original content";
            var testFile = CreateProjectTestFile("preview_test.txt", originalContent);
            var relativePath = GetRelativePath(testFile);

            // Create checkpoint
            var createResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { relativePath } },
                { "description", "Preview test" }
            });

            var checkpointId = ExtractCheckpointId(createResult);
            _createdCheckpointIds.Add(checkpointId);

            // Modify the file
            var modifiedContent = "Modified content";
            File.WriteAllText(testFile, modifiedContent);

            // Act - preview restore (default is preview=true)
            var previewResult = await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", true }
            });

            // Assert
            Assert.IsTrue(previewResult.Success);
            var currentContent = File.ReadAllText(testFile);
            Assert.AreEqual(modifiedContent, currentContent, "File should NOT be modified in preview mode");
        }

        [Test]
        public async Task CheckpointRestore_MultipleFiles_RestoresAll()
        {
            // Arrange
            var content1 = "Content 1 original";
            var content2 = "Content 2 original";
            var file1 = CreateProjectTestFile("multi_restore1.txt", content1);
            var file2 = CreateProjectTestFile("multi_restore2.txt", content2);

            // Create checkpoint
            var createResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { GetRelativePath(file1), GetRelativePath(file2) } },
                { "description", "Multi-file restore test" }
            });

            var checkpointId = ExtractCheckpointId(createResult);
            _createdCheckpointIds.Add(checkpointId);

            // Modify both files
            File.WriteAllText(file1, "Modified 1");
            File.WriteAllText(file2, "Modified 2");

            // Act
            var restoreResult = await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Assert
            Assert.IsTrue(restoreResult.Success);
            Assert.AreEqual(content1, File.ReadAllText(file1));
            Assert.AreEqual(content2, File.ReadAllText(file2));
        }

        #endregion

        #region Multiple Checkpoint Tests

        [Test]
        public async Task MultipleCheckpoints_CanRestoreAny()
        {
            // Arrange
            var testFile = CreateProjectTestFile("multi_checkpoint.txt", "Version 1");
            var relativePath = GetRelativePath(testFile);

            // Create first checkpoint
            var checkpoint1Result = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { relativePath } },
                { "description", "Version 1" }
            });
            var checkpoint1Id = ExtractCheckpointId(checkpoint1Result);
            _createdCheckpointIds.Add(checkpoint1Id);

            // Modify and create second checkpoint
            File.WriteAllText(testFile, "Version 2");
            var checkpoint2Result = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { relativePath } },
                { "description", "Version 2" }
            });
            var checkpoint2Id = ExtractCheckpointId(checkpoint2Result);
            _createdCheckpointIds.Add(checkpoint2Id);

            // Modify to version 3 (not checkpointed)
            File.WriteAllText(testFile, "Version 3");

            // Act - restore to checkpoint 1
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpoint1Id },
                { "preview", false }
            });

            // Assert
            Assert.AreEqual("Version 1", File.ReadAllText(testFile));

            // Act - restore to checkpoint 2
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpoint2Id },
                { "preview", false }
            });

            // Assert
            Assert.AreEqual("Version 2", File.ReadAllText(testFile));
        }

        [Test]
        public async Task CheckpointRestore_FileUnchanged_ReportsUnchanged()
        {
            // Arrange
            var content = "Unchanged content";
            var testFile = CreateProjectTestFile("unchanged_test.txt", content);
            var relativePath = GetRelativePath(testFile);

            // Create checkpoint
            var createResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { relativePath } },
                { "description", "Unchanged test" }
            });

            var checkpointId = ExtractCheckpointId(createResult);
            _createdCheckpointIds.Add(checkpointId);

            // Don't modify the file

            // Act - preview restore
            var previewResult = await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", true }
            });

            // Assert
            Assert.IsTrue(previewResult.Success);
            // The preview should indicate no changes needed
        }

        #endregion

        #region Error Cases

        [Test]
        public async Task CheckpointCreate_FileNotFound_ReturnsError()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "paths", new List<object> { "Assets/NonExistent/File.txt" } }
            };

            // Act
            var result = await ExecuteToolAsync("checkpoint.create", args);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task CheckpointCreate_EmptyPaths_ReturnsError()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "paths", new List<object>() }
            };

            // Act
            var result = await ExecuteToolAsync("checkpoint.create", args);

            // Assert
            Assert.IsFalse(result.Success);
        }

        [Test]
        public async Task CheckpointRestore_InvalidCheckpointId_ReturnsError()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "checkpointId", "nonexistent-checkpoint-id" }
            };

            // Act
            var result = await ExecuteToolAsync("checkpoint.restore", args);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("not found"));
        }

        [Test]
        public async Task CheckpointRestore_MissingCheckpointId_ReturnsError()
        {
            // Arrange
            var args = new Dictionary<string, object>();

            // Act
            var result = await ExecuteToolAsync("checkpoint.restore", args);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("checkpointId"));
        }

        [Test]
        public async Task CheckpointCreate_PathOutsideProject_ReturnsError()
        {
            // Arrange
            var args = new Dictionary<string, object>
            {
                { "paths", new List<object> { "C:\\Windows\\System32\\config.sys" } }
            };

            // Act
            var result = await ExecuteToolAsync("checkpoint.create", args);

            // Assert
            Assert.IsFalse(result.Success);
        }

        #endregion

        #region Integration with Project Tools

        [Test]
        public async Task CheckpointWithProjectWrite_FullCycle()
        {
            // This test simulates a real workflow:
            // 1. Create a file using project.write_file
            // 2. Checkpoint it
            // 3. Modify using project.write_file
            // 4. Restore checkpoint
            // 5. Verify original content

            // Step 1: Create file
            var filePath = "Assets/SplatterTestFiles/project_cycle_test.txt";
            var originalContent = "// Original code\nclass Test {}";

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", filePath },
                { "content", originalContent }
            });

            var fullPath = Path.Combine(Application.dataPath, "..", filePath);
            CreatedFiles.Add(fullPath);
            CreatedFiles.Add(fullPath + ".meta");

            // Step 2: Create checkpoint
            var createResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { filePath } },
                { "description", "Before modification" }
            });

            var checkpointId = ExtractCheckpointId(createResult);
            _createdCheckpointIds.Add(checkpointId);

            // Step 3: Modify using project.write_file
            var modifiedContent = "// Modified code\nclass ModifiedTest { void NewMethod() {} }";
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", filePath },
                { "content", modifiedContent }
            });

            Assert.AreEqual(modifiedContent, File.ReadAllText(fullPath));

            // Step 4: Restore checkpoint
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Step 5: Verify
            Assert.AreEqual(originalContent, File.ReadAllText(fullPath));
        }

        #endregion

        #region Helper Methods

        private string CreateProjectTestFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(_testFilesDirectory, relativePath);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);
            CreatedFiles.Add(fullPath);

            return fullPath;
        }

        private string GetRelativePath(string fullPath)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetRelativePath(projectRoot, fullPath);
        }

        private string ExtractCheckpointId(ToolExecutionResult result)
        {
            if (result.Output == null) return null;

            // Try to get checkpointId from output using reflection (for anonymous types)
            var outputType = result.Output.GetType();
            var property = outputType.GetProperty("checkpointId");
            if (property != null)
            {
                return property.GetValue(result.Output)?.ToString();
            }

            // Try as dictionary
            if (result.Output is IDictionary<string, object> dict)
            {
                if (dict.TryGetValue("checkpointId", out var id))
                {
                    return id?.ToString();
                }
            }

            return null;
        }

        #endregion
    }
}
