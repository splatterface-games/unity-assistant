// Skill Registry Interface

using Splatter.Protocol;

namespace Splatter.Service.Context;

public interface ISkillRegistry
{
    /// <summary>
    /// Get all available skills for a workspace.
    /// </summary>
    Task<SkillScanResult> ScanSkillsAsync(string workspaceId, bool fullRescan, CancellationToken ct);

    /// <summary>
    /// Get cached skill list (without scanning).
    /// </summary>
    Task<IReadOnlyList<SkillInfo>> GetCachedSkillsAsync(string workspaceId, CancellationToken ct);

    /// <summary>
    /// Read the body of a skill file.
    /// </summary>
    Task<(string? Content, bool Truncated, NormalizedError? Error)> ReadSkillBodyAsync(
        string workspaceId, string skillName, CancellationToken ct);

    /// <summary>
    /// Read a resource from a skill.
    /// </summary>
    Task<(string? Content, string? ContentType, bool Truncated, NormalizedError? Error)> ReadSkillResourceAsync(
        string workspaceId, string skillName, string resourcePath, CancellationToken ct);

    /// <summary>
    /// Enable or disable a skill.
    /// </summary>
    Task<bool> SetSkillEnabledAsync(string workspaceId, string skillName, bool enabled, CancellationToken ct);

    /// <summary>
    /// Grant or revoke the opt-in allowlist entry for a non-internal skill.
    /// Internal/builtin skills are always allowed and ignore this setting.
    /// </summary>
    Task<bool> SetSkillAllowedAsync(string workspaceId, string skillName, bool allowed, CancellationToken ct);

    /// <summary>
    /// Validate skill compatibility with current workspace.
    /// </summary>
    Task<SkillValidateResponse> ValidateSkillAsync(string workspaceId, string skillName, CancellationToken ct);

    /// <summary>
    /// Get builtin skills.
    /// </summary>
    IReadOnlyList<SkillInfo> GetBuiltinSkills();
}
