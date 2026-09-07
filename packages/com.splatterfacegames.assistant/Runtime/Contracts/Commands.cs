// Splatter - Command Request/Response Types
// Protocol commands between Unity and Service


#nullable enable

using System;
using System.Collections.Generic;

namespace Splatter.Protocol
{
#region Service Commands

public sealed record ServiceInfoRequest();

public sealed record ServiceInfoResponse(
    string Version,
    string Protocol,
    IReadOnlyList<string> Capabilities);

public sealed record EditorAttachRequest(
    string WorkspaceId,
    string ProjectRoot,
    string EditorInstanceId,
    string UnityVersion,
    string PackageVersion,
    string Token,
    IReadOnlyList<ToolSpec>? ToolSpecs);

public sealed record EditorAttachResponse(
    bool Success,
    string WorkspaceId,
    NormalizedError? Error);

public sealed record EditorDetachRequest(
    string EditorInstanceId,
    string? Reason);

#endregion

#region Provider Commands

public sealed record ProviderListRequest();

public sealed record ProviderListResponse(
    IReadOnlyList<ProviderInfo> Providers);

public sealed record ProviderInfo(
    string Id,
    string DisplayName,
    ProviderKind Kind,
    AuthStatus AuthStatus,
    IReadOnlyList<string> Capabilities);

public sealed record ProviderAuthStatusRequest(
    string ProviderId);

public sealed record ProviderAuthStatusResponse(
    string ProviderId,
    AuthStatus Status,
    string? Message);

public sealed record ProviderAuthConfigureRequest(
    string ProviderId,
    IReadOnlyDictionary<string, string> Credentials);

public sealed record ProviderAuthConfigureResponse(
    string ProviderId,
    AuthStatus Status,
    NormalizedError? Error);

public sealed record ProviderValidateRequest(
    string ProviderId);

public sealed record ProviderValidateResponse(
    string ProviderId,
    bool Valid,
    string? Message,
    NormalizedError? Error);

#endregion

#region Model Commands

public sealed record ModelListRequest(
    string? ProviderId,
    Modality? Modality,
    bool Refresh = false);

public sealed record ModelListResponse(
    IReadOnlyList<ModelCatalogEntry> Models,
    bool FromCache,
    DateTimeOffset? FetchedAt);

public sealed record ModelRefreshRequest(
    string? ProviderId);

public sealed record ModelRefreshResponse(
    bool Success,
    int ModelsUpdated,
    NormalizedError? Error);

#endregion

#region Conversation Commands

public sealed record ConversationCreateRequest(
    string? Title,
    string? ProviderId,
    string? ModelId,
    AgentPermissionMode Mode = AgentPermissionMode.AskBeforeWrite);

public sealed record ConversationCreateResponse(
    Conversation Conversation);

public sealed record ConversationListRequest(
    int Limit = 50,
    int Offset = 0);

public sealed record ConversationListResponse(
    IReadOnlyList<Conversation> Conversations,
    int Total);

public sealed record ConversationLoadRequest(
    string ConversationId);

public sealed record ConversationLoadResponse(
    Conversation? Conversation,
    NormalizedError? Error);

public sealed record ConversationDeleteRequest(
    string ConversationId);

public sealed record ConversationDeleteResponse(
    bool Success);

#endregion

#region Agent Commands

public sealed record AgentStartRequest(
    string ConversationId,
    string ProviderId,
    string? ModelId,
    AgentPermissionMode Mode);

public sealed record AgentStartResponse(
    string SessionId,
    NormalizedError? Error);

public sealed record AgentPromptRequest(
    string SessionId,
    string Text,
    IReadOnlyList<string>? ContextIds,
    IReadOnlyList<ContextAttachment>? Attachments);

public sealed record ContextAttachment(
    string Type,
    string DisplayName,
    object? Content,
    string? Path,
    string? Guid,
    IReadOnlyDictionary<string, object?>? Metadata);

public sealed record AgentPromptResponse(
    string TurnId,
    NormalizedError? Error);

public sealed record AgentCancelRequest(
    string SessionId,
    string? Reason);

public sealed record AgentCancelResponse(
    bool Success);

public sealed record AgentSetModeRequest(
    string SessionId,
    AgentPermissionMode Mode);

public sealed record AgentSetModelRequest(
    string SessionId,
    string ProviderId,
    string ModelId);

#endregion

#region Tool Commands

public sealed record ToolListRequest();

public sealed record ToolListResponse(
    IReadOnlyList<ToolSpec> Tools);

public sealed record ToolInvokeRequest(
    string ToolId,
    object? Arguments,
    string? SessionId);

public sealed record ToolInvokeResponse(
    ToolResult Result);

#endregion

#region Permission Commands

public sealed record PermissionRespondRequest(
    string RequestId,
    string OptionId);

public sealed record PermissionRespondResponse(
    PermissionDecision Decision);

#endregion

#region Checkpoint Commands

public sealed record CheckpointCreateRequest(
    IReadOnlyList<string> Paths,
    string? Description,
    string? SessionId,
    string? TurnId);

public sealed record CheckpointCreateResponse(
    Checkpoint Checkpoint,
    NormalizedError? Error);

public sealed record CheckpointRestoreRequest(
    string CheckpointId,
    bool Preview = true);

public sealed record CheckpointRestoreResponse(
    RestorePlan? Plan,
    bool Restored,
    NormalizedError? Error);

#endregion

#region Index Commands

public sealed record IndexStatusRequest(
    string? IndexType);

public sealed record IndexStatusResponse(
    IReadOnlyList<IndexInfo> Indexes);

public sealed record IndexInfo(
    string Type,
    IndexState State,
    int? ItemCount,
    int? PendingCount,
    DateTimeOffset? LastRefresh,
    int SchemaVersion);

public sealed record IndexRefreshRequest(
    string? IndexType,
    bool FullRebuild = false);

public sealed record IndexRefreshResponse(
    bool Started,
    NormalizedError? Error);

#endregion

#region Skill Commands

public sealed record SkillListRequest();

public sealed record SkillListResponse(
    IReadOnlyList<SkillMetadata> Skills,
    string ScanStatus);

public sealed record SkillMetadata(
    string Name,
    string Description,
    string Source,
    bool Enabled,
    IReadOnlyDictionary<string, string>? RequiredPackages,
    string? RequiredEditorVersion,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<string>? Resources);

public sealed record SkillReadBodyRequest(
    string SkillName);

public sealed record SkillReadBodyResponse(
    string? Content,
    bool Truncated,
    NormalizedError? Error);

public sealed record SkillReadResourceRequest(
    string SkillName,
    string ResourcePath);

public sealed record SkillReadResourceResponse(
    string? Content,
    string? ContentType,
    bool Truncated,
    NormalizedError? Error);

#endregion

#region Generator Commands

public sealed record GeneratorQuoteRequest(
    GeneratorModality Modality,
    string ProviderId,
    string ModelId,
    string Mode,
    IReadOnlyDictionary<string, object?>? Parameters);

public sealed record GeneratorQuoteResponse(
    QuoteResult Quote);

public sealed record GeneratorSubmitRequest(
    GeneratorModality Modality,
    string ProviderId,
    string ModelId,
    string Mode,
    string? TargetAssetGuid,
    string? TargetAssetPath,
    IReadOnlyDictionary<string, object?>? Parameters,
    IReadOnlyList<ArtifactReference>? References);

public sealed record GeneratorSubmitResponse(
    GeneratorJob Job,
    NormalizedError? Error);

public sealed record GeneratorCancelRequest(
    string JobId);

public sealed record GeneratorResumeRequest(
    string JobId);

public sealed record GeneratorDiscardRecoveryRequest(
    string JobId);

#endregion

#region Diagnostics Commands

public sealed record DiagnosticsExportRequest(
    IReadOnlyList<string>? IncludeSessionIds,
    bool IncludeFullPayloads = false);

public sealed record DiagnosticsExportResponse(
    string Path,
    long Size);

#endregion

#region MCP Commands

public sealed record McpListServersRequest();

public sealed record McpListServersResponse(
    IReadOnlyList<McpServerState> Servers);

public sealed record McpAddServerRequest(
    McpServerConfig Config);

public sealed record McpAddServerResponse(
    bool Success,
    NormalizedError? Error);

public sealed record McpRemoveServerRequest(
    string ServerId);

public sealed record McpConnectRequest(
    string ServerId);

public sealed record McpConnectResponse(
    McpServerState State,
    NormalizedError? Error);

public sealed record McpDisconnectRequest(
    string ServerId);

public sealed record McpCallToolRequest(
    string ServerId,
    string ToolName,
    object? Arguments);

public sealed record McpCallToolResponse(
    McpToolCallResult Result,
    NormalizedError? Error);

public sealed record McpApproveToolRequest(
    string ServerId,
    string ToolName,
    bool Approved);

#endregion

#region CLI Agent Commands

public sealed record CliAgentListRequest();

public sealed record CliAgentListResponse(
    IReadOnlyList<CliAgentState> Agents);

public sealed record CliAgentAddRequest(
    CliAgentConfig Config);

public sealed record CliAgentAddResponse(
    bool Success,
    NormalizedError? Error);

public sealed record CliAgentRemoveRequest(
    string AgentId);

public sealed record CliAgentStartRequest(
    string AgentId);

public sealed record CliAgentStartResponse(
    CliAgentState State,
    NormalizedError? Error);

public sealed record CliAgentStopRequest(
    string AgentId);

public sealed record CliAgentHealthCheckRequest(
    string AgentId);

public sealed record CliAgentHealthCheckResponse(
    bool Healthy,
    string? Version,
    NormalizedError? Error);

#endregion

#region Graph Commands

public sealed record GraphQueryRequest(
    GraphQuery Query);

public sealed record GraphQueryResponse(
    GraphQueryResult Result,
    NormalizedError? Error);

public sealed record GraphRefreshRequest(
    string? Scope,
    bool FullRebuild = false);

public sealed record GraphRefreshResponse(
    bool Started,
    NormalizedError? Error);

public sealed record GraphDependentsRequest(
    string NodeId,
    int MaxDepth = 3);

public sealed record GraphDependentsResponse(
    IReadOnlyList<GraphNode> Dependents,
    NormalizedError? Error);

public sealed record GraphDependenciesRequest(
    string NodeId,
    int MaxDepth = 3);

public sealed record GraphDependenciesResponse(
    IReadOnlyList<GraphNode> Dependencies,
    NormalizedError? Error);

#endregion

#region Context Commands

public sealed record ContextPackRequest(
    string SessionId,
    IReadOnlyList<ContextAttachment> Attachments,
    ContextPackingOptions? Options);

public sealed record ContextPackResponse(
    PackedContext Context,
    NormalizedError? Error);

public sealed record ContextSearchRequest(
    string Query,
    string? Scope,
    int MaxResults = 50,
    bool IncludeContent = false);

public sealed record ContextSearchResponse(
    IReadOnlyList<LexicalSearchResult> Results,
    IndexFreshness Freshness,
    NormalizedError? Error);

public sealed record FileInventoryRequest(
    string? Path,
    string? Pattern,
    int MaxDepth = 10,
    bool IncludeHidden = false);

public sealed record FileInventoryResponse(
    IReadOnlyList<FileInventoryEntry> Files,
    bool Truncated,
    int TotalCount);

public sealed record AssetSearchRequest(
    string? Query,
    string? Type,
    IReadOnlyList<string>? Labels,
    string? Path,
    int MaxResults = 50);

public sealed record AssetSearchResponse(
    IReadOnlyList<AssetInventoryEntry> Assets,
    bool Truncated,
    int TotalCount);

public sealed record SceneSnapshotRequest(
    string? ScenePath,
    int MaxDepth = 5);

public sealed record SceneSnapshotResponse(
    SceneSnapshot? Snapshot,
    NormalizedError? Error);

#endregion

#region Workspace Commands

public sealed record WorkspaceInfoRequest();

public sealed record WorkspaceInfoResponse(
    WorkspaceInfo? Workspace,
    NormalizedError? Error);

public sealed record ProjectOverviewRequest(
    bool IncludeRecentChanges = true);

public sealed record ProjectOverviewResponse(
    ProjectOverview Overview,
    NormalizedError? Error);

public sealed record CustomInstructionsGetRequest();

public sealed record CustomInstructionsGetResponse(
    string? Instructions);

public sealed record CustomInstructionsSaveRequest(
    string? Instructions);

public sealed record CustomInstructionsSaveResponse(
    bool Success);

#endregion

#region Extended Skill Commands

public sealed record SkillEnableRequest(
    string SkillName,
    bool Enabled);

public sealed record SkillEnableResponse(
    bool Success,
    NormalizedError? Error);

public sealed record SkillScanRequest(
    bool FullRescan = false);

public sealed record SkillScanResponse(
    SkillScanResult Result,
    NormalizedError? Error);

public sealed record SkillValidateRequest(
    string SkillName);

public sealed record SkillValidateResponse(
    bool Compatible,
    IReadOnlyDictionary<string, string>? MissingPackages,
    string? EditorVersionIssue);

#endregion

#region Generator Extended Commands

public sealed record GeneratorHistoryRequest(
    string? AssetGuid,
    GeneratorModality? Modality,
    int Limit = 20);

public sealed record GeneratorHistoryResponse(
    IReadOnlyList<GeneratorHistory> History);

public sealed record GeneratorRecoveryListRequest();

public sealed record GeneratorRecoveryListResponse(
    IReadOnlyList<GeneratorRecoveryInfo> Recoverable);

public sealed record GeneratorApplyRequest(
    string JobId,
    string? ResultId,
    string? TargetAssetPath);

public sealed record GeneratorApplyResponse(
    string? AppliedPath,
    NormalizedError? Error);

public sealed record GeneratorCapabilitiesRequest(
    GeneratorModality Modality,
    string? ProviderId);

public sealed record GeneratorCapabilitiesResponse(
    IReadOnlyList<GeneratorCapabilities> Capabilities);

#endregion

#region Tool Execution Bridge Commands

public sealed record ToolExecutionRequest(
    string ToolCallId,
    string ToolId,
    object? Arguments,
    string SessionId);

public sealed record ToolExecutionResponse(
    ToolResult Result);

public sealed record ToolExecutionProgressEvent(
    string ToolCallId,
    double Progress,
    string? Message);

#endregion
}
