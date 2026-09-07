// Context Packer Implementation

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Context;

public sealed class ContextPacker : IContextPacker
{
    private readonly ILogger<ContextPacker> _logger;
    private readonly IContextIndexer _indexer;

    // Token estimation constants (approximate for most models)
    private const double CharsPerToken = 4.0;
    private const int MaxDownsizeAttempts = 3;

    // Priority weights for ranking
    private static readonly Dictionary<ContextSourceType, float> TypeBasePriority = new()
    {
        [ContextSourceType.SystemInstruction] = 1000f,
        [ContextSourceType.DeveloperInstruction] = 900f,
        [ContextSourceType.UserPrompt] = 850f,
        [ContextSourceType.ExplicitSelection] = 800f,
        [ContextSourceType.CustomInstructions] = 750f,
        [ContextSourceType.ProjectOverview] = 700f,
        [ContextSourceType.GraphContext] = 600f,
        [ContextSourceType.LexicalMatch] = 500f,
        [ContextSourceType.SemanticMatch] = 400f,
        [ContextSourceType.SkillBody] = 350f,
        [ContextSourceType.SkillResource] = 300f,
        [ContextSourceType.FileContent] = 250f,
        [ContextSourceType.AssetContent] = 200f,
        [ContextSourceType.SceneSnapshot] = 150f,
        [ContextSourceType.ConsoleLog] = 100f,
        [ContextSourceType.Image] = 50f
    };

    public ContextPacker(ILogger<ContextPacker> logger, IContextIndexer indexer)
    {
        _logger = logger;
        _indexer = indexer;
    }

    public async Task<PackedContext> PackContextAsync(ContextPackingRequest request, CancellationToken ct)
    {
        // Deduplicate sources first
        var sources = DeduplicateSources(request.Sources).ToList();

        // Get ranking signals
        var signals = GetRankingSignals(sources, request.RankingOptions);
        var signalMap = signals.ToDictionary(s => s.SourceId);

        // Sort by priority (highest first)
        sources = sources
            .OrderByDescending(s => signalMap.TryGetValue(s.Id, out var sig) ? sig.Priority : 0f)
            .ToList();

        var packedSources = new List<ContextSource>();
        var omittedSources = new List<OmittedSource>();

        int usedTextTokens = 0;
        int usedImageBytes = 0;
        int availableTextTokens = request.TextTokenBudget - request.ReserveOutputTokens;

        foreach (var source in sources)
        {
            if (source.Type == ContextSourceType.Image)
            {
                // Handle image budget separately
                var imageSize = source.SizeBytes ?? 0;
                if (usedImageBytes + imageSize <= request.ImageBudgetBytes)
                {
                    packedSources.Add(source);
                    usedImageBytes += imageSize;
                }
                else
                {
                    omittedSources.Add(new OmittedSource(
                        source.Id,
                        source.DisplayName,
                        source.Type,
                        "Image budget exceeded",
                        source.SizeBytes,
                        null));
                }
                continue;
            }

            // Estimate tokens for this source
            var content = source.Content ?? "";
            var tokens = EstimateTokens(content, request.ModelId);

            if (usedTextTokens + tokens <= availableTextTokens)
            {
                // Fits completely
                packedSources.Add(source);
                usedTextTokens += tokens;
            }
            else
            {
                // Try downsizing
                var remainingBudget = availableTextTokens - usedTextTokens;
                var downsized = TryDownsize(source, remainingBudget, request.ModelId);

                if (downsized != null)
                {
                    packedSources.Add(downsized);
                    usedTextTokens += EstimateTokens(downsized.Content ?? "", request.ModelId);
                    omittedSources.Add(new OmittedSource(
                        source.Id,
                        source.DisplayName,
                        source.Type,
                        "Truncated to fit budget",
                        source.SizeBytes,
                        tokens));
                }
                else
                {
                    omittedSources.Add(new OmittedSource(
                        source.Id,
                        source.DisplayName,
                        source.Type,
                        "Exceeded token budget",
                        source.SizeBytes,
                        tokens));
                }
            }
        }

        // Generate omitted context summary if requested
        string? omittedSummary = null;
        if (request.IncludeOmittedSummary && omittedSources.Count > 0)
        {
            omittedSummary = GenerateOmittedSummary(omittedSources);
        }

        _logger.LogDebug(
            "Packed context: {PackedCount} sources ({UsedTokens} tokens), {OmittedCount} omitted",
            packedSources.Count, usedTextTokens, omittedSources.Count);

        return new PackedContext(
            packedSources,
            omittedSources,
            usedTextTokens,
            availableTextTokens,
            usedImageBytes,
            request.ImageBudgetBytes,
            omittedSummary);
    }

