// Splatter bridge - the chat-free connection core (extracted from ConversationManager).
//
// Owns the editor<->service link every feature depends on: service bootstrap, the
// WebSocket client, credential sync (generators), the Unity tool-execution handler
// (MCP tool forwarding), the service event stream, and workspace attachment. On top
// of that it exposes the launcher-mode RPCs: mint/close/list interactive sessions,
// harness checks, and permission responses.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Splatter.Editor.Bootstrap;
using Splatter.Editor.Handlers;
using Splatter.Editor.Settings;
using Splatter.Editor.Tools;
using Splatter.Editor.Transport;
using Splatter.Protocol;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.Bridge
{
    public sealed class SplatterBridge
    {
        private static SplatterBridge _instance;
        public static SplatterBridge Instance => _instance ??= new SplatterBridge();

        private readonly ServiceClient _client;
        private readonly ToolExecutionHandler _toolHandler;
        private IDisposable _eventSubscription;
        private IDisposable _toolSubscription;

        public event Action<ServiceEvent> OnEvent;
        public event Action<string> OnError;
        public event Action OnConnected;
        public event Action OnDisconnected;

        public bool IsConnected => _client.IsConnected;

        private SplatterBridge()
        {
            _client = ServiceClient.Instance;
            _toolHandler = new ToolExecutionHandler(UnityToolRegistry.Instance, _client);

            _client.OnConnected += () => MainThreadDispatcher.Enqueue(() => OnConnected?.Invoke());
            _client.OnDisconnected += _ => MainThreadDispatcher.Enqueue(() => OnDisconnected?.Invoke());
            _client.OnError += ex => MainThreadDispatcher.Enqueue(() => OnError?.Invoke(ex.Message));
        }

        /// <summary>
        /// Ensures the service is running, connects, syncs credentials, starts the Unity
        /// tool round-trip, subscribes to events, and attaches this editor to its workspace.
        /// Safe to call repeatedly (reconnects re-subscribe without doubling handlers).
        /// </summary>
        public async Task<bool> InitializeAsync(CancellationToken ct = default)
        {
            if (!ServiceBootstrap.IsRunning)
            {
                var started = await ServiceBootstrap.StartServiceAsync(ct);
                if (!started)
                {
                    OnError?.Invoke("Failed to start Splatter service");
                    return false;
                }
            }

            var connected = await _client.ConnectAsync(
                ServiceBootstrap.ServiceUrl, ServiceBootstrap.AuthToken, ct);
            if (!connected)
            {
                OnError?.Invoke("Failed to connect to Splatter service");
                return false;
            }

            // Push locally-configured provider API keys to the service (the process that
            // calls the generator providers). Keys live in the OS keychain editor-side.
            await SyncCredentialsAsync(ct);

            // Dispose prior subscriptions before re-subscribing - SubscribeToAllEvents is
            // additive, so a reconnect would otherwise deliver every event twice.
            _toolSubscription?.Dispose();
            _eventSubscription?.Dispose();
            _toolSubscription = _toolHandler.StartListening();
            _eventSubscription = _client.SubscribeToAllEvents(HandleServiceEvent);

            // Bind this editor to its workspace so tool.execute / tool.activity /
            // permission broadcasts target it (conversation.create used to do this).
            try
            {
                await _client.SendRequestAsync<WorkspaceAttachRequest, WorkspaceAttachResponse>(
                    "workspace.attach",
                    new WorkspaceAttachRequest { workspace_id = GetWorkspaceId() },
                    TimeSpan.FromSeconds(10),
                    ct);
            }
            catch (Exception ex)
            {
                // Non-fatal: broadcasts fall back to all-clients on loopback.
                Debug.LogWarning($"[Splatter] workspace.attach failed: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Mints a token-scoped interactive session for a harness terminal launch.
        /// The token rides on the CLI's MCP requests so its tool calls carry the
        /// permission mode chosen here.
        /// </summary>
        public Task<McpSessionCreated> MintSessionAsync(
            string providerId, AgentPermissionMode mode, string label = null, CancellationToken ct = default)
        {
            return _client.SendRequestAsync<McpSessionCreateRequest, McpSessionCreated>(
                "mcp.session.create",
                new McpSessionCreateRequest
                {
                    workspace_id = GetWorkspaceId(),
                    provider_id = providerId,
                    mode = mode.ToString(),
                    label = label
                },
                TimeSpan.FromSeconds(10),
                ct);
        }

        public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
        {
            return _client.SendRequestAsync<McpSessionCloseRequest, McpSessionClosed>(
                "mcp.session.close",
                new McpSessionCloseRequest { session_id = sessionId },
                TimeSpan.FromSeconds(10),
                ct);
        }

        public async Task<List<McpSessionSummary>> ListSessionsAsync(CancellationToken ct = default)
        {
            var response = await _client.SendRequestAsync<McpSessionListRequest, McpSessionListResult>(
                "mcp.session.list",
                new McpSessionListRequest { workspace_id = GetWorkspaceId() },
                TimeSpan.FromSeconds(10),
                ct);
            return new List<McpSessionSummary>(response.sessions ?? Array.Empty<McpSessionSummary>());
        }

        /// <summary>
        /// Validates that a harness CLI is launchable: resolves it (explicit path override
        /// wins over PATH) and runs `--version` service-side.
        /// </summary>
        public Task<HarnessCheckResult> CheckHarnessAsync(
            string providerId, string pathOverride = null, CancellationToken ct = default)
        {
            return _client.SendRequestAsync<HarnessCheckRequest, HarnessCheckResult>(
                "harness.check",
                new HarnessCheckRequest { provider_id = providerId, path = pathOverride },
                TimeSpan.FromSeconds(20),
                ct);
        }

        public Task RespondToPermissionAsync(
            string requestId, PermissionOutcome outcome, PermissionScope scope,
            string reason = null, CancellationToken ct = default)
        {
            return _client.SendRequestAsync<PermissionRespondRequest, PermissionRespondResult>(
                "permission.respond",
                new PermissionRespondRequest
                {
                    request_id = requestId,
                    outcome = outcome.ToString(),
                    scope = scope.ToString(),
                    reason = reason
                },
                TimeSpan.FromSeconds(10),
                ct);
        }

        /// <summary>Human-friendly harness/provider label for a provider id.</summary>
        public static string ProviderLabel(string providerId) => providerId switch
        {
            "claude-code" => "Claude Agent",
            "codex" => "Codex",
            "grok-build" => "Grok Build",
            "anthropic" => "Anthropic",
            "openai" => "OpenAI",
            "gemini" => "Gemini",
            null or "" => "—",
            _ => providerId
        };

        /// <summary>
        /// Deterministic workspace id for this project. SHA1-based (not GetHashCode,
        /// which is process-stable only) so a service that outlives an editor restart
        /// still routes broadcasts to the right workspace. Opaque to the service.
        /// </summary>
        public static string GetWorkspaceId()
        {
            var projectPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..")).ToLowerInvariant();
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(projectPath));
            return $"ws_{hash[0]:X2}{hash[1]:X2}{hash[2]:X2}{hash[3]:X2}";
        }

        /// <summary>The service's MCP endpoint URL derived from the WebSocket URL.</summary>
        public static string GetMcpUrl()
        {
            var serviceUrl = ServiceBootstrap.ServiceUrl;
            if (string.IsNullOrEmpty(serviceUrl)) return null;
            var http = serviceUrl
                .Replace("wss://", "https://")
                .Replace("ws://", "http://")
                .TrimEnd('/');
            return http + "/mcp";
        }

        private void HandleServiceEvent(MessageEnvelope envelope)
        {
            // tool.execute is handled by ToolExecutionHandler.
            if (envelope.Type == "tool.execute")
                return;

            var evt = new ServiceEvent
            {
                Type = envelope.Type,
                SessionId = envelope.SessionId,
                Payload = envelope.Payload
            };
            MainThreadDispatcher.Enqueue(() => OnEvent?.Invoke(evt));
        }

        // Sends locally-stored provider API keys to the service (generators use them).
        private async Task SyncCredentialsAsync(CancellationToken ct)
        {
            string[] providers = { "openai", "anthropic", "gemini" };
            foreach (var providerId in providers)
            {
                if (!Security.SecureCredentialStore.HasCredential(providerId))
                    continue;

                var key = Security.SecureCredentialStore.GetCredential(providerId);
                try
                {
                    await _client.SendRequestAsync<CredentialSetRequest, CredentialSetResponse>(
                        "credentials.set",
                        new CredentialSetRequest { provider_id = providerId, api_key = key },
                        TimeSpan.FromSeconds(10),
                        ct);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Splatter] Failed to sync credential for {providerId}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>A service event delivered on the main thread. Payload is the wire JToken;
    /// use ServiceClient.PayloadAs&lt;T&gt;() to deserialize.</summary>
    public class ServiceEvent
    {
        public string Type;
        public string SessionId;
        public object Payload;
    }

    #region Wire DTOs (snake_case, JsonUtility-compatible shapes)

    [Serializable]
    public class WorkspaceAttachRequest { public string workspace_id; }

    [Serializable]
    public class WorkspaceAttachResponse { public bool ok; public string workspace_id; }

    [Serializable]
    public class McpSessionCreateRequest
    {
        public string workspace_id;
        public string provider_id;
        public string mode;
        public string label;
    }

    [Serializable]
    public class McpSessionCreated
    {
        public string session_id;
        public string token;
        public string mcp_url;
        public long created_at_ms;
    }

    [Serializable]
    public class McpSessionCloseRequest { public string session_id; }

    [Serializable]
    public class McpSessionClosed { public bool ok; public string session_id; }

    [Serializable]
    public class McpSessionListRequest { public string workspace_id; }

    [Serializable]
    public class McpSessionListResult { public McpSessionSummary[] sessions; }

    [Serializable]
    public class McpSessionSummary
    {
        public string session_id;
        public string provider_id;
        public string mode;
        public string label;
        public long created_at_ms;
        public long last_activity_ms;
        public int tool_call_count;
    }

    [Serializable]
    public class HarnessCheckRequest { public string provider_id; public string path; }

    [Serializable]
    public class HarnessCheckResult
    {
        public string provider_id;
        public bool ok;
        public string version;
        public string path;
        public string message;
    }

    [Serializable]
    public class PermissionRespondRequest
    {
        public string request_id;
        public string outcome;
        public string scope;
        public string reason;
    }

    [Serializable]
    public class PermissionRespondResult { public bool recorded; }

    [Serializable]
    public class CredentialSetRequest { public string provider_id; public string api_key; }

    [Serializable]
    public class CredentialSetResponse { public bool success; public string provider_id; }

    #endregion
}
