// Path Security Validator - Ensures file operations stay within safe project bounds
// Blocks access to sensitive files, credentials, and system directories

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Splatter.Editor.Security
{
    /// <summary>
    /// Provides secure path validation for file system operations.
    /// Ensures paths stay within project bounds and blocks access to sensitive files.
    /// </summary>
    public static class PathValidator
    {
        /// <summary>
        /// Defines the scope of allowed paths for an operation.
        /// </summary>
        public enum PathScope
        {
            /// <summary>Only allow paths within the Assets folder.</summary>
            AssetsOnly,

            /// <summary>Allow paths within Assets or Packages folders.</summary>
            AssetsOrPackages,

            /// <summary>Allow paths anywhere within the project root (with sensitive path restrictions).</summary>
            ProjectRoot
        }

        #region Sensitive Path Patterns

        // Directories that are always blocked
        private static readonly string[] BlockedDirectories =
        {
            ".git",
            ".svn",
            ".hg",
            ".vs",
            ".idea",
            "UserSettings",
            "Logs",
            "Temp",
            "obj",
            "bin"
        };

        // Files/patterns that are always blocked (case-insensitive)
        private static readonly string[] BlockedFilePatterns =
        {
            // Credential files
            @"\.credentials$",
            @"\.secret$",
            @"\.secrets$",
            @"password",
            @"apikey",
            @"api_key",
            @"api-key",
            @"\.pem$",
            @"\.key$",
            @"\.pfx$",
            @"\.p12$",
            @"\.keystore$",
            @"\.jks$",

            // Environment files
            @"\.env$",
            @"\.env\.",

            // SSH/GPG
            @"id_rsa",
            @"id_dsa",
            @"id_ecdsa",
            @"id_ed25519",
            @"\.gpg$",
            @"\.pgp$",

            // Auth tokens
            @"token\.json$",
            @"tokens\.json$",
            @"auth\.json$",
            @"credentials\.json$",

            // Git credentials
            @"\.git-credentials$",
            @"\.netrc$"
        };

        // Specific files in ProjectSettings that are read-only or should not be modified
        private static readonly string[] ReadOnlyProjectSettingsFiles =
        {
            "ProjectVersion.txt",
            "ProjectSettings.asset" // Core project settings - rarely need AI modification
        };

        // Library subdirectories that are allowed (for caching/state)
        private static readonly string[] AllowedLibrarySubdirs =
        {
            "SplatterAI",
            "PackageCache" // Read-only access for package inspection
        };

        // Compiled regex patterns for performance
        private static readonly Regex[] _blockedFileRegexes;
        private static readonly Regex _parentDirRegex = new Regex(@"(^|[/\\])\.\.([/\\]|$)", RegexOptions.Compiled);
        private static readonly Regex _driveLetterRegex = new Regex(@"^[a-zA-Z]:", RegexOptions.Compiled);

        #endregion

        #region Cached Paths

        private static string _projectRoot;
        private static string _assetsPath;
        private static string _packagesPath;
        private static string _libraryPath;
        private static string _projectSettingsPath;

        private static string ProjectRoot => _projectRoot ??= GetNormalizedPath(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
        private static string AssetsPath => _assetsPath ??= GetNormalizedPath(Application.dataPath);
        private static string PackagesPath => _packagesPath ??= GetNormalizedPath(Path.Combine(ProjectRoot, "Packages"));
        private static string LibraryPath => _libraryPath ??= GetNormalizedPath(Path.Combine(ProjectRoot, "Library"));
        private static string ProjectSettingsPath => _projectSettingsPath ??= GetNormalizedPath(Path.Combine(ProjectRoot, "ProjectSettings"));

        #endregion

        static PathValidator()
        {
            // Pre-compile regex patterns for sensitive file detection
            _blockedFileRegexes = BlockedFilePatterns
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
                .ToArray();
        }

        #region Public API

        /// <summary>
        /// Validates a path and returns detailed result with error message if invalid.
        /// This is the recommended method for comprehensive validation.
        /// </summary>
        /// <param name="path">The path to validate (can be relative or absolute).</param>
        /// <param name="scope">The allowed scope for this operation.</param>
        /// <returns>Tuple with validation result and error message (null if valid).</returns>
        public static (bool valid, string error) ValidatePath(string path, PathScope scope)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return (false, "Path cannot be null or empty");
            }

            // Normalize the path first
            string normalizedPath;
            try
            {
                normalizedPath = NormalizePath(path);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to normalize path: {ex.Message}");
            }

            // Check for path traversal attempts
            if (ContainsPathTraversal(path))
            {
                return (false, "Path traversal (../) is not allowed");
            }

            // Get the full resolved path
            string fullPath;
            try
            {
                fullPath = ResolveFullPath(normalizedPath);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to resolve path: {ex.Message}");
            }

            // Handle symlinks - resolve and re-validate
            string resolvedPath = fullPath;
            try
            {
                resolvedPath = ResolveSymlinks(fullPath);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to resolve symlinks: {ex.Message}");
            }

            // Check if path is within allowed scope
            var scopeResult = ValidateScope(resolvedPath, scope);
            if (!scopeResult.valid)
            {
                return scopeResult;
            }

            // Check for sensitive paths
            var sensitiveResult = ValidateSensitivePath(resolvedPath);
            if (!sensitiveResult.valid)
            {
                return sensitiveResult;
            }

            return (true, null);
        }

        /// <summary>
        /// Quick check if a path is allowed. Use ValidatePath for detailed error messages.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <param name="scope">The allowed scope for this operation.</param>
        /// <returns>True if the path is allowed, false otherwise.</returns>
        public static bool IsPathAllowed(string path, PathScope scope)
        {
            return ValidatePath(path, scope).valid;
        }

        /// <summary>
        /// Normalizes a path to use forward slashes and removes redundant separators.
        /// Does not resolve the path or check for existence.
        /// </summary>
        /// <param name="path">The path to normalize.</param>
        /// <returns>Normalized path with forward slashes.</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            // Replace backslashes with forward slashes
            var normalized = path.Replace('\\', '/');

            // Remove duplicate slashes (except for UNC paths)
            while (normalized.Contains("//"))
            {
                // Preserve leading // for UNC paths on Windows
                if (normalized.StartsWith("//"))
                {
                    var rest = normalized.Substring(2).Replace("//", "/");
                    normalized = "//" + rest;
                }
                else
                {
                    normalized = normalized.Replace("//", "/");
                }
            }

            // Remove trailing slash (unless it's the root)
            if (normalized.Length > 1 && normalized.EndsWith("/"))
            {
                // But keep trailing slash for drive roots like "C:/"
                if (!_driveLetterRegex.IsMatch(normalized.TrimEnd('/')))
                {
                    normalized = normalized.TrimEnd('/');
                }
            }

            return normalized;
        }

        /// <summary>
        /// Checks if a path points to a sensitive file or directory.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>True if the path is sensitive and should be blocked.</returns>
        public static bool IsSensitivePath(string path)
        {
            return !ValidateSensitivePath(ResolveFullPath(NormalizePath(path))).valid;
        }

        /// <summary>
        /// Gets a safe version of the path for logging (masks sensitive portions).
        /// </summary>
        /// <param name="path">The path to sanitize for logging.</param>
        /// <returns>Sanitized path safe for logging.</returns>
        public static string GetSafePathForLogging(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "[empty]";
            }

            var normalized = NormalizePath(path);

            // Mask credential-like filenames
            foreach (var pattern in BlockedFilePatterns)
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                if (regex.IsMatch(normalized))
                {
                    var dir = Path.GetDirectoryName(normalized) ?? "";
                    return $"{dir}/[SENSITIVE_FILE_REDACTED]";
                }
            }

            return normalized;
        }

        /// <summary>
        /// Converts an absolute path to a project-relative path.
        /// </summary>
        /// <param name="absolutePath">The absolute path to convert.</param>
        /// <returns>Project-relative path, or the original path if not within project.</returns>
        public static string ToProjectRelativePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return absolutePath;
            }

            var normalized = NormalizePath(Path.GetFullPath(absolutePath));
            var projectRoot = ProjectRoot;

            if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring(projectRoot.Length);
                return relative.TrimStart('/');
            }

            return absolutePath;
        }

        /// <summary>
        /// Converts a project-relative path to an absolute path.
        /// </summary>
        /// <param name="relativePath">The relative path to convert.</param>
        /// <returns>Absolute path.</returns>
        public static string ToAbsolutePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return relativePath;
            }

            if (Path.IsPathRooted(relativePath))
            {
                return NormalizePath(relativePath);
            }

            return NormalizePath(Path.Combine(ProjectRoot, relativePath));
        }

        #endregion

        #region Internal Validation Methods

        private static (bool valid, string error) ValidateScope(string fullPath, PathScope scope)
        {
            var normalized = GetNormalizedPath(fullPath);

            switch (scope)
            {
                case PathScope.AssetsOnly:
                    if (!normalized.StartsWith(AssetsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"Path must be within Assets folder. Got: {ToProjectRelativePath(fullPath)}");
                    }
                    break;

                case PathScope.AssetsOrPackages:
                    if (!normalized.StartsWith(AssetsPath, StringComparison.OrdinalIgnoreCase) &&
                        !normalized.StartsWith(PackagesPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"Path must be within Assets or Packages folder. Got: {ToProjectRelativePath(fullPath)}");
                    }
                    break;

                case PathScope.ProjectRoot:
                    if (!normalized.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"Path must be within project root. Got: {fullPath}");
                    }
                    break;
            }

            return (true, null);
        }

        private static (bool valid, string error) ValidateSensitivePath(string fullPath)
        {
            var normalized = GetNormalizedPath(fullPath);
            var pathParts = normalized.Split('/');

            // Check for blocked directories in path
            foreach (var part in pathParts)
            {
                foreach (var blockedDir in BlockedDirectories)
                {
                    if (string.Equals(part, blockedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"Access to '{blockedDir}' directory is blocked");
                    }
                }
            }

            // Check for Library access (only allow specific subdirectories)
            if (normalized.StartsWith(LibraryPath, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = normalized.Substring(LibraryPath.Length).TrimStart('/');
                var firstSegment = relativePath.Split('/').FirstOrDefault();

                if (string.IsNullOrEmpty(firstSegment))
                {
                    return (false, "Direct access to Library folder is blocked");
                }

                var isAllowedSubdir = AllowedLibrarySubdirs.Any(
                    allowed => string.Equals(firstSegment, allowed, StringComparison.OrdinalIgnoreCase));

                if (!isAllowedSubdir)
                {
                    return (false, $"Access to Library/{firstSegment} is blocked. Allowed: {string.Join(", ", AllowedLibrarySubdirs)}");
                }
            }

            // Check for read-only ProjectSettings files
            if (normalized.StartsWith(ProjectSettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(normalized);
                foreach (var readOnlyFile in ReadOnlyProjectSettingsFiles)
                {
                    if (string.Equals(fileName, readOnlyFile, StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"ProjectSettings/{readOnlyFile} is read-only");
                    }
                }
            }

            // Check for sensitive file patterns
            var fileNameLower = Path.GetFileName(normalized).ToLowerInvariant();
            foreach (var regex in _blockedFileRegexes)
            {
                if (regex.IsMatch(fileNameLower) || regex.IsMatch(normalized))
                {
                    return (false, "Access to credential/secret files is blocked");
                }
            }

            return (true, null);
        }

        private static bool ContainsPathTraversal(string path)
        {
            return _parentDirRegex.IsMatch(path);
        }

        private static string ResolveFullPath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                return GetNormalizedPath(Path.GetFullPath(path));
            }

            // Relative path - resolve from project root
            return GetNormalizedPath(Path.GetFullPath(Path.Combine(ProjectRoot, path)));
        }

        private static string ResolveSymlinks(string path)
        {
            // If the path doesn't exist yet (for write operations), just return normalized
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                // Check parent directories for symlinks
                var current = path;
                while (!string.IsNullOrEmpty(current))
                {
                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent) || parent == current)
                    {
                        break;
                    }

                    if (Directory.Exists(parent))
                    {
                        var dirInfo = new DirectoryInfo(parent);
                        if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            // Parent is a symlink - resolve it
                            var resolvedParent = GetSymlinkTarget(parent);
                            if (!string.IsNullOrEmpty(resolvedParent))
                            {
                                // Reconstruct path with resolved parent
                                var remainder = path.Substring(parent.Length);
                                return GetNormalizedPath(Path.Combine(resolvedParent, remainder.TrimStart('/', '\\')));
                            }
                        }
                    }

                    current = parent;
                }

                return GetNormalizedPath(path);
            }

            // Path exists - check if it's a symlink
            try
            {
                if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var target = GetSymlinkTarget(path);
                        if (!string.IsNullOrEmpty(target))
                        {
                            return GetNormalizedPath(target);
                        }
                    }
                }
                else if (Directory.Exists(path))
                {
                    var dirInfo = new DirectoryInfo(path);
                    if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var target = GetSymlinkTarget(path);
                        if (!string.IsNullOrEmpty(target))
                        {
                            return GetNormalizedPath(target);
                        }
                    }
                }
            }
            catch
            {
                // If we can't resolve symlinks, fail safe by returning normalized path
            }

            return GetNormalizedPath(path);
        }

        private static string GetSymlinkTarget(string path)
        {
            try
            {
#if NET6_0_OR_GREATER
                // .NET 6+ has built-in support
                var target = File.ResolveLinkTarget(path, true);
                return target?.FullName;
#else
                // netstandard2.1 (Unity) has no FileInfo.LinkTarget / File.ResolveLinkTarget.
                // We can detect a reparse point (symlink/junction) but cannot read its target
                // here; leave resolution to the normalized-path containment check. Returning
                // null below means "no resolvable symlink target".
                var attrs = File.Exists(path)
                    ? new FileInfo(path).Attributes
                    : Directory.Exists(path)
                        ? new DirectoryInfo(path).Attributes
                        : (FileAttributes?)null;
                _ = attrs.HasValue && (attrs.Value & FileAttributes.ReparsePoint) != 0;
#endif
            }
            catch
            {
                // Symlink resolution failed
            }

            return null;
        }

        private static string GetNormalizedPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            // Get full path and normalize separators
            var full = Path.GetFullPath(path).Replace('\\', '/');

            // Remove trailing slash except for root paths
            if (full.Length > 1 && full.EndsWith("/"))
            {
                if (!_driveLetterRegex.IsMatch(full.TrimEnd('/')))
                {
                    full = full.TrimEnd('/');
                }
            }

            return full;
        }

        #endregion

        #region Utility Methods for Tool Executors

        /// <summary>
        /// Validates a path for read operations. Less restrictive than write operations.
        /// </summary>
        /// <param name="path">Path to validate.</param>
        /// <param name="scope">Allowed scope.</param>
        /// <returns>Validation result.</returns>
        public static (bool valid, string error) ValidateForRead(string path, PathScope scope)
        {
            return ValidatePath(path, scope);
        }

        /// <summary>
        /// Validates a path for write operations. More restrictive, blocks additional paths.
        /// </summary>
        /// <param name="path">Path to validate.</param>
        /// <param name="scope">Allowed scope.</param>
        /// <returns>Validation result.</returns>
        public static (bool valid, string error) ValidateForWrite(string path, PathScope scope)
        {
            var result = ValidatePath(path, scope);
            if (!result.valid)
            {
                return result;
            }

            var normalized = GetNormalizedPath(ResolveFullPath(NormalizePath(path)));

            // Additional write restrictions

            // Block writing to Packages folder (packages should be read-only)
            if (normalized.StartsWith(PackagesPath, StringComparison.OrdinalIgnoreCase))
            {
                // Exception: allow writing to local packages (Packages/com.* if they exist as directories)
                var relativePath = normalized.Substring(PackagesPath.Length).TrimStart('/');
                var firstSegment = relativePath.Split('/').FirstOrDefault();

                if (!string.IsNullOrEmpty(firstSegment))
                {
                    var packagePath = Path.Combine(PackagesPath, firstSegment);
                    if (!Directory.Exists(packagePath))
                    {
                        return (false, "Cannot write to external packages (read-only)");
                    }
                }
            }

            // Block writing to .meta files directly (Unity manages these)
            if (normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Cannot write .meta files directly - Unity manages these");
            }

            return (true, null);
        }

        /// <summary>
        /// Validates a path for delete operations. Most restrictive.
        /// </summary>
        /// <param name="path">Path to validate.</param>
        /// <param name="scope">Allowed scope.</param>
        /// <returns>Validation result.</returns>
        public static (bool valid, string error) ValidateForDelete(string path, PathScope scope)
        {
            // Delete operations are even more restrictive - only allow AssetsOnly scope
            if (scope != PathScope.AssetsOnly)
            {
                scope = PathScope.AssetsOnly;
            }

            var result = ValidatePath(path, scope);
            if (!result.valid)
            {
                return result;
            }

            var normalized = GetNormalizedPath(ResolveFullPath(NormalizePath(path)));

            // Block deleting critical asset folders
            var criticalPaths = new[]
            {
                "Assets/Editor",
                "Assets/Plugins",
                "Assets/Resources",
                "Assets/StreamingAssets"
            };

            var projectRelative = ToProjectRelativePath(normalized);
            foreach (var critical in criticalPaths)
            {
                if (string.Equals(projectRelative, critical, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"Cannot delete critical folder: {critical}");
                }
            }

            return (true, null);
        }

        /// <summary>
        /// Checks if a file extension is allowed for a specific operation type.
        /// </summary>
        /// <param name="path">Path with the file extension to check.</param>
        /// <param name="allowedExtensions">List of allowed extensions (with dots, e.g., ".cs").</param>
        /// <returns>True if the extension is allowed.</returns>
        public static bool IsExtensionAllowed(string path, IEnumerable<string> allowedExtensions)
        {
            if (string.IsNullOrEmpty(path) || allowedExtensions == null)
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return allowedExtensions.Any(
                allowed => string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase));
        }

        #endregion
    }
}
