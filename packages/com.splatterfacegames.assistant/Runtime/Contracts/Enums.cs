// Splatter - Core Enumerations
// Shared between Unity package and local service

#nullable enable

using System;

namespace Splatter.Protocol
{
public enum MessageKind
{
    Request,
    Response,
    Event,
    Cancel
}

public enum AgentPermissionMode
{
    ReadOnly,
    AskBeforeWrite,
    WorkspaceWrite,
    FullAuto
}

public enum AgentSessionStatus
{
    Starting,
    Ready,
    Running,
    Cancelling,
    Cancelled,
    Failed,
    Completed
}

public enum TurnRole
{
    User,
    Assistant,
    Tool,
    System
}

public enum TurnStatus
{
    Pending,
    Streaming,
    Complete,
    Cancelled,
    Failed
}

public enum ToolCallStatus
{
    Requested,
    AwaitingPermission,
    Running,
    Complete,
    Denied,
    Failed,
    Cancelled
}

public enum PermissionClass
{
    ReadProject,
    ReadExternal,
    WriteProject,
    WriteExternal,
    DeleteProject,
    ExecuteShell,
    ExecuteUnityCode,
    Network,
    PackageManage,
    SceneMutation,
    ScreenCapture,
    CredentialAccess,
    McpExternal,
    GenerationSpend,
    UserInteraction
}

public enum PermissionRisk
{
    Low,
    Medium,
    High,
    Critical
}

public enum PermissionOutcome
{
    Allowed,
    Denied
}

public enum PermissionScope
{
    Once,
    Session,
    Workspace,
    Global
}

public enum PermissionDecisionSource
{
    User,
    Policy,
    Mode,
    Timeout
}

public enum ToolCategory
{
    ProjectRead,
    AssetRead,
    SceneRead,
    CodeEdit,
    UnityMutation,
    Shell,
    Package,
    Network,
    Generator,
    Mcp,
    Skill,
    UserInteraction
}

public enum PathScope
{
    None,
    ProjectRoot,
    AssetsOnly,
    AssetsOrPackages,
    SpecificSettingsFile,
    ExternalRead
}

public enum NetworkPolicy
{
    None,
    ProviderOnly,
    ExplicitHostAllowed,
    UnityRegistry,
    AnyAfterPrompt
}

public enum ProviderKind
{
    Api,
    Cli,
    Local,
    Broker,
    Custom
}

public enum CatalogSource
{
    Static,
    ProviderApi,
    User,
    Broker,
    Cache
}

public enum Modality
{
    Text,
    Image,
    Audio,
    Video,
    Mesh,
    Animation,
    Embedding
}

public enum GeneratorJobStatus
{
    Draft,
    Quoted,
    Submitted,
    Queued,
    Running,
    Downloading,
    Complete,
    Recoverable,
    Failed,
    Cancelled
}

public enum GeneratorModality
{
    Image,
    MaterialPbr,
    Mesh,
    Sound,
    Animation
}

public enum IndexState
{
    Missing,
    Building,
    Fresh,
    Stale,
    Refreshing,
    Partial,
    Corrupt,
    Disabled
}

public enum FileChangeKind
{
    Created,
    Modified,
    Deleted,
    Moved
}

public enum CheckpointProviderType
{
    Git,
    Snapshot,
    UnityAsset
}

public enum AuthStatus
{
    NotConfigured,
    Valid,
    Invalid,
    Expired,
    RateLimited
}

public enum ConversationPartType
{
    Text,
    Markdown,
    Code,
    FileReference,
    AssetReference,
    ToolCall,
    ToolResult,
    Diff,
    Plan,
    Error,
    PermissionRequest,
    GeneratedAsset
}

public enum ThoughtVisibility
{
    Hidden,
    Summary,
    Full
}

public enum PlanItemStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped
}

public enum SkillSource
{
    Project,
    User,
    Builtin,
    Package
}

public enum SkillScanStatus
{
    Fresh,
    Partial,
    Scanning,
    Invalid
}

public enum GraphNodeType
{
    Project,
    Scene,
    Asset,
    AssetType,
    Script,
    Prefab,
    Material,
    Texture,
    AudioClip,
    AnimationClip,
    Package,
    Tool,
    ToolCategory
}

public enum GraphEdgeType
{
    DirectDependency,
    SceneDependency,
    AssetReferencedByScene,
    Inheritance,
    InterfaceImplementation,
    TypeUsage,
    Declaration,
    PackageDependency,
    ComponentReference
}

public enum ContextSourceType
{
    SystemInstruction,
    DeveloperInstruction,
    UserPrompt,
    ExplicitSelection,
    CustomInstructions,
    ProjectOverview,
    GraphContext,
    LexicalMatch,
    SemanticMatch,
    SkillBody,
    SkillResource,
    FileContent,
    AssetContent,
    SceneSnapshot,
    ConsoleLog,
    Image
}

public enum IndexFreshnessState
{
    Missing,
    Building,
    Fresh,
    Stale,
    Refreshing,
    Partial,
    Corrupt,
    Disabled
}

public enum GeneratorMode
{
    Generate,
    Transform,
    Upscale,
    Edit,
    Remove,
    Recolor,
    Retopology,
    Texture,
    Rig,
    TrimLoop
}

public enum AssetImportType
{
    Texture,
    Sprite,
    Cubemap,
    Material,
    TerrainLayer,
    Prefab,
    Model,
    AudioClip,
    AnimationClip
}

public enum RecoveryAction
{
    Resume,
    Delete,
    Skip
}

public enum McpConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

public enum McpToolApprovalState
{
    Pending,
    Approved,
    Denied
}
}
