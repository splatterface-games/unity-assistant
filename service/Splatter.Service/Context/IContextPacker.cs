// Context Packer Interface

using Splatter.Protocol;

namespace Splatter.Service.Context;

public interface IContextPacker
{
    /// <summary>
    /// Pack context sources into a compact representation within budget.
    /// </summary>
    Task<PackedContext> PackContextAsync(ContextPackingRequest request, CancellationToken ct);

    /// <summary>
    /// Estimate token count for content.
    /// </summary>
    int EstimateTokens(string content, string? modelId = null);

    /// <summary>
    /// Get ranking signals for context sources.
    /// </summary>
    IReadOnlyList<ContextRankingSignal> GetRankingSignals(IReadOnlyList<ContextSource> sources, ContextRankingOptions options);

    /// <summary>
    /// Deduplicate context sources by identity and content.
    /// </summary>
    IReadOnlyList<ContextSource> DeduplicateSources(IReadOnlyList<ContextSource> sources);
}

public sealed record ContextPackingRequest(
    string? ModelId,
    int TextTokenBudget,
    int ImageBudgetBytes,
    int ReserveOutputTokens,
    IReadOnlyList<ContextSource> Sources,
    ContextRankingOptions RankingOptions,
    bool IncludeOmittedSummary = true);

public sealed record ContextRankingOptions(
    IReadOnlySet<string>? UserSelectedPaths = null,
    IReadOnlySet<string>? MentionedPaths = null,
    IReadOnlySet<string>? RecentEditPaths = null,
    string? CurrentScenePath = null,
    IReadOnlySet<string>? GraphProximityPaths = null,
    ContextTaskType TaskType = ContextTaskType.General);

public enum ContextTaskType
{
    General,
    Explain,
    Edit,
    Debug,
    Generate,
    Profile,
    Refactor
}

public sealed record ContextRankingSignal(
    string SourceId,
    float Priority,
    IReadOnlyList<string> Signals,
    float LexicalScore,
    float SemanticScore,
    float FreshnessScore,
    float ProximityScore);
