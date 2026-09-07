// Interactive-session registry tests (launcher mode).
// The registry backs mcp.session.create/close/list: it tracks token-scoped
// sessions minted for interactive CLI terminals so their MCP calls resolve to
// the permission mode chosen at launch.

using Splatter.Protocol;
using Splatter.Service.Protocol;
using Xunit;

namespace Splatter.Service.Tests.Protocol;

public sealed class InteractiveSessionRegistryTests
{
    private static InteractiveSessionRegistry.Entry MakeEntry(
        string sessionId = "isess_test0001",
        string workspaceId = "ws_ABC123",
        string providerId = "claude-code",
        AgentPermissionMode mode = AgentPermissionMode.AskBeforeWrite)
    {
        var now = DateTimeOffset.UtcNow;
        return new InteractiveSessionRegistry.Entry
        {
            SessionId = sessionId,
            Token = Guid.NewGuid().ToString("N"),
            Session = new AgentSession(
                sessionId, workspaceId, sessionId, providerId, null, null,
                mode, AgentSessionStatus.Running, now, now, null),
            ProviderId = providerId,
            CreatedAt = now,
            LastActivity = now,
        };
    }

    [Fact]
    public void Register_Then_Get_Returns_Entry_With_Mode()
    {
        var registry = new InteractiveSessionRegistry();
        var entry = MakeEntry(mode: AgentPermissionMode.FullAuto);

        registry.Register(entry);

        var found = registry.Get(entry.SessionId);
        Assert.NotNull(found);
        Assert.Equal(AgentPermissionMode.FullAuto, found!.Session.Mode);
        Assert.Equal(entry.Token, found.Token);
    }

    [Fact]
    public void Close_Removes_And_Returns_Entry_For_Token_Unregistration()
    {
        var registry = new InteractiveSessionRegistry();
        var entry = MakeEntry();
        registry.Register(entry);

        var closed = registry.Close(entry.SessionId);

        Assert.NotNull(closed);
        Assert.True(closed!.Ended);
        Assert.Equal(entry.Token, closed.Token);
        Assert.Null(registry.Get(entry.SessionId));
    }

    [Fact]
    public void Close_Is_Idempotent()
    {
        var registry = new InteractiveSessionRegistry();
        var entry = MakeEntry();
        registry.Register(entry);

        Assert.NotNull(registry.Close(entry.SessionId));
        Assert.Null(registry.Close(entry.SessionId));
        Assert.Null(registry.Close("isess_never_existed"));
    }

    [Fact]
    public void ListForWorkspace_Filters_And_Orders_By_Creation()
    {
        var registry = new InteractiveSessionRegistry();
        var a = MakeEntry(sessionId: "isess_a", workspaceId: "ws_ONE");
        var b = MakeEntry(sessionId: "isess_b", workspaceId: "ws_ONE", providerId: "codex");
        var other = MakeEntry(sessionId: "isess_c", workspaceId: "ws_TWO");
        registry.Register(a);
        registry.Register(b);
        registry.Register(other);

        var list = registry.ListForWorkspace("ws_ONE");

        Assert.Equal(2, list.Count);
        Assert.Equal("isess_a", list[0].SessionId);
        Assert.Equal("isess_b", list[1].SessionId);
        Assert.DoesNotContain(list, e => e.SessionId == "isess_c");
    }

    [Fact]
    public void Closed_Sessions_Do_Not_Appear_In_List()
    {
        var registry = new InteractiveSessionRegistry();
        var entry = MakeEntry(workspaceId: "ws_ONE");
        registry.Register(entry);
        registry.Close(entry.SessionId);

        Assert.Empty(registry.ListForWorkspace("ws_ONE"));
    }

    [Fact]
    public void Touch_And_CountToolCall_Update_Activity()
    {
        var registry = new InteractiveSessionRegistry();
        var entry = MakeEntry();
        var created = entry.LastActivity;
        registry.Register(entry);

        Thread.Sleep(5);
        registry.Touch(entry.SessionId);
        registry.CountToolCall(entry.SessionId);
        registry.CountToolCall(entry.SessionId);

        var found = registry.Get(entry.SessionId)!;
        Assert.True(found.LastActivity > created);
        Assert.Equal(2, found.ToolCallCount);

        // Unknown ids are no-ops, not errors.
        registry.Touch("isess_unknown");
        registry.CountToolCall("isess_unknown");
    }
}
