// SQLite persistence round-trip tests against a real database file.
// These exercise the parameter-binding + migration paths that unit tests
// previously skipped by mocking IPersistenceStore.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Splatter.Protocol;
using Splatter.Service;
using Splatter.Service.Storage;
using Xunit;

namespace Splatter.Service.Tests.Storage;

public sealed class SqlitePersistenceStoreTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir;
    private SqlitePersistenceStore _store = null!;

    public SqlitePersistenceStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "splatter-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public async Task InitializeAsync()
    {
        _store = new SqlitePersistenceStore(
            NullLogger<SqlitePersistenceStore>.Instance,
            new ServiceConfiguration { DataDirectory = _dir });
        await _store.InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Migration_IsIdempotent_AcrossReInitialize()
    {
        // Second initialize on the same DB must not throw "table already exists".
        await _store.InitializeAsync(CancellationToken.None);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Conversation_RoundTrips()
    {
        var created = await _store.CreateConversationAsync(
            "ws1", "My chat", "openai", "gpt-4o", AgentPermissionMode.AskBeforeWrite, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(created.Id));

        var loaded = await _store.GetConversationAsync(created.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("My chat", loaded!.Title);
        Assert.Equal("openai", loaded.ProviderId);
        Assert.Equal(AgentPermissionMode.AskBeforeWrite, loaded.Mode);
    }

    [Fact]
    public async Task ListConversations_ReturnsCreated()
    {
        await _store.CreateConversationAsync("wsList", "a", "openai", "gpt-4o", AgentPermissionMode.ReadOnly, CancellationToken.None);
        await _store.CreateConversationAsync("wsList", "b", "openai", "gpt-4o", AgentPermissionMode.ReadOnly, CancellationToken.None);

        var list = await _store.ListConversationsAsync("wsList", 10, 0, CancellationToken.None);

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task DeleteConversation_Removes()
    {
        var created = await _store.CreateConversationAsync("wsDel", "tmp", "openai", "gpt-4o", AgentPermissionMode.ReadOnly, CancellationToken.None);

        await _store.DeleteConversationAsync(created.Id, CancellationToken.None);

        Assert.Null(await _store.GetConversationAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetMissingConversation_ReturnsNull()
    {
        Assert.Null(await _store.GetConversationAsync("does-not-exist", CancellationToken.None));
    }
}
