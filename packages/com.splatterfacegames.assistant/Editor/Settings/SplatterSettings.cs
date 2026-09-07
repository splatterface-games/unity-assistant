// Splatter Settings - Persistent editor settings (API keys are NOT stored here)

using System;
using System.IO;
using UnityEngine;

namespace Splatter.Editor.Settings
{
    /// <summary>
    /// Persistent settings for Splatter AI.
    /// NOTE: API keys are stored in the OS keychain via SecureCredentialStore, not here.
    /// Harness CLI path overrides are machine-scoped and live in EditorPrefs.
    /// </summary>
    [Serializable]
    public class SplatterSettings
    {
        private const string SettingsPath = "ProjectSettings/SplatterSettings.json";
        private static SplatterSettings _instance;

        /// <summary>
        /// Singleton instance. Loads from disk on first access.
        /// </summary>
        public static SplatterSettings instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Load();
                }
                return _instance;
            }
        }

        // Local model URLs (generators)
        [SerializeField] private string _lmStudioUrl = "http://localhost:1234";
        [SerializeField] private string _comfyUIUrl = "http://localhost:8188";
        [SerializeField] private string _hunyuan3DUrl = "http://localhost:8080";

        // Behavior settings
        [SerializeField] private bool _autoStartService = true;
        [SerializeField] private bool _verboseLogging;

        // Service settings
        [SerializeField] private int _servicePort;

        /// <summary>
        /// LM Studio server URL for local LLM inference.
        /// </summary>
        public string LMStudioUrl
        {
            get => _lmStudioUrl;
            set => _lmStudioUrl = value;
        }

        /// <summary>
        /// ComfyUI server URL for local image generation.
        /// </summary>
        public string ComfyUIUrl
        {
            get => _comfyUIUrl;
            set => _comfyUIUrl = value;
        }

        /// <summary>
        /// Hunyuan3D-2 server URL for local 3D mesh generation.
        /// </summary>
        public string Hunyuan3DUrl
        {
            get => _hunyuan3DUrl;
            set => _hunyuan3DUrl = value;
        }

        /// <summary>
        /// Whether to auto-start the service when Unity opens.
        /// </summary>
        public bool AutoStartService
        {
            get => _autoStartService;
            set => _autoStartService = value;
        }

        /// <summary>
        /// Enable verbose debug logging.
        /// </summary>
        public bool VerboseLogging
        {
            get => _verboseLogging;
            set => _verboseLogging = value;
        }

        /// <summary>
        /// Custom service port (0 = auto-select).
        /// </summary>
        public int ServicePort
        {
            get => _servicePort;
            set => _servicePort = value;
        }

        /// <summary>
        /// Save settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                var json = JsonUtility.ToJson(this, true);
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset all settings to defaults.
        /// </summary>
        public void Reset()
        {
            _lmStudioUrl = "http://localhost:1234";
            _comfyUIUrl = "http://localhost:8188";
            _hunyuan3DUrl = "http://localhost:8080";
            _autoStartService = true;
            _verboseLogging = false;
            _servicePort = 0;
            Save();
        }

        private static SplatterSettings Load()
        {
            try
            {
                // JsonUtility ignores unknown fields, so settings files written by older
                // versions (preferred provider/model, permission mode, ...) load cleanly.
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonUtility.FromJson<SplatterSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Splatter] Failed to load settings, using defaults: {ex.Message}");
            }

            return new SplatterSettings();
        }

        /// <summary>
        /// Force reload settings from disk.
        /// </summary>
        public static void Reload()
        {
            _instance = null;
            _ = instance; // Triggers reload
        }
    }
}
