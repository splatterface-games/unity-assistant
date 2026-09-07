# Splatter AI - Tools Implementation Plan

This document outlines the tools system architecture and remaining work for the Splatter AI Unity Assistant.

## Overview

The tools system enables the AI agent to interact with Unity projects through a well-defined set of operations. Tools are split between:
- **Service-side tools**: File operations, shell commands, text search (run in the .NET service)
- **Unity-side tools**: Asset manipulation, scene operations, selection (require Unity main thread)

## Current Architecture

### Service Side (`service/Splatter.Service/Tools/`)

```
ToolDefinitions.cs    - All 50+ tool specifications with JSON schemas
ToolRegistry.cs       - Registration, execution, delegation to Unity
IToolRegistry.cs      - Interface definitions
CheckpointManager.cs  - File checkpoint/restore functionality
```

**Key patterns:**
- Tools are defined as `ToolSpec` records with full JSON schema for parameters
- Each tool has a `ToolCategory`, `PermissionRequirement`, `SideEffectSpec[]`
- Service executes file/shell tools directly
- Unity-requiring tools are delegated via WebSocket events

### Unity Side (`packages/com.splatterfacegames.assistant/Editor/Tools/`)

```
IToolExecutor.cs           - Interface and result types
UnityToolRegistry.cs       - Singleton registry
Handlers/ToolExecutionHandler.cs - Receives delegated calls from service

Executors/
  ProjectToolExecutors.cs  - list_files, read_file, write_file, search
  AssetToolExecutors.cs    - import, create, delete
  SceneToolExecutors.cs    - get_hierarchy, create/modify_gameobject
  ScriptToolExecutors.cs   - compile_check, get_references
  ConsoleToolExecutors.cs  - get_logs, clear
  SelectionToolExecutors.cs - get, set
```

**Key patterns:**
- Each executor implements `IToolExecutor` with `ToolId` and `ExecuteAsync`
- Results are `ToolExecutionResult` with success/output/error/affected paths
- All Unity API calls happen on main thread via `MainThreadDispatcher`
- Undo integration via `Undo.RecordObject` / `Undo.RegisterCreatedObjectUndo`

## Tool Categories

| Category | Permission | Side Effects | Examples |
|----------|------------|--------------|----------|
| ProjectRead | ReadProject | None | list_files, read_file, search_text |
| AssetRead | ReadProject | None | asset.search, asset.read, asset.get_dependencies |
| SceneRead | ReadProject | None | scene.get_hierarchy, scene.find_objects |
| CodeEdit | WriteProject | file_write | code.propose_patch, code.apply_patch, code.create_file |
| UnityMutation | SceneMutation | scene_modify | scene.create_gameobject, scene.add_component |
| Shell | ExecuteShell | command_exec | shell.run |
| Package | PackageManage | package_install | package.add, package.remove |
| Generator | GenerationSpend | generation_job | generator.submit, generator.apply |
| Skill | ReadProject | None | skill.list, skill.read_body |

## Remaining Work

### Priority 1: Core Tool Completion

#### 1.1 Missing Unity Executors
Create executors for tools defined in `ToolDefinitions.cs` but missing implementations:

- [ ] `AssetReadExecutor` - Read asset metadata and serialized properties
- [ ] `AssetDependenciesExecutor` - Get asset dependency graph
- [ ] `SceneFindObjectsExecutor` - Find objects by name/tag/layer/component
- [ ] `SceneReadObjectExecutor` - Read detailed GameObject info with components
- [ ] `SceneDeleteGameObjectExecutor` - Delete GameObjects with undo
- [ ] `SceneAddComponentExecutor` - Add components to GameObjects
- [ ] `SceneSetComponentPropertyExecutor` - Set component properties via reflection
- [ ] `AssetCreateExecutor` - Create new assets (Materials, ScriptableObjects, etc.)
- [ ] `PackageAddExecutor` - Add packages via Package Manager API
- [ ] `PackageRemoveExecutor` - Remove packages
- [ ] `CaptureSceneExecutor` - Capture scene view to image
- [ ] `CaptureGameExecutor` - Capture game view to image

