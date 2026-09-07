// Generator Intent Gate Tests - explicit-intent gating for paid generation
// Covers the upstream 2.9.0-pre.2 generator intent deltas.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Splatter.Protocol;
using Splatter.Service;
using Splatter.Service.Generators;
using Splatter.Service.Tools;
using Xunit;

namespace Splatter.Service.Tests.Tools;

public sealed class GeneratorIntentGateTests
{
    private readonly Mock<IGeneratorService> _generators = new();
    private readonly ToolRegistry _registry;

    public GeneratorIntentGateTests()
    {
        _generators
            .Setup(g => g.GetQuoteAsync(It.IsAny<GeneratorQuoteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuoteResult(true, 0.20m, "USD", null));
        _generators
            .Setup(g => g.SubmitJobAsync(It.IsAny<string>(), It.IsAny<GeneratorSubmitRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeJob());

        _registry = new ToolRegistry(
            NullLogger<ToolRegistry>.Instance,
            new ServiceConfiguration(),
            _generators.Object);
    }

    [Fact]
    public async Task Submit_WithoutIntent_IsBlocked()
    {
        var result = await Execute("generator.submit", new
        {
            modality = "image", providerId = "comfyui", modelId = "sd-xl", mode = "generate", prompt = "a cat"
        });

        Assert.False(result.Ok);
        Assert.Contains("explicit intent", result.Error!.Message);
        _generators.Verify(g => g.SubmitJobAsync(It.IsAny<string>(), It.IsAny<GeneratorSubmitRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Submit_WithConfirmGeneration_IsAllowed()
    {
        var result = await Execute("generator.submit", new
        {
            modality = "image", providerId = "comfyui", modelId = "sd-xl", mode = "generate",
            prompt = "a cat", confirmGeneration = true
        });

        Assert.True(result.Ok);
        _generators.Verify(g => g.SubmitJobAsync(It.IsAny<string>(), It.IsAny<GeneratorSubmitRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Submit_AfterQuote_IsAllowed()
    {
        // A quote in the same session establishes costed intent.
        await Execute("generator.quote", new { modality = "image", providerId = "comfyui", modelId = "sd-xl", mode = "generate" });

        var result = await Execute("generator.submit", new
        {
            modality = "image", providerId = "comfyui", modelId = "sd-xl", mode = "generate", prompt = "a cat"
        });

        Assert.True(result.Ok);
        _generators.Verify(g => g.SubmitJobAsync(It.IsAny<string>(), It.IsAny<GeneratorSubmitRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // === Helpers ===

    private Task<ToolResult> Execute(string toolId, object args)
    {
        var call = new ToolCall(
            Guid.NewGuid().ToString("N"),
            toolId,
            JsonSerializer.SerializeToElement(args),
            new ToolCallSource("test", null, "session-1", "turn-1"),
            ToolCallStatus.Requested);
        return _registry.ExecuteAsync(call, TestSession(), CancellationToken.None);
    }

    private static AgentSession TestSession() => new(
        "session-1", "ws1", "conv-1", "openai", "gpt-4o", null,
        AgentPermissionMode.AskBeforeWrite, AgentSessionStatus.Ready,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static GeneratorJob FakeJob() => new(
        "job-1", "ws1", GeneratorModality.Image, "comfyui", "sd-xl",
        null, null, "generate", GeneratorJobStatus.Queued,
        null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
}
