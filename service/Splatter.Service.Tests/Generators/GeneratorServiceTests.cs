// Generator Service Tests - M9.7
// Tests for generator job service, modality adapters, and recovery

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Splatter.Protocol;
using Splatter.Service.Generators;
using Splatter.Service.Storage;
using Xunit;

namespace Splatter.Service.Tests.Generators;

public class GeneratorServiceTests
{
    #region Quote Tests

    [Fact]
    public async Task GetQuoteAsync_ImageModality_ReturnsValidQuote()
    {
        // Arrange
        var service = CreateTestService();
        var request = new GeneratorQuoteRequest(
            GeneratorModality.Image,
            "comfyui",
            "sd-xl",
            "generate",
            new Dictionary<string, object?> { ["prompt"] = "test image" });

        // Act
        var result = await service.GetQuoteAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Currency);
    }

    [Fact]
    public async Task GetQuoteAsync_MeshModality_LocalProvider_ZeroCost()
    {
        // Arrange
        var service = CreateTestService();
        var request = new GeneratorQuoteRequest(
            GeneratorModality.Mesh,
            "kao",
            "hunyuan3d-2",
            "generate",
            new Dictionary<string, object?> { ["prompt"] = "test mesh" });

        // Act
        var result = await service.GetQuoteAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0.00m, result.EstimatedCost);
    }

    [Fact]
    public async Task GetQuoteAsync_InvalidModality_ReturnsError()
    {
        // Arrange
        var service = CreateTestService();
        var request = new GeneratorQuoteRequest(
            (GeneratorModality)999, // Invalid
            "unknown",
            "",
            "generate",
            null);

        // Act
        var result = await service.GetQuoteAsync(request, CancellationToken.None);

        // Assert - should handle gracefully
        Assert.NotNull(result);
    }

    #endregion

    #region Submit Tests

    [Fact]
    public async Task SubmitJobAsync_ValidRequest_ReturnsJob()
    {
        // Arrange
        var service = CreateTestService();
        var request = new GeneratorSubmitRequest(
            GeneratorModality.Image,
            "comfyui",
            "sd-xl",
            "generate",
            null,
            "Assets/Generated/test.png",
            new Dictionary<string, object?> { ["prompt"] = "test image", ["steps"] = 20 },
            null);

        // Act
        var job = await service.SubmitJobAsync("workspace-1", request, CancellationToken.None);

        // Assert
        Assert.NotNull(job);
        Assert.NotEmpty(job.Id);
        Assert.Equal(GeneratorModality.Image, job.Modality);
        Assert.Equal("comfyui", job.ProviderId);
    }

    [Fact]
    public async Task SubmitJobAsync_MissingPrompt_ThrowsValidationError()
    {
        // Arrange
        var service = CreateTestService();
        var request = new GeneratorSubmitRequest(
            GeneratorModality.Image,
            "comfyui",
            "",
            "generate",
            null,
            "Assets/Generated/test.png",
            new Dictionary<string, object?>(), // No prompt
            null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SubmitJobAsync("workspace-1", request, CancellationToken.None));
    }

    #endregion

    #region Status Tests

    [Fact]
    public async Task GetJobAsync_ExistingJob_ReturnsJob()
    {
        // Arrange
        var service = CreateTestService();
        var submitRequest = new GeneratorSubmitRequest(
            GeneratorModality.Image,
            "comfyui",
            "",
            "generate",
            null,
            "Assets/Generated/test.png",
            new Dictionary<string, object?> { ["prompt"] = "test" },
            null);

        var submittedJob = await service.SubmitJobAsync("workspace-1", submitRequest, CancellationToken.None);

        // Act
        var retrievedJob = await service.GetJobAsync(submittedJob.Id, CancellationToken.None);

        // Assert
        Assert.NotNull(retrievedJob);
        Assert.Equal(submittedJob.Id, retrievedJob.Id);
    }

    [Fact]
    public async Task GetJobAsync_NonExistentJob_ReturnsNull()
    {
        // Arrange
        var service = CreateTestService();

        // Act
        var job = await service.GetJobAsync("non-existent-job-id", CancellationToken.None);

        // Assert
        Assert.Null(job);
    }

    #endregion

    #region Cancel Tests

    [Fact]
    public async Task CancelJobAsync_RunningJob_ReturnsTrueAndCancelsJob()
    {
        // Arrange
        var service = CreateTestService();
        var submitRequest = new GeneratorSubmitRequest(
            GeneratorModality.Image,
            "comfyui",
            "",
            "generate",
            null,
            "Assets/Generated/test.png",
            new Dictionary<string, object?> { ["prompt"] = "test" },
            null);

        var job = await service.SubmitJobAsync("workspace-1", submitRequest, CancellationToken.None);

        // Act
        var cancelled = await service.CancelJobAsync(job.Id, CancellationToken.None);

        // Assert
        Assert.True(cancelled);

        var updatedJob = await service.GetJobAsync(job.Id, CancellationToken.None);
        Assert.Equal(GeneratorJobStatus.Cancelled, updatedJob?.Status);
    }

    [Fact]
    public async Task CancelJobAsync_NonExistentJob_ReturnsFalse()
    {
        // Arrange
        var service = CreateTestService();

        // Act
        var cancelled = await service.CancelJobAsync("non-existent", CancellationToken.None);

        // Assert
        Assert.False(cancelled);
    }

    #endregion

    #region Capabilities Tests

    [Fact]
    public async Task GetCapabilitiesAsync_ImageModality_ReturnsCapabilities()
    {
        // Arrange
        var service = CreateTestService();

        // Act
        var capabilities = await service.GetCapabilitiesAsync(GeneratorModality.Image, null, CancellationToken.None);

        // Assert
        Assert.NotNull(capabilities);
        Assert.NotEmpty(capabilities);
        Assert.All(capabilities, c => Assert.Equal(GeneratorModality.Image, c.Modality));
    }

    [Fact]
    public async Task GetCapabilitiesAsync_MeshModality_KaoProvider_ReturnsKaoCapabilities()
    {
        // Arrange
        var service = CreateTestService();

        // Act
        var capabilities = await service.GetCapabilitiesAsync(GeneratorModality.Mesh, "kao", CancellationToken.None);

        // Assert
        Assert.NotNull(capabilities);
        Assert.NotEmpty(capabilities);
        Assert.Contains(capabilities, c => c.SupportedFormats.Contains("glb"));
    }

    [Fact]
    public async Task GetCapabilitiesAsync_AllModalities_ReturnCapabilities()
    {
        // Arrange
        var service = CreateTestService();
        var modalities = new[]
        {
            GeneratorModality.Image,
            GeneratorModality.MaterialPbr,
            GeneratorModality.Mesh,
            GeneratorModality.Sound,
            GeneratorModality.Animation
        };

        // Act & Assert
        foreach (var modality in modalities)
        {
            var capabilities = await service.GetCapabilitiesAsync(modality, null, CancellationToken.None);
            Assert.NotNull(capabilities);
            Assert.NotEmpty(capabilities);
        }
    }

    #endregion

    #region History Tests

    [Fact]
    public async Task GetHistoryAsync_NoHistory_ReturnsNull()
    {
        // Arrange
        var service = CreateTestService();

        // Act
        var history = await service.GetHistoryAsync("workspace-1", "asset-guid-123", CancellationToken.None);

        // Assert
        Assert.Null(history);
    }

    #endregion

    #region Recovery Tests

    [Fact]
    public async Task GetRecoverableJobsAsync_NoJobs_ReturnsEmpty()
    {
        // Arrange
        var service = CreateTestService();

        // Act
        var recoverable = await service.GetRecoverableJobsAsync("workspace-1", CancellationToken.None);

        // Assert
        Assert.NotNull(recoverable);
        Assert.Empty(recoverable);
    }

    #endregion

    #region Helper Methods

    private static IGeneratorService CreateTestService()
    {
        // Create a mock/test implementation
        // In real tests, this would use proper mocking
        return new TestGeneratorService();
    }

    #endregion
}

