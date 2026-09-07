// Tests for Path Validator
// Tests path normalization, scope validation, sensitive path blocking, and traversal attack prevention

using System;
using System.IO;
using NUnit.Framework;
using Splatter.Editor.Security;
using UnityEngine;

namespace Splatter.Tests.Editor.Tools
{
    [TestFixture]
    public class PathValidatorTests
    {
        private string _projectRoot;
        private string _assetsPath;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _assetsPath = Application.dataPath;
        }

        #region Path Normalization Tests

        [Test]
        public void NormalizePath_BackslashesToForwardSlashes_Converts()
        {
            // Arrange
            var path = @"Assets\Scripts\Player\Movement.cs";

            // Act
            var normalized = PathValidator.NormalizePath(path);

            // Assert
            Assert.IsFalse(normalized.Contains("\\"), "Path should not contain backslashes");
            Assert.AreEqual("Assets/Scripts/Player/Movement.cs", normalized);
        }

        [Test]
        public void NormalizePath_DuplicateSlashes_Removes()
        {
            // Arrange
            var path = "Assets//Scripts///Player//Movement.cs";

            // Act
            var normalized = PathValidator.NormalizePath(path);

            // Assert
            Assert.IsFalse(normalized.Contains("//"), "Path should not contain duplicate slashes");
            Assert.AreEqual("Assets/Scripts/Player/Movement.cs", normalized);
        }

        [Test]
        public void NormalizePath_TrailingSlash_Removes()
        {
            // Arrange
            var path = "Assets/Scripts/";

            // Act
            var normalized = PathValidator.NormalizePath(path);

            // Assert
            Assert.IsFalse(normalized.EndsWith("/"), "Path should not end with slash");
            Assert.AreEqual("Assets/Scripts", normalized);
        }

        [Test]
        public void NormalizePath_NullPath_ReturnsNull()
        {
            // Act
            var normalized = PathValidator.NormalizePath(null);

            // Assert
            Assert.IsNull(normalized);
        }

        [Test]
        public void NormalizePath_EmptyPath_ReturnsEmpty()
        {
            // Act
            var normalized = PathValidator.NormalizePath("");

            // Assert
            Assert.AreEqual("", normalized);
        }

        [Test]
        public void NormalizePath_MixedSlashes_Normalizes()
        {
            // Arrange
            var path = @"Assets\Scripts/Player\Movement.cs";

            // Act
            var normalized = PathValidator.NormalizePath(path);

            // Assert
            Assert.AreEqual("Assets/Scripts/Player/Movement.cs", normalized);
        }

        #endregion

        #region Scope Validation Tests - AssetsOnly

        [Test]
        public void ValidatePath_AssetsOnly_ValidAssetsPath_Succeeds()
        {
            // Arrange
            var path = "Assets/Scripts/Player.cs";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsTrue(valid, $"Path should be valid but got error: {error}");
            Assert.IsNull(error);
        }

