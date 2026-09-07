// Animation Adapter Validation Tests - video-to-motion VideoClip reference
// Covers the upstream 2.9.0-pre.2 video-to-motion delta.

using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Splatter.Protocol;
using Splatter.Service.Generators;
using Splatter.Service.Storage;
using Xunit;

namespace Splatter.Service.Tests.Generators;

public sealed class AnimationValidationTests
{
    private readonly AnimationModalityAdapter _adapter = new(
        NullLogger.Instance, new HttpClient(), Mock.Of<ICredentialStore>());

    [Fact]
    public async Task Validate_PromptOnly_IsValid()
    {
        var error = await _adapter.ValidateInputsAsync(
            new Dictionary<string, object?> { ["prompt"] = "a wave" }, null, CancellationToken.None);
        Assert.Null(error);
    }

    [Fact]
    public async Task Validate_NoPromptNoVideo_Fails()
    {
        var error = await _adapter.ValidateInputsAsync(null, null, CancellationToken.None);
        Assert.NotNull(error);
        Assert.Contains("required", error!);
    }

    [Fact]
    public async Task Validate_VideoClipPathParameter_IsValid()
    {
        var error = await _adapter.ValidateInputsAsync(
            new Dictionary<string, object?> { ["videoPath"] = "Assets/Clips/take1.mp4" },
            null, CancellationToken.None);
        Assert.Null(error);
    }

    [Fact]
    public async Task Validate_VideoReferencePath_IsValid()
    {
        var references = new[] { new ArtifactReference("v1", "video", "Assets/Clips/take1.mov", null, null) };
        var error = await _adapter.ValidateInputsAsync(null, references, CancellationToken.None);
        Assert.Null(error);
    }

    [Fact]
    public async Task Validate_InlineBase64Video_IsValid()
    {
        var references = new[] { new ArtifactReference("v1", "video", null, null, null, null, "AAAABBBB") };
        var error = await _adapter.ValidateInputsAsync(null, references, CancellationToken.None);
        Assert.Null(error);
    }

    [Fact]
    public async Task Validate_UnsupportedVideoExtension_Fails()
    {
        var error = await _adapter.ValidateInputsAsync(
            new Dictionary<string, object?> { ["videoPath"] = "Assets/Clips/notes.txt" },
            null, CancellationToken.None);
        Assert.NotNull(error);
        Assert.Contains("Unsupported video", error!);
    }
}