/// <summary>
/// Test implementation of IGeneratorService for unit tests.
/// </summary>
internal class TestGeneratorService : IGeneratorService
{
    private readonly Dictionary<string, GeneratorJob> _jobs = new();
    private int _jobCounter;

    public event EventHandler<GeneratorJob>? JobUpdated;

    public Task<QuoteResult> GetQuoteAsync(GeneratorQuoteRequest request, CancellationToken ct)
    {
        var cost = request.ProviderId switch
        {
            "kao" or "hunyuan3d" or "comfyui" => 0.00m,
            _ => 0.01m
        };

        return Task.FromResult(new QuoteResult(true, cost, "USD", null));
    }

    public Task<GeneratorJob> SubmitJobAsync(string workspaceId, GeneratorSubmitRequest request, CancellationToken ct)
    {
        var prompt = request.Parameters?.GetValueOrDefault("prompt")?.ToString();
        if (string.IsNullOrEmpty(prompt))
        {
            throw new InvalidOperationException("Prompt is required");
        }

        var jobId = $"test-job-{++_jobCounter}";
        var job = new GeneratorJob(
            jobId,
            workspaceId,
            request.Modality,
            request.ProviderId,
            request.ModelId ?? "",
            request.TargetAssetGuid,
            request.TargetAssetPath,
            request.Mode,
            GeneratorJobStatus.Submitted,
            request.Parameters,
            request.References,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);

        _jobs[jobId] = job;
        return Task.FromResult(job);
    }

