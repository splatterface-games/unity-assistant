// Generator Service Interface

using Splatter.Protocol;

namespace Splatter.Service.Generators;

public interface IGeneratorService
{
    /// <summary>
    /// Get a quote for a generation job.
    /// </summary>
    Task<QuoteResult> GetQuoteAsync(GeneratorQuoteRequest request, CancellationToken ct);

    /// <summary>
    /// Submit a generation job.
    /// </summary>
    Task<GeneratorJob> SubmitJobAsync(string workspaceId, GeneratorSubmitRequest request, CancellationToken ct);

    /// <summary>
    /// Cancel a generation job.
    /// </summary>
    Task<bool> CancelJobAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Resume a recoverable job.
    /// </summary>
    Task<GeneratorJob?> ResumeJobAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Discard recovery data for a job.
    /// </summary>
    Task<bool> DiscardRecoveryAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Get job status.
    /// </summary>
    Task<GeneratorJob?> GetJobAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Get history for an asset.
    /// </summary>
    Task<GeneratorHistory?> GetHistoryAsync(string workspaceId, string assetGuid, CancellationToken ct);

    /// <summary>
    /// Get all recoverable jobs.
    /// </summary>
    Task<IReadOnlyList<GeneratorRecoveryInfo>> GetRecoverableJobsAsync(string workspaceId, CancellationToken ct);

    /// <summary>
    /// Get capabilities for a modality/provider.
    /// </summary>
    Task<IReadOnlyList<GeneratorCapabilities>> GetCapabilitiesAsync(GeneratorModality modality, string? providerId, CancellationToken ct);

    /// <summary>
    /// Apply a result to an asset.
    /// </summary>
    Task<string?> ApplyResultAsync(string workspaceId, GeneratorApplyRequest request, CancellationToken ct);

    /// <summary>
    /// Event fired when a job is updated.
    /// </summary>
    event EventHandler<GeneratorJob>? JobUpdated;
}