    public int EstimateTokens(string content, string? modelId = null)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        // Use model-specific estimation if available
        var charsPerToken = modelId switch
        {
            string id when id.Contains("gpt-4") => 4.0,
            string id when id.Contains("gpt-3.5") => 4.0,
            string id when id.Contains("claude") => 3.5,
            string id when id.Contains("gemini") => 4.0,
            _ => CharsPerToken
        };

        return (int)Math.Ceiling(content.Length / charsPerToken);
    }

    public IReadOnlyList<ContextRankingSignal> GetRankingSignals(
        IReadOnlyList<ContextSource> sources,
        ContextRankingOptions options)
    {
        var signals = new List<ContextRankingSignal>();

        foreach (var source in sources)
        {
            var signalList = new List<string>();
            float priority = TypeBasePriority.GetValueOrDefault(source.Type, 0f);

            // User selection boost
            if (options.UserSelectedPaths?.Contains(source.Path ?? "") == true)
            {
                priority += 200f;
                signalList.Add("user-selected");
            }

            // Direct mention boost
            if (options.MentionedPaths?.Contains(source.Path ?? "") == true)
            {
                priority += 150f;
                signalList.Add("mentioned-in-prompt");
            }

            // Recent edit boost
            if (options.RecentEditPaths?.Contains(source.Path ?? "") == true)
            {
                priority += 100f;
                signalList.Add("recently-edited");
            }

            // Current scene boost
            if (!string.IsNullOrEmpty(options.CurrentScenePath) &&
                source.Path?.StartsWith(options.CurrentScenePath) == true)
            {
                priority += 75f;
                signalList.Add("current-scene");
            }

            // Graph proximity boost
            if (options.GraphProximityPaths?.Contains(source.Path ?? "") == true)
            {
                priority += 50f;
                signalList.Add("graph-proximity");
            }

            // Task-specific adjustments
            priority = AdjustForTaskType(priority, source, options.TaskType, signalList);

            // Freshness score (higher is fresher)
            float freshnessScore = source.Freshness switch
            {
                IndexFreshnessState.Fresh => 1.0f,
                IndexFreshnessState.Refreshing => 0.9f,
                IndexFreshnessState.Stale => 0.5f,
                IndexFreshnessState.Partial => 0.3f,
                _ => 0.0f
            };

            signals.Add(new ContextRankingSignal(
                source.Id,
                priority,
                signalList,
                source.LexicalScore ?? 0f,
                source.SemanticScore ?? 0f,
                freshnessScore,
                options.GraphProximityPaths?.Contains(source.Path ?? "") == true ? 1.0f : 0f));
        }

        return signals;
    }

    public IReadOnlyList<ContextSource> DeduplicateSources(IReadOnlyList<ContextSource> sources)
    {
        var seen = new HashSet<string>();
        var contentHashes = new HashSet<string>();
        var result = new List<ContextSource>();

        foreach (var source in sources)
        {
            // Check by ID first
            if (seen.Contains(source.Id))
                continue;

            // Check by path if available
            if (!string.IsNullOrEmpty(source.Path) && seen.Contains($"path:{source.Path}"))
                continue;

            // Check by content hash for non-trivial content
            if (!string.IsNullOrEmpty(source.Content) && source.Content.Length > 100)
            {
                var hash = ComputeContentHash(source.Content);
                if (contentHashes.Contains(hash))
                    continue;
                contentHashes.Add(hash);
            }

            seen.Add(source.Id);
            if (!string.IsNullOrEmpty(source.Path))
                seen.Add($"path:{source.Path}");

            result.Add(source);
        }

        return result;
    }

    private float AdjustForTaskType(float priority, ContextSource source, ContextTaskType taskType, List<string> signals)
    {
        switch (taskType)
        {
            case ContextTaskType.Edit:
                // For edits, prioritize exact file content and code
                if (source.Type == ContextSourceType.FileContent)
                {
                    priority += 100f;
                    signals.Add("edit-task-file-boost");
                }
                break;

            case ContextTaskType.Debug:
                // For debugging, prioritize console logs and stack traces
                if (source.Type == ContextSourceType.ConsoleLog)
                {
                    priority += 150f;
                    signals.Add("debug-task-console-boost");
                }
                break;

            case ContextTaskType.Explain:
                // For explanations, prioritize project overview and graph context
                if (source.Type is ContextSourceType.ProjectOverview or ContextSourceType.GraphContext)
                {
                    priority += 50f;
                    signals.Add("explain-task-overview-boost");
                }
                break;

            case ContextTaskType.Refactor:
                // For refactoring, prioritize graph context and dependencies
                if (source.Type == ContextSourceType.GraphContext)
                {
                    priority += 100f;
                    signals.Add("refactor-task-graph-boost");
                }
                break;

            case ContextTaskType.Generate:
                // For generation, prioritize semantic matches and examples
                if (source.Type == ContextSourceType.SemanticMatch)
                {
                    priority += 75f;
                    signals.Add("generate-task-semantic-boost");
                }
                break;

            case ContextTaskType.Profile:
                // For profiling, prioritize skill bodies
                if (source.Type == ContextSourceType.SkillBody)
                {
                    priority += 100f;
                    signals.Add("profile-task-skill-boost");
                }
                break;
        }

        return priority;
    }

    private ContextSource? TryDownsize(ContextSource source, int targetTokens, string? modelId)
    {
        if (targetTokens <= 0)
            return null;

        var content = source.Content ?? "";
        if (string.IsNullOrEmpty(content))
            return null;

        // Try different downsizing strategies
        for (int attempt = 0; attempt < MaxDownsizeAttempts; attempt++)
        {
            var factor = 1.0 / (attempt + 2); // 0.5, 0.33, 0.25
            var targetChars = (int)(targetTokens * CharsPerToken * factor);

            if (targetChars < 100)
                break;

            var downsized = DownsizeContent(content, targetChars, source.Type);
            var tokens = EstimateTokens(downsized, modelId);

            if (tokens <= targetTokens)
            {
                return source with
                {
                    Content = downsized,
                    Truncated = true,
                    SizeBytes = Encoding.UTF8.GetByteCount(downsized)
                };
            }
        }

        return null;
    }

    private string DownsizeContent(string content, int targetChars, ContextSourceType type)
    {
        if (content.Length <= targetChars)
            return content;

        switch (type)
        {
            case ContextSourceType.ConsoleLog:
                // For logs, try removing stack traces first
                return DownsizeLogContent(content, targetChars);

            case ContextSourceType.FileContent:
                // For files, keep beginning and end
                return DownsizeFileContent(content, targetChars);

            case ContextSourceType.SceneSnapshot:
                // For scenes, try to keep hierarchy structure
                return DownsizeSceneContent(content, targetChars);

            default:
                // Default: truncate with ellipsis
                return content[..(targetChars - 20)] + "\n...[truncated]...";
        }
    }

    private string DownsizeLogContent(string content, int targetChars)
    {
        var lines = content.Split('\n');
        var result = new StringBuilder();
        var stackTracePattern = new[] { "  at ", "   at ", "\tat " };

        foreach (var line in lines)
        {
            // Skip stack trace lines first
            if (stackTracePattern.Any(p => line.StartsWith(p)))
                continue;

            if (result.Length + line.Length + 1 > targetChars)
            {
                result.AppendLine("...[stack traces and additional logs truncated]...");
                break;
            }
            result.AppendLine(line);
        }

        return result.ToString();
    }

    private string DownsizeFileContent(string content, int targetChars)
    {
        // Keep first 60% and last 30% of content, with gap indicator
        var firstPart = (int)(targetChars * 0.6);
        var lastPart = (int)(targetChars * 0.3);
        var gap = targetChars - firstPart - lastPart;

        if (gap < 50)
        {
            // Not enough room for a useful gap indicator
            return content[..targetChars];
        }

        var beginning = content[..firstPart];
        var ending = content[^lastPart..];
        var skippedLines = content[firstPart..^lastPart].Count(c => c == '\n');

        return $"{beginning}\n\n... [{skippedLines} lines omitted] ...\n\n{ending}";
    }

    private string DownsizeSceneContent(string content, int targetChars)
    {
        var lines = content.Split('\n');
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            // Track indentation depth
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            // Keep root and first-level items
            if (indent <= 4)
            {
                if (result.Length + line.Length + 1 > targetChars - 50)
                {
                    result.AppendLine("...[additional objects truncated]...");
                    break;
                }
                result.AppendLine(line);
            }
        }

        return result.ToString();
    }

    private string GenerateOmittedSummary(IReadOnlyList<OmittedSource> omitted)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Omitted Context Summary ---");

        var byType = omitted.GroupBy(o => o.Type).OrderByDescending(g => g.Count());

        foreach (var group in byType)
        {
            var totalTokens = group.Sum(o => o.EstimatedTokens ?? 0);
            sb.AppendLine($"- {group.Key}: {group.Count()} items (~{totalTokens} tokens)");

            // List first few items
            foreach (var item in group.Take(3))
            {
                sb.AppendLine($"  • {item.DisplayName}: {item.Reason}");
            }

            if (group.Count() > 3)
            {
                sb.AppendLine($"  • ... and {group.Count() - 3} more");
            }
        }

        return sb.ToString();
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16];
    }
}
