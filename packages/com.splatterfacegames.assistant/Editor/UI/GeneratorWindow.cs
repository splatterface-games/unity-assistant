// Generator Window - AI content generation UI for Unity Editor
// M9.4: Unified asset-scoped generator window

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Splatter.Editor.Bootstrap;
using Splatter.Editor.Transport;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.UI
{
    /// <summary>
    /// Main editor window for AI content generation.
    /// Supports image, material, mesh, sound, and animation generation.
    /// </summary>
    public class GeneratorWindow : EditorWindow
    {
        private static GeneratorWindow _instance;

        [MenuItem("Window/Splatterface Games/Assistant/Generator")]
        public static void ShowWindow()
        {
            _instance = GetWindow<GeneratorWindow>("AI Generator");
            _instance.minSize = new Vector2(500, 600);
        }

        public static void ShowWindowForAsset(UnityEngine.Object asset)
        {
            ShowWindow();
            if (_instance != null)
            {
                _instance._targetAsset = asset;
                _instance._targetAssetPath = AssetDatabase.GetAssetPath(asset);
                _instance.DetectModalityFromAsset();
            }
        }

        #region State

        // Target asset
        private UnityEngine.Object _targetAsset;
        private string _targetAssetPath;
        private string _targetAssetGuid;

        // Modality selection
        private GeneratorModality _selectedModality = GeneratorModality.Image;
        private readonly string[] _modalityNames = { "Image", "Material", "Mesh", "Sound", "Animation" };

        // Provider/Model
        private string _selectedProvider = "comfyui";
        private string _selectedModel = "";
        private readonly string[] _imageProviders = { "comfyui", "openai", "stability", "gemini" };
        private readonly string[] _meshProviders = { "kao", "hunyuan3d", "meshy" };
        private readonly string[] _soundProviders = { "elevenlabs" };
        private readonly string[] _materialProviders = { "comfyui", "stability" };

        // Generation parameters
        private string _prompt = "";
        private string _negativePrompt = "";
        private int _seed = -1;
        private int _width = 512;
        private int _height = 512;
        private int _steps = 30;
        private float _guidanceScale = 7.5f;
        private string _outputFormat = "png";

        // Mesh-specific
        private int _octreeResolution = 256;
        private bool _generateTexture = true;

        // Sound-specific
        private float _duration = 5.0f;
        private bool _loop;

        // Job tracking
        private readonly List<GeneratorJobInfo> _activeJobs = new();
        private readonly List<GeneratorHistoryEntry> _history = new();
        private GeneratorJobInfo _selectedJob;

        // UI state
        private Vector2 _scrollPosition;
        private Vector2 _historyScrollPosition;
        private bool _showAdvanced;
        private bool _isGenerating;
        private string _statusMessage = "";
        private float _progress;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _historyItemStyle;
        private bool _stylesInitialized;

        #endregion

        #region Lifecycle

        private void OnEnable()
        {
            _instance = this;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.update += PollJobStatus;

            // Initialize with current selection
            OnSelectionChanged();
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.update -= PollJobStatus;
        }

        private void OnSelectionChanged()
        {
            if (Selection.activeObject != null)
            {
                _targetAsset = Selection.activeObject;
                _targetAssetPath = AssetDatabase.GetAssetPath(_targetAsset);
                _targetAssetGuid = AssetDatabase.AssetPathToGUID(_targetAssetPath);
                DetectModalityFromAsset();
                Repaint();
            }
        }

        private void DetectModalityFromAsset()
        {
            if (_targetAsset == null) return;

            if (_targetAsset is Texture2D || _targetAsset is Sprite)
            {
                _selectedModality = GeneratorModality.Image;
            }
            else if (_targetAsset is Material)
            {
                _selectedModality = GeneratorModality.Material;
            }
            else if (_targetAsset is Mesh || _targetAsset is GameObject)
            {
                _selectedModality = GeneratorModality.Mesh;
            }
            else if (_targetAsset is AudioClip)
            {
                _selectedModality = GeneratorModality.Sound;
            }
            else if (_targetAsset is AnimationClip)
            {
                _selectedModality = GeneratorModality.Animation;
            }
        }

        #endregion

        #region GUI

        private void OnGUI()
        {
            InitializeStyles();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            // Header
            DrawHeader();

            EditorGUILayout.Space(10);

            // Asset card
            DrawAssetCard();

            EditorGUILayout.Space(10);

            // Modality tabs
            DrawModalityTabs();

            EditorGUILayout.Space(10);

            // Provider/Model selection
            DrawProviderSection();

            EditorGUILayout.Space(10);

            // Generation parameters
            DrawParametersSection();

            EditorGUILayout.Space(10);

            // Generate button
            DrawGenerateButton();

            EditorGUILayout.Space(10);

            // Active jobs
            if (_activeJobs.Count > 0)
            {
                DrawActiveJobs();
                EditorGUILayout.Space(10);
            }

            // History
            DrawHistorySection();

            EditorGUILayout.EndScrollView();

            // Status bar
            DrawStatusBar();
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                margin = new RectOffset(0, 0, 5, 5)
            };

            _sectionStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(5, 5, 5, 5)
            };

            _historyItemStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 5, 5),
                margin = new RectOffset(0, 0, 2, 2)
            };

            _stylesInitialized = true;
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("AI Generator", _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", GUILayout.Width(60)))
            {
                RefreshHistory();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAssetCard()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);

            EditorGUILayout.LabelField("Target Asset", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            // Asset preview
            if (_targetAsset != null)
            {
                var preview = AssetPreview.GetAssetPreview(_targetAsset);
                if (preview != null)
                {
                    GUILayout.Label(preview, GUILayout.Width(64), GUILayout.Height(64));
                }
                else
                {
                    GUILayout.Box("No Preview", GUILayout.Width(64), GUILayout.Height(64));
                }

                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(_targetAsset.name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(_targetAssetPath, EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Type: {_targetAsset.GetType().Name}", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
            else
            {
                GUILayout.Box("Select Asset", GUILayout.Width(64), GUILayout.Height(64));
                EditorGUILayout.LabelField("No asset selected. Select an asset to generate content for it, or generate a new asset.");
            }

            EditorGUILayout.EndHorizontal();

            // New asset option
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Generate New Asset"))
            {
                _targetAsset = null;
                _targetAssetPath = GetDefaultOutputPath();
                _targetAssetGuid = null;
            }
            if (_targetAsset != null && GUILayout.Button("Clear Selection"))
            {
                _targetAsset = null;
                _targetAssetPath = GetDefaultOutputPath();
                _targetAssetGuid = null;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawModalityTabs()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            EditorGUILayout.LabelField("Generation Type", EditorStyles.boldLabel);

            var newModality = (GeneratorModality)GUILayout.Toolbar(
                (int)_selectedModality,
                _modalityNames);

            if (newModality != _selectedModality)
            {
                _selectedModality = newModality;
                UpdateProviderForModality();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawProviderSection()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            EditorGUILayout.LabelField("Provider & Model", EditorStyles.boldLabel);

            var providers = GetProvidersForModality();
            var providerIndex = Array.IndexOf(providers, _selectedProvider);
            if (providerIndex < 0) providerIndex = 0;

            var newIndex = EditorGUILayout.Popup("Provider", providerIndex, providers);
            if (newIndex != providerIndex || string.IsNullOrEmpty(_selectedProvider))
            {
                _selectedProvider = providers[newIndex];
            }

            // Model field (free text for now, could be dropdown from capabilities)
            _selectedModel = EditorGUILayout.TextField("Model", _selectedModel);

            // Provider-specific info
            EditorGUILayout.HelpBox(GetProviderDescription(), MessageType.Info);

            EditorGUILayout.EndVertical();
        }

        private void DrawParametersSection()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            EditorGUILayout.LabelField("Generation Parameters", EditorStyles.boldLabel);

            // Prompt (always shown)
            EditorGUILayout.LabelField("Prompt");
            _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.Height(60));

            // Modality-specific parameters
            switch (_selectedModality)
            {
                case GeneratorModality.Image:
                    DrawImageParameters();
                    break;
                case GeneratorModality.Material:
                    DrawMaterialParameters();
                    break;
                case GeneratorModality.Mesh:
                    DrawMeshParameters();
                    break;
                case GeneratorModality.Sound:
                    DrawSoundParameters();
                    break;
                case GeneratorModality.Animation:
                    DrawAnimationParameters();
                    break;
            }

            // Advanced section
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced Settings");
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                _negativePrompt = EditorGUILayout.TextField("Negative Prompt", _negativePrompt);
                _seed = EditorGUILayout.IntField("Seed (-1 = random)", _seed);
                _steps = EditorGUILayout.IntSlider("Steps", _steps, 1, 100);
                _guidanceScale = EditorGUILayout.Slider("Guidance Scale", _guidanceScale, 1f, 20f);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawImageParameters()
        {
            EditorGUILayout.BeginHorizontal();
            _width = EditorGUILayout.IntField("Width", _width);
            _height = EditorGUILayout.IntField("Height", _height);
            EditorGUILayout.EndHorizontal();

            // Preset sizes
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("512x512")) { _width = 512; _height = 512; }
            if (GUILayout.Button("768x768")) { _width = 768; _height = 768; }
            if (GUILayout.Button("1024x1024")) { _width = 1024; _height = 1024; }
            if (GUILayout.Button("1024x768")) { _width = 1024; _height = 768; }
            EditorGUILayout.EndHorizontal();

            _outputFormat = EditorGUILayout.TextField("Output Format", _outputFormat);
        }

        private void DrawMaterialParameters()
        {
            EditorGUILayout.BeginHorizontal();
            _width = EditorGUILayout.IntField("Map Size", _width);
            _height = _width; // Keep square for materials
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Generated Maps:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  - Albedo/Diffuse", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  - Normal", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  - Roughness/Smoothness", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  - Metallic", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  - Height/Displacement", EditorStyles.miniLabel);
        }

        private void DrawMeshParameters()
        {
            _octreeResolution = EditorGUILayout.IntSlider("Resolution", _octreeResolution, 64, 512);
            _generateTexture = EditorGUILayout.Toggle("Generate Texture", _generateTexture);

            var formats = new[] { "glb", "obj", "ply" };
            var formatIndex = Array.IndexOf(formats, _outputFormat);
            if (formatIndex < 0) formatIndex = 0;
            formatIndex = EditorGUILayout.Popup("Output Format", formatIndex, formats);
            _outputFormat = formats[formatIndex];

            // Reference image
            EditorGUILayout.LabelField("Reference Image (optional):");
            var refTexture = EditorGUILayout.ObjectField("Image", null, typeof(Texture2D), false) as Texture2D;
        }

        private void DrawSoundParameters()
        {
            _duration = EditorGUILayout.Slider("Duration (seconds)", _duration, 0.5f, 30f);
            _loop = EditorGUILayout.Toggle("Loop", _loop);

            var formats = new[] { "wav", "mp3", "ogg" };
            var formatIndex = Array.IndexOf(formats, _outputFormat);
            if (formatIndex < 0) formatIndex = 0;
            formatIndex = EditorGUILayout.Popup("Output Format", formatIndex, formats);
            _outputFormat = formats[formatIndex];
        }

        private void DrawAnimationParameters()
        {
            _duration = EditorGUILayout.Slider("Duration (seconds)", _duration, 0.5f, 60f);
            _loop = EditorGUILayout.Toggle("Loop", _loop);

            EditorGUILayout.HelpBox("Animation generation is experimental. Best results with humanoid motion descriptions.", MessageType.Info);
        }

        private void DrawGenerateButton()
        {
            EditorGUI.BeginDisabledGroup(_isGenerating || string.IsNullOrWhiteSpace(_prompt));

            var buttonText = _isGenerating ? "Generating..." : $"Generate {_modalityNames[(int)_selectedModality]}";

            if (GUILayout.Button(buttonText, GUILayout.Height(40)))
            {
                StartGeneration();
            }

            EditorGUI.EndDisabledGroup();

            if (_isGenerating && _progress > 0)
            {
                var rect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, _progress, $"{_progress * 100:F0}%");
            }
        }

        private void DrawActiveJobs()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            EditorGUILayout.LabelField("Active Jobs", EditorStyles.boldLabel);

            foreach (var job in _activeJobs.ToList())
            {
                EditorGUILayout.BeginHorizontal(_historyItemStyle);

                EditorGUILayout.LabelField(job.JobId, GUILayout.Width(150));
                EditorGUILayout.LabelField(job.Status, GUILayout.Width(80));

                if (job.Status == "running" || job.Status == "queued")
                {
                    if (GUILayout.Button("Cancel", GUILayout.Width(60)))
                    {
                        CancelJob(job.JobId);
                    }
                }
                else if (job.Status == "complete")
                {
                    if (GUILayout.Button("Apply", GUILayout.Width(60)))
                    {
                        ApplyResult(job.JobId, job.ResultId);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawHistorySection()
        {
            EditorGUILayout.BeginVertical(_sectionStyle);
            EditorGUILayout.LabelField("History", EditorStyles.boldLabel);

            if (_history.Count == 0)
            {
                EditorGUILayout.HelpBox("No generation history yet.", MessageType.Info);
            }
            else
            {
                _historyScrollPosition = EditorGUILayout.BeginScrollView(
                    _historyScrollPosition,
                    GUILayout.Height(150));

                foreach (var entry in _history.TakeLast(20).Reverse())
                {
                    EditorGUILayout.BeginHorizontal(_historyItemStyle);

                    // Thumbnail
                    if (entry.Thumbnail != null)
                    {
                        GUILayout.Label(entry.Thumbnail, GUILayout.Width(40), GUILayout.Height(40));
                    }
                    else
                    {
                        GUILayout.Box("", GUILayout.Width(40), GUILayout.Height(40));
                    }

                    EditorGUILayout.BeginVertical();
                    EditorGUILayout.LabelField(TruncatePrompt(entry.Prompt, 50), EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"{entry.Modality} | {entry.Provider} | {entry.CreatedAt:g}", EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();

                    if (GUILayout.Button("Reuse", GUILayout.Width(50)))
                    {
                        _prompt = entry.Prompt;
                        _selectedModality = entry.Modality;
                        _selectedProvider = entry.Provider;
                        _seed = entry.Seed;
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (!ServiceBootstrap.IsRunning)
            {
                EditorGUILayout.LabelField("Service not running", EditorStyles.miniLabel);
                if (GUILayout.Button("Start", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
                    _ = ServiceBootstrap.StartServiceAsync();
                }
            }
            else if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.LabelField(_statusMessage, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("Ready", EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"Jobs: {_activeJobs.Count}", EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Generation

        private async void StartGeneration()
        {
            if (string.IsNullOrWhiteSpace(_prompt))
            {
                _statusMessage = "Please enter a prompt";
                return;
            }

            _isGenerating = true;
            _statusMessage = "Submitting job...";
            Repaint();

            try
            {
                var parameters = BuildParameters();
                var outputPath = _targetAssetPath ?? GetDefaultOutputPath();

                var request = new GeneratorSubmitMessage
                {
                    Modality = _selectedModality.ToString().ToLowerInvariant(),
                    ProviderId = _selectedProvider,
                    ModelId = _selectedModel,
                    Mode = "generate",
                    TargetAssetPath = outputPath,
                    Parameters = parameters
                };

                // Send via service client
                var response = await ServiceClient.Instance.SendRequestAsync<GeneratorSubmitMessage, GeneratorSubmitResponse>(
                    "generator.submit",
                    request,
                    TimeSpan.FromSeconds(30));

                if (response != null && !string.IsNullOrEmpty(response.JobId))
                {
                    var job = new GeneratorJobInfo
                    {
                        JobId = response.JobId,
                        Status = response.Status ?? "queued",
                        Modality = _selectedModality,
                        Provider = _selectedProvider,
                        Prompt = _prompt
                    };
                    _activeJobs.Add(job);
                    _statusMessage = $"Job submitted: {response.JobId}";
                }
                else
                {
                    _statusMessage = "Failed to submit job";
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                Debug.LogError($"[Splatter Generator] {ex}");
            }
            finally
            {
                _isGenerating = false;
                Repaint();
            }
        }

        private Dictionary<string, object> BuildParameters()
        {
            var parameters = new Dictionary<string, object>
            {
                ["prompt"] = _prompt
            };

            if (!string.IsNullOrEmpty(_negativePrompt))
                parameters["negative_prompt"] = _negativePrompt;

            if (_seed >= 0)
                parameters["seed"] = _seed;

            parameters["steps"] = _steps;
            parameters["guidance_scale"] = _guidanceScale;

            switch (_selectedModality)
            {
                case GeneratorModality.Image:
                case GeneratorModality.Material:
                    parameters["width"] = _width;
                    parameters["height"] = _height;
                    parameters["output_format"] = _outputFormat;
                    break;

                case GeneratorModality.Mesh:
                    parameters["octree_resolution"] = _octreeResolution;
                    parameters["generate_texture"] = _generateTexture;
                    parameters["output_format"] = _outputFormat;
                    break;

                case GeneratorModality.Sound:
                    parameters["duration"] = _duration;
                    parameters["loop"] = _loop;
                    parameters["output_format"] = _outputFormat;
                    break;

                case GeneratorModality.Animation:
                    parameters["duration"] = _duration;
                    parameters["loop"] = _loop;
                    break;
            }

            return parameters;
        }

        private async void CancelJob(string jobId)
        {
            try
            {
                var request = new GeneratorCancelMessage { JobId = jobId };
                var response = await ServiceClient.Instance.SendRequestAsync<GeneratorCancelMessage, GeneratorCancelResponse>(
                    "generator.cancel",
                    request,
                    TimeSpan.FromSeconds(10));

                if (response?.Cancelled == true)
                {
                    var job = _activeJobs.FirstOrDefault(j => j.JobId == jobId);
                    if (job != null)
                    {
                        job.Status = "cancelled";
                    }
                    _statusMessage = "Job cancelled";
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"Cancel failed: {ex.Message}";
            }

            Repaint();
        }

        private async void ApplyResult(string jobId, string resultId)
        {
            try
            {
                var request = new GeneratorApplyMessage
                {
                    JobId = jobId,
                    ResultId = resultId,
                    TargetPath = _targetAssetPath
                };

                var response = await ServiceClient.Instance.SendRequestAsync<GeneratorApplyMessage, GeneratorApplyResponse>(
                    "generator.apply",
                    request,
                    TimeSpan.FromSeconds(30));

                if (response?.Success == true)
                {
                    _statusMessage = $"Applied to: {response.AssetPath}";
                    AssetDatabase.Refresh();

                    // Remove from active jobs
                    _activeJobs.RemoveAll(j => j.JobId == jobId);
                }
                else
                {
                    _statusMessage = "Apply failed";
                }
            }
            catch (Exception ex)
            {
                _statusMessage = $"Apply failed: {ex.Message}";
            }

            Repaint();
        }

        private double _lastPollTime;
        private const double PollInterval = 2.0;

        private async void PollJobStatus()
        {
            if (_activeJobs.Count == 0) return;
            if (EditorApplication.timeSinceStartup - _lastPollTime < PollInterval) return;

            _lastPollTime = EditorApplication.timeSinceStartup;

            foreach (var job in _activeJobs.ToList())
            {
                if (job.Status == "complete" || job.Status == "failed" || job.Status == "cancelled")
                    continue;

                try
                {
                    var request = new GeneratorStatusMessage { JobId = job.JobId };
                    var response = await ServiceClient.Instance.SendRequestAsync<GeneratorStatusMessage, GeneratorStatusResponse>(
                        "generator.status",
                        request,
                        TimeSpan.FromSeconds(5));

                    if (response != null)
                    {
                        job.Status = response.Status;
                        job.Progress = response.Progress;
                        job.ResultId = response.Results?.FirstOrDefault()?.Id;

                        if (response.Status == "complete")
                        {
                            // Add to history
                            _history.Add(new GeneratorHistoryEntry
                            {
                                Prompt = job.Prompt,
                                Modality = job.Modality,
                                Provider = job.Provider,
                                Seed = _seed,
                                CreatedAt = DateTime.Now
                            });
                        }
                    }
                }
                catch
                {
                    // Ignore poll errors
                }
            }

            // Clean up old completed jobs
            _activeJobs.RemoveAll(j =>
                (j.Status == "complete" || j.Status == "failed" || j.Status == "cancelled") &&
                (DateTime.Now - j.CreatedAt).TotalMinutes > 5);

            Repaint();
        }

        private void RefreshHistory()
        {
            // Would call generator.history API
            _statusMessage = "History refreshed";
            Repaint();
        }

        #endregion

        #region Helpers

        private string[] GetProvidersForModality()
        {
            return _selectedModality switch
            {
                GeneratorModality.Image => _imageProviders,
                GeneratorModality.Material => _materialProviders,
                GeneratorModality.Mesh => _meshProviders,
                GeneratorModality.Sound => _soundProviders,
                GeneratorModality.Animation => new[] { "motion" },
                _ => _imageProviders
            };
        }

        private void UpdateProviderForModality()
        {
            var providers = GetProvidersForModality();
            if (!providers.Contains(_selectedProvider))
            {
                _selectedProvider = providers[0];
            }
        }

        private string GetProviderDescription()
        {
            return _selectedProvider switch
            {
                "comfyui" => "ComfyUI - Local Stable Diffusion (localhost:8188)",
                "kao" => "Kao - Local 3D generation with multiple backends (localhost:11435)",
                "hunyuan3d" => "Hunyuan3D - Local mesh generation via Gradio (localhost:7860)",
                "meshy" => "Meshy - Cloud 3D generation API",
                "openai" => "OpenAI DALL-E - Cloud image generation",
                "stability" => "Stability AI - Cloud image generation",
                "gemini" => "Google Gemini - Cloud multimodal generation",
                "elevenlabs" => "ElevenLabs - Cloud sound/voice generation",
                _ => $"Provider: {_selectedProvider}"
            };
        }

        private string GetDefaultOutputPath()
        {
            var folder = "Assets/GeneratedAssets";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "GeneratedAssets");
            }

            var extension = _selectedModality switch
            {
                GeneratorModality.Image => "png",
                GeneratorModality.Material => "mat",
                GeneratorModality.Mesh => "glb",
                GeneratorModality.Sound => "wav",
                GeneratorModality.Animation => "anim",
                _ => "asset"
            };

            return $"{folder}/Generated_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}";
        }

        private static string TruncatePrompt(string prompt, int maxLength)
        {
            if (string.IsNullOrEmpty(prompt)) return "";
            return prompt.Length <= maxLength ? prompt : prompt[..(maxLength - 3)] + "...";
        }

        #endregion
    }

    #region Data Types

    public enum GeneratorModality
    {
        Image = 0,
        Material = 1,
        Mesh = 2,
        Sound = 3,
        Animation = 4
    }

    internal class GeneratorJobInfo
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public float Progress { get; set; }
        public GeneratorModality Modality { get; set; }
        public string Provider { get; set; }
        public string Prompt { get; set; }
        public string ResultId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    internal class GeneratorHistoryEntry
    {
        public string Prompt { get; set; }
        public GeneratorModality Modality { get; set; }
        public string Provider { get; set; }
        public int Seed { get; set; }
        public DateTime CreatedAt { get; set; }
        public Texture2D Thumbnail { get; set; }
    }

    // Message types for service communication
    [Serializable]
    internal class GeneratorSubmitMessage
    {
        public string Modality;
        public string ProviderId;
        public string ModelId;
        public string Mode;
        public string TargetAssetPath;
        public Dictionary<string, object> Parameters;
    }

    [Serializable]
    internal class GeneratorSubmitResponse
    {
        public string JobId;
        public string Status;
    }

    [Serializable]
    internal class GeneratorStatusMessage
    {
        public string JobId;
    }

    [Serializable]
    internal class GeneratorStatusResponse
    {
        public string JobId;
        public string Status;
        public float Progress;
        public GeneratorResultInfo[] Results;
    }

    [Serializable]
    internal class GeneratorResultInfo
    {
        public string Id;
        public string SourceUrl;
    }

    [Serializable]
    internal class GeneratorCancelMessage
    {
        public string JobId;
    }

    [Serializable]
    internal class GeneratorCancelResponse
    {
        public bool Cancelled;
    }

    [Serializable]
    internal class GeneratorApplyMessage
    {
        public string JobId;
        public string ResultId;
        public string TargetPath;
    }

    [Serializable]
    internal class GeneratorApplyResponse
    {
        public bool Success;
        public string AssetPath;
    }

    #endregion
}
