// Splatter - Core Data Contracts
// Shared between Unity package and local service


#nullable enable

using System;
using System.Collections.Generic;

namespace Splatter.Protocol
{
#region Message Envelope

public sealed record MessageEnvelope(
    string Protocol,
    MessageKind Kind,
    string Id,
    string? CorrelationId,
    string? WorkspaceId,
    string? SessionId,
    string Type,
    DateTimeOffset Timestamp,
    object? Payload)
{
    public const string ProtocolVersion = "splatter.v1";

    public static MessageEnvelope Request(string type, object? payload, string? workspaceId = null, string? sessionId = null)
        => new(ProtocolVersion, MessageKind.Request, MessageId.New(), null, workspaceId, sessionId, type, DateTimeOffset.UtcNow, payload);

    public static MessageEnvelope Response(string correlationId, string type, object? payload, string? workspaceId = null)
        => new(ProtocolVersion, MessageKind.Response, MessageId.New(), correlationId, workspaceId, null, type, DateTimeOffset.UtcNow, payload);

    public static MessageEnvelope Event(string type, object? payload, string? workspaceId = null, string? sessionId = null)
        => new(ProtocolVersion, MessageKind.Event, MessageId.New(), null, workspaceId, sessionId, type, DateTimeOffset.UtcNow, payload);
}

#endregion

#region Conversation

public sealed record Conversation(
    string Id,
    string WorkspaceId,
    string? Title,
    string? ProviderId,
    string? ModelId,
    AgentPermissionMode Mode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationTurn> Turns,
    IReadOnlyDictionary<string, object?>? Metadata);

public sealed record ConversationTurn(
    string Id,
    TurnRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    TurnStatus Status,
    IReadOnlyList<ConversationPart> Parts,
    string? LinkedSessionId,
    string? LinkedCheckpointId);

public sealed record ConversationPart(
    ConversationPartType Type,
    object? Content,
    IReadOnlyDictionary<string, object?>? Metadata);

#endregion

#region Agent Session

public sealed record AgentSession(
    string Id,
    string WorkspaceId,
    string ConversationId,
    string ProviderId,
    string? ModelId,
    string? AdapterSessionId,
    AgentPermissionMode Mode,
    AgentSessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CurrentTurnId);

#endregion

#region Model Catalog

public sealed record ModelCatalogEntry(
    string ProviderId,
    string ModelId,
    string DisplayName,
    string? Description,
    IReadOnlyList<Modality> Modalities,
    ModelCapabilities Capabilities,
    ModelLimits? Limits,
    IReadOnlyList<ModelOptionSpec>? Options,
    PricingInfo? Pricing,
    CatalogSource Source,
    DateTimeOffset? FetchedAt,
    string? DeprecationStatus);

public sealed record ModelCapabilities(
    bool Chat = false,
    bool Tools = false,
    bool StructuredOutput = false,
    bool VisionInput = false,
    bool AudioInput = false,
    bool ImageGeneration = false,
    bool ReasoningEffort = false,
    bool Streaming = true);

public sealed record ModelLimits(
    int? ContextTokens,
    int? MaxOutputTokens,
    int? MaxImages,
    int? MaxAudioSeconds);

public sealed record ModelOptionSpec(
    string Id,
    string DisplayName,
    string Kind,
    object? DefaultValue,
    IReadOnlyList<ModelOptionValue>? AllowedValues,
    double? Min,
    double? Max,
    double? Step);

public sealed record ModelOptionValue(
    string Value,
    string Label,
    string? Description);

public sealed record PricingInfo(
    decimal? InputPer1kTokens,
    decimal? OutputPer1kTokens,
    decimal? PerImage,
    string Currency = "USD");

#endregion

#region Tool System

public sealed record ToolSpec(
    string Id,
    string DisplayName,
    string Description,
    ToolCategory Category,
    object InputSchema,
    object? OutputSchema,
    PermissionRequirement Permission,
    IReadOnlyList<SideEffectSpec> SideEffects,
    ToolExecutionSpec Execution,
    ToolConstraints? Constraints);

public sealed record PermissionRequirement(
    PermissionClass Class,
    PermissionRisk Risk,
    string Reason,
    IReadOnlyList<string>? Scopes);

public sealed record SideEffectSpec(
    string Type,
    string Description);

public sealed record ToolExecutionSpec(
    bool RequiresUnityMainThread,
    int TimeoutMs,
    bool Cancellable,
    bool Idempotent);

public sealed record ToolConstraints(
    PathScope PathScope = PathScope.None,
    IReadOnlyList<string>? AllowedExtensions = null,
    NetworkPolicy Network = NetworkPolicy.None,
    int? MaxInputBytes = null,
    int? MaxOutputBytes = null);

public sealed record ToolCall(
    string Id,
    string ToolId,
    object? Arguments,
    ToolCallSource RequestedBy,
    ToolCallStatus Status);

public sealed record ToolCallSource(
    string ProviderId,
    string? ModelId,
    string SessionId,
    string TurnId);

public sealed record ToolResult(
    string ToolCallId,
    bool Ok,
    object? Value,
    string? Summary,
    NormalizedError? Error,
    IReadOnlyList<ArtifactReference>? Artifacts,
    ToolAuditRecord? Audit);

public sealed record ToolAuditRecord(
    string ToolCallId,
    string ToolId,
    string SessionId,
    object? NormalizedArguments,
    IReadOnlyList<string>? ResolvedPaths,
    IReadOnlyList<string>? NetworkHosts,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    ToolCallStatus Status,
    bool SideEffectsCommitted,
    IReadOnlyList<string>? PermissionDecisionIds);

public sealed record ArtifactReference(
    string Id,
    string Type,
    string? Path,
    long? Size,
    string? ContentType,
    string? MimeType = null,
    string? Base64Data = null);

#endregion

#region Permissions

public sealed record PermissionRequest(
    string Id,
    string SessionId,
    string TurnId,
    string ToolCallId,
    PermissionRequirement Requirement,
    PermissionPreview Preview,
    IReadOnlyList<PermissionOption> Options,
    DateTimeOffset? ExpiresAt);

public sealed record PermissionPreview(
    string Title,
    string Summary,
    IReadOnlyList<string>? Paths,
    IReadOnlyList<string>? Assets,
    string? Command,
    string? Diff,
    string? Cost);

public sealed record PermissionOption(
    string Id,
    string Label,
    PermissionOutcome Outcome,
    PermissionScope Scope);

public sealed record PermissionDecision(
    string RequestId,
    PermissionOutcome Outcome,
    PermissionScope Scope,
    PermissionDecisionSource DecidedBy,
    DateTimeOffset DecidedAt,
    string? Reason);

public sealed record PermissionGrant(
    string Id,
    string WorkspaceId,
    string SessionId,
    string ToolId,
    PermissionClass PermissionClass,
    PermissionScope Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

#endregion

#region Errors

public sealed record NormalizedError(
    string Code,
    string Message,
    string? Detail,
    bool Retryable,
    string? SuggestedAction,
    IReadOnlyDictionary<string, string>? RelatedIds);

public static class ErrorCodes
{
    public const string AuthRequired = "auth_required";
    public const string AuthInvalid = "auth_invalid";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string RateLimited = "rate_limited";
    public const string QuotaExceeded = "quota_exceeded";
    public const string NetworkError = "network_error";
    public const string ModelNotFound = "model_not_found";
    public const string ContextTooLarge = "context_too_large";
    public const string ToolPermissionDenied = "tool_permission_denied";
    public const string ToolFailed = "tool_failed";
    public const string ToolTimeout = "tool_timeout";
    public const string UserCancelled = "user_cancelled";
    public const string ServiceDisconnected = "service_disconnected";
    public const string UnityDomainReload = "unity_domain_reload";
    public const string UnityCompileError = "unity_compile_error";
    public const string StorageCorrupt = "storage_corrupt";
    public const string GenerationFailed = "generation_failed";
    public const string UnsupportedCapability = "unsupported_capability";
}

#endregion

#region Checkpoints

public sealed record Checkpoint(
    string Id,
    string WorkspaceId,
    string? SessionId,
    string? TurnId,
    string? ToolCallId,
    CheckpointProviderType ProviderType,
    string Reference,
    IReadOnlyList<string> Paths,
    DateTimeOffset CreatedAt,
    string? Description);

public sealed record RestorePlan(
    string CheckpointId,
    IReadOnlyList<FileRestoreInfo> FilesToRestore,
    IReadOnlyList<RestoreConflict> Conflicts,
    bool CanRestore);

public sealed record FileRestoreInfo(
    string Path,
    string Action,
    long? Size);

public sealed record RestoreConflict(
    string Path,
    string Reason,
    bool UserEditedAfterCheckpoint);

#endregion

#region Generator Jobs

public sealed record GeneratorJob(
    string Id,
    string WorkspaceId,
    GeneratorModality Modality,
    string ProviderId,
    string ModelId,
    string? TargetAssetGuid,
    string? TargetAssetPath,
    string Mode,
    GeneratorJobStatus Status,
    IReadOnlyDictionary<string, object?>? Parameters,
    IReadOnlyList<ArtifactReference>? References,
    QuoteResult? Quote,
    IReadOnlyList<GeneratorResult>? Results,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    NormalizedError? Error);

public sealed record QuoteResult(
    bool Success,
    decimal? EstimatedCost,
    string? Currency,
    string? ErrorMessage);

public sealed record GeneratorResult(
    string Id,
    string SourceUrl,
    string ContentType,
    long Size,
    int? Seed,
    int VariationIndex,
    IReadOnlyDictionary<string, object?>? Metadata,
    string? OutputPath = null);

#endregion

#region Usage

public sealed record UsageInfo(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    decimal? EstimatedCost,
    string? Currency);

#endregion

#region Skills Extended

public sealed record SkillInfo(
    string Name,
    string Description,
    SkillSource Source,
    string SourcePath,
    bool Enabled,
    bool Compatible,
    string? IncompatibilityReason,
    IReadOnlyDictionary<string, string>? RequiredPackages,
    string? RequiredEditorVersion,
    IReadOnlyList<SkillToolRef>? Tools,
    IReadOnlyList<SkillResourceRef>? Resources,
    long BodySize,
    string? Version,
    // Whether the skill is permitted to load. Internal/builtin skills are always
    // allowed; non-internal (User/Project/Package) skills are denied by default
    // until the user explicitly opts in via the allowlist.
    bool Allowed = true);

public sealed record SkillToolRef(
    string Id,
    string DisplayName,
    string Description);

public sealed record SkillResourceRef(
    string Path,
    string ContentType,
    long Size);

public sealed record SkillScanResult(
    SkillScanStatus Status,
    IReadOnlyList<SkillInfo> Skills,
    IReadOnlyList<SkillParseError>? ParseErrors,
    DateTimeOffset ScannedAt,
    // Deterministic duplicate-name resolution surfaced for diagnostics: which
    // source won and which were shadowed for each colliding skill name.
    IReadOnlyList<SkillDuplicateInfo>? Duplicates = null);

public sealed record SkillParseError(
    string Path,
    string Error,
    int? Line);

public sealed record SkillDuplicateInfo(
    string Name,
    SkillSource WinningSource,
    SkillSource ShadowedSource);

#endregion

#region Context Indexing

public sealed record FileInventoryEntry(
    string Path,
    string RelativePath,
    long Size,
    DateTimeOffset ModifiedAt,
    string? ContentHash,
    string Extension,
    bool IsBinary,
    bool IsGenerated,
    int? LineCount);

public sealed record LexicalSearchResult(
    string Path,
    int LineNumber,
    int ColumnStart,
    int ColumnEnd,
    string LineContent,
    IReadOnlyList<string>? ContextBefore,
    IReadOnlyList<string>? ContextAfter,
    double Score);

public sealed record AssetInventoryEntry(
    string Guid,
    string Path,
    string TypeName,
    string MainType,
    IReadOnlyList<string>? Labels,
    long FileSize,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string>? Dependencies,
    bool IsImported);

public sealed record SceneSnapshot(
    string ScenePath,
    string SceneGuid,
    IReadOnlyList<SceneObjectInfo> RootObjects,
    int ObjectCount,
    DateTimeOffset CapturedAt,
    bool IsDirty);

public sealed record SceneObjectInfo(
    int InstanceId,
    string Name,
    string Path,
    bool IsActive,
    int Layer,
    string? Tag,
    bool IsPrefab,
    string? PrefabGuid,
    IReadOnlyList<string> Components,
    IReadOnlyList<SceneObjectInfo>? Children);

public sealed record IndexFreshness(
    string IndexType,
    IndexState State,
    int ItemCount,
    int PendingCount,
    DateTimeOffset? LastFullScan,
    DateTimeOffset? LastIncremental,
    int SchemaVersion,
    IReadOnlyList<string>? StalePaths);

#endregion

#region Dependency Graph

public sealed record GraphNode(
    string Id,
    GraphNodeType NodeType,
    string DisplayName,
    string? Path,
    string? Guid,
    string? TypeName,
    IReadOnlyDictionary<string, object?>? Metadata);

public sealed record GraphEdge(
    string SourceId,
    string TargetId,
    GraphEdgeType EdgeType,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    double Weight = 1.0);

public sealed record DependencyGraph(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    DateTimeOffset GeneratedAt,
    string Scope);

public sealed record GraphQuery(
    string? RootNodeId,
    string Direction,
    int MaxDepth,
    IReadOnlyList<GraphEdgeType>? EdgeTypes,
    IReadOnlyList<GraphNodeType>? NodeTypes);

public sealed record GraphQueryResult(
    DependencyGraph Graph,
    IReadOnlyList<IReadOnlyList<string>>? Paths,
    bool Truncated,
    int TotalNodes);

#endregion

#region Context Packing

public sealed record ContextSource(
    string Id,
    ContextSourceType Type,
    string DisplayName,
    string? Path,
    string? Guid,
    string? Content,
    int TokenCount,
    int Priority,
    bool Truncated,
    string Provenance,
    int? SizeBytes = null,
    IndexFreshnessState Freshness = IndexFreshnessState.Fresh,
    float? LexicalScore = null,
    float? SemanticScore = null);

public sealed record PackedContext(
    IReadOnlyList<ContextSource> Sources,
    IReadOnlyList<OmittedSource>? OmittedSources,
    int UsedTextTokens,
    int BudgetTextTokens,
    int UsedImageBytes,
    int BudgetImageBytes,
    string? OmittedSummary);

public sealed record OmittedSource(
    string Id,
    string DisplayName,
    ContextSourceType Type,
    string Reason,
    int? SizeBytes,
    int? EstimatedTokens);

public sealed record ContextPackingOptions(
    int MaxTokens,
    int ReserveForResponse,
    bool PrioritizeExact,
    bool IncludeSkillMetadata,
    bool DeduplicateContent);

#endregion

#region MCP Support

public sealed record McpServerConfig(
    string Id,
    string DisplayName,
    string Transport,
    string? Command,
    IReadOnlyList<string>? Args,
    string? Url,
    IReadOnlyDictionary<string, string>? Env,
    bool Enabled,
    IReadOnlyList<string>? AutoApproveTools);

public sealed record McpServerState(
    McpServerConfig Config,
    McpConnectionState State,
    IReadOnlyList<McpTool>? Tools,
    string? LastError,
    DateTimeOffset? ConnectedAt);

public sealed record McpTool(
    string Name,
    string? Description,
    object InputSchema,
    McpToolApprovalState ApprovalState,
    PermissionClass RiskClass);

public sealed record McpToolCallRequest(
    string ServerId,
    string ToolName,
    object? Arguments);

public sealed record McpToolCallResult(
    string ServerId,
    string ToolName,
    IReadOnlyList<McpContent> Content,
    bool IsError);

public sealed record McpContent(
    string Type,
    string? Text,
    string? Data,
    string? MimeType);

#endregion

#region CLI Agent Adapters

public sealed record CliAgentConfig(
    string Id,
    string DisplayName,
    string Command,
    IReadOnlyList<string>? Args,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Env,
    IReadOnlyList<string>? EnvAllowlist,
    string Protocol,
    string? HealthCheckCommand,
    string? VersionCommand);

public sealed record CliAgentState(
    CliAgentConfig Config,
    bool IsRunning,
    int? ProcessId,
    string? Version,
    DateTimeOffset? LastHealthCheck,
    string? LastError);

public sealed record CliAgentMessage(
    string Type,
    string? Id,
    object? Payload);

#endregion

#region Generator Extended

public sealed record GeneratorCapabilities(
    GeneratorModality Modality,
    IReadOnlyList<GeneratorModeSpec> Modes,
    IReadOnlyList<string> SupportedFormats,
    int? MaxResolution,
    int? MaxDurationSeconds,
    bool SupportsNegativePrompt,
    bool SupportsSeed,
    bool SupportsReferences);

public sealed record GeneratorModeSpec(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<string>? OptionalInputs);

public sealed record GeneratorHistory(
    string AssetGuid,
    string AssetPath,
    IReadOnlyList<GeneratorJobSummary> Jobs,
    string? AppliedJobId);

public sealed record GeneratorJobSummary(
    string Id,
    GeneratorModality Modality,
    string Mode,
    GeneratorJobStatus Status,
    int ResultCount,
    DateTimeOffset CreatedAt,
    string? Prompt);

public sealed record GeneratorRecoveryInfo(
    string JobId,
    GeneratorJobStatus Status,
    int RecoveredResults,
    int FailedResults,
    IReadOnlyList<RecoveryAction> AvailableActions);

public sealed record GeneratorAssetMetadata(
    DateTimeOffset GeneratedAt,
    string Prompt,
    string? NegativePrompt,
    string ProviderId,
    string ModelId,
    string Mode,
    int? Seed,
    IReadOnlyList<string>? SourceAssets,
    IReadOnlyDictionary<string, object?>? Parameters);

#endregion

#region Workspace and Project

public sealed record WorkspaceInfo(
    string Id,
    string ProjectRoot,
    string ProjectName,
    string UnityVersion,
    string? RenderPipeline,
    string? ScriptingBackend,
    IReadOnlyList<PackageInfo>? InstalledPackages,
    bool HasGit,
    DateTimeOffset AttachedAt);

public sealed record PackageInfo(
    string Name,
    string Version,
    string Source,
    bool IsEmbedded);

public sealed record ProjectOverview(
    WorkspaceInfo Workspace,
    IReadOnlyDictionary<string, int> AssetCounts,
    IReadOnlyList<string> ScenePaths,
    int ScriptCount,
    string? CustomInstructions,
    IReadOnlyList<RecentChange>? RecentChanges);

public sealed record RecentChange(
    string Path,
    FileChangeKind ChangeType,
    DateTimeOffset Timestamp,
    string? Author);

#endregion
}
