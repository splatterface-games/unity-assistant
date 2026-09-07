// SQLite Persistence Store

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;

namespace Splatter.Service.Storage;

public sealed class SqlitePersistenceStore : IPersistenceStore, IAsyncDisposable
{
    private readonly ILogger<SqlitePersistenceStore> _logger;
    private readonly ServiceConfiguration _config;
    private SqliteConnection? _connection;

    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SqlitePersistenceStore(ILogger<SqlitePersistenceStore> logger, ServiceConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _connection = new SqliteConnection($"Data Source={_config.DatabasePath}");
        await _connection.OpenAsync(ct);

        await MigrateAsync(ct);
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        // Create migrations table if not exists
        await ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            )", ct);

        var currentVersion = await GetSchemaVersionAsync(ct);

        if (currentVersion < 1)
        {
            await ApplyMigration1Async(ct);
        }

        _logger.LogInformation("Database schema at version {Version}", CurrentSchemaVersion);
    }

    private async Task<int> GetSchemaVersionAsync(CancellationToken ct)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_migrations";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    private async Task ApplyMigration1Async(CancellationToken ct)
    {
        _logger.LogInformation("Applying migration 1...");

        // Run the whole migration in a transaction so an interrupted first run
        // cannot leave a half-created schema behind, and use IF NOT EXISTS so a
        // previously half-applied database can still recover.
        await using var tx = (SqliteTransaction)await _connection!.BeginTransactionAsync(ct);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                title TEXT,
                provider_id TEXT,
                model_id TEXT,
                mode INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                metadata_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_conversations_workspace ON conversations(workspace_id);

            CREATE TABLE IF NOT EXISTS conversation_turns (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                role INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT,
                status INTEGER NOT NULL,
                parts_json TEXT NOT NULL,
                linked_session_id TEXT,
                linked_checkpoint_id TEXT,
                FOREIGN KEY (conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_turns_conversation ON conversation_turns(conversation_id);

            CREATE TABLE IF NOT EXISTS agent_sessions (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                model_id TEXT,
                adapter_session_id TEXT,
                mode INTEGER NOT NULL,
                status INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                current_turn_id TEXT,
                FOREIGN KEY (conversation_id) REFERENCES conversations(id)
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_workspace ON agent_sessions(workspace_id);
            CREATE INDEX IF NOT EXISTS idx_sessions_conversation ON agent_sessions(conversation_id);

            CREATE TABLE IF NOT EXISTS permission_grants (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                tool_id TEXT NOT NULL,
                permission_class INTEGER NOT NULL,
                scope INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                FOREIGN KEY (session_id) REFERENCES agent_sessions(id)
            );

            CREATE INDEX IF NOT EXISTS idx_grants_session ON permission_grants(session_id);

            CREATE TABLE IF NOT EXISTS checkpoints (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                session_id TEXT,
                turn_id TEXT,
                tool_call_id TEXT,
                provider_type INTEGER NOT NULL,
                reference TEXT NOT NULL,
                paths_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                description TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_checkpoints_session ON checkpoints(session_id);

            CREATE TABLE IF NOT EXISTS tool_audits (
                id TEXT PRIMARY KEY,
                tool_call_id TEXT NOT NULL,
                tool_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                normalized_arguments_json TEXT,
                resolved_paths_json TEXT,
                network_hosts_json TEXT,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                status INTEGER NOT NULL,
                side_effects_committed INTEGER NOT NULL,
                permission_decision_ids_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_audits_session ON tool_audits(session_id);

            CREATE TABLE IF NOT EXISTS generator_jobs (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                modality INTEGER NOT NULL,
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                target_asset_guid TEXT,
                target_asset_path TEXT,
                mode TEXT NOT NULL,
                status INTEGER NOT NULL,
                parameters_json TEXT,
                references_json TEXT,
                quote_json TEXT,
                results_json TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                error_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_jobs_workspace ON generator_jobs(workspace_id);
            CREATE INDEX IF NOT EXISTS idx_jobs_status ON generator_jobs(status);

            INSERT INTO schema_migrations (version, applied_at) VALUES (1, @p0);
        ";
        cmd.Parameters.AddWithValue("@p0", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    #region Conversations

    public async Task<Conversation> CreateConversationAsync(string workspaceId, string? title, string? providerId, string? modelId, AgentPermissionMode mode, CancellationToken ct)
    {
        var id = ConversationId.New().Value;
        var now = DateTimeOffset.UtcNow;

        await ExecuteAsync(@"
            INSERT INTO conversations (id, workspace_id, title, provider_id, model_id, mode, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            ct, id, workspaceId, title, providerId, modelId, (int)mode, now.ToString("O"), now.ToString("O"));

        return new Conversation(id, workspaceId, title, providerId, modelId, mode, now, now, Array.Empty<ConversationTurn>(), null);
    }

    public async Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql(@"
            SELECT id, workspace_id, title, provider_id, model_id, mode, created_at, updated_at, metadata_json
            FROM conversations WHERE id = ?");
        cmd.Parameters.AddWithValue("@p0", conversationId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var turns = await GetConversationTurnsAsync(conversationId, ct);

        return new Conversation(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            (AgentPermissionMode)reader.GetInt32(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)),
            turns,
            reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(8), JsonOptions)
        );
    }

    private async Task<IReadOnlyList<ConversationTurn>> GetConversationTurnsAsync(string conversationId, CancellationToken ct)
    {
        var turns = new List<ConversationTurn>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql(@"
            SELECT id, role, created_at, completed_at, status, parts_json, linked_session_id, linked_checkpoint_id
            FROM conversation_turns WHERE conversation_id = ? ORDER BY created_at");
        cmd.Parameters.AddWithValue("@p0", conversationId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            turns.Add(new ConversationTurn(
                reader.GetString(0),
                (TurnRole)reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                (TurnStatus)reader.GetInt32(4),
                JsonSerializer.Deserialize<List<ConversationPart>>(reader.GetString(5), JsonOptions) ?? new(),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)
            ));
        }
        return turns;
    }

    public async Task<IReadOnlyList<Conversation>> ListConversationsAsync(string workspaceId, int limit, int offset, CancellationToken ct)
    {
        var conversations = new List<Conversation>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql(@"
            SELECT id FROM conversations WHERE workspace_id = ? ORDER BY updated_at DESC LIMIT ? OFFSET ?");
        cmd.Parameters.AddWithValue("@p0", workspaceId);
        cmd.Parameters.AddWithValue("@p1", limit);
        cmd.Parameters.AddWithValue("@p2", offset);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        foreach (var id in ids)
        {
            var conv = await GetConversationAsync(id, ct);
            if (conv != null) conversations.Add(conv);
        }

        return conversations;
    }

    public async Task UpdateConversationAsync(Conversation conversation, CancellationToken ct)
    {
        await ExecuteAsync(@"
            UPDATE conversations SET title = ?, provider_id = ?, model_id = ?, mode = ?, updated_at = ?, metadata_json = ?
            WHERE id = ?",
            ct, conversation.Title, conversation.ProviderId, conversation.ModelId,
            (int)conversation.Mode, DateTimeOffset.UtcNow.ToString("O"),
            conversation.Metadata != null ? JsonSerializer.Serialize(conversation.Metadata, JsonOptions) : null,
            conversation.Id);
    }

    public async Task DeleteConversationAsync(string conversationId, CancellationToken ct)
    {
        await ExecuteAsync("DELETE FROM conversations WHERE id = ?", ct, conversationId);
    }

    #endregion

    #region Turns

    public async Task<ConversationTurn> CreateTurnAsync(string conversationId, TurnRole role, CancellationToken ct)
    {
        var id = TurnId.New().Value;
        var now = DateTimeOffset.UtcNow;

        await ExecuteAsync(@"
            INSERT INTO conversation_turns (id, conversation_id, role, created_at, status, parts_json)
            VALUES (?, ?, ?, ?, ?, ?)",
            ct, id, conversationId, (int)role, now.ToString("O"), (int)TurnStatus.Pending, "[]");

        return new ConversationTurn(id, role, now, null, TurnStatus.Pending, Array.Empty<ConversationPart>(), null, null);
    }

    public async Task UpdateTurnAsync(string conversationId, ConversationTurn turn, CancellationToken ct)
    {
        await ExecuteAsync(@"
            UPDATE conversation_turns SET completed_at = ?, status = ?, parts_json = ?, linked_session_id = ?, linked_checkpoint_id = ?
            WHERE id = ?",
            ct, turn.CompletedAt?.ToString("O"), (int)turn.Status,
            JsonSerializer.Serialize(turn.Parts, JsonOptions),
            turn.LinkedSessionId, turn.LinkedCheckpointId, turn.Id);
    }

    #endregion

    #region Sessions

    public async Task<AgentSession> CreateSessionAsync(string workspaceId, string conversationId, string providerId, string? modelId, AgentPermissionMode mode, CancellationToken ct)
    {
        var id = SessionId.New().Value;
        var now = DateTimeOffset.UtcNow;

        await ExecuteAsync(@"
            INSERT INTO agent_sessions (id, workspace_id, conversation_id, provider_id, model_id, mode, status, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
            ct, id, workspaceId, conversationId, providerId, modelId, (int)mode,
            (int)AgentSessionStatus.Starting, now.ToString("O"), now.ToString("O"));

        return new AgentSession(id, workspaceId, conversationId, providerId, modelId, null, mode,
            AgentSessionStatus.Starting, now, now, null);
    }

    public async Task<AgentSession?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql(@"
            SELECT id, workspace_id, conversation_id, provider_id, model_id, adapter_session_id, mode, status, created_at, updated_at, current_turn_id
            FROM agent_sessions WHERE id = ?");
        cmd.Parameters.AddWithValue("@p0", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new AgentSession(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            (AgentPermissionMode)reader.GetInt32(6),
            (AgentSessionStatus)reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10)
        );
    }

    public async Task UpdateSessionAsync(AgentSession session, CancellationToken ct)
    {
        await ExecuteAsync(@"
            UPDATE agent_sessions SET adapter_session_id = ?, mode = ?, status = ?, updated_at = ?, current_turn_id = ?
            WHERE id = ?",
            ct, session.AdapterSessionId, (int)session.Mode, (int)session.Status,
            DateTimeOffset.UtcNow.ToString("O"), session.CurrentTurnId, session.Id);
    }

    public async Task<string?> GetLatestAdapterSessionIdAsync(string conversationId, CancellationToken ct)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql(@"
            SELECT adapter_session_id FROM agent_sessions
            WHERE conversation_id = ? AND adapter_session_id IS NOT NULL AND adapter_session_id <> ''
            ORDER BY created_at DESC LIMIT 1");
        cmd.Parameters.AddWithValue("@p0", conversationId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null || result is DBNull ? null : result.ToString();
    }

    #endregion

    #region Permissions

    public async Task<PermissionGrant?> GetGrantAsync(string sessionId, string toolId, PermissionClass permissionClass, CancellationToken ct)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql(@"
            SELECT id, workspace_id, session_id, tool_id, permission_class, scope, created_at, expires_at
            FROM permission_grants WHERE session_id = ? AND tool_id = ? AND permission_class = ?
            AND (expires_at IS NULL OR expires_at > ?)");
        cmd.Parameters.AddWithValue("@p0", sessionId);
        cmd.Parameters.AddWithValue("@p1", toolId);
        cmd.Parameters.AddWithValue("@p2", (int)permissionClass);
        cmd.Parameters.AddWithValue("@p3", DateTimeOffset.UtcNow.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new PermissionGrant(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            (PermissionClass)reader.GetInt32(4), (PermissionScope)reader.GetInt32(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))
        );
    }

    public async Task CreateGrantAsync(PermissionGrant grant, CancellationToken ct)
    {
        await ExecuteAsync(@"
            INSERT INTO permission_grants (id, workspace_id, session_id, tool_id, permission_class, scope, created_at, expires_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            ct, grant.Id, grant.WorkspaceId, grant.SessionId, grant.ToolId,
            (int)grant.PermissionClass, (int)grant.Scope, grant.CreatedAt.ToString("O"),
            grant.ExpiresAt?.ToString("O"));
    }

    public async Task<IReadOnlyList<PermissionGrant>> GetSessionGrantsAsync(string sessionId, CancellationToken ct)
    {
        var grants = new List<PermissionGrant>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql("SELECT * FROM permission_grants WHERE session_id = ?");
        cmd.Parameters.AddWithValue("@p0", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            grants.Add(new PermissionGrant(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                (PermissionClass)reader.GetInt32(4), (PermissionScope)reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))
            ));
        }
        return grants;
    }

    #endregion

    #region Checkpoints

    public async Task CreateCheckpointAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        await ExecuteAsync(@"
            INSERT INTO checkpoints (id, workspace_id, session_id, turn_id, tool_call_id, provider_type, reference, paths_json, created_at, description)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            ct, checkpoint.Id, checkpoint.WorkspaceId, checkpoint.SessionId, checkpoint.TurnId,
            checkpoint.ToolCallId, (int)checkpoint.ProviderType, checkpoint.Reference,
            JsonSerializer.Serialize(checkpoint.Paths, JsonOptions), checkpoint.CreatedAt.ToString("O"),
            checkpoint.Description);
    }

    public async Task<Checkpoint?> GetCheckpointAsync(string checkpointId, CancellationToken ct)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql("SELECT * FROM checkpoints WHERE id = ?");
        cmd.Parameters.AddWithValue("@p0", checkpointId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Checkpoint(
            reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            (CheckpointProviderType)reader.GetInt32(5),
            reader.GetString(6),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(7), JsonOptions) ?? new(),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9)
        );
    }

    public async Task<IReadOnlyList<Checkpoint>> GetSessionCheckpointsAsync(string sessionId, CancellationToken ct)
    {
        var checkpoints = new List<Checkpoint>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql("SELECT * FROM checkpoints WHERE session_id = ? ORDER BY created_at");
        cmd.Parameters.AddWithValue("@p0", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            checkpoints.Add(new Checkpoint(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                (CheckpointProviderType)reader.GetInt32(5),
                reader.GetString(6),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(7), JsonOptions) ?? new(),
                DateTimeOffset.Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9)
            ));
        }
        return checkpoints;
    }

    #endregion

    #region Tool Audits

    public async Task CreateToolAuditAsync(ToolAuditRecord audit, CancellationToken ct)
    {
        await ExecuteAsync(@"
            INSERT INTO tool_audits (id, tool_call_id, tool_id, session_id, normalized_arguments_json, resolved_paths_json, network_hosts_json, started_at, finished_at, status, side_effects_committed, permission_decision_ids_json)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            ct, Guid.NewGuid().ToString("N"), audit.ToolCallId, audit.ToolId, audit.SessionId,
            audit.NormalizedArguments != null ? JsonSerializer.Serialize(audit.NormalizedArguments, JsonOptions) : null,
            audit.ResolvedPaths != null ? JsonSerializer.Serialize(audit.ResolvedPaths, JsonOptions) : null,
            audit.NetworkHosts != null ? JsonSerializer.Serialize(audit.NetworkHosts, JsonOptions) : null,
            audit.StartedAt.ToString("O"), audit.FinishedAt?.ToString("O"),
            (int)audit.Status, audit.SideEffectsCommitted ? 1 : 0,
            audit.PermissionDecisionIds != null ? JsonSerializer.Serialize(audit.PermissionDecisionIds, JsonOptions) : null);
    }

    public async Task<IReadOnlyList<ToolAuditRecord>> GetSessionAuditsAsync(string sessionId, CancellationToken ct)
    {
        // Simplified - implement full query as needed
        return Array.Empty<ToolAuditRecord>();
    }

    #endregion

    #region Generator Jobs

    public async Task CreateGeneratorJobAsync(GeneratorJob job, CancellationToken ct)
    {
        await ExecuteAsync(@"
            INSERT INTO generator_jobs (id, workspace_id, modality, provider_id, model_id, target_asset_guid, target_asset_path, mode, status, parameters_json, references_json, quote_json, results_json, created_at, updated_at, error_json)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            ct, job.Id, job.WorkspaceId, (int)job.Modality, job.ProviderId, job.ModelId,
            job.TargetAssetGuid, job.TargetAssetPath, job.Mode, (int)job.Status,
            job.Parameters != null ? JsonSerializer.Serialize(job.Parameters, JsonOptions) : null,
            job.References != null ? JsonSerializer.Serialize(job.References, JsonOptions) : null,
            job.Quote != null ? JsonSerializer.Serialize(job.Quote, JsonOptions) : null,
            job.Results != null ? JsonSerializer.Serialize(job.Results, JsonOptions) : null,
            job.CreatedAt.ToString("O"), job.UpdatedAt.ToString("O"),
            job.Error != null ? JsonSerializer.Serialize(job.Error, JsonOptions) : null);
    }

    public async Task<GeneratorJob?> GetGeneratorJobAsync(string jobId, CancellationToken ct)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql("SELECT * FROM generator_jobs WHERE id = ?");
        cmd.Parameters.AddWithValue("@p0", jobId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return ReadGeneratorJob(reader);
    }

    public async Task UpdateGeneratorJobAsync(GeneratorJob job, CancellationToken ct)
    {
        await ExecuteAsync(@"
            UPDATE generator_jobs SET status = ?, quote_json = ?, results_json = ?, updated_at = ?, error_json = ?
            WHERE id = ?",
            ct, (int)job.Status,
            job.Quote != null ? JsonSerializer.Serialize(job.Quote, JsonOptions) : null,
            job.Results != null ? JsonSerializer.Serialize(job.Results, JsonOptions) : null,
            DateTimeOffset.UtcNow.ToString("O"),
            job.Error != null ? JsonSerializer.Serialize(job.Error, JsonOptions) : null,
            job.Id);
    }

    public async Task<IReadOnlyList<GeneratorJob>> GetRecoverableJobsAsync(string workspaceId, CancellationToken ct)
    {
        var jobs = new List<GeneratorJob>();
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql("SELECT * FROM generator_jobs WHERE workspace_id = ? AND status = ?");
        cmd.Parameters.AddWithValue("@p0", workspaceId);
        cmd.Parameters.AddWithValue("@p1", (int)GeneratorJobStatus.Recoverable);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(ReadGeneratorJob(reader));
        }
        return jobs;
    }

    private static GeneratorJob ReadGeneratorJob(SqliteDataReader reader)
    {
        return new GeneratorJob(
            reader.GetString(0), reader.GetString(1),
            (GeneratorModality)reader.GetInt32(2),
            reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            (GeneratorJobStatus)reader.GetInt32(8),
            reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(9), JsonOptions),
            reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<List<ArtifactReference>>(reader.GetString(10), JsonOptions),
            reader.IsDBNull(11) ? null : JsonSerializer.Deserialize<QuoteResult>(reader.GetString(11), JsonOptions),
            reader.IsDBNull(12) ? null : JsonSerializer.Deserialize<List<GeneratorResult>>(reader.GetString(12), JsonOptions),
            DateTimeOffset.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : JsonSerializer.Deserialize<NormalizedError>(reader.GetString(15), JsonOptions)
        );
    }

    #endregion

    private async Task ExecuteAsync(string sql, CancellationToken ct, params object?[] parameters)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = NormalizeSql(sql);

        for (var i = 0; i < parameters.Length; i++)
        {
            cmd.Parameters.AddWithValue($"@p{i}", parameters[i] ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Converts positional '?' placeholders to named @pN parameters so they bind to
    /// the @pN values added via AddWithValue. Microsoft.Data.Sqlite does not bind
    /// named parameter objects to '?' positional placeholders, which is the root of
    /// the original "Must add values for the following parameters" failures. SQL in
    /// this store never contains '?' inside a string literal, so a straight
    /// left-to-right substitution is safe.
    /// </summary>
    private static string NormalizeSql(string sql)
    {
        if (sql.IndexOf('?') < 0)
        {
            return sql;
        }

        var sb = new StringBuilder(sql.Length + 16);
        var index = 0;
        foreach (var ch in sql)
        {
            if (ch == '?')
            {
                sb.Append("@p").Append(index++);
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}
