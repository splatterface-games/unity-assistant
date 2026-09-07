// Context Service Interfaces

using Splatter.Protocol;

namespace Splatter.Service.Context;

public interface IContextService
{
    Task<string> BuildSystemPromptAsync(string workspaceId, AgentPermissionMode mode, CancellationToken ct);
    Task<string> BuildContextAsync(string workspaceId, IReadOnlyList<ContextAttachment> attachments, CancellationToken ct);
}
