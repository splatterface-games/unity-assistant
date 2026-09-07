// Standardized Error Handling System for Splatter AI Tools
// Provides consistent error codes, messages, and validation utilities

using System;
using System.Collections.Generic;
using System.Linq;
using Splatter.Editor.Security;

namespace Splatter.Editor.Tools
{
    #region Error Codes

    /// <summary>
    /// Standardized error codes for tool execution failures.
    /// Each category uses a distinct numeric range for easy identification.
    /// </summary>
    public enum ToolErrorCode
    {
        // General errors (0-99)
        /// <summary>Unknown or unexpected error occurred.</summary>
        Unknown = 0,
        /// <summary>Operation was cancelled by the user or system.</summary>
        Cancelled = 1,
        /// <summary>Operation timed out.</summary>
        Timeout = 2,
        /// <summary>Operation is not supported in the current context.</summary>
        NotSupported = 3,
        /// <summary>An internal error occurred within the tool executor.</summary>
        InternalError = 4,

        // Validation errors (100-199)
        /// <summary>An argument value is invalid.</summary>
        InvalidArgument = 100,
        /// <summary>A required argument was not provided.</summary>
        MissingRequiredArgument = 101,
        /// <summary>The path format is invalid.</summary>
        InvalidPath = 102,
        /// <summary>The path is outside allowed project bounds.</summary>
        PathOutOfBounds = 103,
        /// <summary>The argument type is incorrect.</summary>
        InvalidArgumentType = 104,
        /// <summary>The argument value is out of acceptable range.</summary>
        ArgumentOutOfRange = 105,
        /// <summary>Multiple validation errors occurred.</summary>
        MultipleValidationErrors = 106,

        // Asset errors (200-299)
        /// <summary>The specified asset was not found.</summary>
        AssetNotFound = 200,
        /// <summary>Failed to load the asset.</summary>
        AssetLoadFailed = 201,
        /// <summary>The asset type does not match the expected type.</summary>
        AssetTypeMismatch = 202,
        /// <summary>The provided GUID is invalid or does not exist.</summary>
        InvalidGuid = 203,
        /// <summary>Failed to create the asset.</summary>
        AssetCreateFailed = 204,
        /// <summary>Failed to delete the asset.</summary>
        AssetDeleteFailed = 205,
        /// <summary>Failed to modify the asset.</summary>
        AssetModifyFailed = 206,
        /// <summary>The asset is locked or read-only.</summary>
        AssetLocked = 207,
        /// <summary>Asset import failed.</summary>
        AssetImportFailed = 208,

        // Scene errors (300-399)
        /// <summary>The specified GameObject was not found.</summary>
        GameObjectNotFound = 300,
        /// <summary>The specified component was not found.</summary>
        ComponentNotFound = 301,
        /// <summary>The component type does not match the expected type.</summary>
        ComponentTypeMismatch = 302,
        /// <summary>The hierarchy path is invalid or does not exist.</summary>
        InvalidHierarchyPath = 303,
        /// <summary>The scene is not loaded or not available.</summary>
        SceneNotLoaded = 304,
        /// <summary>Failed to modify the scene.</summary>
        SceneModifyFailed = 305,
        /// <summary>The property was not found on the component.</summary>
        PropertyNotFound = 306,
        /// <summary>Failed to set the property value.</summary>
        PropertySetFailed = 307,
        /// <summary>The specified prefab was not found.</summary>
        PrefabNotFound = 308,

        // File system errors (400-499)
        /// <summary>The specified file was not found.</summary>
        FileNotFound = 400,
        /// <summary>Access to the file was denied.</summary>
        FileAccessDenied = 401,
        /// <summary>Failed to write to the file.</summary>
        FileWriteFailed = 402,
        /// <summary>The specified directory was not found.</summary>
        DirectoryNotFound = 403,
        /// <summary>Failed to read the file.</summary>
        FileReadFailed = 404,
        /// <summary>The file already exists.</summary>
        FileAlreadyExists = 405,
        /// <summary>Failed to create the directory.</summary>
        DirectoryCreateFailed = 406,
        /// <summary>The file is in use by another process.</summary>
        FileInUse = 407,
        /// <summary>The file extension is not allowed.</summary>
        InvalidFileExtension = 408,

        // Package errors (500-599)
        /// <summary>The specified package was not found.</summary>
        PackageNotFound = 500,
        /// <summary>Failed to install the package.</summary>
        PackageInstallFailed = 501,
        /// <summary>Failed to remove the package.</summary>
        PackageRemoveFailed = 502,
        /// <summary>The package version is incompatible.</summary>
        PackageVersionMismatch = 503,
        /// <summary>Package resolution failed.</summary>
        PackageResolutionFailed = 504,

        // Security errors (600-699)
        /// <summary>Access to the resource is denied due to security restrictions.</summary>
        AccessDenied = 600,
        /// <summary>Access to sensitive path is blocked.</summary>
        SensitivePathBlocked = 601,
        /// <summary>Symlink resolution would escape project bounds.</summary>
        SymlinkViolation = 602,
        /// <summary>Operation would violate security policy.</summary>
        SecurityPolicyViolation = 603,
        /// <summary>Credential or secret file access is blocked.</summary>
        CredentialAccessBlocked = 604,

