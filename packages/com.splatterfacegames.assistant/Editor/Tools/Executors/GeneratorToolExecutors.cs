// Generator Tool Executors - AI asset generation operations
// Interfaces with external AI generation services (ComfyUI, Hunyuan3D-2, Kao, LM Studio, etc.)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatter.Editor.Tools
{
    #region Generator Provider Interface

    /// <summary>
    /// Result from getting a generation quote.
    /// </summary>
    public class QuoteResult
    {
        public double EstimatedCost { get; set; }
        public double EstimatedTime { get; set; }
        public string Provider { get; set; }
        public bool Supported { get; set; }
        public string UnsupportedReason { get; set; }
        public Dictionary<string, object> ProviderMetadata { get; set; }
    }

    /// <summary>
    /// Result from submitting a generation job.
    /// </summary>
    public class SubmitResult
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public double EstimatedCompletion { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result from checking job status.
    /// </summary>
    public class StatusResult
    {
        public string JobId { get; set; }
        public string Status { get; set; }
        public int Progress { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Request for AI content generation.
    /// </summary>
    public class GenerationRequest
    {
        public string Provider { get; set; }
        public string Type { get; set; }
        public string Prompt { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public string OutputPath { get; set; }
    }

    /// <summary>
    /// Interface for AI generation service providers.
    /// Implement this interface to add support for new generation backends.
    /// </summary>
    public interface IGeneratorProvider
    {
        /// <summary>
        /// Unique identifier for this provider (e.g., "comfyui", "hunyuan3d", "lmstudio").
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// Types of content this provider can generate (e.g., "image", "3d_model", "material", "texture").
        /// </summary>
        string[] SupportedTypes { get; }

        /// <summary>
        /// Gets a quote for the generation request including estimated cost and time.
        /// </summary>
        Task<QuoteResult> GetQuoteAsync(GenerationRequest request);

        /// <summary>
        /// Submits a generation job to the provider.
        /// </summary>
        Task<SubmitResult> SubmitAsync(GenerationRequest request);

        /// <summary>
        /// Gets the current status of a generation job.
        /// </summary>
        Task<StatusResult> GetStatusAsync(string jobId);

        /// <summary>
        /// Gets the generated result data for a completed job.
        /// </summary>
        Task<byte[]> GetResultAsync(string jobId);
    }

    #endregion

    #region Generator Provider Registry

    /// <summary>
    /// Registry for managing generator providers.
    /// </summary>
    public sealed class GeneratorProviderRegistry
    {
        private readonly Dictionary<string, IGeneratorProvider> _providers = new();
        private static GeneratorProviderRegistry _instance;

        public static GeneratorProviderRegistry Instance => _instance ??= new GeneratorProviderRegistry();

        private GeneratorProviderRegistry()
        {
            // Register built-in stub providers
            RegisterProvider(new ComfyUIProvider());
            RegisterProvider(new Hunyuan3DProvider());
            RegisterProvider(new KaoProvider());
            RegisterProvider(new LMStudioProvider());
        }

        public void RegisterProvider(IGeneratorProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            _providers[provider.ProviderId.ToLowerInvariant()] = provider;
            Debug.Log($"[Splatter] Registered generator provider: {provider.ProviderId}");
        }

        public void UnregisterProvider(string providerId)
        {
            _providers.Remove(providerId.ToLowerInvariant());
        }

        public IGeneratorProvider GetProvider(string providerId)
        {
            return _providers.TryGetValue(providerId.ToLowerInvariant(), out var provider) ? provider : null;
        }

        public IEnumerable<IGeneratorProvider> GetAllProviders()
        {
            return _providers.Values;
        }

        public IGeneratorProvider GetProviderForType(string type)
        {
            return _providers.Values.FirstOrDefault(p =>
                p.SupportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase));
        }
    }

    #endregion

    #region Job State Management

    /// <summary>
    /// Manages persistent state for generation jobs.
    /// </summary>
    internal static class GeneratorJobStore
    {
        private static string JobsDirectory =>
            Path.Combine(Application.dataPath, "..", "Library", "SplatterAI", "Jobs");

        public static void EnsureDirectoryExists()
        {
            if (!Directory.Exists(JobsDirectory))
            {
                Directory.CreateDirectory(JobsDirectory);
            }
        }

        public static string GetJobPath(string jobId)
        {
            return Path.Combine(JobsDirectory, $"{jobId}.json");
        }

        public static string GetJobResultPath(string jobId)
        {
            return Path.Combine(JobsDirectory, $"{jobId}.result");
        }

        public static void SaveJob(GeneratorJobState job)
        {
            EnsureDirectoryExists();
            var json = JsonUtility.ToJson(job, true);
            File.WriteAllText(GetJobPath(job.jobId), json);
        }

        public static GeneratorJobState LoadJob(string jobId)
        {
            var path = GetJobPath(jobId);
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonUtility.FromJson<GeneratorJobState>(json);
        }

        public static void SaveResult(string jobId, byte[] data)
        {
            EnsureDirectoryExists();
            File.WriteAllBytes(GetJobResultPath(jobId), data);
        }

        public static byte[] LoadResult(string jobId)
        {
            var path = GetJobResultPath(jobId);
            if (!File.Exists(path))
                return null;

            return File.ReadAllBytes(path);
        }

        public static void DeleteJob(string jobId)
        {
            var jobPath = GetJobPath(jobId);
            var resultPath = GetJobResultPath(jobId);

            if (File.Exists(jobPath))
                File.Delete(jobPath);
            if (File.Exists(resultPath))
                File.Delete(resultPath);
        }

        public static List<GeneratorJobState> GetAllJobs()
        {
            EnsureDirectoryExists();
            var jobs = new List<GeneratorJobState>();

            foreach (var file in Directory.GetFiles(JobsDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var job = JsonUtility.FromJson<GeneratorJobState>(json);
                    if (job != null)
                    {
                        jobs.Add(job);
                    }
                }
                catch
                {
                    // Skip malformed job files
                }
            }

            return jobs;
        }
    }

    /// <summary>
    /// Persistent state for a generation job.
    /// </summary>
    [Serializable]
    internal class GeneratorJobState
    {
        public string jobId;
        public string provider;
        public string type;
        public string prompt;
        public string status;
        public int progress;
        public string outputPath;
        public string createdAt;
        public string updatedAt;
        public string completedAt;
        public string error;
        public string resultPath;
        public string resultMimeType;
        public long resultSize;
    }

    #endregion

    #region Stub Provider Implementations

    /// <summary>
    /// Stub implementation for ComfyUI image generation.
    /// Replace with actual API integration when available.
    /// </summary>
    internal class ComfyUIProvider : IGeneratorProvider
    {
        public string ProviderId => "comfyui";
        public string[] SupportedTypes => new[] { "image", "texture" };

        public Task<QuoteResult> GetQuoteAsync(GenerationRequest request)
        {
            var width = GetParameter<int>(request.Parameters, "width", 512);
            var height = GetParameter<int>(request.Parameters, "height", 512);
            var steps = GetParameter<int>(request.Parameters, "steps", 20);

            // Estimate based on resolution and steps
            var pixelCount = width * height;
            var baseTime = 5.0; // Base seconds
            var timePerMegapixel = 10.0;
            var timePerStep = 0.5;

            var estimatedTime = baseTime + (pixelCount / 1000000.0 * timePerMegapixel) + (steps * timePerStep);
            var estimatedCost = estimatedTime * 0.01; // Cost per second

            return Task.FromResult(new QuoteResult
            {
                EstimatedCost = Math.Round(estimatedCost, 4),
                EstimatedTime = Math.Round(estimatedTime, 1),
                Provider = ProviderId,
                Supported = true,
                ProviderMetadata = new Dictionary<string, object>
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["steps"] = steps,
                    ["model"] = GetParameter<string>(request.Parameters, "model", "sd_xl_base_1.0")
                }
            });
        }

        public Task<SubmitResult> SubmitAsync(GenerationRequest request)
        {
            var jobId = GenerateJobId();
            var estimatedTime = 30.0; // Stub estimate

            // Create and save job state
            var job = new GeneratorJobState
            {
                jobId = jobId,
                provider = ProviderId,
                type = request.Type,
                prompt = request.Prompt,
                status = "queued",
                progress = 0,
                outputPath = request.OutputPath,
                createdAt = DateTime.UtcNow.ToString("o"),
                updatedAt = DateTime.UtcNow.ToString("o")
            };

            GeneratorJobStore.SaveJob(job);

            // In a real implementation, this would submit to the ComfyUI API
            // For now, we simulate queued status
            Debug.Log($"[Splatter] ComfyUI job submitted: {jobId} - Prompt: {request.Prompt}");

            return Task.FromResult(new SubmitResult
            {
                JobId = jobId,
                Status = "queued",
                EstimatedCompletion = estimatedTime
            });
        }

        public Task<StatusResult> GetStatusAsync(string jobId)
        {
            var job = GeneratorJobStore.LoadJob(jobId);
            if (job == null)
            {
                return Task.FromResult(new StatusResult
                {
                    JobId = jobId,
                    Status = "not_found",
                    Error = $"Job not found: {jobId}"
                });
            }

            // In a real implementation, this would check the ComfyUI API
            // For stub, we simulate progress
            return Task.FromResult(new StatusResult
            {
                JobId = jobId,
                Status = job.status,
                Progress = job.progress,
                Error = job.error,
                Metadata = new Dictionary<string, object>
                {
                    ["provider"] = job.provider,
                    ["type"] = job.type,
                    ["createdAt"] = job.createdAt
                }
            });
        }

        public Task<byte[]> GetResultAsync(string jobId)
        {
            var result = GeneratorJobStore.LoadResult(jobId);
            if (result == null)
            {
                // Return a placeholder 1x1 PNG for stub
                return Task.FromResult(CreatePlaceholderImage());
            }

            return Task.FromResult(result);
        }

        private static string GenerateJobId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var guid = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"comfyui-{timestamp}-{guid}";
        }

        private static byte[] CreatePlaceholderImage()
        {
            // Create a simple 64x64 placeholder texture
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color32[64 * 64];

            for (int i = 0; i < pixels.Length; i++)
            {
                int x = i % 64;
                int y = i / 64;
                bool checker = ((x / 8) + (y / 8)) % 2 == 0;
                pixels[i] = checker ? new Color32(100, 100, 100, 255) : new Color32(150, 150, 150, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            var bytes = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            return bytes;
        }

        private static T GetParameter<T>(Dictionary<string, object> parameters, string key, T defaultValue)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value))
                return defaultValue;

            try
            {
                if (value is T typedValue)
                    return typedValue;

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    /// <summary>
    /// Stub implementation for Hunyuan3D-2 3D model generation.
    /// Replace with actual API integration when available.
    /// </summary>
    internal class Hunyuan3DProvider : IGeneratorProvider
    {
        public string ProviderId => "hunyuan3d";
        public string[] SupportedTypes => new[] { "3d_model" };

        public Task<QuoteResult> GetQuoteAsync(GenerationRequest request)
        {
            var quality = GetParameter<string>(request.Parameters, "quality", "medium");
            var format = GetParameter<string>(request.Parameters, "format", "glb");

            // Estimate based on quality level
            double estimatedTime = quality.ToLowerInvariant() switch
            {
                "low" => 30.0,
                "medium" => 60.0,
                "high" => 120.0,
                "ultra" => 300.0,
                _ => 60.0
            };

            var estimatedCost = estimatedTime * 0.02; // 3D models cost more

            return Task.FromResult(new QuoteResult
            {
                EstimatedCost = Math.Round(estimatedCost, 4),
                EstimatedTime = estimatedTime,
                Provider = ProviderId,
                Supported = true,
                ProviderMetadata = new Dictionary<string, object>
                {
                    ["quality"] = quality,
                    ["format"] = format,
                    ["textured"] = GetParameter<bool>(request.Parameters, "textured", true)
                }
            });
        }

        public Task<SubmitResult> SubmitAsync(GenerationRequest request)
        {
            var jobId = GenerateJobId();

            var quality = GetParameter<string>(request.Parameters, "quality", "medium");
            double estimatedTime = quality.ToLowerInvariant() switch
            {
                "low" => 30.0,
                "medium" => 60.0,
                "high" => 120.0,
                "ultra" => 300.0,
                _ => 60.0
            };

            var job = new GeneratorJobState
            {
                jobId = jobId,
                provider = ProviderId,
                type = request.Type,
                prompt = request.Prompt,
                status = "queued",
                progress = 0,
                outputPath = request.OutputPath,
                createdAt = DateTime.UtcNow.ToString("o"),
                updatedAt = DateTime.UtcNow.ToString("o"),
                resultMimeType = "model/gltf-binary"
            };

            GeneratorJobStore.SaveJob(job);

            Debug.Log($"[Splatter] Hunyuan3D job submitted: {jobId} - Prompt: {request.Prompt}");

            return Task.FromResult(new SubmitResult
            {
                JobId = jobId,
                Status = "queued",
                EstimatedCompletion = estimatedTime
            });
        }

        public Task<StatusResult> GetStatusAsync(string jobId)
        {
            var job = GeneratorJobStore.LoadJob(jobId);
            if (job == null)
            {
                return Task.FromResult(new StatusResult
                {
                    JobId = jobId,
                    Status = "not_found",
                    Error = $"Job not found: {jobId}"
                });
            }

            return Task.FromResult(new StatusResult
            {
                JobId = jobId,
                Status = job.status,
                Progress = job.progress,
                Error = job.error,
                Metadata = new Dictionary<string, object>
                {
                    ["provider"] = job.provider,
                    ["type"] = job.type,
                    ["createdAt"] = job.createdAt
                }
            });
        }

        public Task<byte[]> GetResultAsync(string jobId)
        {
            var result = GeneratorJobStore.LoadResult(jobId);
            if (result == null)
            {
                // Return empty byte array for stub - real impl would return GLB data
                return Task.FromResult(Array.Empty<byte>());
            }

            return Task.FromResult(result);
        }

        private static string GenerateJobId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var guid = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"hunyuan3d-{timestamp}-{guid}";
        }

        private static T GetParameter<T>(Dictionary<string, object> parameters, string key, T defaultValue)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value))
                return defaultValue;

            try
            {
                if (value is T typedValue)
                    return typedValue;

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    /// <summary>
    /// Stub implementation for LM Studio text-to-content generation.
    /// Can be used for material/shader generation via code.
    /// Replace with actual API integration when available.
    /// </summary>
    internal class LMStudioProvider : IGeneratorProvider
    {
        public string ProviderId => "lmstudio";
        public string[] SupportedTypes => new[] { "material", "shader", "script" };

        public Task<QuoteResult> GetQuoteAsync(GenerationRequest request)
        {
            var maxTokens = GetParameter<int>(request.Parameters, "max_tokens", 2048);

            // LM Studio is local, so cost is essentially free
            // Time estimate based on token count
            var estimatedTime = maxTokens / 100.0; // ~100 tokens per second

            return Task.FromResult(new QuoteResult
            {
                EstimatedCost = 0.0, // Local model, no cost
                EstimatedTime = Math.Round(estimatedTime, 1),
                Provider = ProviderId,
                Supported = true,
                ProviderMetadata = new Dictionary<string, object>
                {
                    ["maxTokens"] = maxTokens,
                    ["model"] = GetParameter<string>(request.Parameters, "model", "local"),
                    ["temperature"] = GetParameter<double>(request.Parameters, "temperature", 0.7)
                }
            });
        }

        public Task<SubmitResult> SubmitAsync(GenerationRequest request)
        {
            var jobId = GenerateJobId();

            var maxTokens = GetParameter<int>(request.Parameters, "max_tokens", 2048);
            var estimatedTime = maxTokens / 100.0;

            var job = new GeneratorJobState
            {
                jobId = jobId,
                provider = ProviderId,
                type = request.Type,
                prompt = request.Prompt,
                status = "queued",
                progress = 0,
                outputPath = request.OutputPath,
                createdAt = DateTime.UtcNow.ToString("o"),
                updatedAt = DateTime.UtcNow.ToString("o"),
                resultMimeType = "text/plain"
            };

            GeneratorJobStore.SaveJob(job);

            Debug.Log($"[Splatter] LM Studio job submitted: {jobId} - Prompt: {request.Prompt}");

            return Task.FromResult(new SubmitResult
            {
                JobId = jobId,
                Status = "queued",
                EstimatedCompletion = estimatedTime
            });
        }

        public Task<StatusResult> GetStatusAsync(string jobId)
        {
            var job = GeneratorJobStore.LoadJob(jobId);
            if (job == null)
            {
                return Task.FromResult(new StatusResult
                {
                    JobId = jobId,
                    Status = "not_found",
                    Error = $"Job not found: {jobId}"
                });
            }

            return Task.FromResult(new StatusResult
            {
                JobId = jobId,
                Status = job.status,
                Progress = job.progress,
                Error = job.error,
                Metadata = new Dictionary<string, object>
                {
                    ["provider"] = job.provider,
                    ["type"] = job.type,
                    ["createdAt"] = job.createdAt
                }
            });
        }

        public Task<byte[]> GetResultAsync(string jobId)
        {
            var result = GeneratorJobStore.LoadResult(jobId);
            if (result == null)
            {
                // Return placeholder shader code for stub
                var placeholderCode = "// Generated by LM Studio\n// Placeholder content\n";
                return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(placeholderCode));
            }

            return Task.FromResult(result);
        }

        private static string GenerateJobId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var guid = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"lmstudio-{timestamp}-{guid}";
        }

        private static T GetParameter<T>(Dictionary<string, object> parameters, string key, T defaultValue)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value))
                return defaultValue;

            try
            {
                if (value is T typedValue)
                    return typedValue;

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    /// <summary>
    /// Kao unified 3D generation provider (local).
    /// Supports multiple backends: Hunyuan3D (2.1/2/mini turbo, 2mv), TripoSR, Point-E, Shap-E.
    /// Runs locally over REST; the service auto-selects a port (11435-11550) and
    /// publishes the live URL to ~/.kao/service.json. Actual generation is executed
    /// service-side; this provider only produces local time/cost estimates.
    /// </summary>
    internal class KaoProvider : IGeneratorProvider
    {
        public string ProviderId => "kao";
        public string[] SupportedTypes => new[] { "3d_model", "pointcloud" };

        // Rough local time estimate (seconds) per Kao backend. Accepts both current
        // Kao model ids (turbo / input-split variants) and legacy aliases.
        private static double EstimateKaoTime(string model, int steps) => model.ToLowerInvariant() switch
        {
            "hunyuan3d-2.1-turbo" or "hunyuan3d-2-turbo" or "hunyuan3d-2" or "hunyuan3d" or "hunyuan"
                => 45.0 + (steps - 30) * 0.5,
            "hunyuan3d-mini-turbo" or "hunyuan3d-mini" or "mini" => 20.0,
            "hunyuan3d-2mv" or "multiview" => 50.0 + (steps - 30) * 0.5,
            "triposr" or "tripo" => 15.0,
            "point-e-image" or "point-e-text" or "point-e" or "pointe" => 10.0,
            "shap-e-image" or "shap-e-text" or "shap-e" or "shape" => 25.0,
            _ => 45.0
        };

        public Task<QuoteResult> GetQuoteAsync(GenerationRequest request)
        {
            var model = GetParameter<string>(request.Parameters, "model", "hunyuan3d-2.1-turbo");
            var steps = GetParameter<int>(request.Parameters, "steps", 30);

            // Estimate time based on model
            double estimatedTime = EstimateKaoTime(model, steps);

            return Task.FromResult(new QuoteResult
            {
                EstimatedCost = 0.0, // Local model, no cost
                EstimatedTime = Math.Round(estimatedTime, 1),
                Provider = ProviderId,
                Supported = true,
                ProviderMetadata = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["steps"] = steps,
                    ["generate_texture"] = GetParameter<bool>(request.Parameters, "generate_texture", true),
                    ["output_format"] = GetParameter<string>(request.Parameters, "output_format", "glb")
                }
            });
        }

        public Task<SubmitResult> SubmitAsync(GenerationRequest request)
        {
            var jobId = GenerateJobId();

            var model = GetParameter<string>(request.Parameters, "model", "hunyuan3d-2.1-turbo");
            var steps = GetParameter<int>(request.Parameters, "steps", 30);

            double estimatedTime = EstimateKaoTime(model, steps);

            var job = new GeneratorJobState
            {
                jobId = jobId,
                provider = ProviderId,
                type = request.Type,
                prompt = request.Prompt,
                status = "queued",
                progress = 0,
                outputPath = request.OutputPath,
                createdAt = DateTime.UtcNow.ToString("o"),
                updatedAt = DateTime.UtcNow.ToString("o"),
                resultMimeType = "model/gltf-binary"
            };

            GeneratorJobStore.SaveJob(job);

            Debug.Log($"[Splatter] Kao job submitted: {jobId} - Model: {model}, Prompt: {request.Prompt}");

            return Task.FromResult(new SubmitResult
            {
                JobId = jobId,
                Status = "queued",
                EstimatedCompletion = estimatedTime
            });
        }

        public Task<StatusResult> GetStatusAsync(string jobId)
        {
            var job = GeneratorJobStore.LoadJob(jobId);
            if (job == null)
            {
                return Task.FromResult(new StatusResult
                {
                    JobId = jobId,
                    Status = "not_found",
                    Error = $"Job not found: {jobId}"
                });
            }

            return Task.FromResult(new StatusResult
            {
                JobId = jobId,
                Status = job.status,
                Progress = job.progress,
                Error = job.error,
                Metadata = new Dictionary<string, object>
                {
                    ["provider"] = job.provider,
                    ["type"] = job.type,
                    ["createdAt"] = job.createdAt
                }
            });
        }

        public Task<byte[]> GetResultAsync(string jobId)
        {
            var result = GeneratorJobStore.LoadResult(jobId);
            if (result == null)
            {
                // Return empty placeholder for stub
                return Task.FromResult(Array.Empty<byte>());
            }

            return Task.FromResult(result);
        }

        private static string GenerateJobId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var guid = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"kao-{timestamp}-{guid}";
        }

        private static T GetParameter<T>(Dictionary<string, object> parameters, string key, T defaultValue)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value))
                return defaultValue;

            try
            {
                if (value is T typedValue)
                    return typedValue;

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    #endregion

    #region Tool Executors

    /// <summary>
    /// Gets a quote for AI content generation.
    /// Tool ID: generator.quote
    /// </summary>
    public class GeneratorQuoteExecutor : IToolExecutor
    {
        public string ToolId => "generator.quote";

        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var provider = context.Arguments.TryGetValue("provider", out var p) ? p?.ToString() : null;
                var type = context.Arguments.TryGetValue("type", out var t) ? t?.ToString() : null;
                var prompt = context.Arguments.TryGetValue("prompt", out var pr) ? pr?.ToString() : null;
                var parameters = context.Arguments.TryGetValue("parameters", out var param)
                    ? param as Dictionary<string, object> ?? ParseParameters(param)
                    : new Dictionary<string, object>();

                if (string.IsNullOrEmpty(type))
                {
                    return ToolExecutionResult.Failed("type is required (image, 3d_model, material, texture, etc.)");
                }

                if (string.IsNullOrEmpty(prompt))
                {
                    return ToolExecutionResult.Failed("prompt is required");
                }

                // Find provider - either specified or auto-select based on type
                IGeneratorProvider generatorProvider;
                if (!string.IsNullOrEmpty(provider))
                {
                    generatorProvider = GeneratorProviderRegistry.Instance.GetProvider(provider);
                    if (generatorProvider == null)
                    {
                        var availableProviders = string.Join(", ",
                            GeneratorProviderRegistry.Instance.GetAllProviders().Select(p => p.ProviderId));
                        return ToolExecutionResult.Failed(
                            $"Provider not found: {provider}. Available providers: {availableProviders}");
                    }

                    // Check if provider supports the requested type
                    if (!generatorProvider.SupportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                    {
                        return ToolExecutionResult.Succeeded(new
                        {
                            estimatedCost = 0,
                            estimatedTime = 0,
                            provider,
                            supported = false,
                            reason = $"Provider '{provider}' does not support type '{type}'. " +
                                     $"Supported types: {string.Join(", ", generatorProvider.SupportedTypes)}"
                        });
                    }
                }
                else
                {
                    generatorProvider = GeneratorProviderRegistry.Instance.GetProviderForType(type);
                    if (generatorProvider == null)
                    {
                        return ToolExecutionResult.Succeeded(new
                        {
                            estimatedCost = 0,
                            estimatedTime = 0,
                            provider = (string)null,
                            supported = false,
                            reason = $"No provider available for type '{type}'"
                        });
                    }
                }

                var request = new GenerationRequest
                {
                    Provider = generatorProvider.ProviderId,
                    Type = type,
                    Prompt = prompt,
                    Parameters = parameters
                };

                var quote = await generatorProvider.GetQuoteAsync(request);

                return ToolExecutionResult.Succeeded(new
                {
                    estimatedCost = quote.EstimatedCost,
                    estimatedTime = quote.EstimatedTime,
                    provider = quote.Provider,
                    supported = quote.Supported,
                    reason = quote.UnsupportedReason,
                    metadata = quote.ProviderMetadata
                });
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.Failed(ex.Message);
            }
        }

        private Dictionary<string, object> ParseParameters(object param)
        {
            if (param == null)
                return new Dictionary<string, object>();

            if (param is Dictionary<string, object> dict)
                return dict;

            // Try to parse as JSON string
            if (param is string jsonString)
            {
                try
                {
                    // Simple JSON parsing for basic key-value pairs
                    // In production, use a proper JSON library
                    var result = new Dictionary<string, object>();
                    // For now, return empty dict - proper JSON parsing would go here
                    return result;
                }
                catch
                {
                    return new Dictionary<string, object>();
                }
            }

            return new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Submits an AI content generation job.
    /// Tool ID: generator.submit
    /// </summary>
    public class GeneratorSubmitExecutor : IToolExecutor
    {
        public string ToolId => "generator.submit";

        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var provider = context.Arguments.TryGetValue("provider", out var p) ? p?.ToString() : null;
                var type = context.Arguments.TryGetValue("type", out var t) ? t?.ToString() : null;
                var prompt = context.Arguments.TryGetValue("prompt", out var pr) ? pr?.ToString() : null;
                var outputPath = context.Arguments.TryGetValue("outputPath", out var op) ? op?.ToString() : null;
                var parameters = context.Arguments.TryGetValue("parameters", out var param)
                    ? param as Dictionary<string, object> ?? ParseParameters(param)
                    : new Dictionary<string, object>();

                if (string.IsNullOrEmpty(type))
                {
                    return ToolExecutionResult.Failed("type is required (image, 3d_model, material, texture, etc.)");
                }

                if (string.IsNullOrEmpty(prompt))
                {
                    return ToolExecutionResult.Failed("prompt is required");
                }

                // Find provider
                IGeneratorProvider generatorProvider;
                if (!string.IsNullOrEmpty(provider))
                {
                    generatorProvider = GeneratorProviderRegistry.Instance.GetProvider(provider);
                    if (generatorProvider == null)
                    {
                        var availableProviders = string.Join(", ",
                            GeneratorProviderRegistry.Instance.GetAllProviders().Select(p => p.ProviderId));
                        return ToolExecutionResult.Failed(
                            $"Provider not found: {provider}. Available providers: {availableProviders}");
                    }

                    if (!generatorProvider.SupportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                    {
                        return ToolExecutionResult.Failed(
                            $"Provider '{provider}' does not support type '{type}'. " +
                            $"Supported types: {string.Join(", ", generatorProvider.SupportedTypes)}");
                    }
                }
                else
                {
                    generatorProvider = GeneratorProviderRegistry.Instance.GetProviderForType(type);
                    if (generatorProvider == null)
                    {
                        return ToolExecutionResult.Failed($"No provider available for type '{type}'");
                    }
                }

                var request = new GenerationRequest
                {
                    Provider = generatorProvider.ProviderId,
                    Type = type,
                    Prompt = prompt,
                    Parameters = parameters,
                    OutputPath = outputPath
                };

                var result = await generatorProvider.SubmitAsync(request);

                if (!string.IsNullOrEmpty(result.Error))
                {
                    return ToolExecutionResult.Failed(result.Error);
                }

                return ToolExecutionResult.Succeeded(new
                {
                    jobId = result.JobId,
                    status = result.Status,
                    estimatedCompletion = result.EstimatedCompletion,
                    provider = generatorProvider.ProviderId,
                    type
                });
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.Failed(ex.Message);
            }
        }

        private Dictionary<string, object> ParseParameters(object param)
        {
            if (param == null)
                return new Dictionary<string, object>();

            if (param is Dictionary<string, object> dict)
                return dict;

            return new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Gets the status of a generation job.
    /// Tool ID: generator.status
    /// </summary>
    public class GeneratorStatusExecutor : IToolExecutor
    {
        public string ToolId => "generator.status";

        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var jobId = context.Arguments.TryGetValue("jobId", out var j) ? j?.ToString() : null;

                if (string.IsNullOrEmpty(jobId))
                {
                    return ToolExecutionResult.Failed("jobId is required");
                }

                // Load job state to determine provider
                var job = GeneratorJobStore.LoadJob(jobId);
                if (job == null)
                {
                    return ToolExecutionResult.Succeeded(new
                    {
                        jobId,
                        status = "not_found",
                        progress = 0,
                        result = (object)null,
                        error = $"Job not found: {jobId}"
                    });
                }

                var provider = GeneratorProviderRegistry.Instance.GetProvider(job.provider);
                if (provider == null)
                {
                    return ToolExecutionResult.Succeeded(new
                    {
                        jobId,
                        status = job.status,
                        progress = job.progress,
                        result = (object)null,
                        error = $"Provider not available: {job.provider}"
                    });
                }

                var status = await provider.GetStatusAsync(jobId);

                object resultInfo = null;
                if (status.Status == "completed")
                {
                    resultInfo = new
                    {
                        type = job.type,
                        mimeType = job.resultMimeType,
                        size = job.resultSize,
                        outputPath = job.outputPath
                    };
                }

                return ToolExecutionResult.Succeeded(new
                {
                    jobId = status.JobId,
                    status = status.Status,
                    progress = status.Progress,
                    result = resultInfo,
                    error = status.Error,
                    metadata = status.Metadata
                });
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.Failed(ex.Message);
            }
        }
    }

    /// <summary>
    /// Applies a completed generation result to create/update a Unity asset.
    /// Tool ID: generator.apply
    /// </summary>
    public class GeneratorApplyExecutor : IToolExecutor
    {
        public string ToolId => "generator.apply";

        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var jobId = context.Arguments.TryGetValue("jobId", out var j) ? j?.ToString() : null;
                var targetPath = context.Arguments.TryGetValue("targetPath", out var tp) ? tp?.ToString() : null;
                var importSettings = context.Arguments.TryGetValue("importSettings", out var isArg)
                    ? isArg as Dictionary<string, object> ?? new Dictionary<string, object>()
                    : new Dictionary<string, object>();

                if (string.IsNullOrEmpty(jobId))
                {
                    return ToolExecutionResult.Failed("jobId is required");
                }

                if (string.IsNullOrEmpty(targetPath))
                {
                    return ToolExecutionResult.Failed("targetPath is required");
                }

                // Ensure target path is within Assets
                if (!targetPath.StartsWith("Assets/") && !targetPath.StartsWith("Assets\\"))
                {
                    targetPath = "Assets/" + targetPath;
                }

                // Validate path is within project
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                var fullTargetPath = Path.GetFullPath(Path.Combine(projectRoot, targetPath));
                if (!fullTargetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolExecutionResult.Failed("Target path is outside project");
                }

                // Load job state
                var job = GeneratorJobStore.LoadJob(jobId);
                if (job == null)
                {
                    return ToolExecutionResult.Failed($"Job not found: {jobId}");
                }

                if (job.status != "completed")
                {
                    return ToolExecutionResult.Failed(
                        $"Job is not completed. Current status: {job.status}. Progress: {job.progress}%");
                }

                // Get provider and result
                var provider = GeneratorProviderRegistry.Instance.GetProvider(job.provider);
                if (provider == null)
                {
                    return ToolExecutionResult.Failed($"Provider not available: {job.provider}");
                }

                var resultData = await provider.GetResultAsync(jobId);
                if (resultData == null || resultData.Length == 0)
                {
                    return ToolExecutionResult.Failed("No result data available for this job");
                }

                // Ensure directory exists
                var targetDir = Path.GetDirectoryName(fullTargetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // Write result to file
                File.WriteAllBytes(fullTargetPath, resultData);

                // Refresh AssetDatabase
                AssetDatabase.Refresh();

                // Apply import settings if provided
                var assetPath = targetPath.Replace('\\', '/');
                ApplyImportSettings(assetPath, job.type, importSettings);

                // Get asset info
                var asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);

                // Update job state with output path
                job.outputPath = assetPath;
                job.updatedAt = DateTime.UtcNow.ToString("o");
                GeneratorJobStore.SaveJob(job);

                return ToolExecutionResult.Succeeded(new
                {
                    success = true,
                    assetPath,
                    assetGuid,
                    assetType = asset?.GetType().Name ?? "Unknown",
                    fileSize = resultData.Length
                }, new List<string> { assetPath });
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.Failed(ex.Message);
            }
        }

        private void ApplyImportSettings(string assetPath, string generationType, Dictionary<string, object> settings)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
                return;

            switch (generationType.ToLowerInvariant())
            {
                case "image":
                case "texture":
                    if (importer is TextureImporter textureImporter)
                    {
                        ApplyTextureImportSettings(textureImporter, settings);
                    }
                    break;

                case "3d_model":
                    if (importer is ModelImporter modelImporter)
                    {
                        ApplyModelImportSettings(modelImporter, settings);
                    }
                    break;
            }

            importer.SaveAndReimport();
        }

        private void ApplyTextureImportSettings(TextureImporter importer, Dictionary<string, object> settings)
        {
            if (settings.TryGetValue("textureType", out var textureType))
            {
                if (Enum.TryParse<TextureImporterType>(textureType.ToString(), true, out var type))
                {
                    importer.textureType = type;
                }
            }

            if (settings.TryGetValue("sRGB", out var sRGB))
            {
                importer.sRGBTexture = Convert.ToBoolean(sRGB);
            }

            if (settings.TryGetValue("alphaIsTransparency", out var alphaTransparency))
            {
                importer.alphaIsTransparency = Convert.ToBoolean(alphaTransparency);
            }

            if (settings.TryGetValue("maxSize", out var maxSize))
            {
                importer.maxTextureSize = Convert.ToInt32(maxSize);
            }

            if (settings.TryGetValue("filterMode", out var filterMode))
            {
                if (Enum.TryParse<FilterMode>(filterMode.ToString(), true, out var mode))
                {
                    importer.filterMode = mode;
                }
            }

            if (settings.TryGetValue("wrapMode", out var wrapMode))
            {
                if (Enum.TryParse<TextureWrapMode>(wrapMode.ToString(), true, out var mode))
                {
                    importer.wrapMode = mode;
                }
            }

            if (settings.TryGetValue("generateMipMaps", out var mipmaps))
            {
                importer.mipmapEnabled = Convert.ToBoolean(mipmaps);
            }

            if (settings.TryGetValue("compression", out var compression))
            {
                if (Enum.TryParse<TextureImporterCompression>(compression.ToString(), true, out var comp))
                {
                    importer.textureCompression = comp;
                }
            }
        }

        private void ApplyModelImportSettings(ModelImporter importer, Dictionary<string, object> settings)
        {
            if (settings.TryGetValue("scaleFactor", out var scale))
            {
                importer.globalScale = Convert.ToSingle(scale);
            }

            if (settings.TryGetValue("importMaterials", out var materials))
            {
                // Unity 6 removed ModelImporter.importMaterials; use materialImportMode.
                importer.materialImportMode = Convert.ToBoolean(materials)
                    ? ModelImporterMaterialImportMode.ImportStandard
                    : ModelImporterMaterialImportMode.None;
            }

            if (settings.TryGetValue("generateColliders", out var colliders))
            {
                importer.addCollider = Convert.ToBoolean(colliders);
            }

            if (settings.TryGetValue("importAnimation", out var animation))
            {
                importer.importAnimation = Convert.ToBoolean(animation);
            }

            if (settings.TryGetValue("meshCompression", out var meshCompression))
            {
                if (Enum.TryParse<ModelImporterMeshCompression>(meshCompression.ToString(), true, out var comp))
                {
                    importer.meshCompression = comp;
                }
            }

            if (settings.TryGetValue("optimizeMesh", out var optimize))
            {
                importer.optimizeMeshPolygons = Convert.ToBoolean(optimize);
                importer.optimizeMeshVertices = Convert.ToBoolean(optimize);
            }

            if (settings.TryGetValue("importNormals", out var normals))
            {
                if (Enum.TryParse<ModelImporterNormals>(normals.ToString(), true, out var normalsMode))
                {
                    importer.importNormals = normalsMode;
                }
            }
        }
    }

    #endregion
}