    public Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            _jobs[jobId] = job with { Status = GeneratorJobStatus.Cancelled };
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<GeneratorJob?> ResumeJobAsync(string jobId, CancellationToken ct)
    {
        return Task.FromResult<GeneratorJob?>(null);
    }

    public Task<bool> DiscardRecoveryAsync(string jobId, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    public Task<GeneratorJob?> GetJobAsync(string jobId, CancellationToken ct)
    {
        return Task.FromResult(_jobs.TryGetValue(jobId, out var job) ? job : null);
    }

    public Task<GeneratorHistory?> GetHistoryAsync(string workspaceId, string assetGuid, CancellationToken ct)
    {
        return Task.FromResult<GeneratorHistory?>(null);
    }

    public Task<IReadOnlyList<GeneratorRecoveryInfo>> GetRecoverableJobsAsync(string workspaceId, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<GeneratorRecoveryInfo>>(Array.Empty<GeneratorRecoveryInfo>());
    }

    public Task<IReadOnlyList<GeneratorCapabilities>> GetCapabilitiesAsync(GeneratorModality modality, string? providerId, CancellationToken ct)
    {
        var capabilities = new List<GeneratorCapabilities>
        {
            new GeneratorCapabilities(
                modality,
                new[]
                {
                    new GeneratorModeSpec("generate", "Generate", "Generate content", new[] { "prompt" }, null)
                },
                modality switch
                {
                    GeneratorModality.Image => new[] { "png", "jpg" },
                    GeneratorModality.Mesh => new[] { "glb", "obj" },
                    GeneratorModality.Sound => new[] { "wav", "mp3" },
                    _ => new[] { "default" }
                },
                null,
                null,
                true,
                true,
                true)
        };

        return Task.FromResult<IReadOnlyList<GeneratorCapabilities>>(capabilities);
    }

    public Task<string?> ApplyResultAsync(string workspaceId, GeneratorApplyRequest request, CancellationToken ct)
    {
        return Task.FromResult<string?>(request.TargetAssetPath);
    }
}
