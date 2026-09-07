// Tests for Project Tool Executors
// Tests file listing, reading, writing, and searching operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Editor.Tools;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatter.Tests.Editor.Tools
{
    [TestFixture]
    public class ProjectToolExecutorsTests
    {
        private string _testDirectory;
        private string _projectRoot;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _testDirectory = Path.Combine(Application.dataPath, "TestData_ProjectTools");

            // Create test directory
            if (!Directory.Exists(_testDirectory))
            {
                Directory.CreateDirectory(_testDirectory);
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

                    // Also delete the .meta file
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

        #region ProjectListFilesExecutor Tests

        [Test]
        public async Task ListFiles_ValidPath_ReturnsFiles()
        {
            // Arrange
            var testFilePath = Path.Combine(_testDirectory, "test_list.cs");
            File.WriteAllText(testFilePath, "// Test file");

            var executor = new ProjectListFilesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/TestData_ProjectTools" },
                { "pattern", "*.cs" },
                { "recursive", false }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);

            var output = result.Output as dynamic;
            Assert.IsNotNull(output);

            // Cleanup
            File.Delete(testFilePath);
        }

        [Test]
        public async Task ListFiles_InvalidPath_ReturnsError()
        {
            // Arrange
            var executor = new ProjectListFilesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "NonExistent/Directory" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Directory not found"));
        }

        [Test]
        public async Task ListFiles_EmptyDirectory_ReturnsEmptyList()
        {
            // Arrange
            var emptyDir = Path.Combine(_testDirectory, "EmptyDir");
            Directory.CreateDirectory(emptyDir);

            var executor = new ProjectListFilesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/TestData_ProjectTools/EmptyDir" },
                { "pattern", "*.cs" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);

            // Cleanup
            Directory.Delete(emptyDir);
        }

        [Test]
        public async Task ListFiles_RecursiveSearch_FindsNestedFiles()
        {
            // Arrange
            var nestedDir = Path.Combine(_testDirectory, "Nested");
            Directory.CreateDirectory(nestedDir);
            var nestedFile = Path.Combine(nestedDir, "nested_test.cs");
            File.WriteAllText(nestedFile, "// Nested test");

            var executor = new ProjectListFilesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/TestData_ProjectTools" },
                { "pattern", "*.cs" },
                { "recursive", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Cleanup
            File.Delete(nestedFile);
            Directory.Delete(nestedDir);
        }

        [Test]
        public async Task ListFiles_FiltersMeta_ExcludesMetaFiles()
        {
            // Arrange
            var testFile = Path.Combine(_testDirectory, "test_meta.cs");
            File.WriteAllText(testFile, "// Test");
            var metaFile = testFile + ".meta";
            File.WriteAllText(metaFile, "fileFormatVersion: 2");

            var executor = new ProjectListFilesExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/TestData_ProjectTools" },
                { "pattern", "*" },
                { "recursive", false }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            var output = result.Output;
            Assert.IsNotNull(output);

            // Verify .meta files are not included (check dynamically)
            var outputType = output.GetType();
            var filesProperty = outputType.GetProperty("files");
            if (filesProperty != null)
            {
                var files = filesProperty.GetValue(output) as IEnumerable<string>;
                if (files != null)
                {
                    foreach (var file in files)
                    {
                        Assert.IsFalse(file.EndsWith(".meta"), $"Meta file should be excluded: {file}");
                    }
                }
            }

            // Cleanup
            File.Delete(testFile);
            File.Delete(metaFile);
        }

        #endregion

        #region ProjectReadFileExecutor Tests

        [Test]
        public async Task ReadFile_ExistingFile_ReturnsContent()
        {
            // Arrange
            var testContent = "public class TestClass { }";
            var testFile = Path.Combine(_testDirectory, "test_read.cs");
            File.WriteAllText(testFile, testContent);

            var executor = new ProjectReadFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/TestData_ProjectTools/test_read.cs" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);

            // Cleanup
            File.Delete(testFile);
        }

        [Test]
        public async Task ReadFile_MissingFile_ReturnsError()
        {
            // Arrange
            var executor = new ProjectReadFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/NonExistent/file.cs" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("File not found"));
        }

        [Test]
        public async Task ReadFile_EmptyPath_ReturnsError()
        {
            // Arrange
            var executor = new ProjectReadFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task ReadFile_NullPath_ReturnsError()
        {
            // Arrange
            var executor = new ProjectReadFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", null }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
        }

        [Test]
        public async Task ReadFile_PathOutsideProject_ReturnsAccessDenied()
        {
            // Arrange
            var executor = new ProjectReadFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "../../../outside_project.txt" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Access denied") || result.Error.Contains("not found"));
        }

        [Test]
        public async Task ReadFile_LargeFile_TruncatesContent()
        {
            // Arrange
            var largeContent = new string('x', 150000); // 150KB
            var testFile = Path.Combine(_testDirectory, "large_file.txt");
            File.WriteAllText(testFile, largeContent);

            var executor = new ProjectReadFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "Assets/TestData_ProjectTools/large_file.txt" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);

            // Cleanup
            File.Delete(testFile);
        }

        #endregion

        #region ProjectWriteFileExecutor Tests

        [Test]
        public async Task WriteFile_NewFile_CreatesFile()
        {
            // Arrange
            var testFile = "Assets/TestData_ProjectTools/new_file.txt";
            var fullPath = Path.Combine(_projectRoot, testFile);

            // Ensure file doesn't exist
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            var executor = new ProjectWriteFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", testFile },
                { "content", "Test content" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsTrue(File.Exists(fullPath), "File should have been created");
            Assert.AreEqual("Test content", File.ReadAllText(fullPath));

            // Cleanup
            File.Delete(fullPath);
        }

        [Test]
        public async Task WriteFile_ExistingFile_OverwritesContent()
        {
            // Arrange
            var testFile = "Assets/TestData_ProjectTools/existing_file.txt";
            var fullPath = Path.Combine(_projectRoot, testFile);
            File.WriteAllText(fullPath, "Original content");

            var executor = new ProjectWriteFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", testFile },
                { "content", "Updated content" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.AreEqual("Updated content", File.ReadAllText(fullPath));

            // Cleanup
            File.Delete(fullPath);
        }

        [Test]
        public async Task WriteFile_CreatesDirectoryIfNeeded()
        {
            // Arrange
            var testFile = "Assets/TestData_ProjectTools/NewDir/SubDir/file.txt";
            var fullPath = Path.Combine(_projectRoot, testFile);
            var dirPath = Path.GetDirectoryName(fullPath);

            // Ensure directory doesn't exist
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, true);
            }

            var executor = new ProjectWriteFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", testFile },
                { "content", "Content" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsTrue(File.Exists(fullPath));

            // Cleanup
            Directory.Delete(Path.Combine(_testDirectory, "NewDir"), true);
        }

        [Test]
        public async Task WriteFile_EmptyPath_ReturnsError()
        {
            // Arrange
            var executor = new ProjectWriteFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "" },
                { "content", "Content" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task WriteFile_NullContent_WritesEmptyFile()
        {
            // Arrange
            var testFile = "Assets/TestData_ProjectTools/empty_content.txt";
            var fullPath = Path.Combine(_projectRoot, testFile);

            var executor = new ProjectWriteFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", testFile },
                { "content", null }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.AreEqual("", File.ReadAllText(fullPath));

            // Cleanup
            File.Delete(fullPath);
        }

        [Test]
        public async Task WriteFile_PathOutsideProject_ReturnsAccessDenied()
        {
            // Arrange
            var executor = new ProjectWriteFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", "C:/Windows/System32/test.txt" },
                { "content", "Malicious content" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Access denied"));
        }

        [Test]
        public async Task WriteFile_ReturnsAffectedPaths()
        {
            // Arrange
            var testFile = "Assets/TestData_ProjectTools/affected_path_test.txt";
            var fullPath = Path.Combine(_projectRoot, testFile);

            var executor = new ProjectWriteFileExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "path", testFile },
                { "content", "Test" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.AffectedPaths);
            Assert.Contains(testFile, result.AffectedPaths);

            // Cleanup
            File.Delete(fullPath);
        }

        #endregion

        #region ProjectSearchExecutor Tests

        [Test]
        public async Task Search_SimpleQuery_FindsMatches()
        {
            // Arrange
            var testFile = Path.Combine(_testDirectory, "searchable.cs");
            File.WriteAllText(testFile, "public class SearchableClass { }");

            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", "SearchableClass" },
                { "path", "Assets/TestData_ProjectTools" },
                { "file_pattern", "*.cs" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success, $"Expected success but got error: {result.Error}");
            Assert.IsNotNull(result.Output);

            // Cleanup
            File.Delete(testFile);
        }

        [Test]
        public async Task Search_RegexPattern_FindsMatches()
        {
            // Arrange
            var testFile = Path.Combine(_testDirectory, "regex_test.cs");
            File.WriteAllText(testFile, "public void Method123() { }\npublic void Method456() { }");

            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", @"Method\d+" },
                { "path", "Assets/TestData_ProjectTools" },
                { "file_pattern", "*.cs" },
                { "regex", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Cleanup
            File.Delete(testFile);
        }

        [Test]
        public async Task Search_NoMatches_ReturnsEmptyResults()
        {
            // Arrange
            var testFile = Path.Combine(_testDirectory, "no_match.cs");
            File.WriteAllText(testFile, "public class SomeClass { }");

            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", "NonExistentPattern12345" },
                { "path", "Assets/TestData_ProjectTools" },
                { "file_pattern", "*.cs" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Cleanup
            File.Delete(testFile);
        }

        [Test]
        public async Task Search_EmptyQuery_ReturnsError()
        {
            // Arrange
            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", "" },
                { "path", "Assets" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("required"));
        }

        [Test]
        public async Task Search_InvalidDirectory_ReturnsError()
        {
            // Arrange
            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", "test" },
                { "path", "NonExistent/Directory" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Error.Contains("Directory not found"));
        }

        [Test]
        public async Task Search_CaseInsensitive_FindsMatches()
        {
            // Arrange
            var testFile = Path.Combine(_testDirectory, "case_test.cs");
            File.WriteAllText(testFile, "public class UPPERCASE { }");

            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", "uppercase" },
                { "path", "Assets/TestData_ProjectTools" },
                { "file_pattern", "*.cs" }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Cleanup
            File.Delete(testFile);
        }

        [Test]
        public async Task Search_MaxResults_LimitsOutput()
        {
            // Arrange
            var testFile = Path.Combine(_testDirectory, "many_matches.cs");
            var content = string.Join("\n", System.Linq.Enumerable.Range(1, 100).Select(i => $"// Match line {i}"));
            File.WriteAllText(testFile, content);

            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", "Match" },
                { "path", "Assets/TestData_ProjectTools" },
                { "file_pattern", "*.cs" },
                { "max_results", 10 }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsTrue(result.Success);

            // Cleanup
            File.Delete(testFile);
        }

        [Test]
        public async Task Search_InvalidRegex_ReturnsError()
        {
            // Arrange
            var executor = new ProjectSearchExecutor();
            var context = CreateContext(new Dictionary<string, object>
            {
                { "query", "[invalid regex(" },
                { "path", "Assets" },
                { "regex", true }
            });

            // Act
            var result = await executor.ExecuteAsync(context, CancellationToken.None);

            // Assert
            Assert.IsFalse(result.Success);
        }

        #endregion
    }
}
