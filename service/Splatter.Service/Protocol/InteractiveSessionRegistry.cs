// Registry of interactive harness sessions (launcher mode).
//
// When the editor launches an interactive CLI (Claude Code, Codex) it mints a
// session via mcp.session.create; the resulting token rides on the CLI's MCP
// requests (X-Splatter-Session header or ?session= query param) so tool calls
// resolve to the right AgentSession — carrying the permission mode the user
// chose at launch. The registry is in-memory only: terminal sessions do not
// survive a service restart, and a stale token simply falls back to the
// synthetic read-only MCP session.

using System.Collections.Concurrent;
using Splatter.Protocol;

namespace Splatter.Service.Protocol;

public sealed class InteractiveSessionRegistry
{
    public sealed class Entry
    {
        public required string SessionId { get; init; }
        public required string Token { get; init; }
        public required AgentSession Session { get; init; }
        public required string ProviderId { get; init; }
        public string? Label { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        // Updated from the MCP request path; approximate is fine (UI display only).
        public DateTimeOffset LastActivity { get; set; }
        public int ToolCallCount;
        public bool Ended { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _bySessionId = new();

    public void Register(Entry entry) => _bySessionId[entry.SessionId] = entry;

    public Entry? Get(string sessionId) =>
        _bySessionId.TryGetValue(sessionId, out var e) ? e : null;

    /// <summary>Marks a session ended and removes it. Idempotent; returns the entry (for token unregistration) or null.</summary>
    public Entry? Close(string sessionId)
    {
        if (!_bySessionId.TryRemove(sessionId, out var entry)) return null;
        entry.Ended = true;
        return entry;
    }

    public IReadOnlyList<Entry> ListForWorkspace(string workspaceId) =>
        _bySessionId.Values
            .Where(e => e.Session.WorkspaceId == workspaceId && !e.Ended)
            .OrderBy(e => e.CreatedAt)
            .ToList();

    public void Touch(string sessionId)
    {
        if (_bySessionId.TryGetValue(sessionId, out var e))
            e.LastActivity = DateTimeOffset.UtcNow;
    }

    public void CountToolCall(string sessionId)
    {
        if (_bySessionId.TryGetValue(sessionId, out var e))
            Interlocked.Increment(ref e.ToolCallCount);
    }
}