#### 1.2 Checkpoint System
The checkpoint system allows rollback of changes:

- [ ] Implement `CheckpointCreateExecutor` in Unity
- [ ] Implement `CheckpointRestoreExecutor` with preview mode
- [ ] Wire up to service's `CheckpointManager`
- [ ] Store checkpoints in project-specific data directory

#### 1.3 Graph Query Tool
For dependency graph traversal:

- [ ] Build dependency graph from AssetDatabase
- [ ] Implement `graph.query` executor
- [ ] Support traversal directions (incoming, outgoing, both)
- [ ] Cache graph for performance

### Priority 2: Tool Quality

#### 2.1 Error Handling
- [ ] Standardize error codes across all tools
- [ ] Add validation for all tool arguments
- [ ] Return actionable error messages
- [ ] Handle Unity domain reload gracefully

#### 2.2 Path Security
- [ ] Validate all paths are within project root
- [ ] Block access to sensitive files (.git, credentials, etc.)
- [ ] Normalize paths consistently (forward slashes)
- [ ] Handle symlinks safely

#### 2.3 Performance
- [ ] Add cancellation token support to all long-running tools
- [ ] Implement pagination for list operations
- [ ] Cache frequently-accessed data (hierarchy, assets)
- [ ] Batch related operations where possible

### Priority 3: Advanced Tools

#### 3.1 Generator Tools
For AI asset generation (images, 3D, materials):

- [ ] `generator.quote` - Cost estimation
- [ ] `generator.submit` - Submit job to provider
- [ ] `generator.apply` - Apply result to asset
- [ ] Integration with ComfyUI, Hunyuan3D-2, LM Studio

#### 3.2 Skill System
For user-defined AI workflows:

- [ ] `skill.list` - List available skills
- [ ] `skill.read_body` - Read skill instructions
- [ ] `skill.read_resource` - Read skill resources
- [ ] Skill discovery from project and user directories

### Priority 4: Testing & Documentation

#### 4.1 Unit Tests
- [ ] Test each executor with valid/invalid inputs
- [ ] Test path security edge cases
- [ ] Test cancellation behavior
- [ ] Mock Unity APIs for service tests

#### 4.2 Integration Tests
- [ ] End-to-end tool execution via WebSocket
- [ ] Permission prompt flow
- [ ] Checkpoint create/restore cycle
- [ ] Multi-file code changes

## Implementation Guidelines

### Adding a New Tool

1. **Define the tool spec** in `ToolDefinitions.cs`:
```csharp
new ToolSpec(
    "category.tool_name",
    "Display Name",
    "Description of what this tool does",
    ToolCategory.CategoryName,
    new { type = "object", properties = new { ... }, required = new[] { ... } },
    outputSchema,
    new PermissionRequirement(PermissionClass.X, PermissionRisk.Y, "action", null),
    new[] { new SideEffectSpec("effect_type", "description") },
    new ToolExecutionSpec(requiresUnity, timeoutMs, cacheable, readOnly),
    new ToolConstraints(PathScope.X)
)
```

2. **Create the executor** in Unity:
```csharp
public class MyToolExecutor : IToolExecutor
{
    public string ToolId => "category.tool_name";

    public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
    {
        // Extract arguments
        var arg = context.Arguments.TryGetValue("key", out var v) ? v?.ToString() : "default";

        // Validate
        if (string.IsNullOrEmpty(arg))
            return Task.FromResult(ToolExecutionResult.Failed("key is required"));

        // Execute
        // ... Unity API calls ...

        // Return result
        return Task.FromResult(ToolExecutionResult.Succeeded(new {
            result = "value"
        }, affectedPaths));
    }
}
```

3. **Register the executor** in `UnityToolRegistry.cs`:
```csharp
Register(new MyToolExecutor());
```