        // Script/Code errors (700-799)
        /// <summary>The script has compilation errors.</summary>
        CompilationError = 700,
        /// <summary>The specified type was not found.</summary>
        TypeNotFound = 701,
        /// <summary>The specified method was not found.</summary>
        MethodNotFound = 702,
        /// <summary>Failed to execute the script.</summary>
        ScriptExecutionFailed = 703,

        // Console/Editor errors (800-899)
        /// <summary>The console log was not found.</summary>
        ConsoleLogNotFound = 800,
        /// <summary>Failed to clear the console.</summary>
        ConsoleClearFailed = 801,
        /// <summary>Editor operation failed.</summary>
        EditorOperationFailed = 802
    }

    #endregion

    #region Tool Error

    /// <summary>
    /// Represents a detailed tool execution error with code, message, and context.
    /// Provides factory methods for creating common error types with actionable messages.
    /// </summary>
    public class ToolError
    {
        /// <summary>The standardized error code.</summary>
        public ToolErrorCode Code { get; }

        /// <summary>A human-readable error message.</summary>
        public string Message { get; }

        /// <summary>Additional details about the error for debugging.</summary>
        public string Details { get; }

        /// <summary>Contextual data about the error (argument values, paths, etc.).</summary>
        public Dictionary<string, object> Context { get; }

        /// <summary>The exception that caused this error, if any.</summary>
        public Exception Exception { get; }

        /// <summary>
        /// Creates a new ToolError instance.
        /// </summary>
        public ToolError(
            ToolErrorCode code,
            string message,
            string details = null,
            Dictionary<string, object> context = null,
            Exception exception = null)
        {
            Code = code;
            Message = message ?? GetDefaultMessage(code);
            Details = details;
            Context = context ?? new Dictionary<string, object>();
            Exception = exception;
        }

        /// <summary>
        /// Gets a formatted string representation of the error.
        /// </summary>
        public override string ToString()
        {
            var result = $"[{Code}] {Message}";
            if (!string.IsNullOrEmpty(Details))
            {
                result += $" - {Details}";
            }
            return result;
        }

        /// <summary>
        /// Converts the error to a dictionary for JSON serialization.
        /// </summary>
        public Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>
            {
                ["code"] = Code.ToString(),
                ["codeValue"] = (int)Code,
                ["message"] = Message
            };

            if (!string.IsNullOrEmpty(Details))
            {
                dict["details"] = Details;
            }

            if (Context.Count > 0)
            {
                dict["context"] = Context;
            }

            return dict;
        }

        private static string GetDefaultMessage(ToolErrorCode code)
        {
            return code switch
            {
                ToolErrorCode.Unknown => "An unknown error occurred",
                ToolErrorCode.Cancelled => "Operation was cancelled",
                ToolErrorCode.Timeout => "Operation timed out",
                ToolErrorCode.NotSupported => "Operation is not supported",
                ToolErrorCode.InternalError => "An internal error occurred",
                ToolErrorCode.InvalidArgument => "Invalid argument provided",
                ToolErrorCode.MissingRequiredArgument => "A required argument is missing",
                ToolErrorCode.InvalidPath => "The path format is invalid",
                ToolErrorCode.PathOutOfBounds => "Path is outside allowed bounds",
                ToolErrorCode.AssetNotFound => "Asset not found",
                ToolErrorCode.GameObjectNotFound => "GameObject not found",
                ToolErrorCode.ComponentNotFound => "Component not found",
                ToolErrorCode.FileNotFound => "File not found",
                ToolErrorCode.AccessDenied => "Access denied",
                _ => $"Error: {code}"
            };
        }

        #region Factory Methods - General

        /// <summary>Creates an Unknown error.</summary>
        public static ToolError Unknown(string message = null, Exception ex = null)
        {
            return new ToolError(
                ToolErrorCode.Unknown,
                message ?? "An unexpected error occurred",
                ex?.Message,
                exception: ex);
        }

        /// <summary>Creates a Cancelled error.</summary>
        public static ToolError Cancelled(string operation = null)
        {
            var message = string.IsNullOrEmpty(operation)
                ? "Operation was cancelled"
                : $"Operation '{operation}' was cancelled";
            return new ToolError(ToolErrorCode.Cancelled, message);
        }

        /// <summary>Creates a Timeout error.</summary>
        public static ToolError Timeout(string operation, TimeSpan? duration = null)
        {
            var message = $"Operation '{operation}' timed out";
            var details = duration.HasValue ? $"Timeout after {duration.Value.TotalSeconds:F1} seconds" : null;
            return new ToolError(ToolErrorCode.Timeout, message, details);
        }

        /// <summary>Creates a NotSupported error.</summary>
        public static ToolError NotSupported(string feature, string reason = null)
        {
            var message = $"'{feature}' is not supported";
            return new ToolError(ToolErrorCode.NotSupported, message, reason);
        }

        #endregion

        #region Factory Methods - Validation

