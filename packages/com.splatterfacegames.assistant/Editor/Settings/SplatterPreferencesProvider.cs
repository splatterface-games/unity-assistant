// Machine-scoped Splatter settings (Preferences). Where your agent CLIs live (path
// overrides + availability checks) and your API keys (stored in the OS keychain) are
// per-user/per-machine — not project-shared — so they live here rather than in
// Project Settings.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Splatter.Editor.Bridge;
using Splatter.Editor.Launch;

namespace Splatter.Editor.Settings
{
    public static class SplatterPreferencesProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreatePreferences()
        {
            return new SettingsProvider("Preferences/Splatterface Games/Assistant", SettingsScope.User)
            {
                label = "Splatterface Games Assistant",
                guiHandler = Draw,
                keywords = new HashSet<string>(new[]
                {
                    "Splatter", "AI", "Harness", "Agent", "Claude", "Codex", "Grok", "CLI", "Connect",
                    "API", "Key", "OpenAI", "Anthropic", "xAI"
                })
            };
        }

        private static Vector2 _scroll;

        private static void Draw(string searchContext)
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(10);

            DrawHarnessSection();

            EditorGUILayout.Space(14);

            // API keys (stored per-machine; the generators use them).
            SplatterSettingsProvider.DrawApiKeysSection();

            EditorGUILayout.EndScrollView();
        }

        // ---- Agent CLI location / validation ----

        private enum HState { Unknown, Checking, Ok, Fail }

        private sealed class HInfo
        {
            public HState State = HState.Unknown;
            public string Version;
            public string Path;
            public string Message;
            public string PathDraft;
        }

        private static readonly (string Id, string Label)[] Harnesses =
        {
            (HarnessLaunchService.ClaudeProviderId, "Claude Agent"),
            (HarnessLaunchService.CodexProviderId, "Codex"),
            (HarnessLaunchService.GrokProviderId, "Grok Build"),
        };

        private static readonly Dictionary<string, HInfo> _status = new();

        private static HInfo Status(string id)
        {
            if (!_status.TryGetValue(id, out var info)) { info = new HInfo(); _status[id] = info; }
            return info;
        }

        private static void DrawHarnessSection()
        {
            EditorGUILayout.LabelField("Agent CLIs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Splatter launches these CLIs in their own interactive terminals, wired to this " +
                "editor's tools (they own their own auth). Leave the path empty to use the CLI " +
                "from PATH, or point at a specific install.", MessageType.None);

            EditorGUILayout.Space(2);

            foreach (var (id, label) in Harnesses)
            {
                var info = Status(id);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(label, GUILayout.Width(110));

                var (text, color) = info.State switch
                {
                    HState.Ok => ($"Available · {info.Version}", new Color(0.40f, 0.78f, 0.42f)),
                    HState.Fail => ($"Unavailable · {info.Message}", new Color(0.86f, 0.40f, 0.40f)),
                    HState.Checking => ("Checking...", new Color(0.7f, 0.7f, 0.72f)),
                    _ => ("Not checked", new Color(0.6f, 0.6f, 0.62f)),
                };
                var prev = GUI.color;
                GUI.color = color;
                EditorGUILayout.LabelField(text, EditorStyles.miniLabel);
                GUI.color = prev;

                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(info.State == HState.Checking))
                {
                    if (GUILayout.Button("Check", GUILayout.Width(70)))
                        _ = CheckHarnessAsync(id);
                }
                EditorGUILayout.EndHorizontal();

                // Machine-scoped path override (EditorPrefs). Saved on Check or edit-end.
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("    Path", GUILayout.Width(110));
                var key = HarnessLaunchService.PathOverrideKey(id);
                info.PathDraft ??= EditorPrefs.GetString(key, "");
                var newDraft = EditorGUILayout.TextField(info.PathDraft);
                if (newDraft != info.PathDraft)
                {
                    info.PathDraft = newDraft;
                    EditorPrefs.SetString(key, newDraft ?? "");
                }
                EditorGUILayout.EndHorizontal();

                if (info.State == HState.Ok && !string.IsNullOrEmpty(info.Path))
                    EditorGUILayout.LabelField("    Resolved: " + info.Path, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Check all"))
                foreach (var (id, _) in Harnesses)
                    _ = CheckHarnessAsync(id);
        }

        private static async Task CheckHarnessAsync(string providerId)
        {
            var info = Status(providerId);
            info.State = HState.Checking;
            info.Message = null;
            InternalEditorUtility.RepaintAllViews();

            try
            {
                // Connecting also starts the service if needed — this validates the same
                // resolution the launcher uses.
                var bridge = SplatterBridge.Instance;
                if (!bridge.IsConnected)
                    await bridge.InitializeAsync();

                var resp = await bridge.CheckHarnessAsync(
                    providerId, HarnessLaunchService.GetPathOverride(providerId));

                MainThreadDispatcher.Enqueue(() =>
                {
                    info.State = resp != null && resp.ok ? HState.Ok : HState.Fail;
                    info.Version = resp?.version;
                    info.Path = resp?.path;
                    info.Message = resp?.message ?? "No response from service.";
                    InternalEditorUtility.RepaintAllViews();
                });
            }
            catch (Exception ex)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    info.State = HState.Fail;
                    info.Message = ex.Message;
                    InternalEditorUtility.RepaintAllViews();
                });
            }
        }
    }
}
