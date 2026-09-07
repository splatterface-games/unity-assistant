using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Splatter.Protocol;
using Splatter.Service.Generators;
using Splatter.Service.Language;
using Splatter.Service.Tools;
using Xunit;

namespace Splatter.Service.Tests.Tools;

public sealed class LanguageToolsTests
{
    [Fact]
    public void RegistryExposesReadOnlyCSharpLanguageTools()
    {
        var registry = CreateRegistry(Mock.Of<ICSharpLanguageService>());
        var expected = new[]
        {
            "csharp.status", "csharp.get_diagnostics", "csharp.get_symbols", "csharp.hover",
            "csharp.find_definition", "csharp.find_references", "csharp.get_completions", "csharp.get_signature_help"
        };

        foreach (var id in expected)
        {
            var tool = registry.GetTool(id);
            Assert.NotNull(tool);
            Assert.Equal(ToolCategory.ProjectRead, tool!.Category);
            Assert.False(tool.Execution.RequiresUnityMainThread);
        }
    }

    [Fact]
    public async Task LanguageToolDispatchesToCSharpService()
    {
        var language = new Mock<ICSharpLanguageService>();
        language.Setup(item => item.InvokeAsync("csharp.get_symbols", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { symbols = 3 });
        var registry = CreateRegistry(language.Object);
        var call = new ToolCall("call-1", "csharp.get_symbols", JsonSerializer.SerializeToElement(new { path = "Assets/Test.cs" }),
            new ToolCallSource("test", null, "session-1", "turn-1"), ToolCallStatus.Requested);

        var result = await registry.ExecuteAsync(call, TestSession(), CancellationToken.None);

        Assert.True(result.Ok);
        language.Verify(item => item.InvokeAsync("csharp.get_symbols", It.Is<JsonElement>(args => args.GetProperty("path").GetString() == "Assets/Test.cs"), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ToolRegistry CreateRegistry(ICSharpLanguageService language) => new(
        NullLogger<ToolRegistry>.Instance,
        new ServiceConfiguration { ProjectRoot = Path.GetTempPath() },
        Mock.Of<IGeneratorService>(), language);

    private static AgentSession TestSession() => new(
        "session-1", "ws1", "conv-1", "openai", "gpt-4o", null,
        AgentPermissionMode.ReadOnly, AgentSessionStatus.Ready,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
}