        /// <summary>Creates an InvalidArgument error with details about what was wrong.</summary>
        public static ToolError InvalidArgument(string argName, string reason, object actualValue = null)
        {
            var message = $"Invalid value for argument '{argName}': {reason}";
            var context = new Dictionary<string, object> { ["argument"] = argName };
            if (actualValue != null)
            {
                context["actualValue"] = actualValue.ToString();
            }
            return new ToolError(ToolErrorCode.InvalidArgument, message, context: context);
        }

        /// <summary>Creates a MissingRequiredArgument error.</summary>
        public static ToolError MissingRequiredArgument(string argName, string expectedType = null)
        {
            var message = $"Required argument '{argName}' was not provided";
            var details = expectedType != null ? $"Expected type: {expectedType}" : null;
            return new ToolError(
                ToolErrorCode.MissingRequiredArgument,
                message,
                details,
                new Dictionary<string, object> { ["argument"] = argName });
        }

        /// <summary>Creates an InvalidArgumentType error.</summary>
        public static ToolError InvalidArgumentType(string argName, string expectedType, string actualType)
        {
            var message = $"Argument '{argName}' has wrong type. Expected {expectedType}, got {actualType}";
            return new ToolError(
                ToolErrorCode.InvalidArgumentType,
                message,
                context: new Dictionary<string, object>
                {
                    ["argument"] = argName,
                    ["expectedType"] = expectedType,
                    ["actualType"] = actualType
                });
        }

        /// <summary>Creates an ArgumentOutOfRange error.</summary>
        public static ToolError ArgumentOutOfRange(string argName, object value, object min = null, object max = null)
        {
            var rangeStr = min != null && max != null ? $"[{min}, {max}]"
                : min != null ? $">= {min}"
                : max != null ? $"<= {max}"
                : "valid range";

            var message = $"Argument '{argName}' value {value} is out of range. Expected {rangeStr}";
            return new ToolError(
                ToolErrorCode.ArgumentOutOfRange,
                message,
                context: new Dictionary<string, object>
                {
                    ["argument"] = argName,
                    ["value"] = value,
                    ["min"] = min,
                    ["max"] = max
                });
        }

