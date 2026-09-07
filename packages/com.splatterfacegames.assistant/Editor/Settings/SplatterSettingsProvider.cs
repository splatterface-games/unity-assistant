// Splatter Settings Provider - Project Settings integration with secure API key management

using System;
using System.Collections.Generic;
using System.IO;
using Splatter.Editor.Bootstrap;
using Splatter.Editor.Security;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.Settings
{
    /// <summary>
    /// Provides Splatter settings in Project Settings window.
    /// API keys are stored securely in the OS keychain, NOT in Unity.
    /// </summary>
    public static class SplatterSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider("Project/Splatterface Games/Assistant", SettingsScope.Project)
            {
                label = "Splatterface Games Assistant",
                guiHandler = DrawSettingsGUI,
                keywords = new HashSet<string>(new[] { "Splatterface Games", "Assistant", "AI", "Assistant", "OpenAI", "Claude", "Gemini", "API", "Key" })
            };

            return provider;
        }

        // Provider definitions (API-key providers used by the generators; the agent CLIs
        // own their own auth and are configured in Preferences).
        private static readonly ProviderInfo[] Providers = new[]
        {
            new ProviderInfo("openai", "OpenAI", "GPT-4, GPT-4o, o1, o3", "sk-...", "https://platform.openai.com/api-keys"),
            new ProviderInfo("anthropic", "Anthropic (Claude)", "Claude 3.5, Claude 4", "sk-ant-...", "https://console.anthropic.com/settings/keys"),
            new ProviderInfo("gemini", "Google (Gemini)", "Gemini 2.0, Gemini 2.5", "AIza...", "https://aistudio.google.com/apikey"),
            new ProviderInfo("lmstudio", "LM Studio (Local)", "Local models via LM Studio", null, null),
            new ProviderInfo("openai-compatible", "OpenAI-Compatible", "Ollama, vLLM, etc.", null, null),
        };

        private struct ProviderInfo
        {
            public string Id;
            public string Name;
            public string Description;
            public string KeyPrefix;
            public string KeyUrl;

            public ProviderInfo(string id, string name, string desc, string prefix, string url)
            {
                Id = id;
                Name = name;
                Description = desc;
                KeyPrefix = prefix;
                KeyUrl = url;
            }
        }

        // UI State
        private static Vector2 _scrollPosition;
        private static bool _showApiKeys = true;
        private static bool _showBehaviorSettings = true;
        private static bool _showLocalModels;
        private static bool _showAdvancedSettings;

        // API Key input state (temporary, not persisted)
        private static readonly Dictionary<string, string> _keyInputs = new();
        private static readonly Dictionary<string, bool> _showKeyInput = new();
        private static readonly Dictionary<string, bool> _keyValidating = new();

        private static void DrawSettingsGUI(string searchContext)
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.Space(10);

            DrawHeader();

            EditorGUILayout.Space(10);

            // API keys + harness connections are machine-scoped (keys live in the OS keychain),
            // so they live under Preferences now, not project-shared settings.
            EditorGUILayout.HelpBox(
                "API keys and harness connections are per-machine — they've moved to Preferences > Splatter AI.",
                MessageType.Info);
            if (GUILayout.Button("Open Preferences > Splatter AI"))
                SettingsService.OpenUserPreferences("Preferences/Splatterface Games/Assistant");

            EditorGUILayout.Space(10);

            // Local Models
            DrawLocalModelsSection();

            EditorGUILayout.Space(10);

            // Behavior Settings
            DrawBehaviorSettings();

            EditorGUILayout.Space(10);

            // Advanced Settings
            DrawAdvancedSettings();

            EditorGUILayout.Space(20);

            // About
            DrawAboutSection();

            EditorGUILayout.EndScrollView();
        }

        private static void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Splatter AI Settings", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            // Service status indicator
            var isRunning = ServiceBootstrap.IsRunning;
            var statusStyle = new GUIStyle(EditorStyles.miniLabel);
            statusStyle.normal.textColor = isRunning ? new Color(0.2f, 0.8f, 0.2f) : Color.gray;
            GUILayout.Label(isRunning ? "● Service Running" : "○ Service Stopped", statusStyle);

            EditorGUILayout.EndHorizontal();

            // Security notice
            EditorGUILayout.Space(5);
            var securityBox = new GUIStyle(EditorStyles.helpBox);
            EditorGUILayout.BeginVertical(securityBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(EditorGUIUtility.IconContent("d_Valid"), GUILayout.Width(20), GUILayout.Height(20));
            EditorGUILayout.LabelField("API keys are stored securely in your OS keychain, not in Unity project files.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // Rendered from the Preferences page (machine-scoped: keys live in the OS keychain).
        internal static void DrawApiKeysSection()
        {
            _showApiKeys = EditorGUILayout.Foldout(_showApiKeys, "API Keys", true, EditorStyles.foldoutHeader);
            if (!_showApiKeys) return;

            EditorGUI.indentLevel++;

            foreach (var provider in Providers)
            {
                // Skip local providers that don't need API keys
                if (provider.KeyPrefix == null && provider.Id != "openai-compatible")
                    continue;

                DrawProviderApiKey(provider);
                EditorGUILayout.Space(5);
            }

            EditorGUI.indentLevel--;
        }

        private static void DrawProviderApiKey(ProviderInfo provider)
        {
            var hasKey = SecureCredentialStore.HasCredential(provider.Id);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Provider header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(provider.Name, EditorStyles.boldLabel, GUILayout.Width(180));

            // Status
            if (hasKey)
            {
                var checkStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.2f, 0.8f, 0.2f) } };
                GUILayout.Label("✓ Configured", checkStyle);
            }
            else
            {
                var warnStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } };
                GUILayout.Label("Not configured", warnStyle);
            }

            GUILayout.FlexibleSpace();

            // Get API Key button
            if (!string.IsNullOrEmpty(provider.KeyUrl))
            {
                if (GUILayout.Button("Get API Key", EditorStyles.miniButton, GUILayout.Width(80)))
                {
                    Application.OpenURL(provider.KeyUrl);
                }
            }

            EditorGUILayout.EndHorizontal();

            // Description
            EditorGUILayout.LabelField(provider.Description, EditorStyles.wordWrappedMiniLabel);

            // Show/hide key input
            if (!_showKeyInput.ContainsKey(provider.Id))
                _showKeyInput[provider.Id] = false;

            if (hasKey)
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(_showKeyInput[provider.Id] ? "Cancel" : "Change Key", GUILayout.Width(100)))
                {
                    _showKeyInput[provider.Id] = !_showKeyInput[provider.Id];
                    if (!_showKeyInput[provider.Id])
                        _keyInputs.Remove(provider.Id);
                }

                if (GUILayout.Button("Remove Key", GUILayout.Width(100)))
                {
                    if (EditorUtility.DisplayDialog("Remove API Key",
                        $"Are you sure you want to remove the {provider.Name} API key from your keychain?",
                        "Remove", "Cancel"))
                    {
                        SecureCredentialStore.DeleteCredential(provider.Id);
                        _showKeyInput[provider.Id] = false;
                        _keyInputs.Remove(provider.Id);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                _showKeyInput[provider.Id] = true;
            }

            // Key input field
            if (_showKeyInput[provider.Id])
            {
                EditorGUILayout.Space(3);

                if (!_keyInputs.ContainsKey(provider.Id))
                    _keyInputs[provider.Id] = "";

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("API Key:", GUILayout.Width(60));

                // Password field for security
                _keyInputs[provider.Id] = EditorGUILayout.PasswordField(_keyInputs[provider.Id]);

                var canSave = !string.IsNullOrWhiteSpace(_keyInputs[provider.Id]);
                if (provider.KeyPrefix != null)
                    canSave = canSave && _keyInputs[provider.Id].StartsWith(provider.KeyPrefix.TrimEnd('.'));

                GUI.enabled = canSave;
                if (GUILayout.Button("Save", GUILayout.Width(60)))
                {
                    SaveApiKey(provider.Id, _keyInputs[provider.Id]);
                }
                GUI.enabled = true;

                EditorGUILayout.EndHorizontal();

                // Hint
                if (!string.IsNullOrEmpty(provider.KeyPrefix))
                {
                    EditorGUILayout.LabelField($"Key should start with: {provider.KeyPrefix}", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private static void SaveApiKey(string providerId, string apiKey)
        {
            if (SecureCredentialStore.SetCredential(providerId, apiKey))
            {
                _keyInputs.Remove(providerId);
                _showKeyInput[providerId] = false;
                Debug.Log($"[Splatter] {providerId} API key saved to secure keychain");

                // Notify service if running
                NotifyServiceOfCredentialChange(providerId);
            }
            else
            {
                EditorUtility.DisplayDialog("Error", "Failed to save API key to secure storage.", "OK");
            }
        }

        private static async void NotifyServiceOfCredentialChange(string providerId)
        {
            if (!ServiceBootstrap.IsRunning)
                return;

            try
            {
                // Send credential refresh command to service
                var client = Transport.ServiceClient.Instance;
                if (client.IsConnected)
                {
                    await client.SendRequestAsync<object, object>(
                        "credentials.refresh",
                        new { provider_id = providerId },
                        TimeSpan.FromSeconds(5));
                    Debug.Log($"[Splatter] Notified service of credential change for {providerId}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Splatter] Failed to notify service of credential change: {ex.Message}");
            }
        }

        private static void DrawLocalModelsSection()
        {
            _showLocalModels = EditorGUILayout.Foldout(_showLocalModels, "Local Models", true, EditorStyles.foldoutHeader);
            if (!_showLocalModels) return;

            EditorGUI.indentLevel++;

            var settings = SplatterSettings.instance;

            // LM Studio
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("LM Studio", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Run local LLMs with LM Studio server", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Server URL", GUILayout.Width(80));
            var newLmStudioUrl = EditorGUILayout.TextField(settings.LMStudioUrl);
            if (newLmStudioUrl != settings.LMStudioUrl)
            {
                settings.LMStudioUrl = newLmStudioUrl;
                settings.Save();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);

            // ComfyUI
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("ComfyUI", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Local image generation with Stable Diffusion", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Server URL", GUILayout.Width(80));
            var newComfyUrl = EditorGUILayout.TextField(settings.ComfyUIUrl);
            if (newComfyUrl != settings.ComfyUIUrl)
            {
                settings.ComfyUIUrl = newComfyUrl;
                settings.Save();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);

            // Hunyuan3D
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Hunyuan3D-2", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Local 3D mesh generation", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Server URL", GUILayout.Width(80));
            var newHunyuanUrl = EditorGUILayout.TextField(settings.Hunyuan3DUrl);
            if (newHunyuanUrl != settings.Hunyuan3DUrl)
            {
                settings.Hunyuan3DUrl = newHunyuanUrl;
                settings.Save();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUI.indentLevel--;
        }

        private static void DrawBehaviorSettings()
        {
            _showBehaviorSettings = EditorGUILayout.Foldout(_showBehaviorSettings, "Behavior", true, EditorStyles.foldoutHeader);
            if (!_showBehaviorSettings) return;

            EditorGUI.indentLevel++;

            var settings = SplatterSettings.instance;

            // Auto-start service
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Auto-start Service", GUILayout.Width(150));
            var newAutoStart = EditorGUILayout.Toggle(settings.AutoStartService);
            if (newAutoStart != settings.AutoStartService)
            {
                settings.AutoStartService = newAutoStart;
                settings.Save();
            }
            EditorGUILayout.EndHorizontal();

            // Permission mode is chosen per-launch in the Splatter window (Read only /
            // Ask before write / Full auto), not as a global setting.

            // Verbose logging
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Verbose Logging", GUILayout.Width(150));
            var newVerbose = EditorGUILayout.Toggle(settings.VerboseLogging);
            if (newVerbose != settings.VerboseLogging)
            {
                settings.VerboseLogging = newVerbose;
                settings.Save();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private static void DrawAdvancedSettings()
        {
            _showAdvancedSettings = EditorGUILayout.Foldout(_showAdvancedSettings, "Advanced", true, EditorStyles.foldoutHeader);
            if (!_showAdvancedSettings) return;

            EditorGUI.indentLevel++;

            // Service controls
            EditorGUILayout.LabelField("Service Control", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (!ServiceBootstrap.IsRunning)
            {
                if (GUILayout.Button("Start Service", GUILayout.Width(120)))
                {
                    _ = ServiceBootstrap.StartServiceAsync();
                }
            }
            else
            {
                if (GUILayout.Button("Stop Service", GUILayout.Width(120)))
                {
                    ServiceBootstrap.StopService();
                }
                if (GUILayout.Button("Restart Service", GUILayout.Width(120)))
                {
                    _ = ServiceBootstrap.RestartServiceAsync();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Service info
            if (ServiceBootstrap.IsRunning && !string.IsNullOrEmpty(ServiceBootstrap.ServiceUrl))
            {
                EditorGUILayout.LabelField("Service URL:", ServiceBootstrap.ServiceUrl);
            }

            // Data paths
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Data Locations", EditorStyles.boldLabel);

            var dataDir = GetDataDirectory();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Project Data:", dataDir, EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Open", GUILayout.Width(50)))
            {
                if (Directory.Exists(dataDir))
                    EditorUtility.RevealInFinder(dataDir);
                else
                    Directory.CreateDirectory(dataDir);
            }
            EditorGUILayout.EndHorizontal();

            // Credential location info
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Credential Storage:", GetCredentialStorageInfo(), EditorStyles.wordWrappedMiniLabel);

            // Danger zone
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Danger Zone", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear All API Keys", GUILayout.Width(150)))
            {
                if (EditorUtility.DisplayDialog("Clear All API Keys",
                    "This will remove all API keys from your secure keychain. Are you sure?",
                    "Clear All", "Cancel"))
                {
                    foreach (var provider in Providers)
                    {
                        SecureCredentialStore.DeleteCredential(provider.Id);
                    }
                    Debug.Log("[Splatter] All API keys cleared from keychain");
                }
            }

            if (GUILayout.Button("Reset All Settings", GUILayout.Width(150)))
            {
                if (EditorUtility.DisplayDialog("Reset Settings",
                    "This will reset all Splatter settings to defaults. API keys will NOT be affected.",
                    "Reset", "Cancel"))
                {
                    SplatterSettings.instance.Reset();
                    Debug.Log("[Splatter] Settings reset to defaults");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private static void DrawAboutSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Splatterface Games Assistant", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Version: 0.1.0", EditorStyles.miniLabel);
            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("A local-first AI assistant for Unity development.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Your API keys never leave your machine.", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Documentation", GUILayout.Width(100)))
            {
                Application.OpenURL("https://github.com/splatterfacegames/unity-assistant");
            }
            if (GUILayout.Button("Report Issue", GUILayout.Width(100)))
            {
                Application.OpenURL("https://github.com/splatterfacegames/unity-assistant/issues");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private static string GetDataDirectory()
        {
            var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var projectName = Path.GetFileName(projectPath);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Splatterface Games", "Assistant",
                "Projects",
                projectName);
        }

        private static string GetCredentialStorageInfo()
        {
#if UNITY_EDITOR_WIN
            return "Windows Credential Manager";
#elif UNITY_EDITOR_OSX
            return "macOS Keychain";
#elif UNITY_EDITOR_LINUX
            return "Linux Secret Service / Encrypted File";
#else
            return "Platform-specific secure storage";
#endif
        }
    }
}