4. **Add to Unity delegation** in service's `ToolRegistry.cs` if needed:
```csharp
private static readonly HashSet<string> UnityMainThreadTools = new()
{
    // ... existing tools ...
    "category.tool_name",
};
```

### Undo Support

All mutation tools should support Unity's undo system:

```csharp
// Before modifying
Undo.RecordObject(gameObject, "Modify GameObject");
Undo.RecordObject(gameObject.transform, "Modify Transform");

// For creation
Undo.RegisterCreatedObjectUndo(newObject, "Create Object");

// For deletion
Undo.DestroyObjectImmediate(objectToDelete);

// Mark scene dirty
EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
```

### Permission Model

Tools requiring approval:
- **WriteProject**: File modifications outside Assets
- **DeleteProject**: Any deletion
- **SceneMutation**: Scene hierarchy changes
- **ExecuteShell**: Shell command execution
- **PackageManage**: Package installation/removal
- **GenerationSpend**: Paid API calls

The permission flow:
1. Agent requests tool execution
2. Service checks permission requirements
3. If approval needed, sends `permission.requested` event
4. Unity shows permission UI to user
5. User approves/denies
6. Service continues or aborts

## File Structure Reference

```
service/Splatter.Service/
├── Tools/
│   ├── ToolDefinitions.cs      # All tool specs (source of truth)
│   ├── ToolRegistry.cs         # Execution logic
│   ├── IToolRegistry.cs        # Interfaces
│   └── CheckpointManager.cs    # Checkpoint storage

packages/com.splatterfacegames.assistant/Editor/
├── Tools/
│   ├── IToolExecutor.cs        # Executor interface
│   ├── UnityToolRegistry.cs    # Unity-side registry
│   └── Executors/
│       ├── ProjectToolExecutors.cs
│       ├── AssetToolExecutors.cs
│       ├── SceneToolExecutors.cs
│       ├── ScriptToolExecutors.cs
│       ├── ConsoleToolExecutors.cs
│       └── SelectionToolExecutors.cs
├── Handlers/
│   └── ToolExecutionHandler.cs # Receives delegated calls
└── Transport/
    └── ServiceClient.cs        # WebSocket communication
```

## Notes for Implementation

1. **Start with read-only tools** - They're lower risk and help verify the pipeline works
2. **Test with small changes first** - Use propose_patch before apply_patch
3. **Watch for domain reload** - Unity can reload assemblies mid-operation
4. **Log extensively** - Use `[Splatter]` prefix for all Debug.Log calls
5. **Respect timeouts** - Pass CancellationToken through all async operations
6. **Validate early** - Check arguments before doing any work
7. **Return useful summaries** - The `summary` field helps the AI understand results

## Quick Reference: Existing Tools

### Fully Implemented (Service + Unity)
- `project.list_files` - List files with glob patterns
- `project.read_file` - Read file contents
- `project.write_file` - Write/create files
- `project.search` - Text search with regex
- `scene.get_hierarchy` - Scene hierarchy tree
- `scene.create_gameobject` - Create GameObjects
- `scene.modify_gameobject` - Modify GameObject properties
- `console.get_logs` - Get console entries
- `console.clear` - Clear console
- `selection.get` - Get editor selection
- `selection.set` - Set editor selection

### Service Only (Need Unity Executor)
- `asset.search`
- `asset.read`
- `asset.get_dependencies`
- `scene.find_objects`
- `scene.read_object`
- `scene.delete_gameobject`
- `scene.add_component`
- `scene.set_component_property`
- `asset.create`
- `asset.delete`
- `package.list`
- `package.add`
- `package.remove`
- `capture.scene`
- `capture.game`
- `checkpoint.create`
- `checkpoint.restore`
- `checkpoint.list`

### Defined but Not Implemented
- `generator.quote`
- `generator.submit`
- `generator.apply`
- `skill.list`
- `skill.read_body`
- `skill.read_resource`
- `graph.query`