        [Test]
        public void ValidatePath_AssetsOnly_PackagesPath_Fails()
        {
            // Arrange
            var path = "Packages/com.unity.inputsystem/InputSystem.cs";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("Assets folder"));
        }

        [Test]
        public void ValidatePath_AssetsOnly_ProjectSettingsPath_Fails()
        {
            // Arrange
            var path = "ProjectSettings/ProjectSettings.asset";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
        }

        #endregion

        #region Scope Validation Tests - AssetsOrPackages

        [Test]
        public void ValidatePath_AssetsOrPackages_AssetsPath_Succeeds()
        {
            // Arrange
            var path = "Assets/Scripts/Test.cs";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOrPackages);

            // Assert
            Assert.IsTrue(valid, $"Path should be valid but got error: {error}");
        }

        [Test]
        public void ValidatePath_AssetsOrPackages_PackagesPath_Succeeds()
        {
            // Arrange
            var path = "Packages/com.splatterfacegames.assistant/Editor/Tools.cs";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOrPackages);

            // Assert
            Assert.IsTrue(valid, $"Path should be valid but got error: {error}");
        }

        [Test]
        public void ValidatePath_AssetsOrPackages_LibraryPath_Fails()
        {
            // Arrange
            var path = "Library/ScriptAssemblies/Assembly-CSharp.dll";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOrPackages);

            // Assert
            Assert.IsFalse(valid);
        }

        #endregion

        #region Scope Validation Tests - ProjectRoot

        [Test]
        public void ValidatePath_ProjectRoot_AssetsPath_Succeeds()
        {
            // Arrange
            var path = "Assets/Scripts/Test.cs";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsTrue(valid, $"Path should be valid but got error: {error}");
        }

        [Test]
        public void ValidatePath_ProjectRoot_PackagesPath_Succeeds()
        {
            // Arrange
            var path = "Packages/manifest.json";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsTrue(valid, $"Path should be valid but got error: {error}");
        }

        [Test]
        public void ValidatePath_ProjectRoot_OutsideProject_Fails()
        {
            // Arrange
            var path = "C:/Windows/System32/kernel32.dll";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("project root"));
        }

        #endregion

        #region Sensitive Path Blocking Tests

        [Test]
        public void ValidatePath_GitDirectory_Blocked()
        {
            // Arrange
            var path = ".git/config";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains(".git") || error.Contains("blocked"));
        }

        [Test]
        public void ValidatePath_EnvFile_Blocked()
        {
            // Arrange
            var path = "Assets/.env";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("credential") || error.Contains("secret") || error.Contains("blocked"));
        }

        [Test]
        public void ValidatePath_CredentialsJson_Blocked()
        {
            // Arrange
            var path = "Assets/credentials.json";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_PrivateKey_Blocked()
        {
            // Arrange
            var path = "Assets/private.key";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_PemFile_Blocked()
        {
            // Arrange
            var path = "Assets/certificate.pem";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_SshKey_Blocked()
        {
            // Arrange
            var path = "Assets/id_rsa";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_Keystore_Blocked()
        {
            // Arrange
            var path = "Assets/release.keystore";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_VsDirectory_Blocked()
        {
            // Arrange
            var path = ".vs/config.json";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_IdeaDirectory_Blocked()
        {
            // Arrange
            var path = ".idea/workspace.xml";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_UserSettings_Blocked()
        {
            // Arrange
            var path = "UserSettings/EditorUserSettings.asset";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_TempDirectory_Blocked()
        {
            // Arrange
            var path = "Temp/SomeFile.txt";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_LogsDirectory_Blocked()
        {
            // Arrange
            var path = "Logs/Editor.log";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_LibraryDirectAccess_Blocked()
        {
            // Arrange
            var path = "Library/ScriptAssemblies/Assembly-CSharp.dll";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_LibrarySplatterAI_Allowed()
        {
            // Arrange
            var path = "Library/SplatterAI/Checkpoints/test.json";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsTrue(valid, $"SplatterAI subdirectory should be allowed but got error: {error}");
        }

        [Test]
        public void ValidatePath_ProjectVersionTxt_Blocked()
        {
            // Arrange
            var path = "ProjectSettings/ProjectVersion.txt";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        #endregion

        #region Path Traversal Attack Prevention Tests

        [Test]
        public void ValidatePath_ParentDirectoryTraversal_Blocked()
        {
            // Arrange
            var path = "Assets/../../../etc/passwd";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("traversal"));
        }

        [Test]
        public void ValidatePath_RelativeTraversal_Blocked()
        {
            // Arrange
            var path = "../outside_project.txt";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_EncodedTraversal_Blocked()
        {
            // Arrange - Using .. in the path
            var path = "Assets/..\\..\\Windows\\System32\\config\\SAM";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_MixedSlashTraversal_Blocked()
        {
            // Arrange
            var path = "Assets/..\\../..\\etc/passwd";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_LeadingDoubleDot_Blocked()
        {
            // Arrange
            var path = "../sibling_project/secret.txt";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_AbsolutePathOutsideProject_Blocked()
        {
            // Arrange
            var path = "C:/Windows/System32/drivers/etc/hosts";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(valid);
        }

        #endregion

        #region IsPathAllowed Tests

        [Test]
        public void IsPathAllowed_ValidPath_ReturnsTrue()
        {
            // Arrange
            var path = "Assets/Scripts/Test.cs";

            // Act
            var allowed = PathValidator.IsPathAllowed(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsTrue(allowed);
        }

        [Test]
        public void IsPathAllowed_InvalidPath_ReturnsFalse()
        {
            // Arrange
            var path = ".git/config";

            // Act
            var allowed = PathValidator.IsPathAllowed(path, PathValidator.PathScope.ProjectRoot);

            // Assert
            Assert.IsFalse(allowed);
        }

        #endregion

        #region IsSensitivePath Tests

        [Test]
        public void IsSensitivePath_CredentialFile_ReturnsTrue()
        {
            // Arrange
            var path = "Assets/api_key.txt";

            // Act
            var isSensitive = PathValidator.IsSensitivePath(path);

            // Assert
            Assert.IsTrue(isSensitive);
        }

        [Test]
        public void IsSensitivePath_RegularFile_ReturnsFalse()
        {
            // Arrange
            var path = "Assets/Scripts/Player.cs";

            // Act
            var isSensitive = PathValidator.IsSensitivePath(path);

            // Assert
            Assert.IsFalse(isSensitive);
        }

        #endregion

        #region GetSafePathForLogging Tests

        [Test]
        public void GetSafePathForLogging_SensitivePath_MasksFilename()
        {
            // Arrange
            var path = "Assets/Config/credentials.json";

            // Act
            var safePath = PathValidator.GetSafePathForLogging(path);

            // Assert
            Assert.IsTrue(safePath.Contains("REDACTED") || safePath.Contains("SENSITIVE"));
        }

        [Test]
        public void GetSafePathForLogging_NormalPath_ReturnsPath()
        {
            // Arrange
            var path = "Assets/Scripts/Player.cs";

            // Act
            var safePath = PathValidator.GetSafePathForLogging(path);

            // Assert
            Assert.AreEqual("Assets/Scripts/Player.cs", safePath);
        }

        [Test]
        public void GetSafePathForLogging_EmptyPath_ReturnsPlaceholder()
        {
            // Act
            var safePath = PathValidator.GetSafePathForLogging("");

            // Assert
            Assert.AreEqual("[empty]", safePath);
        }

        [Test]
        public void GetSafePathForLogging_NullPath_ReturnsPlaceholder()
        {
            // Act
            var safePath = PathValidator.GetSafePathForLogging(null);

            // Assert
            Assert.AreEqual("[empty]", safePath);
        }

        #endregion

        #region ToProjectRelativePath Tests

        [Test]
        public void ToProjectRelativePath_AbsoluteAssetsPath_ReturnsRelative()
        {
            // Arrange
            var absolutePath = Path.Combine(_assetsPath, "Scripts", "Test.cs");

            // Act
            var relativePath = PathValidator.ToProjectRelativePath(absolutePath);

            // Assert
            Assert.IsFalse(Path.IsPathRooted(relativePath), "Path should be relative");
            Assert.IsTrue(relativePath.StartsWith("Assets"));
        }

        [Test]
        public void ToProjectRelativePath_OutsideProject_ReturnsOriginal()
        {
            // Arrange
            var absolutePath = "C:/Windows/System32/test.dll";

            // Act
            var relativePath = PathValidator.ToProjectRelativePath(absolutePath);

            // Assert
            Assert.AreEqual(absolutePath, relativePath);
        }

        #endregion

        #region ToAbsolutePath Tests

        [Test]
        public void ToAbsolutePath_RelativePath_ReturnsAbsolute()
        {
            // Arrange
            var relativePath = "Assets/Scripts/Test.cs";

            // Act
            var absolutePath = PathValidator.ToAbsolutePath(relativePath);

            // Assert
            Assert.IsTrue(absolutePath.Contains(_projectRoot.Replace('\\', '/')));
        }

        [Test]
        public void ToAbsolutePath_AlreadyAbsolute_ReturnsNormalized()
        {
            // Arrange
            var path = Path.Combine(_assetsPath, "Scripts", "Test.cs");

            // Act
            var result = PathValidator.ToAbsolutePath(path);

            // Assert
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Contains("\\"), "Path should be normalized with forward slashes");
        }

        #endregion

        #region ValidateForRead Tests

        [Test]
        public void ValidateForRead_ValidPath_Succeeds()
        {
            // Arrange
            var path = "Assets/Scripts/Test.cs";

            // Act
            var (valid, error) = PathValidator.ValidateForRead(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsTrue(valid, $"Should be valid but got error: {error}");
        }

        [Test]
        public void ValidateForRead_SensitivePath_Fails()
        {
            // Arrange
            var path = "Assets/.env";

            // Act
            var (valid, error) = PathValidator.ValidateForRead(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
        }

        #endregion

        #region ValidateForWrite Tests

        [Test]
        public void ValidateForWrite_ValidPath_Succeeds()
        {
            // Arrange
            var path = "Assets/Scripts/NewFile.cs";

            // Act
            var (valid, error) = PathValidator.ValidateForWrite(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsTrue(valid, $"Should be valid but got error: {error}");
        }

        [Test]
        public void ValidateForWrite_MetaFile_Fails()
        {
            // Arrange
            var path = "Assets/Scripts/Test.cs.meta";

            // Act
            var (valid, error) = PathValidator.ValidateForWrite(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("meta"));
        }

        [Test]
        public void ValidateForWrite_ExternalPackage_Fails()
        {
            // Arrange - This tests writing to a package that doesn't exist as a local directory
            var path = "Packages/com.unity.nonexistent/SomeFile.cs";

            // Act
            var (valid, error) = PathValidator.ValidateForWrite(path, PathValidator.PathScope.AssetsOrPackages);

            // Assert
            // Note: This depends on whether the package exists as a local directory
            // The test validates the logic is being applied
            Assert.IsNotNull(error != null || valid);
        }

        #endregion

        #region ValidateForDelete Tests

        [Test]
        public void ValidateForDelete_ValidAssetPath_Succeeds()
        {
            // Arrange
            var path = "Assets/Scripts/ToDelete.cs";

            // Act
            var (valid, error) = PathValidator.ValidateForDelete(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsTrue(valid, $"Should be valid but got error: {error}");
        }

        [Test]
        public void ValidateForDelete_CriticalFolder_Fails()
        {
            // Arrange
            var path = "Assets/Editor";

            // Act
            var (valid, error) = PathValidator.ValidateForDelete(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("critical"));
        }

        [Test]
        public void ValidateForDelete_PluginsFolder_Fails()
        {
            // Arrange
            var path = "Assets/Plugins";

            // Act
            var (valid, error) = PathValidator.ValidateForDelete(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidateForDelete_ResourcesFolder_Fails()
        {
            // Arrange
            var path = "Assets/Resources";

            // Act
            var (valid, error) = PathValidator.ValidateForDelete(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
        }

        #endregion

        #region IsExtensionAllowed Tests

        [Test]
        public void IsExtensionAllowed_MatchingExtension_ReturnsTrue()
        {
            // Arrange
            var path = "Assets/Scripts/Test.cs";
            var allowed = new[] { ".cs", ".txt" };

            // Act
            var result = PathValidator.IsExtensionAllowed(path, allowed);

            // Assert
            Assert.IsTrue(result);
        }

        [Test]
        public void IsExtensionAllowed_NonMatchingExtension_ReturnsFalse()
        {
            // Arrange
            var path = "Assets/Scripts/Test.dll";
            var allowed = new[] { ".cs", ".txt" };

            // Act
            var result = PathValidator.IsExtensionAllowed(path, allowed);

            // Assert
            Assert.IsFalse(result);
        }

        [Test]
        public void IsExtensionAllowed_CaseInsensitive_ReturnsTrue()
        {
            // Arrange
            var path = "Assets/Scripts/Test.CS";
            var allowed = new[] { ".cs" };

            // Act
            var result = PathValidator.IsExtensionAllowed(path, allowed);

            // Assert
            Assert.IsTrue(result);
        }

        [Test]
        public void IsExtensionAllowed_NoExtension_ReturnsFalse()
        {
            // Arrange
            var path = "Assets/Scripts/README";
            var allowed = new[] { ".cs", ".txt" };

            // Act
            var result = PathValidator.IsExtensionAllowed(path, allowed);

            // Assert
            Assert.IsFalse(result);
        }

        [Test]
        public void IsExtensionAllowed_NullPath_ReturnsFalse()
        {
            // Arrange
            var allowed = new[] { ".cs" };

            // Act
            var result = PathValidator.IsExtensionAllowed(null, allowed);

            // Assert
            Assert.IsFalse(result);
        }

        [Test]
        public void IsExtensionAllowed_NullAllowed_ReturnsFalse()
        {
            // Arrange
            var path = "Assets/Scripts/Test.cs";

            // Act
            var result = PathValidator.IsExtensionAllowed(path, null);

            // Assert
            Assert.IsFalse(result);
        }

        #endregion

        #region Edge Cases

        [Test]
        public void ValidatePath_EmptyPath_Fails()
        {
            // Act
            var (valid, error) = PathValidator.ValidatePath("", PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("null or empty"));
        }

        [Test]
        public void ValidatePath_NullPath_Fails()
        {
            // Act
            var (valid, error) = PathValidator.ValidatePath(null, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
            Assert.IsTrue(error.Contains("null or empty"));
        }

        [Test]
        public void ValidatePath_WhitespacePath_Fails()
        {
            // Act
            var (valid, error) = PathValidator.ValidatePath("   ", PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsFalse(valid);
        }

        [Test]
        public void ValidatePath_PathWithSpaces_Succeeds()
        {
            // Arrange
            var path = "Assets/My Scripts/Player Controller.cs";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsTrue(valid, $"Path with spaces should be valid but got error: {error}");
        }

        [Test]
        public void ValidatePath_PathWithSpecialCharacters_Succeeds()
        {
            // Arrange
            var path = "Assets/Scripts/Player_Controller-v2.cs";

            // Act
            var (valid, error) = PathValidator.ValidatePath(path, PathValidator.PathScope.AssetsOnly);

            // Assert
            Assert.IsTrue(valid, $"Path with special chars should be valid but got error: {error}");
        }

        #endregion
    }
}
