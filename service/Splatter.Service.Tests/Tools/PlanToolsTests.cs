// Plan Tool Tests - WritePlan (create-only), EditPlan (diff), WriteTodos, path containment
// Covers the upstream 2.9.0-pre.2 plan tooling deltas.

using System;
using System.IO;
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

public sealed class PlanToolsTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly ToolRegistry _registry;

    public PlanToolsTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "splatter-plans-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);

        var config = new ServiceConfiguration { ProjectRoot = _projectRoot };
        _registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance, config, Mock.Of<IGeneratorService>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task PlanWrite_CreatesFileUnderAssetsPlans()
    {
        var result = await Execute("plan.write", new { filePath = "feature.md", content = "# Plan\nStep one\n" });

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_projectRoot, "Assets", "Plans", "feature.md")));
    }

    [Fact]
    public async Task PlanWrite_IsCreationOnly()
    {
        await Execute("plan.write", new { filePath = "feature.md", content = "v1" });
        var second = await Execute("plan.write", new { filePath = "feature.md", content = "v2" });

        Assert.False(second.Ok);
        Assert.Contains("already exists", second.Error!.Message);
    }

    [Fact]
    public async Task PlanWrite_RejectsPathOutsidePlansFolder()
    {
        var result = await Execute("plan.write", new { filePath = "../../escape.md", content = "x" });

        Assert.False(result.Ok);
        Assert.Contains("Assets/Plans", result.Error!.Message);
    }

    [Fact]
    public async Task PlanWrite_RejectsNonMarkdown()
    {
        var result = await Execute("plan.write", new { filePath = "feature.txt", content = "x" });

        Assert.False(result.Ok);
        Assert.Contains(".md", result.Error!.Message);
    }

    [Fact]
    public async Task PlanEdit_ReplacesExactString()
    {
        await Execute("plan.write", new { filePath = "feature.md", content = "# Plan\nold line\n" });

        var result = await Execute("plan.edit", new
        {
            filePath = "feature.md",
            oldString = "old line",
            newString = "new line"
        });

        Assert.True(result.Ok);
        var text = await File.ReadAllTextAsync(Path.Combine(_projectRoot, "Assets", "Plans", "feature.md"));
        Assert.Contains("new line", text);
        Assert.DoesNotContain("old line", text);
    }

    [Fact]
    public async Task PlanEdit_RejectsEmptyOldString()
    {
        await Execute("plan.write", new { filePath = "feature.md", content = "content" });

        var result = await Execute("plan.edit", new { filePath = "feature.md", oldString = "", newString = "x" });

        Assert.False(result.Ok);
        Assert.Contains("non-empty", result.Error!.Message);
    }

    [Fact]
    public async Task PlanEdit_RejectsOccurrenceMismatch()
    {
        await Execute("plan.write", new { filePath = "feature.md", content = "a a a" });

        var result = await Execute("plan.edit", new
        {
            filePath = "feature.md",
            oldString = "a",
            newString = "b",
            expectedOccurrences = 1
        });

        Assert.False(result.Ok);
        Assert.Contains("found 3", result.Error!.Message);
    }

    [Fact]
    public async Task PlanEdit_RequiresExistingFile()
    {
        var result = await Execute("plan.edit", new { filePath = "missing.md", oldString = "a", newString = "b" });

        Assert.False(result.Ok);
        Assert.Contains("not found", result.Error!.Message);
    }

    [Fact]
    public async Task PlanWriteTodos_RejectsMultipleInProgress()
    {
        var result = await Execute("plan.write_todos", new
        {
            todos = new[]
            {
                new { description = "one", status = "in_progress" },
                new { description = "two", status = "in_progress" }
            }
        });

        Assert.False(result.Ok);
        Assert.Contains("in_progress", result.Error!.Message);
    }

    [Fact]
    public async Task PlanWriteTodos_AcceptsSingleInProgress()
    {
        var result = await Execute("plan.write_todos", new
        {
            todos = new[]
            {
                new { description = "one", status = "in_progress" },
                new { description = "two", status = "pending" }
            }
        });

        Assert.True(result.Ok);
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
}
