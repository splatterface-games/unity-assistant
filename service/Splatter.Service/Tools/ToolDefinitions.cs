// Tool Definitions - All tool specifications per spec

using Splatter.Protocol;

namespace Splatter.Service.Tools;

/// <summary>
/// Defines all tool specifications for the Splatterface Games Assistant.
/// </summary>
public static class ToolDefinitions
{
    public static IReadOnlyList<ToolSpec> GetAllTools() => AllTools;

    private static readonly List<ToolSpec> AllTools = new()
    {
        #region Project Read Tools

        new ToolSpec(
            "project.list_files",
            "List Project Files",
            "List files in the Unity project with optional glob pattern and depth filters. Returns file paths, sizes, and modification times.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path relative to project root (default: Assets)" },
                    pattern = new { type = "string", description = "Glob pattern filter (e.g., '**/*.cs')" },
                    maxDepth = new { type = "integer", description = "Maximum directory depth (default: 10)", minimum = 1, maximum = 50 },
                    includeHidden = new { type = "boolean", description = "Include hidden files/folders", @default = false }
                }
            },
            new { type = "object", properties = new { files = new { type = "array" }, truncated = new { type = "boolean" }, totalCount = new { type = "integer" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List project files", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOrPackages)),

        new ToolSpec(
            "project.read_file",
            "Read File",
            "Read the contents of a file in the project. Supports line ranges for large files.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path relative to project root" },
                    startLine = new { type = "integer", description = "Starting line number (1-indexed)", minimum = 1 },
                    endLine = new { type = "integer", description = "Ending line number (inclusive)" },
                    encoding = new { type = "string", description = "Text encoding (default: utf-8)" }
                },
                required = new[] { "path" }
            },
            new { type = "object", properties = new { content = new { type = "string" }, lineCount = new { type = "integer" }, truncated = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read file contents", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 10000, true, true),
            new ToolConstraints(PathScope.AssetsOrPackages, null, NetworkPolicy.None, 10 * 1024 * 1024, null)),

        new ToolSpec(
            "project.search_text",
            "Search Text",
            "Search for text patterns in project files using regular expressions. Returns matching lines with context.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string", description = "Search pattern (regex supported)" },
                    path = new { type = "string", description = "Directory to search (default: Assets)" },
                    filePattern = new { type = "string", description = "File glob pattern (e.g., '*.cs')" },
                    maxResults = new { type = "integer", description = "Maximum results (default: 50)", minimum = 1, maximum = 500 },
                    caseSensitive = new { type = "boolean", description = "Case-sensitive search", @default = false },
                    includeContext = new { type = "boolean", description = "Include surrounding lines", @default = true }
                },
                required = new[] { "pattern" }
            },
            new { type = "object", properties = new { results = new { type = "array" }, truncated = new { type = "boolean" }, totalMatches = new { type = "integer" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Search project files", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 60000, true, true),
            new ToolConstraints(PathScope.AssetsOrPackages)),

        new ToolSpec(
            "project.get_overview",
            "Get Project Overview",
            "Get a high-level overview of the project including structure, asset counts, and configuration.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    includeRecentChanges = new { type = "boolean", description = "Include recent file changes", @default = true }
                }
            },
            new { type = "object", properties = new { overview = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read project overview", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.ProjectRoot)),

        #endregion

        #region Asset Read Tools

        new ToolSpec(
            "asset.search",
            "Search Assets",
            "Search Unity assets by name, type, labels, or path. Returns asset GUIDs and metadata.",
            ToolCategory.AssetRead,
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query (matches name, path)" },
                    type = new { type = "string", description = "Asset type filter (e.g., 'Texture2D', 'Material')" },
                    labels = new { type = "array", items = new { type = "string" }, description = "Required labels" },
                    path = new { type = "string", description = "Path filter" },
                    maxResults = new { type = "integer", description = "Maximum results (default: 50)", minimum = 1, maximum = 200 }
                }
            },
            new { type = "object", properties = new { assets = new { type = "array" }, truncated = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Search assets", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "asset.read",
            "Read Asset",
            "Read asset metadata and serialized properties. For text assets, includes content.",
            ToolCategory.AssetRead,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Asset path" },
                    guid = new { type = "string", description = "Asset GUID (alternative to path)" },
                    includeSerializedData = new { type = "boolean", description = "Include serialized properties", @default = false }
                }
            },
            new { type = "object", properties = new { asset = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read asset", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 10000, true, true),
            new ToolConstraints(PathScope.AssetsOrPackages)),

        new ToolSpec(
            "asset.get_dependencies",
            "Get Asset Dependencies",
            "Get the dependencies of an asset (what it references and what references it).",
            ToolCategory.AssetRead,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Asset path" },
                    guid = new { type = "string", description = "Asset GUID (alternative to path)" },
                    direction = new { type = "string", @enum = new[] { "dependencies", "dependents", "both" }, description = "Which relationships to include" },
                    maxDepth = new { type = "integer", description = "Maximum traversal depth", minimum = 1, maximum = 10 }
                }
            },
            new { type = "object", properties = new { dependencies = new { type = "array" }, dependents = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get asset dependencies", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOrPackages)),

        #endregion

        #region Scene Read Tools

        new ToolSpec(
            "scene.get_hierarchy",
            "Get Scene Hierarchy",
            "Get the GameObject hierarchy of the active or specified scene.",
            ToolCategory.SceneRead,
            new
            {
                type = "object",
                properties = new
                {
                    scenePath = new { type = "string", description = "Scene path (default: active scene)" },
                    maxDepth = new { type = "integer", description = "Maximum hierarchy depth", minimum = 1, maximum = 20 },
                    includeComponents = new { type = "boolean", description = "Include component names", @default = true }
                }
            },
            new { type = "object", properties = new { hierarchy = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read scene hierarchy", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "scene.find_objects",
            "Find Scene Objects",
            "Find GameObjects in the scene by name, tag, layer, or component type.",
            ToolCategory.SceneRead,
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Name pattern (supports wildcards)" },
                    tag = new { type = "string", description = "Tag filter" },
                    layer = new { type = "integer", description = "Layer filter" },
                    componentType = new { type = "string", description = "Required component type" },
                    scenePath = new { type = "string", description = "Scene path (default: active scene)" },
                    maxResults = new { type = "integer", description = "Maximum results", minimum = 1, maximum = 500 }
                }
            },
            new { type = "object", properties = new { objects = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Find scene objects", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "scene.read_object",
            "Read Scene Object",
            "Read detailed information about a specific GameObject including components and their properties.",
            ToolCategory.SceneRead,
            new
            {
                type = "object",
                properties = new
                {
                    objectPath = new { type = "string", description = "Object path in hierarchy (e.g., 'Canvas/Panel/Button')" },
                    instanceId = new { type = "integer", description = "Object instance ID (alternative to path)" },
                    includeChildren = new { type = "boolean", description = "Include child objects", @default = false },
                    maxDepth = new { type = "integer", description = "Child depth if includeChildren", minimum = 1, maximum = 5 }
                }
            },
            new { type = "object", properties = new { @object = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read scene object", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 10000, true, true),
            new ToolConstraints(PathScope.None)),

        #endregion

        #region Code Edit Tools

        new ToolSpec(
            "code.propose_patch",
            "Propose Code Patch",
            "Propose changes to a file. Creates a diff for user review before applying.",
            ToolCategory.CodeEdit,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path" },
                    oldContent = new { type = "string", description = "Content to find and replace (if partial edit)" },
                    newContent = new { type = "string", description = "New content" },
                    description = new { type = "string", description = "Description of the change" },
                    createIfNotExists = new { type = "boolean", description = "Create file if it doesn't exist", @default = false }
                },
                required = new[] { "path", "newContent" }
            },
            new { type = "object", properties = new { diff = new { type = "object" }, checkpointId = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Propose file changes", null),
            new[] { new SideEffectSpec("file_diff", "Proposes changes for review") },
            new ToolExecutionSpec(false, 10000, true, false),
            new ToolConstraints(PathScope.AssetsOnly, new[] { ".cs", ".shader", ".json", ".xml", ".txt", ".md", ".yaml", ".yml", ".asmdef" })),

        new ToolSpec(
            "code.apply_patch",
            "Apply Code Patch",
            "Apply a previously proposed patch after user approval.",
            ToolCategory.CodeEdit,
            new
            {
                type = "object",
                properties = new
                {
                    patchId = new { type = "string", description = "ID of the proposed patch" },
                    validateFirst = new { type = "boolean", description = "Run validation before applying", @default = true }
                },
                required = new[] { "patchId" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" }, path = new { type = "string" }, validationResult = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Apply file changes", null),
            new[] { new SideEffectSpec("file_write", "Modifies file on disk") },
            new ToolExecutionSpec(true, 30000, false, false),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "code.create_file",
            "Create File",
            "Create a new file in the project.",
            ToolCategory.CodeEdit,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path relative to project root" },
                    content = new { type = "string", description = "File content" },
                    overwrite = new { type = "boolean", description = "Overwrite if exists", @default = false }
                },
                required = new[] { "path", "content" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" }, path = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Create new file", null),
            new[] { new SideEffectSpec("file_create", "Creates new file") },
            new ToolExecutionSpec(true, 10000, false, false),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "code.delete_file",
            "Delete File",
            "Delete a file from the project.",
            ToolCategory.CodeEdit,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path" }
                },
                required = new[] { "path" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.DeleteProject, PermissionRisk.High, "Delete file", null),
            new[] { new SideEffectSpec("file_delete", "Permanently deletes file") },
            new ToolExecutionSpec(true, 10000, false, false),
            new ToolConstraints(PathScope.AssetsOnly)),

        #endregion

        #region Unity Mutation Tools

        new ToolSpec(
            "scene.create_gameobject",
            "Create GameObject",
            "Create a new GameObject in the scene.",
            ToolCategory.UnityMutation,
            new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "GameObject name" },
                    parentPath = new { type = "string", description = "Parent object path (null for root)" },
                    position = new { type = "array", items = new { type = "number" }, description = "World position [x,y,z]" },
                    rotation = new { type = "array", items = new { type = "number" }, description = "Euler rotation [x,y,z]" },
                    scale = new { type = "array", items = new { type = "number" }, description = "Local scale [x,y,z]" },
                    primitive = new { type = "string", @enum = new[] { "Cube", "Sphere", "Cylinder", "Capsule", "Plane", "Quad" }, description = "Create as primitive" }
                },
                required = new[] { "name" }
            },
            new { type = "object", properties = new { instanceId = new { type = "integer" }, path = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Create GameObject", null),
            new[] { new SideEffectSpec("scene_modify", "Modifies scene hierarchy") },
            new ToolExecutionSpec(true, 10000, true, false),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "scene.modify_gameobject",
            "Modify GameObject",
            "Modify properties of an existing GameObject.",
            ToolCategory.UnityMutation,
            new
            {
                type = "object",
                properties = new
                {
                    objectPath = new { type = "string", description = "Object path in hierarchy" },
                    instanceId = new { type = "integer", description = "Object instance ID" },
                    name = new { type = "string", description = "New name" },
                    active = new { type = "boolean", description = "Set active state" },
                    layer = new { type = "integer", description = "Set layer" },
                    tag = new { type = "string", description = "Set tag" },
                    position = new { type = "array", items = new { type = "number" }, description = "New position [x,y,z]" },
                    rotation = new { type = "array", items = new { type = "number" }, description = "New rotation [x,y,z]" },
                    scale = new { type = "array", items = new { type = "number" }, description = "New scale [x,y,z]" }
                }
            },
            new { type = "object", properties = new { success = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Modify GameObject", null),
            new[] { new SideEffectSpec("scene_modify", "Modifies scene object") },
            new ToolExecutionSpec(true, 10000, true, false),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "scene.delete_gameobject",
            "Delete GameObject",
            "Delete a GameObject from the scene.",
            ToolCategory.UnityMutation,
            new
            {
                type = "object",
                properties = new
                {
                    objectPath = new { type = "string", description = "Object path in hierarchy" },
                    instanceId = new { type = "integer", description = "Object instance ID" }
                }
            },
            new { type = "object", properties = new { success = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.High, "Delete GameObject", null),
            new[] { new SideEffectSpec("scene_modify", "Deletes scene object") },
            new ToolExecutionSpec(true, 10000, false, false),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "scene.add_component",
            "Add Component",
            "Add a component to a GameObject.",
            ToolCategory.UnityMutation,
            new
            {
                type = "object",
                properties = new
                {
                    objectPath = new { type = "string", description = "Object path in hierarchy" },
                    instanceId = new { type = "integer", description = "Object instance ID" },
                    componentType = new { type = "string", description = "Component type name (e.g., 'BoxCollider', 'Rigidbody')" }
                },
                required = new[] { "componentType" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" }, componentId = new { type = "integer" } } },
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Add component", null),
            new[] { new SideEffectSpec("scene_modify", "Adds component to object") },
            new ToolExecutionSpec(true, 10000, true, false),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "scene.set_component_property",
            "Set Component Property",
            "Set a property value on a component.",
            ToolCategory.UnityMutation,
            new
            {
                type = "object",
                properties = new
                {
                    objectPath = new { type = "string", description = "Object path in hierarchy" },
                    instanceId = new { type = "integer", description = "Object instance ID" },
                    componentType = new { type = "string", description = "Component type name" },
                    propertyPath = new { type = "string", description = "Property path (e.g., 'size', 'material.color')" },
                    value = new { description = "Property value" }
                },
                required = new[] { "componentType", "propertyPath", "value" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.SceneMutation, PermissionRisk.Medium, "Set component property", null),
            new[] { new SideEffectSpec("scene_modify", "Modifies component property") },
            new ToolExecutionSpec(true, 10000, true, false),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "asset.create",
            "Create Asset",
            "Create a new asset in the project.",
            ToolCategory.UnityMutation,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Asset path" },
                    type = new { type = "string", description = "Asset type (e.g., 'Material', 'ScriptableObject')" },
                    properties = new { type = "object", description = "Initial property values" }
                },
                required = new[] { "path", "type" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" }, guid = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Create asset", null),
            new[] { new SideEffectSpec("asset_create", "Creates new asset") },
            new ToolExecutionSpec(true, 30000, false, false),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "asset.delete",
            "Delete Asset",
            "Delete an asset from the project.",
            ToolCategory.UnityMutation,
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Asset path" },
                    guid = new { type = "string", description = "Asset GUID" }
                }
            },
            new { type = "object", properties = new { success = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.DeleteProject, PermissionRisk.High, "Delete asset", null),
            new[] { new SideEffectSpec("asset_delete", "Permanently deletes asset") },
            new ToolExecutionSpec(true, 10000, false, false),
            new ToolConstraints(PathScope.AssetsOnly)),

        #endregion

        #region Shell Tools

        new ToolSpec(
            "shell.run",
            "Run Shell Command",
            "Execute a shell command in the project directory.",
            ToolCategory.Shell,
            new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "Command to execute" },
                    workingDirectory = new { type = "string", description = "Working directory (default: project root)" },
                    timeout = new { type = "integer", description = "Timeout in seconds (default: 120)", minimum = 1, maximum = 600 },
                    env = new { type = "object", description = "Additional environment variables" }
                },
                required = new[] { "command" }
            },
            new { type = "object", properties = new { exitCode = new { type = "integer" }, stdout = new { type = "string" }, stderr = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.ExecuteShell, PermissionRisk.High, "Execute shell command", null),
            new[] { new SideEffectSpec("command_execution", "Executes system command") },
            new ToolExecutionSpec(false, 120000, true, false),
            new ToolConstraints(PathScope.ProjectRoot)),

        #endregion

        #region Package Tools

        new ToolSpec(
            "package.list",
            "List Packages",
            "List installed packages in the project.",
            ToolCategory.Package,
            new
            {
                type = "object",
                properties = new
                {
                    includeBuiltIn = new { type = "boolean", description = "Include built-in packages", @default = false }
                }
            },
            new { type = "object", properties = new { packages = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List packages", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 10000, true, true),
            new ToolConstraints(PathScope.ProjectRoot)),

        new ToolSpec(
            "package.search",
            "Search Packages",
            "Search for packages in the Unity registry.",
            ToolCategory.Package,
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query" }
                },
                required = new[] { "query" }
            },
            new { type = "object", properties = new { packages = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.Network, PermissionRisk.Low, "Search package registry", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 30000, true, true),
            new ToolConstraints(Network: NetworkPolicy.UnityRegistry)),

        new ToolSpec(
            "package.add",
            "Add Package",
            "Add a package to the project.",
            ToolCategory.Package,
            new
            {
                type = "object",
                properties = new
                {
                    packageId = new { type = "string", description = "Package identifier (e.g., 'com.unity.textmeshpro')" },
                    version = new { type = "string", description = "Version (default: latest)" }
                },
                required = new[] { "packageId" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" }, installedVersion = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.PackageManage, PermissionRisk.High, "Add package", null),
            new[] { new SideEffectSpec("package_install", "Modifies package manifest") },
            new ToolExecutionSpec(true, 120000, true, false),
            new ToolConstraints(Network: NetworkPolicy.UnityRegistry)),

        new ToolSpec(
            "package.remove",
            "Remove Package",
            "Remove a package from the project.",
            ToolCategory.Package,
            new
            {
                type = "object",
                properties = new
                {
                    packageId = new { type = "string", description = "Package identifier" }
                },
                required = new[] { "packageId" }
            },
            new { type = "object", properties = new { success = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.PackageManage, PermissionRisk.High, "Remove package", null),
            new[] { new SideEffectSpec("package_remove", "Modifies package manifest") },
            new ToolExecutionSpec(true, 60000, true, false),
            new ToolConstraints(PathScope.ProjectRoot)),

        #endregion

        #region Skill Tools

        new ToolSpec(
            "skill.list",
            "List Skills",
            "List available skills with their metadata and compatibility status.",
            ToolCategory.Skill,
            new
            {
                type = "object",
                properties = new
                {
                    includeDisabled = new { type = "boolean", description = "Include disabled skills", @default = false },
                    source = new { type = "string", @enum = new[] { "all", "project", "user", "builtin" }, description = "Filter by source" }
                }
            },
            new { type = "object", properties = new { skills = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List skills", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 10000, true, true),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "skill.read_body",
            "Read Skill Body",
            "Read the full body/instructions of a skill.",
            ToolCategory.Skill,
            new
            {
                type = "object",
                properties = new
                {
                    skillName = new { type = "string", description = "Name of the skill" }
                },
                required = new[] { "skillName" }
            },
            new { type = "object", properties = new { content = new { type = "string" }, truncated = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read skill body", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 10000, true, true),
            new ToolConstraints(PathScope.None, null, NetworkPolicy.None, 1024 * 1024, null)),

        new ToolSpec(
            "skill.read_resource",
            "Read Skill Resource",
            "Read a resource file from a skill.",
            ToolCategory.Skill,
            new
            {
                type = "object",
                properties = new
                {
                    skillName = new { type = "string", description = "Name of the skill" },
                    resourcePath = new { type = "string", description = "Path to resource within skill directory" }
                },
                required = new[] { "skillName", "resourcePath" }
            },
            new { type = "object", properties = new { content = new { type = "string" }, contentType = new { type = "string" }, truncated = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read skill resource", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 10000, true, true),
            new ToolConstraints(PathScope.None, null, NetworkPolicy.None, 1024 * 1024, null)),

        #endregion

        #region Console Tools

        new ToolSpec(
            "console.get_logs",
            "Get Console Logs",
            "Get recent Unity console logs, errors, and warnings.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    count = new { type = "integer", description = "Number of logs to retrieve (default: 50)", minimum = 1, maximum = 500 },
                    types = new { type = "array", items = new { type = "string", @enum = new[] { "Log", "Warning", "Error", "Exception" } }, description = "Log types to include" },
                    filter = new { type = "string", description = "Text filter" }
                }
            },
            new { type = "object", properties = new { logs = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Read console logs", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 10000, true, true),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "console.clear",
            "Clear Console",
            "Clear the Unity console.",
            ToolCategory.UserInteraction,
            new { type = "object", properties = new { } },
            new { type = "object", properties = new { success = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.UserInteraction, PermissionRisk.Low, "Clear console", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 5000, true, true),
            new ToolConstraints(PathScope.None)),

        #endregion

        #region Selection Tools

        new ToolSpec(
            "selection.get",
            "Get Selection",
            "Get the current Editor selection (objects, assets, or folders).",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    includeDetails = new { type = "boolean", description = "Include detailed information", @default = true }
                }
            },
            new { type = "object", properties = new { selection = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get selection", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 5000, true, true),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "selection.set",
            "Set Selection",
            "Set the Editor selection.",
            ToolCategory.UserInteraction,
            new
            {
                type = "object",
                properties = new
                {
                    assetPaths = new { type = "array", items = new { type = "string" }, description = "Asset paths to select" },
                    assetGuids = new { type = "array", items = new { type = "string" }, description = "Asset GUIDs to select" },
                    objectPaths = new { type = "array", items = new { type = "string" }, description = "Scene object paths to select" }
                }
            },
            new { type = "object", properties = new { success = new { type = "boolean" }, selectedCount = new { type = "integer" } } },
            new PermissionRequirement(PermissionClass.UserInteraction, PermissionRisk.Low, "Set selection", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 5000, true, true),
            new ToolConstraints(PathScope.None)),

        #endregion

        #region Checkpoint Tools

        new ToolSpec(
            "checkpoint.create",
            "Create Checkpoint",
            "Create a checkpoint of specified files before making changes.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    paths = new { type = "array", items = new { type = "string" }, description = "File paths to checkpoint" },
                    description = new { type = "string", description = "Checkpoint description" }
                },
                required = new[] { "paths" }
            },
            new { type = "object", properties = new { checkpointId = new { type = "string" }, fileCount = new { type = "integer" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Create checkpoint", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "checkpoint.restore",
            "Restore Checkpoint",
            "Restore files from a checkpoint (preview mode available).",
            ToolCategory.CodeEdit,
            new
            {
                type = "object",
                properties = new
                {
                    checkpointId = new { type = "string", description = "Checkpoint ID to restore" },
                    preview = new { type = "boolean", description = "Preview only, don't apply", @default = true }
                },
                required = new[] { "checkpointId" }
            },
            new { type = "object", properties = new { plan = new { type = "object" }, restored = new { type = "boolean" } } },
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Restore checkpoint", null),
            new[] { new SideEffectSpec("file_restore", "Restores files from checkpoint") },
            new ToolExecutionSpec(false, 60000, true, false),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "checkpoint.list",
            "List Checkpoints",
            "List available checkpoints.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    limit = new { type = "integer", description = "Maximum checkpoints to return", minimum = 1, maximum = 100 }
                }
            },
            new { type = "object", properties = new { checkpoints = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "List checkpoints", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 10000, true, true),
            new ToolConstraints(PathScope.None)),

        #endregion

        #region Generator Tools

        new ToolSpec(
            "generator.quote",
            "Get Generation Quote",
            "Get a cost estimate for an asset generation job.",
            ToolCategory.Generator,
            new
            {
                type = "object",
                properties = new
                {
                    modality = new { type = "string", @enum = new[] { "Image", "MaterialPbr", "Mesh", "Sound", "Animation" }, description = "Generation modality" },
                    providerId = new { type = "string", description = "Provider ID" },
                    modelId = new { type = "string", description = "Model ID" },
                    mode = new { type = "string", description = "Generation mode (e.g., 'generate', 'transform')" },
                    parameters = new { type = "object", description = "Generation parameters" }
                },
                required = new[] { "modality", "providerId", "modelId", "mode" }
            },
            new { type = "object", properties = new { quote = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.Network, PermissionRisk.Low, "Get generation quote", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 30000, true, true),
            new ToolConstraints(Network: NetworkPolicy.ProviderOnly)),

        new ToolSpec(
            "generator.submit",
            "Submit Generation Job",
            "Submit an asset generation job.",
            ToolCategory.Generator,
            new
            {
                type = "object",
                properties = new
                {
                    modality = new { type = "string", @enum = new[] { "Image", "MaterialPbr", "Mesh", "Sound", "Animation" }, description = "Generation modality" },
                    providerId = new { type = "string", description = "Provider ID" },
                    modelId = new { type = "string", description = "Model ID" },
                    mode = new { type = "string", description = "Generation mode" },
                    targetAssetPath = new { type = "string", description = "Target asset path for result" },
                    parameters = new { type = "object", description = "Generation parameters" },
                    references = new { type = "array", description = "Reference assets" }
                },
                required = new[] { "modality", "providerId", "modelId", "mode", "parameters" }
            },
            new { type = "object", properties = new { job = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.GenerationSpend, PermissionRisk.Medium, "Submit generation job", null),
            new[] { new SideEffectSpec("generation_job", "Submits paid generation request") },
            new ToolExecutionSpec(false, 300000, true, false),
            new ToolConstraints(Network: NetworkPolicy.ProviderOnly)),

        new ToolSpec(
            "generator.apply",
            "Apply Generated Result",
            "Apply a generated result to an asset.",
            ToolCategory.Generator,
            new
            {
                type = "object",
                properties = new
                {
                    jobId = new { type = "string", description = "Generation job ID" },
                    resultId = new { type = "string", description = "Result ID to apply (default: first)" },
                    targetAssetPath = new { type = "string", description = "Target asset path" }
                },
                required = new[] { "jobId" }
            },
            new { type = "object", properties = new { appliedPath = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.WriteProject, PermissionRisk.Medium, "Apply generated result", null),
            new[] { new SideEffectSpec("asset_modify", "Modifies or creates asset") },
            new ToolExecutionSpec(true, 30000, true, false),
            new ToolConstraints(PathScope.AssetsOnly)),

        #endregion

        #region Capture Tools

        new ToolSpec(
            "capture.scene",
            "Capture Scene View",
            "Capture an image of the current scene view.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    width = new { type = "integer", description = "Image width", minimum = 64, maximum = 4096 },
                    height = new { type = "integer", description = "Image height", minimum = 64, maximum = 4096 },
                    outputPath = new { type = "string", description = "Output path (temp if not specified)" }
                }
            },
            new { type = "object", properties = new { path = new { type = "string" }, width = new { type = "integer" }, height = new { type = "integer" } } },
            new PermissionRequirement(PermissionClass.ScreenCapture, PermissionRisk.Low, "Capture scene view", null),
            new[] { new SideEffectSpec("file_create", "Creates image file") },
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOnly)),

        new ToolSpec(
            "capture.game",
            "Capture Game View",
            "Capture an image of the game view.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    width = new { type = "integer", description = "Image width", minimum = 64, maximum = 4096 },
                    height = new { type = "integer", description = "Image height", minimum = 64, maximum = 4096 },
                    outputPath = new { type = "string", description = "Output path (temp if not specified)" }
                }
            },
            new { type = "object", properties = new { path = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.ScreenCapture, PermissionRisk.Low, "Capture game view", null),
            new[] { new SideEffectSpec("file_create", "Creates image file") },
            new ToolExecutionSpec(true, 30000, true, true),
            new ToolConstraints(PathScope.AssetsOnly)),

        #endregion

        #region Script Analysis Tools

        new ToolSpec(
            "script.compile_check",
            "Check Compilation",
            "Check for compilation errors in the project.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new { }
            },
            new { type = "object", properties = new { hasErrors = new { type = "boolean" }, errors = new { type = "array" }, warnings = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Check compilation", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            new ToolConstraints(PathScope.None)),

        new ToolSpec(
            "script.get_references",
            "Get Type References",
            "Find references to a type, method, or field in the codebase.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    symbolName = new { type = "string", description = "Symbol name to search for" },
                    symbolType = new { type = "string", @enum = new[] { "type", "method", "field", "property" }, description = "Symbol type" }
                },
                required = new[] { "symbolName" }
            },
            new { type = "object", properties = new { references = new { type = "array" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get type references", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(true, 60000, true, true),
            new ToolConstraints(PathScope.AssetsOrPackages)),

        #endregion

        #region Graph Tools

        new ToolSpec(
            "graph.query",
            "Query Dependency Graph",
            "Query the project dependency graph.",
            ToolCategory.ProjectRead,
            new
            {
                type = "object",
                properties = new
                {
                    rootNodeId = new { type = "string", description = "Starting node ID (optional)" },
                    direction = new { type = "string", @enum = new[] { "outgoing", "incoming", "both" }, description = "Traversal direction" },
                    maxDepth = new { type = "integer", description = "Maximum depth", minimum = 1, maximum = 10 },
                    edgeTypes = new { type = "array", items = new { type = "string" }, description = "Edge types to include" },
                    nodeTypes = new { type = "array", items = new { type = "string" }, description = "Node types to include" }
                }
            },
            new { type = "object", properties = new { result = new { type = "object" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Query dependency graph", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 30000, true, true),
            new ToolConstraints(PathScope.None)),

        #endregion

        #region Custom Instructions Tool

        new ToolSpec(
            "project.get_custom_instructions",
            "Get Custom Instructions",
            "Get the project's custom AI instructions.",
            ToolCategory.ProjectRead,
            new { type = "object", properties = new { } },
            new { type = "object", properties = new { instructions = new { type = "string" } } },
            new PermissionRequirement(PermissionClass.ReadProject, PermissionRisk.Low, "Get custom instructions", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 5000, true, true),
            new ToolConstraints(PathScope.None)),

        #endregion
    };
}
