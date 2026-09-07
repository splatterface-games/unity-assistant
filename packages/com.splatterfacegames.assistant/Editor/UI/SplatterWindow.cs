// Splatter Window - launcher, activity feed, and permission prompts.
//
// The window no longer embeds a chat. It launches interactive harness terminals
// (Claude Code, Codex) wired to Splatter's MCP server, shows the bridge/service
// status, renders a live feed of the tool calls those terminals make against the
// editor, and hosts the Allow/Deny prompt for gated Unity mutations. The
// conversation itself happens in the genuine first-party CLI TUI.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Splatter.Editor.Bootstrap;
using Splatter.Editor.Bridge;
using Splatter.Editor.Launch;
using Splatter.Protocol;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.UI
{
    public class SplatterWindow : EditorWindow
    {
        // Sibling of the Generator item so "Splatterface Games Assistant" is a submenu, not a leaf.
        [MenuItem("Window/Splatterface Games/Assistant/Chat")]
        public static void ShowWindow()
        {
            var window = GetWindow<SplatterWindow>("Splatterface Games Assistant");
            window.minSize = new Vector2(420, 320);
        }

        private static readonly string[] Harnesses =
        {
            HarnessLaunchService.ClaudeProviderId,
            HarnessLaunchService.CodexProviderId,
            HarnessLaunchService.GrokProviderId,
        };

        private static readonly string[] ModeLabels = { "Ask before write", "Read only", "Full auto (YOLO)" };
        private static readonly AgentPermissionMode[] ModeValues =
            { AgentPermissionMode.AskBeforeWrite, AgentPermissionMode.ReadOnly, AgentPermissionMode.FullAuto };

        [Serializable]
        private class HarnessCardState
        {
            public string providerId;
            public int modeIndex;          // index into ModeValues
            public bool checkedOnce;
            public bool available;
            public string version;
            public string path;
            public string message;
            public string pathDraft;       // inline first-run path field
            public bool checking;
            public bool launching;
        }

        [Serializable]
        private class ActivityEntry
        {
            public string activityId;
            public string sessionId;
            public string providerId;
            public string toolId;
            public string argsSummary;
            public bool running;
            public bool ok;
            public bool denied;
            public long durationMs;
        }

        [Serializable]
        private class PendingPermission
        {
            public string requestId;
            public string title;
            public string description;
            public string providerId;
        }

        private const int FeedCapacity = 200;

        [SerializeField] private List<HarnessCardState> _cards = new();
        [SerializeField] private List<ActivityEntry> _feed = new();
        [SerializeField] private List<McpSessionSummary> _sessions = new();
        [SerializeField] private PendingPermission _pendingPermission;
        [SerializeField] private Vector2 _feedScroll;
        [SerializeField] private Vector2 _mainScroll;

        private bool _isInitializing;
        private string _statusMessage = "";
        private double _lastConnectAttempt;
        private Task _connectTask;
        [NonSerialized] private bool _stylesInitialized;
        private GUIStyle _cardStyle, _feedRowStyle, _dimStyle, _headerStyle;

        private void OnEnable()
        {
            EnsureCards();
            SplatterBridge.Instance.OnEvent += HandleServiceEvent;
            SplatterBridge.Instance.OnError += HandleError;
            SplatterBridge.Instance.OnConnected += HandleConnected;
            SplatterBridge.Instance.OnDisconnected += HandleDisconnected;
            EditorApplication.update += AutoConnectTick;
        }

        private void OnDisable()
        {
            SplatterBridge.Instance.OnEvent -= HandleServiceEvent;
            SplatterBridge.Instance.OnError -= HandleError;
            SplatterBridge.Instance.OnConnected -= HandleConnected;
            SplatterBridge.Instance.OnDisconnected -= HandleDisconnected;
            EditorApplication.update -= AutoConnectTick;
        }

        private void EnsureCards()
        {
            foreach (var providerId in Harnesses)
            {
                if (_cards.Exists(c => c.providerId == providerId)) continue;
                _cards.Add(new HarnessCardState { providerId = providerId });
            }
        }

        #region Connection

        private void AutoConnectTick()
        {
            if (SplatterBridge.Instance.IsConnected || _isInitializing) return;
            // Only auto-connect to an already-running service; spawning it is deferred to
            // an explicit Connect or Launch so opening the window starts no processes.
            if (!ServiceBootstrap.IsRunning) return;
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastConnectAttempt < 3.0) return;
            _lastConnectAttempt = now;
            _ = EnsureConnectedAsync();
        }

        private Task EnsureConnectedAsync()
        {
            if (SplatterBridge.Instance.IsConnected) return Task.CompletedTask;
            if (_connectTask != null && !_connectTask.IsCompleted) return _connectTask;
            _lastConnectAttempt = EditorApplication.timeSinceStartup;
            _connectTask = InitializeAsync();
            return _connectTask;
        }

        private async Task InitializeAsync()
        {
            _isInitializing = true;
            _statusMessage = "Starting service...";
            Repaint();
            try
            {
                var success = await SplatterBridge.Instance.InitializeAsync();
                _statusMessage = success ? "" : "Failed to connect";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                Debug.LogError($"[Splatter] Initialization failed: {ex}");
            }
            finally
            {
                _isInitializing = false;
                Repaint();
            }
        }

        private void HandleConnected()
        {
            _statusMessage = "";
            _ = RefreshAfterConnectAsync();
            Repaint();
        }

        private async Task RefreshAfterConnectAsync()
        {
            await RefreshSessionsAsync();
            foreach (var card in _cards)
                if (!card.checkedOnce)
                    _ = CheckHarnessAsync(card, null);
        }

        private void HandleDisconnected() => Repaint();

        private void HandleError(string message)
        {
            _statusMessage = message;
            Repaint();
        }

        #endregion

        #region Sessions / harness checks

        private async Task RefreshSessionsAsync()
        {
            try
            {
                var sessions = await SplatterBridge.Instance.ListSessionsAsync();
                _sessions = sessions;
                ActiveSessionTracker.Reconcile(sessions.ConvertAll(s => s.session_id));
                Repaint();
            }
            catch
            {
                // list is cosmetic; ignore transient failures
            }
        }

        private async Task CheckHarnessAsync(HarnessCardState card, string pathOverride)
        {
            card.checking = true;
            Repaint();
            try
            {
                if (!SplatterBridge.Instance.IsConnected && !await SplatterBridge.Instance.InitializeAsync())
                    return;

                if (pathOverride != null)
                    EditorPrefs.SetString(HarnessLaunchService.PathOverrideKey(card.providerId), pathOverride);

                var result = await SplatterBridge.Instance.CheckHarnessAsync(
                    card.providerId, HarnessLaunchService.GetPathOverride(card.providerId));
                card.checkedOnce = true;
                card.available = result?.ok == true;
                card.version = result?.version;
                card.path = result?.path;
                card.message = result?.message;
            }
            catch (Exception ex)
            {
                card.checkedOnce = true;
                card.available = false;
                card.message = ex.Message;
            }
            finally
            {
                card.checking = false;
                Repaint();
            }
        }

        private async Task LaunchAsync(HarnessCardState card)
        {
            card.launching = true;
            Repaint();
            try
            {
                var mode = ModeValues[Mathf.Clamp(card.modeIndex, 0, ModeValues.Length - 1)];
                if (await HarnessLaunchService.LaunchAsync(card.providerId, mode))
                    await RefreshSessionsAsync();
            }
            finally
            {
                card.launching = false;
                Repaint();
            }
        }

        #endregion

        #region GUI

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;
            _cardStyle = new GUIStyle("HelpBox") { padding = new RectOffset(10, 10, 8, 8) };
            _feedRowStyle = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = false };
            _dimStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            _headerStyle = new GUIStyle(EditorStyles.boldLabel);
            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitializeStyles();
            DrawStatusStrip();

            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            GUILayout.Space(4);
            EditorGUILayout.LabelField("Agents", _headerStyle);
            foreach (var card in _cards)
                DrawHarnessCard(card);

            if (_sessions.Count > 0 || ActiveSessionTracker.Sessions.Count > 0)
            {
                GUILayout.Space(6);
                EditorGUILayout.LabelField("Sessions", _headerStyle);
                DrawSessions();
            }

            if (_pendingPermission != null)
                DrawPermissionPrompt();

            GUILayout.Space(6);
            DrawFeedHeader();
            DrawFeed();

            EditorGUILayout.EndScrollView();
        }

        private void DrawStatusStrip()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var connected = SplatterBridge.Instance.IsConnected;
            var dot = connected ? "●" : "○";
            var dotColor = connected ? new Color(0.3f, 0.85f, 0.4f) : new Color(0.85f, 0.35f, 0.3f);
            var prev = GUI.color;
            GUI.color = dotColor;
            GUILayout.Label(dot, GUILayout.Width(14));
            GUI.color = prev;

            var status = connected ? "Connected"
                : _isInitializing ? "Connecting..."
                : ServiceBootstrap.IsRunning ? "Service running" : "Offline";
            GUILayout.Label(status, EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(_statusMessage))
                GUILayout.Label("· " + _statusMessage, EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            if (!connected && GUILayout.Button("Connect", EditorStyles.toolbarButton, GUILayout.Width(70)))
                _ = EnsureConnectedAsync();
            if (connected && GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                _ = RefreshSessionsAsync();
            if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(70)))
                SettingsService.OpenUserPreferences("Preferences/Splatterface Games/Assistant");

            EditorGUILayout.EndHorizontal();
        }

        private void DrawHarnessCard(HarnessCardState card)
        {
            EditorGUILayout.BeginVertical(_cardStyle);

            EditorGUILayout.BeginHorizontal();
            var accent = ProviderColor(card.providerId);
            var prev = GUI.color;
            GUI.color = accent;
            GUILayout.Label(ProviderGlyph(card.providerId), EditorStyles.boldLabel, GUILayout.Width(16));
            GUI.color = prev;
            GUILayout.Label(SplatterBridge.ProviderLabel(card.providerId), EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

            card.modeIndex = EditorGUILayout.Popup(card.modeIndex, ModeLabels, GUILayout.Width(140));

            using (new EditorGUI.DisabledScope(card.launching || card.checking || (card.checkedOnce && !card.available)))
            {
                if (GUILayout.Button(card.launching ? "Launching..." : "Launch", GUILayout.Width(90)))
                    _ = LaunchAsync(card);
            }
            EditorGUILayout.EndHorizontal();

            // Status line: version/path when available, inline path setup when not.
            if (card.checking)
            {
                EditorGUILayout.LabelField("Checking...", _dimStyle);
            }
            else if (!card.checkedOnce)
            {
                EditorGUILayout.LabelField(
                    SplatterBridge.Instance.IsConnected ? "Not checked yet." : "Connect to check availability.",
                    _dimStyle);
            }
            else if (card.available)
            {
                EditorGUILayout.LabelField($"{card.version} · {card.path}", _dimStyle);
            }
            else
            {
                EditorGUILayout.LabelField($"Unavailable: {card.message}", _dimStyle);
                EditorGUILayout.BeginHorizontal();
                card.pathDraft = EditorGUILayout.TextField(
                    string.IsNullOrEmpty(card.pathDraft)
                        ? EditorPrefs.GetString(HarnessLaunchService.PathOverrideKey(card.providerId), "")
                        : card.pathDraft);
                if (GUILayout.Button("Check", GUILayout.Width(60)))
                    _ = CheckHarnessAsync(card, card.pathDraft ?? "");
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSessions()
        {
            foreach (var session in _sessions)
            {
                EditorGUILayout.BeginHorizontal(_cardStyle);
                GUILayout.Label(SplatterBridge.ProviderLabel(session.provider_id), EditorStyles.boldLabel, GUILayout.Width(110));
                GUILayout.Label(session.mode, _dimStyle, GUILayout.Width(110));
                GUILayout.Label($"{session.tool_call_count} tool calls", _dimStyle, GUILayout.Width(90));
                GUILayout.Label("since " + FromUnixMs(session.created_at_ms).ToLocalTime().ToString("HH:mm"), _dimStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("End", GUILayout.Width(50)))
                {
                    var id = session.session_id;
                    _ = EndSessionAsync(id);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private async Task EndSessionAsync(string sessionId)
        {
            await HarnessLaunchService.EndSessionAsync(sessionId);
            await RefreshSessionsAsync();
        }

        private void DrawPermissionPrompt()
        {
            GUILayout.Space(6);
            EditorGUILayout.BeginVertical(_cardStyle);

            var provider = string.IsNullOrEmpty(_pendingPermission.providerId)
                ? "" : $" · {SplatterBridge.ProviderLabel(_pendingPermission.providerId)}";
            EditorGUILayout.LabelField($"Permission requested{provider}", _headerStyle);
            EditorGUILayout.LabelField(_pendingPermission.title ?? "", EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(_pendingPermission.description))
                EditorGUILayout.LabelField(_pendingPermission.description, _dimStyle);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Allow Once"))
                RespondToPermission(PermissionOutcome.Allowed, PermissionScope.Once);
            if (GUILayout.Button("Allow for Session"))
                RespondToPermission(PermissionOutcome.Allowed, PermissionScope.Session);
            if (GUILayout.Button("Deny"))
                RespondToPermission(PermissionOutcome.Denied, PermissionScope.Once);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void RespondToPermission(PermissionOutcome outcome, PermissionScope scope)
        {
            var requestId = _pendingPermission?.requestId;
            _pendingPermission = null;
            if (string.IsNullOrEmpty(requestId)) return;
            _ = SplatterBridge.Instance.RespondToPermissionAsync(requestId, outcome, scope);
            Repaint();
        }

        private void DrawFeedHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Activity", _headerStyle);
            GUILayout.FlexibleSpace();
            if (_feed.Count > 0 && GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(50)))
                _feed.Clear();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFeed()
        {
            if (_feed.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No activity yet. Launch an agent above and talk to it in its terminal - " +
                    "every Unity tool call it makes shows up here.", _dimStyle);
                return;
            }

            _feedScroll = EditorGUILayout.BeginScrollView(_feedScroll, GUILayout.MinHeight(120));
            // Newest first.
            for (int i = _feed.Count - 1; i >= 0; i--)
            {
                var entry = _feed[i];
                var glyph = entry.running ? "…" : entry.denied ? "⛔" : entry.ok ? "✓" : "✗";
                var color = entry.running ? "#999999" : entry.denied ? "#cc6644" : entry.ok ? "#55aa55" : "#cc5555";
                var duration = entry.running ? "" : $"  <color=#777777>{entry.durationMs}ms</color>";
                var summary = Truncate(entry.argsSummary, 80);
                EditorGUILayout.LabelField(
                    $"<color={color}>{glyph}</color> <b>{PrettyToolName(entry.toolId)}</b> " +
                    $"<color=#888888>{summary}</color>{duration}",
                    _feedRowStyle);
            }
            EditorGUILayout.EndScrollView();
        }

        private static Color ProviderColor(string providerId) => providerId switch
        {
            "claude-code" or "anthropic" => new Color(0.85f, 0.47f, 0.34f), // terracotta
            "openai" or "codex" => new Color(0.04f, 0.64f, 0.55f),          // teal
            "gemini" => new Color(0.26f, 0.52f, 0.96f),                     // blue
            "grok-build" => new Color(0.78f, 0.78f, 0.80f),                 // xAI monochrome
            _ => Color.gray
        };

        private static string ProviderGlyph(string providerId) => providerId switch
        {
            "claude-code" or "anthropic" => "C",
            "openai" or "codex" => "O",
            "gemini" => "G",
            "grok-build" => "X",
            _ => "·"
        };

        #endregion

        #region Events

        private void HandleServiceEvent(ServiceEvent evt)
        {
            switch (evt.Type)
            {
                case "tool.activity":
                    HandleToolActivity(evt);
                    break;
                case "permission.requested":
                    HandlePermissionRequested(evt);
                    break;
            }
        }

        private void HandleToolActivity(ServiceEvent evt)
        {
            try
            {
                var payload = Transport.ServiceClient.PayloadAs<ToolActivityPayload>(evt.Payload);
                if (payload == null || string.IsNullOrEmpty(payload.activity_id)) return;

                var entry = _feed.Find(e => e.activityId == payload.activity_id);
                if (payload.phase == "started")
                {
                    if (entry == null)
                    {
                        _feed.Add(new ActivityEntry
                        {
                            activityId = payload.activity_id,
                            sessionId = payload.session_id,
                            providerId = payload.provider_id,
                            toolId = payload.tool_id,
                            argsSummary = payload.args_summary,
                            running = true
                        });
                        if (_feed.Count > FeedCapacity)
                            _feed.RemoveRange(0, _feed.Count - FeedCapacity);
                    }
                }
                else
                {
                    if (entry == null)
                    {
                        entry = new ActivityEntry
                        {
                            activityId = payload.activity_id,
                            sessionId = payload.session_id,
                            providerId = payload.provider_id,
                            toolId = payload.tool_id,
                            argsSummary = payload.args_summary
                        };
                        _feed.Add(entry);
                    }
                    entry.running = false;
                    entry.ok = payload.ok;
                    entry.denied = payload.gate_outcome == "denied";
                    entry.durationMs = payload.duration_ms;
                }
                Repaint();
            }
            catch
            {
                // feed is cosmetic; never let a malformed event throw in the UI
            }
        }

        private void HandlePermissionRequested(ServiceEvent evt)
        {
            try
            {
                var payload = Transport.ServiceClient.PayloadAs<PermissionRequestedPayload>(evt.Payload);
                if (payload == null) return;

                // Code changes go to the diff review window; everything else inline.
                if (IsDiffProposal(payload))
                {
                    ShowDiffView(payload);
                }
                else
                {
                    _pendingPermission = new PendingPermission
                    {
                        requestId = payload.request_id,
                        title = payload.title,
                        description = payload.description,
                        providerId = payload.provider_id
                    };
                }
                Repaint();
                Focus();
            }
            catch
            {
            }
        }

        private static bool IsDiffProposal(PermissionRequestedPayload payload)
        {
            return !string.IsNullOrEmpty(payload.file_path) &&
                   (payload.old_content != null || payload.new_content != null) &&
                   (payload.tool_id == "code.propose_patch" ||
                    payload.tool_id == "code.create_file" ||
                    payload.tool_id == "project.write_file");
        }

        private void ShowDiffView(PermissionRequestedPayload payload)
        {
            DiffViewWindow.ShowDiff(
                payload.file_path,
                payload.description,
                payload.old_content ?? "",
                payload.new_content ?? "",
                payload.patch_id,
                accepted =>
                {
                    var outcome = accepted ? PermissionOutcome.Allowed : PermissionOutcome.Denied;
                    _ = SplatterBridge.Instance.RespondToPermissionAsync(
                        payload.request_id, outcome, PermissionScope.Once);
                    Repaint();
                });
        }

        #endregion

        #region Helpers / payload DTOs

        // Strips the MCP server prefix so it reads as the bare tool name.
        private static string PrettyToolName(string toolId)
        {
            if (string.IsNullOrEmpty(toolId)) return toolId;
            if (toolId.StartsWith("mcp__"))
            {
                var rest = toolId.Substring("mcp__".Length);
                var sep = rest.IndexOf("__", StringComparison.Ordinal);
                if (sep >= 0) return rest.Substring(sep + 2);
            }
            return toolId;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static DateTimeOffset FromUnixMs(long ms) =>
            ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.Now;

        [Serializable]
        private class ToolActivityPayload
        {
            public string activity_id;
            public string session_id;
            public string provider_id;
            public string phase;
            public string tool_id;
            public string args_summary;
            public bool ok;
            public string error;
            public long duration_ms;
            public bool gated;
            public string gate_outcome;
        }

        [Serializable]
        private class PermissionRequestedPayload
        {
            public string request_id;
            public string title;
            public string description;
            public string tool_id;
            public string session_id;
            public string provider_id;
            public string file_path;
            public string old_content;
            public string new_content;
            public string patch_id;
        }

        #endregion
    }
}
