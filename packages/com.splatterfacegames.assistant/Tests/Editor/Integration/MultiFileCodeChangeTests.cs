// Multi-File Code Change Integration Tests
// Tests modifying multiple files with checkpoint and rollback support

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatter.Tests.Editor.Integration
{
    /// <summary>
    /// Integration tests for multi-file code changes.
    /// Tests modifying multiple files in sequence with checkpoint and rollback capabilities.
    /// </summary>
    [TestFixture]
    public class MultiFileCodeChangeTests : IntegrationTestBase
    {
        private string _testFilesDirectory;
        private string _checkpointsDirectory;
        private List<string> _createdCheckpointIds;

        public override void SetUp()
        {
            base.SetUp();

            // Create a test files directory
            _testFilesDirectory = Path.Combine(Application.dataPath, "SplatterMultiFileTests");
            if (!Directory.Exists(_testFilesDirectory))
            {
                Directory.CreateDirectory(_testFilesDirectory);
            }

            _createdCheckpointIds = new List<string>();
            _checkpointsDirectory = Path.Combine(Application.dataPath, "..", "Library", "SplatterAI", "Checkpoints");
        }

        public override void TearDown()
        {
            // Cleanup checkpoints
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
                catch { }
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

        #region Sequential Multi-File Modification Tests

        [Test]
        public async Task ModifyMultipleFiles_InSequence_AllModified()
        {
            // Arrange - create multiple files
            var file1 = CreateProjectFile("File1.cs", "// File 1 original");
            var file2 = CreateProjectFile("File2.cs", "// File 2 original");
            var file3 = CreateProjectFile("File3.cs", "// File 3 original");

            // Act - modify each file
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file1) },
                { "content", "// File 1 modified" }
            });

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file2) },
                { "content", "// File 2 modified" }
            });

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file3) },
                { "content", "// File 3 modified" }
            });

            // Assert
            Assert.AreEqual("// File 1 modified", File.ReadAllText(file1));
            Assert.AreEqual("// File 2 modified", File.ReadAllText(file2));
            Assert.AreEqual("// File 3 modified", File.ReadAllText(file3));
        }

        [Test]
        public async Task ModifyRelatedFiles_MaintainsConsistency()
        {
            // Simulate modifying a class and its corresponding test file
            var classFile = CreateProjectFile("MyClass.cs",
@"namespace MyNamespace
{
    public class MyClass
    {
        public int Value { get; set; }
    }
}");

            var testFile = CreateProjectFile("MyClassTests.cs",
@"using NUnit.Framework;
namespace MyNamespace.Tests
{
    public class MyClassTests
    {
        [Test]
        public void Value_DefaultsToZero()
        {
            var obj = new MyClass();
            Assert.AreEqual(0, obj.Value);
        }
    }
}");

            // Modify the class to add a method
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(classFile) },
                { "content",
@"namespace MyNamespace
{
    public class MyClass
    {
        public int Value { get; set; }

        public int Double()
        {
            return Value * 2;
        }
    }
}" }
            });

            // Add a corresponding test
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(testFile) },
                { "content",
@"using NUnit.Framework;
namespace MyNamespace.Tests
{
    public class MyClassTests
    {
        [Test]
        public void Value_DefaultsToZero()
        {
            var obj = new MyClass();
            Assert.AreEqual(0, obj.Value);
        }

        [Test]
        public void Double_ReturnsDoubleValue()
        {
            var obj = new MyClass { Value = 5 };
            Assert.AreEqual(10, obj.Double());
        }
    }
}" }
            });

            // Assert
            var classContent = File.ReadAllText(classFile);
            var testContent = File.ReadAllText(testFile);

            Assert.IsTrue(classContent.Contains("public int Double()"));
            Assert.IsTrue(testContent.Contains("Double_ReturnsDoubleValue"));
        }

        #endregion

        #region Checkpoint Before Multi-File Changes

        [Test]
        public async Task CheckpointBeforeChanges_CanRollbackAll()
        {
            // Arrange - create files with original content
            var file1 = CreateProjectFile("Rollback1.cs", "Original 1");
            var file2 = CreateProjectFile("Rollback2.cs", "Original 2");
            var file3 = CreateProjectFile("Rollback3.cs", "Original 3");

            // Create checkpoint before changes
            var checkpointResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object>
                    {
                        GetRelativePath(file1),
                        GetRelativePath(file2),
                        GetRelativePath(file3)
                    }
                },
                { "description", "Before multi-file changes" }
            });

            var checkpointId = ExtractCheckpointId(checkpointResult);
            _createdCheckpointIds.Add(checkpointId);

            // Make changes to all files
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file1) },
                { "content", "Modified 1" }
            });

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file2) },
                { "content", "Modified 2" }
            });

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file3) },
                { "content", "Modified 3" }
            });

            // Verify all modified
            Assert.AreEqual("Modified 1", File.ReadAllText(file1));
            Assert.AreEqual("Modified 2", File.ReadAllText(file2));
            Assert.AreEqual("Modified 3", File.ReadAllText(file3));

            // Rollback all changes
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Assert all restored
            Assert.AreEqual("Original 1", File.ReadAllText(file1));
            Assert.AreEqual("Original 2", File.ReadAllText(file2));
            Assert.AreEqual("Original 3", File.ReadAllText(file3));
        }

        [Test]
        public async Task CheckpointWithPartialChanges_CanRollback()
        {
            // Arrange - create files
            var file1 = CreateProjectFile("Partial1.cs", "Original 1");
            var file2 = CreateProjectFile("Partial2.cs", "Original 2");

            // Create checkpoint
            var checkpointResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object>
                    {
                        GetRelativePath(file1),
                        GetRelativePath(file2)
                    }
                }
            });

            var checkpointId = ExtractCheckpointId(checkpointResult);
            _createdCheckpointIds.Add(checkpointId);

            // Modify only file1
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file1) },
                { "content", "Modified 1" }
            });

            // File2 stays original
            Assert.AreEqual("Modified 1", File.ReadAllText(file1));
            Assert.AreEqual("Original 2", File.ReadAllText(file2));

            // Restore checkpoint
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Both should be at original state
            Assert.AreEqual("Original 1", File.ReadAllText(file1));
            Assert.AreEqual("Original 2", File.ReadAllText(file2));
        }

        #endregion

        #region Rollback of Partial Changes

        [Test]
        public async Task RollbackPartialChanges_AfterError_RestoresOriginal()
        {
            // This simulates a scenario where:
            // 1. Checkpoint is created
            // 2. Some files are modified successfully
            // 3. An error occurs (simulated by invalid operation)
            // 4. User decides to rollback all changes

            // Arrange
            var file1 = CreateProjectFile("Error1.cs", "Original 1");
            var file2 = CreateProjectFile("Error2.cs", "Original 2");
            var file3 = CreateProjectFile("Error3.cs", "Original 3");

            // Create checkpoint
            var checkpointResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object>
                    {
                        GetRelativePath(file1),
                        GetRelativePath(file2),
                        GetRelativePath(file3)
                    }
                }
            });

            var checkpointId = ExtractCheckpointId(checkpointResult);
            _createdCheckpointIds.Add(checkpointId);

            // Modify first two files successfully
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file1) },
                { "content", "Modified 1" }
            });

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file2) },
                { "content", "Modified 2" }
            });

            // Simulate error - don't modify file3, just verify partial state
            Assert.AreEqual("Modified 1", File.ReadAllText(file1));
            Assert.AreEqual("Modified 2", File.ReadAllText(file2));
            Assert.AreEqual("Original 3", File.ReadAllText(file3)); // Not modified

            // Rollback due to "error"
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // All should be restored
            Assert.AreEqual("Original 1", File.ReadAllText(file1));
            Assert.AreEqual("Original 2", File.ReadAllText(file2));
            Assert.AreEqual("Original 3", File.ReadAllText(file3));
        }

        [Test]
        public async Task MultipleCheckpoints_RollbackToSpecificPoint()
        {
            // Create file
            var file = CreateProjectFile("Versions.cs", "Version 1");

            // Create checkpoint 1
            var cp1Result = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { GetRelativePath(file) } },
                { "description", "Version 1" }
            });
            var cp1Id = ExtractCheckpointId(cp1Result);
            _createdCheckpointIds.Add(cp1Id);

            // Modify to version 2
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file) },
                { "content", "Version 2" }
            });

            // Create checkpoint 2
            var cp2Result = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { GetRelativePath(file) } },
                { "description", "Version 2" }
            });
            var cp2Id = ExtractCheckpointId(cp2Result);
            _createdCheckpointIds.Add(cp2Id);

            // Modify to version 3
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file) },
                { "content", "Version 3" }
            });

            // Create checkpoint 3
            var cp3Result = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { GetRelativePath(file) } },
                { "description", "Version 3" }
            });
            var cp3Id = ExtractCheckpointId(cp3Result);
            _createdCheckpointIds.Add(cp3Id);

            // Modify to version 4 (current)
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file) },
                { "content", "Version 4" }
            });

            Assert.AreEqual("Version 4", File.ReadAllText(file));

            // Rollback directly to version 1
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", cp1Id },
                { "preview", false }
            });

            Assert.AreEqual("Version 1", File.ReadAllText(file));

            // Jump to version 3
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", cp3Id },
                { "preview", false }
            });

            Assert.AreEqual("Version 3", File.ReadAllText(file));
        }

        #endregion

        #region Complex Multi-File Scenarios

        [Test]
        public async Task RefactoringScenario_RenameAcrossFiles()
        {
            // Simulate a refactoring scenario where a class is renamed across multiple files

            var serviceFile = CreateProjectFile("Services/UserService.cs",
@"namespace Services
{
    public class UserService
    {
        public User GetUser(int id) => null;
    }
}");

            var controllerFile = CreateProjectFile("Controllers/UserController.cs",
@"using Services;
namespace Controllers
{
    public class UserController
    {
        private readonly UserService _service;

        public UserController(UserService service)
        {
            _service = service;
        }
    }
}");

            var testFile = CreateProjectFile("Tests/UserServiceTests.cs",
@"using Services;
namespace Tests
{
    public class UserServiceTests
    {
        private UserService _sut;
    }
}");

            // Create checkpoint before refactoring
            var checkpointResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object>
                    {
                        GetRelativePath(serviceFile),
                        GetRelativePath(controllerFile),
                        GetRelativePath(testFile)
                    }
                },
                { "description", "Before rename refactoring" }
            });

            var checkpointId = ExtractCheckpointId(checkpointResult);
            _createdCheckpointIds.Add(checkpointId);

            // Perform the rename in all files
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(serviceFile) },
                { "content",
@"namespace Services
{
    public class AccountService
    {
        public User GetUser(int id) => null;
    }
}" }
            });

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(controllerFile) },
                { "content",
@"using Services;
namespace Controllers
{
    public class UserController
    {
        private readonly AccountService _service;

        public UserController(AccountService service)
        {
            _service = service;
        }
    }
}" }
            });

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(testFile) },
                { "content",
@"using Services;
namespace Tests
{
    public class AccountServiceTests
    {
        private AccountService _sut;
    }
}" }
            });

            // Verify all files updated
            Assert.IsTrue(File.ReadAllText(serviceFile).Contains("AccountService"));
            Assert.IsTrue(File.ReadAllText(controllerFile).Contains("AccountService"));
            Assert.IsTrue(File.ReadAllText(testFile).Contains("AccountService"));

            // Rollback the refactoring
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Verify all files reverted
            Assert.IsTrue(File.ReadAllText(serviceFile).Contains("UserService"));
            Assert.IsTrue(File.ReadAllText(controllerFile).Contains("UserService"));
            Assert.IsTrue(File.ReadAllText(testFile).Contains("UserService"));
        }

        [Test]
        public async Task AddFeatureScenario_MultipleNewFiles()
        {
            // Simulate adding a new feature that spans multiple new files

            // Create initial structure
            var existingFile = CreateProjectFile("Features/Core.cs", "// Core functionality");

            // Checkpoint existing state
            var checkpointResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object> { GetRelativePath(existingFile) } }
            });

            var checkpointId = ExtractCheckpointId(checkpointResult);
            _createdCheckpointIds.Add(checkpointId);

            // Add new feature files
            var newFile1Path = Path.Combine(_testFilesDirectory, "Features", "NewFeature.cs");
            var newFile2Path = Path.Combine(_testFilesDirectory, "Features", "NewFeatureHelper.cs");

            // Ensure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(newFile1Path));

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(newFile1Path) },
                { "content", "// New feature implementation" }
            });
            CreatedFiles.Add(newFile1Path);

            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(newFile2Path) },
                { "content", "// New feature helper" }
            });
            CreatedFiles.Add(newFile2Path);

            // Modify existing file to use new feature
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(existingFile) },
                { "content", "// Core functionality\n// Using NewFeature" }
            });

            // Verify new files exist
            Assert.IsTrue(File.Exists(newFile1Path));
            Assert.IsTrue(File.Exists(newFile2Path));
            Assert.IsTrue(File.ReadAllText(existingFile).Contains("NewFeature"));

            // Restore checkpoint (should restore existing file, new files remain)
            await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", false }
            });

            // Existing file should be restored
            Assert.IsFalse(File.ReadAllText(existingFile).Contains("NewFeature"));
            Assert.AreEqual("// Core functionality", File.ReadAllText(existingFile));

            // Note: New files that weren't in the checkpoint remain (as expected)
        }

        #endregion

        #region Preview and Dry Run Tests

        [Test]
        public async Task RestorePreview_ShowsWhatWouldChange()
        {
            // Arrange
            var file1 = CreateProjectFile("Preview1.cs", "Original 1");
            var file2 = CreateProjectFile("Preview2.cs", "Original 2");

            // Create checkpoint
            var checkpointResult = await ExecuteToolSuccessAsync("checkpoint.create", new Dictionary<string, object>
            {
                { "paths", new List<object>
                    {
                        GetRelativePath(file1),
                        GetRelativePath(file2)
                    }
                }
            });

            var checkpointId = ExtractCheckpointId(checkpointResult);
            _createdCheckpointIds.Add(checkpointId);

            // Modify file1 only
            await ExecuteToolSuccessAsync("project.write_file", new Dictionary<string, object>
            {
                { "path", GetRelativePath(file1) },
                { "content", "Modified 1" }
            });

            // Get preview of restore
            var previewResult = await ExecuteToolSuccessAsync("checkpoint.restore", new Dictionary<string, object>
            {
                { "checkpointId", checkpointId },
                { "preview", true }
            });

            // Verify files unchanged (preview only)
            Assert.AreEqual("Modified 1", File.ReadAllText(file1), "File should not be modified in preview");
            Assert.AreEqual("Original 2", File.ReadAllText(file2));

            // Preview result should indicate what would change
            Assert.IsTrue(previewResult.Success);
        }

        #endregion

        #region Helper Methods

        private string CreateProjectFile(string relativePath, string content)
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

            var outputType = result.Output.GetType();
            var property = outputType.GetProperty("checkpointId");
            if (property != null)
            {
                return property.GetValue(result.Output)?.ToString();
            }

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
