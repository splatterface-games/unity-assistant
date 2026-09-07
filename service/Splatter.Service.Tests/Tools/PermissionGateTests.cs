// Mutation-gate tests (launcher mode).
// GateToolCallAsync is the server-side authority for MCP tools/call from interactive
// terminals: reads/captures pass silently, mode auto-approval applies, read-only
// sessions fail fast, prompts block on the user and fail safe to Denied.

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Splatter.Protocol;
using Splatter.Service;
using Splatter.Service.Permissions;
using Splatter.Service.Storage;
using Splatter.Service.Tools;
using Xunit;

namespace Splatter.Service.Tests.Tools;

public sealed class PermissionGateTests
{
    private static ToolSpec MakeSpec(string id, PermissionClass cls, PermissionRisk risk = PermissionRisk.Medium) =>
        new(id, id, $"Test tool {id}", ToolCategory.ProjectRead,
            new { type = "object" }, null,
            new PermissionRequirement(cls, risk, "test", null),
            Array.Empty<SideEffectSpec>(),
            new ToolExecutionSpec(false, 60000, true, false),
            null);

    private static AgentSession MakeSession(AgentPermissionMode mode, string id = "isess_gate") =>
        new(id, "ws_gate", id, "claude-code", null, null,
            mode, AgentSessionStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static ToolCall MakeCall(string toolId) =>
        new($"call_{Guid.NewGuid():N}"[..12], toolId, null,
            new ToolCallSource("claude-code", null, "isess_gate", "mcp"), ToolCallStatus.Requested);

    private static PermissionEngine MakeEngine(params ToolSpec[] specs)
    {
        var tools = new Mock<IToolRegistry>();
        foreach (var spec in specs)
            tools.Setup(t => t.GetTool(spec.Id)).Returns(spec);

        var persistence = new Mock<IPersistenceStore>();
        persistence
            .Setup(p => p.GetGrantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PermissionClass>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PermissionGrant?)null);

        return new PermissionEngine(
            NullLogger<PermissionEngine>.Instance, persistence.Object, tools.Object, new ServiceConfiguration());
    }

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task Reads_And_Captures_Pass_Silently_Even_In_ReadOnly()
    {
        var engine = MakeEngine(
            MakeSpec("scene.get_hierarchy", PermissionClass.ReadProject),
            MakeSpec("capture.game", PermissionClass.ScreenCapture));
        var prompts = 0;
        engine.PermissionRequested += (_, _) => prompts++;
        var session = MakeSession(AgentPermissionMode.ReadOnly);

        var read = await engine.GateToolCallAsync(session, MakeCall("scene.get_hierarchy"), "args", ShortTimeout, CancellationToken.None);
        var capture = await engine.GateToolCallAsync(session, MakeCall("capture.game"), "args", ShortTimeout, CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, read.Outcome);
        Assert.Equal(PermissionOutcome.Allowed, capture.Outcome);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task ReadOnly_Session_Denies_Mutations_Without_Prompting()
    {
        var engine = MakeEngine(MakeSpec("scene.create_gameobject", PermissionClass.SceneMutation));
        var prompts = 0;
        engine.PermissionRequested += (_, _) => prompts++;

        var decision = await engine.GateToolCallAsync(
            MakeSession(AgentPermissionMode.ReadOnly), MakeCall("scene.create_gameobject"), "args", ShortTimeout, CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Contains("read-only", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task FullAuto_Approves_Mutations_Silently_But_Not_Spend()
    {
        var engine = MakeEngine(
            MakeSpec("scene.create_gameobject", PermissionClass.SceneMutation),
            MakeSpec("generator.submit", PermissionClass.GenerationSpend));
        var prompts = 0;
        engine.PermissionRequested += (_, _) => prompts++;
        var session = MakeSession(AgentPermissionMode.FullAuto);

        var mutation = await engine.GateToolCallAsync(session, MakeCall("scene.create_gameobject"), "args", ShortTimeout, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allowed, mutation.Outcome);
        Assert.Equal(0, prompts);

        // Generation spend always prompts, even in FullAuto (times out denied here).
        var spend = await engine.GateToolCallAsync(session, MakeCall("generator.submit"), "args", ShortTimeout, CancellationToken.None);
        Assert.Equal(PermissionOutcome.Denied, spend.Outcome);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task AskBeforeWrite_Prompts_And_Honors_The_Users_Decision()
    {
        var engine = MakeEngine(MakeSpec("project.write_file", PermissionClass.WriteProject));
        engine.PermissionRequested += (_, req) =>
            _ = engine.RecordDecisionAsync(req.Id, PermissionOutcome.Allowed, PermissionScope.Once, null, CancellationToken.None);

        var decision = await engine.GateToolCallAsync(
            MakeSession(AgentPermissionMode.AskBeforeWrite), MakeCall("project.write_file"), "args",
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(PermissionOutcome.Allowed, decision.Outcome);
        Assert.Equal(PermissionDecisionSource.User, decision.DecidedBy);
    }

    [Fact]
    public async Task Denial_Comes_Back_Denied()
    {
        var engine = MakeEngine(MakeSpec("asset.delete", PermissionClass.DeleteProject));
        engine.PermissionRequested += (_, req) =>
            _ = engine.RecordDecisionAsync(req.Id, PermissionOutcome.Denied, PermissionScope.Once, "No.", CancellationToken.None);

        var decision = await engine.GateToolCallAsync(
            MakeSession(AgentPermissionMode.AskBeforeWrite), MakeCall("asset.delete"), "args",
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public async Task Unanswered_Prompt_Times_Out_Denied()
    {
        var engine = MakeEngine(MakeSpec("project.write_file", PermissionClass.WriteProject));

        var decision = await engine.GateToolCallAsync(
            MakeSession(AgentPermissionMode.AskBeforeWrite), MakeCall("project.write_file"), "args",
            ShortTimeout, CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
        Assert.Equal(PermissionDecisionSource.Timeout, decision.DecidedBy);
    }

    [Fact]
    public async Task Allow_For_Session_Skips_The_Next_Prompt_For_The_Same_Tool_Only()
    {
        var engine = MakeEngine(
            MakeSpec("scene.create_gameobject", PermissionClass.SceneMutation),
            MakeSpec("scene.delete_gameobject", PermissionClass.SceneMutation));
        var prompts = 0;
        engine.PermissionRequested += (_, req) =>
        {
            prompts++;
            _ = engine.RecordDecisionAsync(req.Id, PermissionOutcome.Allowed, PermissionScope.Session, null, CancellationToken.None);
        };
        var session = MakeSession(AgentPermissionMode.AskBeforeWrite);

        await engine.GateToolCallAsync(session, MakeCall("scene.create_gameobject"), "args", TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = await engine.GateToolCallAsync(session, MakeCall("scene.create_gameobject"), "args", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allowed, second.Outcome);
        Assert.Equal(1, prompts); // grant covered the second call

        // A different mutation tool still prompts: grants are per-tool, not per-class.
        await engine.GateToolCallAsync(session, MakeCall("scene.delete_gameobject"), "args", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(2, prompts);
    }

    [Fact]
    public async Task Unknown_Tool_Is_Denied()
    {
        var engine = MakeEngine();

        var decision = await engine.GateToolCallAsync(
            MakeSession(AgentPermissionMode.FullAuto), MakeCall("no.such_tool"), "args", ShortTimeout, CancellationToken.None);

        Assert.Equal(PermissionOutcome.Denied, decision.Outcome);
    }
}