        /// <summary>Creates an InvalidPath error.</summary>
        public static ToolError InvalidPath(string path, string reason)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"Invalid path '{safePath}': {reason}";
            return new ToolError(
                ToolErrorCode.InvalidPath,
                message,
                context: new Dictionary<string, object> { ["path"] = safePath });
        }

        /// <summary>Creates a PathOutOfBounds error.</summary>
        public static ToolError PathOutOfBounds(string path, PathValidator.PathScope allowedScope)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var scopeDesc = allowedScope switch
            {
                PathValidator.PathScope.AssetsOnly => "Assets folder",
                PathValidator.PathScope.AssetsOrPackages => "Assets or Packages folders",
                PathValidator.PathScope.ProjectRoot => "project root",
                _ => "allowed scope"
            };
            var message = $"Path '{safePath}' is outside {scopeDesc}";
            var details = $"Paths must be within the {scopeDesc} for this operation";
            return new ToolError(
                ToolErrorCode.PathOutOfBounds,
                message,
                details,
                new Dictionary<string, object>
                {
                    ["path"] = safePath,
                    ["allowedScope"] = allowedScope.ToString()
                });
        }

        #endregion

        #region Factory Methods - Asset

        /// <summary>Creates an AssetNotFound error.</summary>
        public static ToolError AssetNotFound(string pathOrGuid, bool isGuid = false)
        {
            var identifier = isGuid ? $"GUID '{pathOrGuid}'" : $"path '{pathOrGuid}'";
            var message = $"Asset not found at {identifier}";
            var details = isGuid
                ? "The GUID may be invalid or the asset may have been deleted"
                : "Verify the asset path is correct and the asset exists";
            return new ToolError(
                ToolErrorCode.AssetNotFound,
                message,
                details,
                new Dictionary<string, object>
                {
                    [isGuid ? "guid" : "path"] = pathOrGuid
                });
        }

        /// <summary>Creates an AssetLoadFailed error.</summary>
        public static ToolError AssetLoadFailed(string path, string reason = null)
        {
            var message = $"Failed to load asset at '{path}'";
            var details = reason ?? "The asset may be corrupted or of an unsupported type";
            return new ToolError(
                ToolErrorCode.AssetLoadFailed,
                message,
                details,
                new Dictionary<string, object> { ["path"] = path });
        }

        /// <summary>Creates an AssetTypeMismatch error.</summary>
        public static ToolError AssetTypeMismatch(string path, string expectedType, string actualType)
        {
            var message = $"Asset type mismatch at '{path}'. Expected {expectedType}, got {actualType}";
            return new ToolError(
                ToolErrorCode.AssetTypeMismatch,
                message,
                context: new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["expectedType"] = expectedType,
                    ["actualType"] = actualType
                });
        }

        /// <summary>Creates an InvalidGuid error.</summary>
        public static ToolError InvalidGuid(string guid)
        {
            var message = $"Invalid or non-existent GUID: '{guid}'";
            var details = "GUIDs must be 32 hexadecimal characters. Use AssetDatabase.AssetPathToGUID to get a valid GUID.";
            return new ToolError(
                ToolErrorCode.InvalidGuid,
                message,
                details,
                new Dictionary<string, object> { ["guid"] = guid });
        }

        /// <summary>Creates an AssetCreateFailed error.</summary>
        public static ToolError AssetCreateFailed(string path, string assetType, string reason = null)
        {
            var message = $"Failed to create {assetType} at '{path}'";
            return new ToolError(
                ToolErrorCode.AssetCreateFailed,
                message,
                reason,
                new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["assetType"] = assetType
                });
        }

        /// <summary>Creates an AssetDeleteFailed error.</summary>
        public static ToolError AssetDeleteFailed(string path, string reason = null)
        {
            var message = $"Failed to delete asset at '{path}'";
            var details = reason ?? "The asset may be locked, read-only, or in use";
            return new ToolError(
                ToolErrorCode.AssetDeleteFailed,
                message,
                details,
                new Dictionary<string, object> { ["path"] = path });
        }

        #endregion

        #region Factory Methods - Scene

        /// <summary>Creates a GameObjectNotFound error.</summary>
        public static ToolError GameObjectNotFound(string identifier, bool isPath = true)
        {
            var message = isPath
                ? $"GameObject not found at path '{identifier}'"
                : $"GameObject not found with instance ID {identifier}";
            var details = isPath
                ? "Verify the hierarchy path is correct. Use scene.find_objects to locate GameObjects."
                : "The GameObject may have been destroyed or the instance ID is invalid.";
            return new ToolError(
                ToolErrorCode.GameObjectNotFound,
                message,
                details,
                new Dictionary<string, object> { [isPath ? "path" : "instanceId"] = identifier });
        }

        /// <summary>Creates a ComponentNotFound error.</summary>
        public static ToolError ComponentNotFound(string componentType, string gameObjectName)
        {
            var message = $"Component '{componentType}' not found on GameObject '{gameObjectName}'";
            var details = "Use scene.read_object to see available components on a GameObject.";
            return new ToolError(
                ToolErrorCode.ComponentNotFound,
                message,
                details,
                new Dictionary<string, object>
                {
                    ["componentType"] = componentType,
                    ["gameObject"] = gameObjectName
                });
        }

        /// <summary>Creates a ComponentTypeMismatch error.</summary>
        public static ToolError ComponentTypeMismatch(string componentType, string expectedBaseType)
        {
            var message = $"Type '{componentType}' is not a valid component type";
            var details = $"The type must derive from {expectedBaseType}";
            return new ToolError(
                ToolErrorCode.ComponentTypeMismatch,
                message,
                details,
                new Dictionary<string, object>
                {
                    ["componentType"] = componentType,
                    ["expectedBaseType"] = expectedBaseType
                });
        }

        /// <summary>Creates an InvalidHierarchyPath error.</summary>
        public static ToolError InvalidHierarchyPath(string path, string reason = null)
        {
            var message = $"Invalid hierarchy path: '{path}'";
            var details = reason ?? "Hierarchy paths should use '/' as separator (e.g., 'Parent/Child/GrandChild')";
            return new ToolError(
                ToolErrorCode.InvalidHierarchyPath,
                message,
                details,
                new Dictionary<string, object> { ["path"] = path });
        }

        /// <summary>Creates a SceneNotLoaded error.</summary>
        public static ToolError SceneNotLoaded(string scenePath)
        {
            var message = $"Scene not loaded: '{scenePath}'";
            var details = "Load the scene using EditorSceneManager.OpenScene before performing operations on it.";
            return new ToolError(
                ToolErrorCode.SceneNotLoaded,
                message,
                details,
                new Dictionary<string, object> { ["scenePath"] = scenePath });
        }

        /// <summary>Creates a PropertyNotFound error.</summary>
        public static ToolError PropertyNotFound(string propertyPath, string componentType)
        {
            var message = $"Property '{propertyPath}' not found on component '{componentType}'";
            var details = "Use scene.read_object to see available properties on a component.";
            return new ToolError(
                ToolErrorCode.PropertyNotFound,
                message,
                details,
                new Dictionary<string, object>
                {
                    ["propertyPath"] = propertyPath,
                    ["componentType"] = componentType
                });
        }

        /// <summary>Creates a PropertySetFailed error.</summary>
        public static ToolError PropertySetFailed(string propertyPath, object value, string reason = null)
        {
            var message = $"Failed to set property '{propertyPath}'";
            var details = reason ?? "The value may be incompatible with the property type";
            return new ToolError(
                ToolErrorCode.PropertySetFailed,
                message,
                details,
                new Dictionary<string, object>
                {
                    ["propertyPath"] = propertyPath,
                    ["value"] = value?.ToString()
                });
        }

        #endregion

        #region Factory Methods - File System

        /// <summary>Creates a FileNotFound error.</summary>
        public static ToolError FileNotFound(string path)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"File not found: '{safePath}'";
            var details = "Verify the file path is correct and the file exists.";
            return new ToolError(
                ToolErrorCode.FileNotFound,
                message,
                details,
                new Dictionary<string, object> { ["path"] = safePath });
        }

        /// <summary>Creates a FileAccessDenied error.</summary>
        public static ToolError FileAccessDenied(string path, string operation = "access")
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"Access denied when trying to {operation} '{safePath}'";
            var details = "The file may be read-only, locked, or you may lack permissions.";
            return new ToolError(
                ToolErrorCode.FileAccessDenied,
                message,
                details,
                new Dictionary<string, object>
                {
                    ["path"] = safePath,
                    ["operation"] = operation
                });
        }

        /// <summary>Creates a FileWriteFailed error.</summary>
        public static ToolError FileWriteFailed(string path, string reason = null)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"Failed to write to file: '{safePath}'";
            var details = reason ?? "The file may be read-only, locked, or the disk may be full.";
            return new ToolError(
                ToolErrorCode.FileWriteFailed,
                message,
                details,
                new Dictionary<string, object> { ["path"] = safePath });
        }

        /// <summary>Creates a DirectoryNotFound error.</summary>
        public static ToolError DirectoryNotFound(string path)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"Directory not found: '{safePath}'";
            var details = "Create the directory first or verify the path is correct.";
            return new ToolError(
                ToolErrorCode.DirectoryNotFound,
                message,
                details,
                new Dictionary<string, object> { ["path"] = safePath });
        }

        /// <summary>Creates a FileAlreadyExists error.</summary>
        public static ToolError FileAlreadyExists(string path)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"File already exists: '{safePath}'";
            var details = "Delete the existing file first or use a different path.";
            return new ToolError(
                ToolErrorCode.FileAlreadyExists,
                message,
                details,
                new Dictionary<string, object> { ["path"] = safePath });
        }

        #endregion

        #region Factory Methods - Package

        /// <summary>Creates a PackageNotFound error.</summary>
        public static ToolError PackageNotFound(string packageId)
        {
            var message = $"Package not found: '{packageId}'";
            var details = "Verify the package identifier is correct. Check available packages in Package Manager.";
            return new ToolError(
                ToolErrorCode.PackageNotFound,
                message,
                details,
                new Dictionary<string, object> { ["packageId"] = packageId });
        }

        /// <summary>Creates a PackageInstallFailed error.</summary>
        public static ToolError PackageInstallFailed(string packageId, string reason = null)
        {
            var message = $"Failed to install package: '{packageId}'";
            var details = reason ?? "Check the Unity console for detailed error messages.";
            return new ToolError(
                ToolErrorCode.PackageInstallFailed,
                message,
                details,
                new Dictionary<string, object> { ["packageId"] = packageId });
        }

        /// <summary>Creates a PackageRemoveFailed error.</summary>
        public static ToolError PackageRemoveFailed(string packageId, string reason = null)
        {
            var message = $"Failed to remove package: '{packageId}'";
            var details = reason ?? "The package may be a dependency of another package.";
            return new ToolError(
                ToolErrorCode.PackageRemoveFailed,
                message,
                details,
                new Dictionary<string, object> { ["packageId"] = packageId });
        }

        #endregion

        #region Factory Methods - Security

        /// <summary>Creates an AccessDenied error.</summary>
        public static ToolError AccessDenied(string resource, string reason = null)
        {
            var message = $"Access denied to resource: '{resource}'";
            var details = reason ?? "This operation is not permitted due to security restrictions.";
            return new ToolError(
                ToolErrorCode.AccessDenied,
                message,
                details,
                new Dictionary<string, object> { ["resource"] = resource });
        }

        /// <summary>Creates a SensitivePathBlocked error.</summary>
        public static ToolError SensitivePathBlocked(string path, string reason = null)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"Access to sensitive path blocked: '{safePath}'";
            var details = reason ?? "This path contains sensitive data and cannot be accessed.";
            return new ToolError(
                ToolErrorCode.SensitivePathBlocked,
                message,
                details,
                new Dictionary<string, object> { ["path"] = safePath });
        }

        /// <summary>Creates a SymlinkViolation error.</summary>
        public static ToolError SymlinkViolation(string path)
        {
            var safePath = PathValidator.GetSafePathForLogging(path);
            var message = $"Symlink at '{safePath}' points outside project bounds";
            var details = "Symbolic links that escape the project directory are not allowed for security.";
            return new ToolError(
                ToolErrorCode.SymlinkViolation,
                message,
                details,
                new Dictionary<string, object> { ["path"] = safePath });
        }

        /// <summary>Creates a CredentialAccessBlocked error.</summary>
        public static ToolError CredentialAccessBlocked(string path)
        {
            var message = "Access to credential or secret files is blocked";
            var details = "Files containing credentials, API keys, or secrets cannot be accessed for security.";
            return new ToolError(
                ToolErrorCode.CredentialAccessBlocked,
                message,
                details);
        }

        #endregion

        #region Factory Methods - Script/Code

        /// <summary>Creates a TypeNotFound error.</summary>
        public static ToolError TypeNotFound(string typeName)
        {
            var message = $"Type not found: '{typeName}'";
            var details = "Ensure the type name is fully qualified or the assembly is loaded.";
            return new ToolError(
                ToolErrorCode.TypeNotFound,
                message,
                details,
                new Dictionary<string, object> { ["typeName"] = typeName });
        }

        /// <summary>Creates a CompilationError error.</summary>
        public static ToolError CompilationError(string scriptPath, string[] errors)
        {
            var message = $"Compilation errors in script: '{scriptPath}'";
            var details = errors?.Length > 0
                ? string.Join("\n", errors.Take(5))
                : "Check the Unity console for compilation errors.";
            return new ToolError(
                ToolErrorCode.CompilationError,
                message,
                details,
                new Dictionary<string, object>
                {
                    ["scriptPath"] = scriptPath,
                    ["errorCount"] = errors?.Length ?? 0
                });
        }

        #endregion

        #region Factory Methods - From Exception

        /// <summary>Creates a ToolError from an exception.</summary>
        public static ToolError FromException(Exception ex, string operation = null)
        {
            var code = ex switch
            {
                OperationCanceledException => ToolErrorCode.Cancelled,
                TimeoutException => ToolErrorCode.Timeout,
                UnauthorizedAccessException => ToolErrorCode.FileAccessDenied,
                System.IO.FileNotFoundException => ToolErrorCode.FileNotFound,
                System.IO.DirectoryNotFoundException => ToolErrorCode.DirectoryNotFound,
                System.IO.IOException => ToolErrorCode.FileWriteFailed,
                ArgumentNullException => ToolErrorCode.MissingRequiredArgument,
                ArgumentOutOfRangeException => ToolErrorCode.ArgumentOutOfRange,
                ArgumentException => ToolErrorCode.InvalidArgument,
                NotSupportedException => ToolErrorCode.NotSupported,
                _ => ToolErrorCode.Unknown
            };

            var message = string.IsNullOrEmpty(operation)
                ? ex.Message
                : $"{operation} failed: {ex.Message}";

            return new ToolError(code, message, ex.StackTrace, exception: ex);
        }

        #endregion
    }

    #endregion

    #region Tool Execution Result Extensions

    /// <summary>
    /// Extension methods for ToolExecutionResult to support standardized error handling.
    /// </summary>
    public static class ToolExecutionResultExtensions
    {
        /// <summary>
        /// Creates a failed result from a ToolError.
        /// </summary>
        public static ToolExecutionResult Failed(ToolError error)
        {
            return new ToolExecutionResult
            {
                Success = false,
                Error = error.ToString(),
                Metadata = new Dictionary<string, object>
                {
                    ["error"] = error.ToDictionary()
                }
            };
        }

        /// <summary>
        /// Creates a failed result from an error code and message.
        /// </summary>
        public static ToolExecutionResult Failed(ToolErrorCode code, string message, string details = null)
        {
            var error = new ToolError(code, message, details);
            return Failed(error);
        }

        /// <summary>
        /// Creates a failed result from an exception.
        /// </summary>
        public static ToolExecutionResult FailedFromException(Exception ex, string operation = null)
        {
            var error = ToolError.FromException(ex, operation);
            return Failed(error);
        }

        /// <summary>
        /// Checks if the result has a specific error code.
        /// </summary>
        public static bool HasErrorCode(this ToolExecutionResult result, ToolErrorCode code)
        {
            if (result.Success || result.Metadata == null)
                return false;

            if (result.Metadata.TryGetValue("error", out var errorObj) &&
                errorObj is Dictionary<string, object> errorDict &&
                errorDict.TryGetValue("codeValue", out var codeValue))
            {
                return Convert.ToInt32(codeValue) == (int)code;
            }

            return false;
        }

        /// <summary>
        /// Gets the error code from a failed result, or null if successful.
        /// </summary>
        public static ToolErrorCode? GetErrorCode(this ToolExecutionResult result)
        {
            if (result.Success || result.Metadata == null)
                return null;

            if (result.Metadata.TryGetValue("error", out var errorObj) &&
                errorObj is Dictionary<string, object> errorDict &&
                errorDict.TryGetValue("codeValue", out var codeValue))
            {
                return (ToolErrorCode)Convert.ToInt32(codeValue);
            }

            return null;
        }
    }

    #endregion

    #region Argument Validator

    /// <summary>
    /// Utility class for validating tool arguments with consistent error reporting.
    /// </summary>
    public static class ArgumentValidator
    {
        #region Required Argument Validators

        /// <summary>
        /// Validates that a string argument is present and non-empty.
        /// </summary>
        public static (bool valid, ToolError error) RequireString(
            Dictionary<string, object> args,
            string key,
            int? minLength = null,
            int? maxLength = null)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
            {
                return (false, ToolError.MissingRequiredArgument(key, "string"));
            }

            var strValue = value.ToString();

            if (string.IsNullOrWhiteSpace(strValue))
            {
                return (false, ToolError.InvalidArgument(key, "cannot be empty or whitespace", strValue));
            }

            if (minLength.HasValue && strValue.Length < minLength.Value)
            {
                return (false, ToolError.InvalidArgument(key, $"must be at least {minLength.Value} characters", strValue));
            }

            if (maxLength.HasValue && strValue.Length > maxLength.Value)
            {
                return (false, ToolError.InvalidArgument(key, $"must be at most {maxLength.Value} characters", strValue));
            }

            return (true, null);
        }

        /// <summary>
        /// Validates that an integer argument is present and within range.
        /// </summary>
        public static (bool valid, ToolError error) RequireInt(
            Dictionary<string, object> args,
            string key,
            int? min = null,
            int? max = null)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
            {
                return (false, ToolError.MissingRequiredArgument(key, "integer"));
            }

            if (!TryConvertToInt(value, out var intValue))
            {
                return (false, ToolError.InvalidArgumentType(key, "integer", value.GetType().Name));
            }

            if (min.HasValue && intValue < min.Value)
            {
                return (false, ToolError.ArgumentOutOfRange(key, intValue, min, max));
            }

            if (max.HasValue && intValue > max.Value)
            {
                return (false, ToolError.ArgumentOutOfRange(key, intValue, min, max));
            }

            return (true, null);
        }

        /// <summary>
        /// Validates that a float argument is present and within range.
        /// </summary>
        public static (bool valid, ToolError error) RequireFloat(
            Dictionary<string, object> args,
            string key,
            float? min = null,
            float? max = null)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
            {
                return (false, ToolError.MissingRequiredArgument(key, "float"));
            }

            if (!TryConvertToFloat(value, out var floatValue))
            {
                return (false, ToolError.InvalidArgumentType(key, "float", value.GetType().Name));
            }

            if (min.HasValue && floatValue < min.Value)
            {
                return (false, ToolError.ArgumentOutOfRange(key, floatValue, min, max));
            }

            if (max.HasValue && floatValue > max.Value)
            {
                return (false, ToolError.ArgumentOutOfRange(key, floatValue, min, max));
            }

            return (true, null);
        }

        /// <summary>
        /// Validates that a boolean argument is present.
        /// </summary>
        public static (bool valid, ToolError error) RequireBool(
            Dictionary<string, object> args,
            string key)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
            {
                return (false, ToolError.MissingRequiredArgument(key, "boolean"));
            }

            if (!TryConvertToBool(value, out _))
            {
                return (false, ToolError.InvalidArgumentType(key, "boolean", value.GetType().Name));
            }

            return (true, null);
        }

        /// <summary>
        /// Validates that a path argument is present and valid within the specified scope.
        /// </summary>
        public static (bool valid, ToolError error) RequirePath(
            Dictionary<string, object> args,
            string key,
            PathValidator.PathScope scope)
        {
            var (strValid, strError) = RequireString(args, key);
            if (!strValid)
            {
                return (false, strError);
            }

            var path = args[key].ToString();
            var (pathValid, pathError) = PathValidator.ValidatePath(path, scope);

            if (!pathValid)
            {
                // Map PathValidator errors to ToolErrors
                if (pathError.Contains("outside"))
                {
                    return (false, ToolError.PathOutOfBounds(path, scope));
                }
                if (pathError.Contains("blocked") || pathError.Contains("denied"))
                {
                    return (false, ToolError.SensitivePathBlocked(path, pathError));
                }
                if (pathError.Contains("symlink"))
                {
                    return (false, ToolError.SymlinkViolation(path));
                }
                return (false, ToolError.InvalidPath(path, pathError));
            }

            return (true, null);
        }

        /// <summary>
        /// Validates that a list argument is present and has items.
        /// </summary>
        public static (bool valid, ToolError error) RequireList(
            Dictionary<string, object> args,
            string key,
            int? minCount = null,
            int? maxCount = null)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
            {
                return (false, ToolError.MissingRequiredArgument(key, "list"));
            }

            if (!(value is IList<object> list))
            {
                return (false, ToolError.InvalidArgumentType(key, "list", value.GetType().Name));
            }

            if (minCount.HasValue && list.Count < minCount.Value)
            {
                return (false, ToolError.InvalidArgument(key, $"must have at least {minCount.Value} items", list.Count));
            }

            if (maxCount.HasValue && list.Count > maxCount.Value)
            {
                return (false, ToolError.InvalidArgument(key, $"must have at most {maxCount.Value} items", list.Count));
            }

            return (true, null);
        }

        /// <summary>
        /// Validates that an enum argument is present and valid.
        /// </summary>
        public static (bool valid, ToolError error) RequireEnum<TEnum>(
            Dictionary<string, object> args,
            string key) where TEnum : struct, Enum
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
            {
                return (false, ToolError.MissingRequiredArgument(key, typeof(TEnum).Name));
            }

            if (!Enum.TryParse<TEnum>(value.ToString(), true, out _))
            {
                var validValues = string.Join(", ", Enum.GetNames(typeof(TEnum)));
                return (false, ToolError.InvalidArgument(key, $"must be one of: {validValues}", value));
            }

            return (true, null);
        }

        #endregion

        #region Optional Argument Getters

        /// <summary>
        /// Gets a string argument value or returns the default.
        /// </summary>
        public static string GetString(Dictionary<string, object> args, string key, string defaultValue = null)
        {
            if (args != null && args.TryGetValue(key, out var value) && value != null)
            {
                var strValue = value.ToString();
                return string.IsNullOrEmpty(strValue) ? defaultValue : strValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets an integer argument value or returns the default.
        /// </summary>
        public static int GetInt(Dictionary<string, object> args, string key, int defaultValue = 0)
        {
            if (args != null && args.TryGetValue(key, out var value) && TryConvertToInt(value, out var intValue))
            {
                return intValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets an integer argument value clamped to a range.
        /// </summary>
        public static int GetIntClamped(Dictionary<string, object> args, string key, int defaultValue, int min, int max)
        {
            var value = GetInt(args, key, defaultValue);
            return Math.Clamp(value, min, max);
        }

        /// <summary>
        /// Gets a float argument value or returns the default.
        /// </summary>
        public static float GetFloat(Dictionary<string, object> args, string key, float defaultValue = 0f)
        {
            if (args != null && args.TryGetValue(key, out var value) && TryConvertToFloat(value, out var floatValue))
            {
                return floatValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets a boolean argument value or returns the default.
        /// </summary>
        public static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue = false)
        {
            if (args != null && args.TryGetValue(key, out var value) && TryConvertToBool(value, out var boolValue))
            {
                return boolValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets a list argument value or returns an empty list.
        /// </summary>
        public static List<object> GetList(Dictionary<string, object> args, string key)
        {
            if (args != null && args.TryGetValue(key, out var value) && value is IList<object> list)
            {
                return list.ToList();
            }
            return new List<object>();
        }

        /// <summary>
        /// Gets a list of strings from an argument.
        /// </summary>
        public static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            return GetList(args, key)
                .Where(item => item != null)
                .Select(item => item.ToString())
                .ToList();
        }

        /// <summary>
        /// Gets an enum argument value or returns the default.
        /// </summary>
        public static TEnum GetEnum<TEnum>(Dictionary<string, object> args, string key, TEnum defaultValue = default)
            where TEnum : struct, Enum
        {
            if (args != null && args.TryGetValue(key, out var value) && value != null)
            {
                if (Enum.TryParse<TEnum>(value.ToString(), true, out var enumValue))
                {
                    return enumValue;
                }
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets a Vector3 from list argument [x, y, z].
        /// </summary>
        public static (bool valid, float x, float y, float z) GetVector3(
            Dictionary<string, object> args,
            string key,
            float defaultX = 0f,
            float defaultY = 0f,
            float defaultZ = 0f)
        {
            if (args != null && args.TryGetValue(key, out var value) && value is IList<object> list && list.Count >= 3)
            {
                if (TryConvertToFloat(list[0], out var x) &&
                    TryConvertToFloat(list[1], out var y) &&
                    TryConvertToFloat(list[2], out var z))
                {
                    return (true, x, y, z);
                }
            }
            return (false, defaultX, defaultY, defaultZ);
        }

        /// <summary>
        /// Gets a dictionary argument value or returns an empty dictionary.
        /// </summary>
        public static Dictionary<string, object> GetDictionary(Dictionary<string, object> args, string key)
        {
            if (args != null && args.TryGetValue(key, out var value) && value is Dictionary<string, object> dict)
            {
                return dict;
            }
            return new Dictionary<string, object>();
        }

        #endregion

        #region Validation Helpers

        /// <summary>
        /// Validates multiple required arguments at once.
        /// Returns the first error encountered, or (true, null) if all are valid.
        /// </summary>
        public static (bool valid, ToolError error) ValidateRequired(
            Dictionary<string, object> args,
            params (string key, string type)[] requirements)
        {
            foreach (var (key, type) in requirements)
            {
                var (valid, error) = type.ToLowerInvariant() switch
                {
                    "string" => RequireString(args, key),
                    "int" or "integer" => RequireInt(args, key),
                    "float" => RequireFloat(args, key),
                    "bool" or "boolean" => RequireBool(args, key),
                    "list" or "array" => RequireList(args, key),
                    _ => RequireString(args, key) // Default to string validation
                };

                if (!valid)
                {
                    return (false, error);
                }
            }

            return (true, null);
        }

        /// <summary>
        /// Validates that at least one of the specified arguments is present.
        /// </summary>
        public static (bool valid, ToolError error) RequireOneOf(
            Dictionary<string, object> args,
            params string[] keys)
        {
            if (args == null || keys == null || keys.Length == 0)
            {
                return (false, ToolError.MissingRequiredArgument(string.Join(" or ", keys)));
            }

            foreach (var key in keys)
            {
                if (args.TryGetValue(key, out var value) && value != null)
                {
                    var strValue = value.ToString();
                    if (!string.IsNullOrWhiteSpace(strValue))
                    {
                        return (true, null);
                    }
                }
            }

            var message = $"At least one of these arguments is required: {string.Join(", ", keys)}";
            return (false, new ToolError(ToolErrorCode.MissingRequiredArgument, message));
        }

        #endregion

        #region Type Conversion Helpers

        private static bool TryConvertToInt(object value, out int result)
        {
            result = 0;
            if (value == null) return false;

            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertToFloat(object value, out float result)
        {
            result = 0f;
            if (value == null) return false;

            try
            {
                result = Convert.ToSingle(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertToBool(object value, out bool result)
        {
            result = false;
            if (value == null) return false;

            if (value is bool b)
            {
                result = b;
                return true;
            }

            var strValue = value.ToString().ToLowerInvariant();
            if (strValue == "true" || strValue == "1" || strValue == "yes")
            {
                result = true;
                return true;
            }
            if (strValue == "false" || strValue == "0" || strValue == "no")
            {
                result = false;
                return true;
            }

            return false;
        }

        #endregion
    }

    #endregion
}
