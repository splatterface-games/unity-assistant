// Generator Service Implementation - Real API Integrations

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Splatter.Protocol;
using Splatter.Service.Storage;

namespace Splatter.Service.Generators;

public sealed class GeneratorService : IGeneratorService
{
    private readonly ILogger<GeneratorService> _logger;
    private readonly ServiceConfiguration _config;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, GeneratorJob> _jobs = new();
    private readonly ConcurrentDictionary<string, List<GeneratorJobSummary>> _assetHistory = new();
    private readonly IReadOnlyDictionary<GeneratorModality, IModalityAdapter> _adapters;

    public event EventHandler<GeneratorJob>? JobUpdated;

    public GeneratorService(
        ILogger<GeneratorService> logger,
        ServiceConfiguration config,
        ICredentialStore credentials)
    {
        _logger = logger;
        _config = config;
        _credentials = credentials;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Initialize modality adapters with shared HTTP client and credentials
        _adapters = new Dictionary<GeneratorModality, IModalityAdapter>
        {
            [GeneratorModality.Image] = new ImageModalityAdapter(logger, _http, credentials),
            [GeneratorModality.MaterialPbr] = new MaterialPbrModalityAdapter(logger, _http, credentials),
            [GeneratorModality.Mesh] = new MeshModalityAdapter(logger, _http, credentials),
            [GeneratorModality.Sound] = new SoundModalityAdapter(logger, _http, credentials),
            [GeneratorModality.Animation] = new AnimationModalityAdapter(logger, _http, credentials)
        };
    }

    public async Task<QuoteResult> GetQuoteAsync(GeneratorQuoteRequest request, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(request.Modality, out var adapter))
        {
            return new QuoteResult(false, null, null, $"Unsupported modality: {request.Modality}");
        }

        try
        {
            return await adapter.GetQuoteAsync(request.ProviderId, request.ModelId, request.Mode, request.Parameters, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get quote for {Modality}", request.Modality);
            return new QuoteResult(false, null, null, ex.Message);
        }
    }

    public async Task<GeneratorJob> SubmitJobAsync(string workspaceId, GeneratorSubmitRequest request, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(request.Modality, out var adapter))
        {
            throw new InvalidOperationException($"Unsupported modality: {request.Modality}");
        }

        var jobId = JobId.New().Value;
        var job = new GeneratorJob(
            jobId,
            workspaceId,
            request.Modality,
            request.ProviderId,
            request.ModelId,
            request.TargetAssetGuid,
            request.TargetAssetPath,
            request.Mode,
            GeneratorJobStatus.Draft,
            request.Parameters,
            request.References,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);

        _jobs[jobId] = job;

        // Validate inputs
        var validationError = await adapter.ValidateInputsAsync(request.Parameters, request.References, ct);
        if (validationError != null)
        {
            job = job with
            {
                Status = GeneratorJobStatus.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = new NormalizedError(ErrorCodes.ToolFailed, validationError, null, false, null, null)
            };
            _jobs[jobId] = job;
            OnJobUpdated(job);
            return job;
        }

        // Get quote first
        var quote = await adapter.GetQuoteAsync(request.ProviderId, request.ModelId, request.Mode, request.Parameters, ct);
        job = job with
        {
            Status = GeneratorJobStatus.Quoted,
            Quote = quote,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _jobs[jobId] = job;
        OnJobUpdated(job);

        if (!quote.Success)
        {
            job = job with
            {
                Status = GeneratorJobStatus.Failed,
                Error = new NormalizedError(ErrorCodes.GenerationFailed, quote.ErrorMessage ?? "Quote failed", null, false, null, null)
            };
            _jobs[jobId] = job;
            OnJobUpdated(job);
            return job;
        }

        // Submit to provider
        job = job with { Status = GeneratorJobStatus.Submitted, UpdatedAt = DateTimeOffset.UtcNow };
        _jobs[jobId] = job;
        OnJobUpdated(job);

        // Start generation in background
        _ = RunGenerationAsync(job, adapter, ct);

        return job;
    }

    public Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return Task.FromResult(false);
        }

        if (job.Status is GeneratorJobStatus.Complete or GeneratorJobStatus.Failed or GeneratorJobStatus.Cancelled)
        {
            return Task.FromResult(false);
        }

        job = job with { Status = GeneratorJobStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
        _jobs[jobId] = job;
        OnJobUpdated(job);

        _logger.LogInformation("Cancelled generation job {JobId}", jobId);
        return Task.FromResult(true);
    }

    public async Task<GeneratorJob?> ResumeJobAsync(string jobId, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return null;
        }

        if (job.Status != GeneratorJobStatus.Recoverable)
        {
            return null;
        }

        if (!_adapters.TryGetValue(job.Modality, out var adapter))
        {
            return null;
        }

        job = job with { Status = GeneratorJobStatus.Downloading, UpdatedAt = DateTimeOffset.UtcNow };
        _jobs[jobId] = job;
        OnJobUpdated(job);

        // Resume download in background
        _ = ResumeDownloadAsync(job, adapter, ct);

        return job;
    }

    public Task<bool> DiscardRecoveryAsync(string jobId, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return Task.FromResult(false);
        }

        if (job.Status != GeneratorJobStatus.Recoverable)
        {
            return Task.FromResult(false);
        }

        job = job with { Status = GeneratorJobStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
        _jobs[jobId] = job;
        OnJobUpdated(job);

        _logger.LogInformation("Discarded recovery for job {JobId}", jobId);
        return Task.FromResult(true);
    }

    public Task<GeneratorJob?> GetJobAsync(string jobId, CancellationToken ct)
    {
        _jobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }

    public Task<GeneratorHistory?> GetHistoryAsync(string workspaceId, string assetGuid, CancellationToken ct)
    {
        var key = $"{workspaceId}:{assetGuid}";
        if (!_assetHistory.TryGetValue(key, out var jobs))
        {
            return Task.FromResult<GeneratorHistory?>(null);
        }

        var appliedJob = jobs.FirstOrDefault(j => j.Status == GeneratorJobStatus.Complete)?.Id;
        var history = new GeneratorHistory(assetGuid, "", jobs, appliedJob);
        return Task.FromResult<GeneratorHistory?>(history);
    }

    public Task<IReadOnlyList<GeneratorRecoveryInfo>> GetRecoverableJobsAsync(string workspaceId, CancellationToken ct)
    {
        var recoverable = _jobs.Values
            .Where(j => j.WorkspaceId == workspaceId && j.Status == GeneratorJobStatus.Recoverable)
            .Select(j => new GeneratorRecoveryInfo(
                j.Id,
                j.Status,
                j.Results?.Count ?? 0,
                0,
                new[] { RecoveryAction.Resume, RecoveryAction.Delete }))
            .ToList();

        return Task.FromResult<IReadOnlyList<GeneratorRecoveryInfo>>(recoverable);
    }

    public Task<IReadOnlyList<GeneratorCapabilities>> GetCapabilitiesAsync(GeneratorModality modality, string? providerId, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(modality, out var adapter))
        {
            return Task.FromResult<IReadOnlyList<GeneratorCapabilities>>(Array.Empty<GeneratorCapabilities>());
        }

        var capabilities = adapter.GetCapabilities(providerId);
        return Task.FromResult(capabilities);
    }

    public Task<string?> ApplyResultAsync(string workspaceId, GeneratorApplyRequest request, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(request.JobId, out var job))
        {
            return Task.FromResult<string?>(null);
        }

        if (job.Status != GeneratorJobStatus.Complete || job.Results == null || job.Results.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var result = string.IsNullOrEmpty(request.ResultId)
            ? job.Results[0]
            : job.Results.FirstOrDefault(r => r.Id == request.ResultId);

        if (result == null)
        {
            return Task.FromResult<string?>(null);
        }

        var targetPath = request.TargetAssetPath ?? job.TargetAssetPath ?? result.OutputPath;

        _logger.LogInformation("Applied result {ResultId} from job {JobId} to {Path}", result.Id, job.Id, targetPath);

        // Record in history
        var historyKey = $"{workspaceId}:{job.TargetAssetGuid ?? "unknown"}";
        var history = _assetHistory.GetOrAdd(historyKey, _ => new List<GeneratorJobSummary>());
        lock (history)
        {
            var summary = new GeneratorJobSummary(
                job.Id,
                job.Modality,
                job.Mode,
                job.Status,
                job.Results.Count,
                job.CreatedAt,
                job.Parameters?.GetValueOrDefault("prompt")?.ToString());
            history.Add(summary);
        }

        return Task.FromResult<string?>(targetPath);
    }

    #region Private Methods

    private async Task RunGenerationAsync(GeneratorJob job, IModalityAdapter adapter, CancellationToken ct)
    {
        try
        {
            job = job with { Status = GeneratorJobStatus.Queued, UpdatedAt = DateTimeOffset.UtcNow };
            _jobs[job.Id] = job;
            OnJobUpdated(job);

            job = job with { Status = GeneratorJobStatus.Running, UpdatedAt = DateTimeOffset.UtcNow };
            _jobs[job.Id] = job;
            OnJobUpdated(job);

            // Execute real generation via provider API
            var results = await adapter.GenerateAsync(
                job.ProviderId,
                job.ModelId,
                job.Mode,
                job.Parameters,
                job.References,
                ct);

            if (results == null || results.Count == 0)
            {
                job = job with
                {
                    Status = GeneratorJobStatus.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = new NormalizedError(ErrorCodes.GenerationFailed, "No results generated", null, false, null, null)
                };
            }
            else
            {
                job = job with { Status = GeneratorJobStatus.Downloading, UpdatedAt = DateTimeOffset.UtcNow };
                _jobs[job.Id] = job;
                OnJobUpdated(job);

                // Download results from provider
                var downloadedResults = await DownloadResultsAsync(job, results, adapter, ct);

                job = job with
                {
                    Status = GeneratorJobStatus.Complete,
                    Results = downloadedResults,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }

            _jobs[job.Id] = job;
            OnJobUpdated(job);

            _logger.LogInformation("Generation job {JobId} completed with status {Status}", job.Id, job.Status);
        }
        catch (OperationCanceledException)
        {
            if (_jobs.TryGetValue(job.Id, out var currentJob) && currentJob.Status == GeneratorJobStatus.Cancelled)
            {
                return;
            }

            job = job with { Status = GeneratorJobStatus.Recoverable, UpdatedAt = DateTimeOffset.UtcNow };
            _jobs[job.Id] = job;
            OnJobUpdated(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation job {JobId} failed", job.Id);

            job = job with
            {
                Status = GeneratorJobStatus.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = new NormalizedError(ErrorCodes.GenerationFailed, ex.Message, null, true, null, null)
            };
            _jobs[job.Id] = job;
            OnJobUpdated(job);
        }
    }

    private async Task<IReadOnlyList<GeneratorResult>> DownloadResultsAsync(
        GeneratorJob job,
        IReadOnlyList<GeneratorResult> pendingResults,
        IModalityAdapter adapter,
        CancellationToken ct)
    {
        var downloadedResults = new List<GeneratorResult>();

        foreach (var result in pendingResults)
        {
            try
            {
                var outputPath = GetOutputPath(job, result);
                await adapter.DownloadResultAsync(result, outputPath, ct);
                downloadedResults.Add(result with { OutputPath = outputPath });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download result {ResultId}", result.Id);
            }
        }

        return downloadedResults;
    }

    private async Task ResumeDownloadAsync(GeneratorJob job, IModalityAdapter adapter, CancellationToken ct)
    {
        try
        {
            if (job.Results == null || job.Results.Count == 0)
            {
                job = job with { Status = GeneratorJobStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow };
                _jobs[job.Id] = job;
                OnJobUpdated(job);
                return;
            }

            var downloadedResults = new List<GeneratorResult>();
            foreach (var result in job.Results)
            {
                if (string.IsNullOrEmpty(result.OutputPath) || !File.Exists(result.OutputPath))
                {
                    var outputPath = GetOutputPath(job, result);
                    await adapter.DownloadResultAsync(result, outputPath, ct);
                    downloadedResults.Add(result with { OutputPath = outputPath });
                }
                else
                {
                    downloadedResults.Add(result);
                }
            }

            job = job with
            {
                Status = GeneratorJobStatus.Complete,
                Results = downloadedResults,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _jobs[job.Id] = job;
            OnJobUpdated(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume download failed for job {JobId}", job.Id);

            job = job with { Status = GeneratorJobStatus.Recoverable, UpdatedAt = DateTimeOffset.UtcNow };
            _jobs[job.Id] = job;
            OnJobUpdated(job);
        }
    }

    private string GetOutputPath(GeneratorJob job, GeneratorResult result)
    {
        var basePath = Path.Combine(_config.DataDirectory, "GeneratedAssets", job.TargetAssetGuid ?? job.Id);
        Directory.CreateDirectory(basePath);

        var extension = result.ContentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "model/gltf-binary" => ".glb",
            "model/gltf+json" => ".gltf",
            "model/fbx" => ".fbx",
            "model/obj" => ".obj",
            "audio/wav" => ".wav",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "application/octet-stream" => ".bin",
            _ => ".dat"
        };

        return Path.Combine(basePath, $"{result.Id}{extension}");
    }

    private void OnJobUpdated(GeneratorJob job)
    {
        JobUpdated?.Invoke(this, job);
    }

    #endregion
}

#region Modality Adapter Interface

public interface IModalityAdapter
{
    Task<QuoteResult> GetQuoteAsync(string providerId, string modelId, string mode, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    Task<string?> ValidateInputsAsync(IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<ArtifactReference>? references, CancellationToken ct);
    Task<IReadOnlyList<GeneratorResult>?> GenerateAsync(string providerId, string modelId, string mode, IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<ArtifactReference>? references, CancellationToken ct);
    Task DownloadResultAsync(GeneratorResult result, string outputPath, CancellationToken ct);
    IReadOnlyList<GeneratorCapabilities> GetCapabilities(string? providerId);
}

#endregion

#region Image Modality Adapter - OpenAI DALL-E / Stability AI / Google Gemini Nano Banana

public class ImageModalityAdapter : IModalityAdapter
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;

    private const string OpenAIBaseUrl = "https://api.openai.com/v1";
    private const string StabilityBaseUrl = "https://api.stability.ai/v1";
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public ImageModalityAdapter(ILogger logger, HttpClient http, ICredentialStore credentials)
    {
        _logger = logger;
        _http = http;
        _credentials = credentials;
    }

    public Task<QuoteResult> GetQuoteAsync(string providerId, string modelId, string mode, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var count = GetIntParam(parameters, "count", 1);
        var size = GetStringParam(parameters, "size", "1024x1024");

        // Cost estimation based on provider and size
        decimal costPerImage = providerId switch
        {
            "openai" => modelId switch
            {
                "dall-e-3" => size == "1792x1024" || size == "1024x1792" ? 0.080m : 0.040m,
                "dall-e-2" => size == "1024x1024" ? 0.020m : size == "512x512" ? 0.018m : 0.016m,
                "gpt-image-1" => 0.040m,
                _ => 0.040m
            },
            "stability" => modelId switch
            {
                "stable-diffusion-xl-1024-v1-0" => 0.03m,
                "stable-diffusion-v1-6" => 0.02m,
                "stable-image-ultra" => 0.08m,
                "stable-image-core" => 0.03m,
                _ => 0.03m
            },
            "google" or "gemini" => modelId switch
            {
                // Nano Banana 2 (Gemini 3.1 Flash Image) - pricing per image
                "nano-banana-2" or "gemini-3.1-flash-image-preview" => size switch
                {
                    "4096x4096" or "4k" => 0.08m,
                    "2048x2048" or "2k" => 0.05m,
                    _ => 0.04m // Default 1024x1024
                },
                // Nano Banana Pro (Gemini 3 Pro Image)
                "nano-banana-pro" or "gemini-3-pro-image-preview" => 0.10m,
                // Original Nano Banana (Gemini 2.5 Flash Image)
                "nano-banana" or "gemini-2.5-flash-image" => 0.03m,
                _ => 0.04m
            },
            // ComfyUI is local - no API costs (but may have compute costs)
            "comfyui" or "comfy" => 0.00m,
            _ => 0.05m
        };

        return Task.FromResult(new QuoteResult(true, costPerImage * count, "USD", null));
    }

    public Task<string?> ValidateInputsAsync(IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<ArtifactReference>? references, CancellationToken ct)
    {
        var prompt = GetStringParam(parameters, "prompt", "");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult<string?>("Prompt is required");
        }

        if (prompt.Length > 4000)
        {
            return Task.FromResult<string?>("Prompt exceeds maximum length of 4000 characters");
        }

        return Task.FromResult<string?>(null);
    }

    public async Task<IReadOnlyList<GeneratorResult>?> GenerateAsync(
        string providerId, string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        return providerId switch
        {
            "openai" => await GenerateWithOpenAIAsync(modelId, mode, parameters, references, ct),
            "stability" => await GenerateWithStabilityAsync(modelId, mode, parameters, references, ct),
            "google" or "gemini" => await GenerateWithGeminiNanoBananaAsync(modelId, mode, parameters, references, ct),
            "comfyui" or "comfy" => await GenerateWithComfyUIAsync(modelId, mode, parameters, references, ct),
            _ => throw new InvalidOperationException($"Unsupported image provider: {providerId}")
        };
    }

    private async Task<IReadOnlyList<GeneratorResult>?> GenerateWithOpenAIAsync(
        string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        var apiKey = await _credentials.GetAsync("openai", ct);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key not configured");
        }

        var prompt = GetStringParam(parameters, "prompt", "");
        var size = GetStringParam(parameters, "size", "1024x1024");
        var quality = GetStringParam(parameters, "quality", "standard");
        var style = GetStringParam(parameters, "style", "vivid");
        var count = GetIntParam(parameters, "count", 1);

        var body = new JsonObject
        {
            ["model"] = modelId,
            ["prompt"] = prompt,
            ["size"] = size,
            ["n"] = count,
            ["response_format"] = "url"
        };

        if (modelId == "dall-e-3")
        {
            body["quality"] = quality;
            body["style"] = style;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{OpenAIBaseUrl}/images/generations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI image generation failed: {Status} - {Response}", response.StatusCode, responseText);
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode}");
        }

        var json = JsonNode.Parse(responseText);
        var data = json?["data"]?.AsArray();

        if (data == null || data.Count == 0)
        {
            return null;
        }

        var results = new List<GeneratorResult>();
        var index = 0;
        foreach (var item in data)
        {
            var url = item?["url"]?.GetValue<string>();
            var revisedPrompt = item?["revised_prompt"]?.GetValue<string>();

            if (!string.IsNullOrEmpty(url))
            {
                results.Add(new GeneratorResult(
                    Guid.NewGuid().ToString("N")[..8],
                    url,
                    "image/png",
                    0, // Size unknown until download
                    Random.Shared.Next(1, int.MaxValue),
                    index++,
                    new Dictionary<string, object?>
                    {
                        ["prompt"] = prompt,
                        ["revisedPrompt"] = revisedPrompt
                    }));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<GeneratorResult>?> GenerateWithStabilityAsync(
        string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        var apiKey = await _credentials.GetAsync("stability", ct);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Stability AI API key not configured");
        }

        var prompt = GetStringParam(parameters, "prompt", "");
        var negativePrompt = GetStringParam(parameters, "negativePrompt", "");
        var width = GetIntParam(parameters, "width", 1024);
        var height = GetIntParam(parameters, "height", 1024);
        var cfgScale = GetFloatParam(parameters, "cfgScale", 7.0);
        var steps = GetIntParam(parameters, "steps", 30);
        var seed = GetIntParam(parameters, "seed", 0);
        var samples = GetIntParam(parameters, "count", 1);

        // Use the newer stable-image API
        var endpoint = modelId.StartsWith("stable-image")
            ? $"{StabilityBaseUrl}/generation/{modelId}/text-to-image"
            : $"{StabilityBaseUrl}/generation/{modelId}/text-to-image";

        var body = new JsonObject
        {
            ["text_prompts"] = new JsonArray
            {
                new JsonObject { ["text"] = prompt, ["weight"] = 1 }
            },
            ["cfg_scale"] = cfgScale,
            ["width"] = width,
            ["height"] = height,
            ["steps"] = steps,
            ["samples"] = samples
        };

        if (!string.IsNullOrEmpty(negativePrompt))
        {
            ((JsonArray)body["text_prompts"]!).Add(new JsonObject { ["text"] = negativePrompt, ["weight"] = -1 });
        }

        if (seed > 0)
        {
            body["seed"] = seed;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Headers.Add("Accept", "application/json");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Stability AI generation failed: {Status} - {Response}", response.StatusCode, responseText);
            throw new InvalidOperationException($"Stability AI API error: {response.StatusCode}");
        }

        var json = JsonNode.Parse(responseText);
        var artifacts = json?["artifacts"]?.AsArray();

        if (artifacts == null || artifacts.Count == 0)
        {
            return null;
        }

        var results = new List<GeneratorResult>();
        var index = 0;
        foreach (var artifact in artifacts)
        {
            var base64 = artifact?["base64"]?.GetValue<string>();
            var seedValue = artifact?["seed"]?.GetValue<long>() ?? 0;
            var finishReason = artifact?["finishReason"]?.GetValue<string>();

            if (!string.IsNullOrEmpty(base64) && finishReason == "SUCCESS")
            {
                // Store base64 data as data URL for download later
                results.Add(new GeneratorResult(
                    Guid.NewGuid().ToString("N")[..8],
                    $"data:image/png;base64,{base64}",
                    "image/png",
                    base64.Length * 3 / 4, // Approximate decoded size
                    (int)seedValue,
                    index++,
                    new Dictionary<string, object?>
                    {
                        ["prompt"] = prompt,
                        ["seed"] = seedValue
                    }));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<GeneratorResult>?> GenerateWithGeminiNanoBananaAsync(
        string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        var apiKey = await _credentials.GetAsync("google", ct) ?? await _credentials.GetAsync("gemini", ct);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Google/Gemini API key not configured");
        }

        var prompt = GetStringParam(parameters, "prompt", "");
        var size = GetStringParam(parameters, "size", "1024x1024");
        var aspectRatio = GetStringParam(parameters, "aspectRatio", "1:1");
        var count = GetIntParam(parameters, "count", 1);
        var thinkingLevel = GetStringParam(parameters, "thinkingLevel", "minimal"); // minimal, high, dynamic

        // Map model aliases to actual Gemini model IDs
        var geminiModelId = modelId.ToLowerInvariant() switch
        {
            "nano-banana-2" => "gemini-3.1-flash-image-preview",
            "nano-banana-pro" => "gemini-3-pro-image-preview",
            "nano-banana" => "gemini-2.5-flash-image",
            _ => modelId
        };

        // Parse size to get dimensions
        var (width, height) = ParseImageSize(size);

        // Build the Gemini API request for image generation
        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = prompt }
                    }
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray { "IMAGE", "TEXT" },
                ["imageSizeOptions"] = new JsonObject
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["aspectRatio"] = aspectRatio
                },
                ["numberOfImages"] = count
            }
        };

        // Add thinking configuration for Nano Banana 2
        if (geminiModelId.Contains("3.1") && thinkingLevel != "minimal")
        {
            body["generationConfig"]!["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = thinkingLevel == "high" ? 8192 : 4096
            };
        }

        // Add reference image for edit mode
        if (mode == "edit" && references?.Count > 0)
        {
            var refImage = references[0];
            if (!string.IsNullOrEmpty(refImage.Base64Data))
            {
                var contents = (JsonArray)body["contents"]!;
                var parts = (JsonArray)contents[0]!["parts"]!;
                parts.Insert(0, new JsonObject
                {
                    ["inlineData"] = new JsonObject
                    {
                        ["mimeType"] = refImage.MimeType ?? "image/png",
                        ["data"] = refImage.Base64Data
                    }
                });
            }
        }

        var endpoint = $"{GeminiBaseUrl}/models/{geminiModelId}:generateContent?key={apiKey}";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini Nano Banana image generation failed: {Status} - {Response}", response.StatusCode, responseText);
            throw new InvalidOperationException($"Gemini API error: {response.StatusCode} - {responseText}");
        }

        var json = JsonNode.Parse(responseText);
        var candidates = json?["candidates"]?.AsArray();

        if (candidates == null || candidates.Count == 0)
        {
            _logger.LogWarning("Gemini returned no candidates");
            return null;
        }

        var results = new List<GeneratorResult>();
        var index = 0;

        foreach (var candidate in candidates)
        {
            var content = candidate?["content"];
            var parts = content?["parts"]?.AsArray();

            if (parts == null) continue;

            foreach (var part in parts)
            {
                var inlineData = part?["inlineData"];
                if (inlineData != null)
                {
                    var mimeType = inlineData["mimeType"]?.GetValue<string>() ?? "image/png";
                    var base64Data = inlineData["data"]?.GetValue<string>();

                    if (!string.IsNullOrEmpty(base64Data))
                    {
                        results.Add(new GeneratorResult(
                            Guid.NewGuid().ToString("N")[..8],
                            $"data:{mimeType};base64,{base64Data}",
                            mimeType,
                            base64Data.Length * 3 / 4,
                            Random.Shared.Next(1, int.MaxValue),
                            index++,
                            new Dictionary<string, object?>
                            {
                                ["prompt"] = prompt,
                                ["model"] = geminiModelId,
                                ["provider"] = "google"
                            }));
                    }
                }
            }
        }

        if (results.Count == 0)
        {
            _logger.LogWarning("Gemini response contained no images");
            return null;
        }

        _logger.LogInformation("Generated {Count} images with Gemini {Model}", results.Count, geminiModelId);
        return results;
    }

    private async Task<IReadOnlyList<GeneratorResult>?> GenerateWithComfyUIAsync(
        string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        // ComfyUI runs locally at http://localhost:8188/api
        // Get custom URL if configured
        var baseUrl = await _credentials.GetAsync("comfyui_url", ct) ?? "http://localhost:8188";
        baseUrl = baseUrl.TrimEnd('/');

        var prompt = GetStringParam(parameters, "prompt", "");
        var negativePrompt = GetStringParam(parameters, "negativePrompt", "");
        var width = GetIntParam(parameters, "width", 1024);
        var height = GetIntParam(parameters, "height", 1024);
        var steps = GetIntParam(parameters, "steps", 30);
        var cfgScale = GetFloatParam(parameters, "cfgScale", 7.0);
        var seed = GetIntParam(parameters, "seed", Random.Shared.Next());
        var checkpoint = GetStringParam(parameters, "checkpoint", modelId);

        // Build ComfyUI workflow JSON
        // This is a basic txt2img workflow - can be customized
        var workflow = new JsonObject
        {
            ["3"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["seed"] = seed,
                    ["steps"] = steps,
                    ["cfg"] = cfgScale,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["denoise"] = 1.0,
                    ["model"] = new JsonArray { "4", 0 },
                    ["positive"] = new JsonArray { "6", 0 },
                    ["negative"] = new JsonArray { "7", 0 },
                    ["latent_image"] = new JsonArray { "5", 0 }
                }
            },
            ["4"] = new JsonObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JsonObject { ["ckpt_name"] = checkpoint }
            },
            ["5"] = new JsonObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new JsonObject
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["batch_size"] = 1
                }
            },
            ["6"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JsonObject
                {
                    ["text"] = prompt,
                    ["clip"] = new JsonArray { "4", 1 }
                }
            },
            ["7"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JsonObject
                {
                    ["text"] = negativePrompt,
                    ["clip"] = new JsonArray { "4", 1 }
                }
            },
            ["8"] = new JsonObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JsonObject
                {
                    ["samples"] = new JsonArray { "3", 0 },
                    ["vae"] = new JsonArray { "4", 2 }
                }
            },
            ["9"] = new JsonObject
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new JsonObject
                {
                    ["filename_prefix"] = "ComfyUI",
                    ["images"] = new JsonArray { "8", 0 }
                }
            }
        };

        // Submit prompt to ComfyUI queue
        var submitBody = new JsonObject
        {
            ["prompt"] = workflow,
            ["client_id"] = Guid.NewGuid().ToString("N")
        };

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/prompt");
        submitRequest.Content = new StringContent(submitBody.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage submitResponse;
        try
        {
            submitResponse = await _http.SendAsync(submitRequest, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Cannot connect to ComfyUI at {BaseUrl}", baseUrl);
            throw new InvalidOperationException($"ComfyUI not running at {baseUrl}. Start ComfyUI server first.");
        }

        var submitText = await submitResponse.Content.ReadAsStringAsync(ct);
        if (!submitResponse.IsSuccessStatusCode)
        {
            _logger.LogError("ComfyUI prompt submission failed: {Status} - {Response}", submitResponse.StatusCode, submitText);
            throw new InvalidOperationException($"ComfyUI error: {submitResponse.StatusCode} - {submitText}");
        }

        var submitJson = JsonNode.Parse(submitText);
        var promptId = submitJson?["prompt_id"]?.GetValue<string>();

        if (string.IsNullOrEmpty(promptId))
        {
            throw new InvalidOperationException("Failed to get prompt ID from ComfyUI");
        }

        // Poll for completion via history endpoint
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            using var historyRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/history/{promptId}");
            using var historyResponse = await _http.SendAsync(historyRequest, ct);

            if (!historyResponse.IsSuccessStatusCode) continue;

            var historyText = await historyResponse.Content.ReadAsStringAsync(ct);
            var historyJson = JsonNode.Parse(historyText);
            var promptHistory = historyJson?[promptId];

            if (promptHistory == null) continue;

            var status = promptHistory["status"]?["status_str"]?.GetValue<string>();

            if (status == "error")
            {
                var errorMessages = promptHistory["status"]?["messages"]?.AsArray();
                var errorMsg = errorMessages?.FirstOrDefault()?["message"]?.GetValue<string>() ?? "ComfyUI execution failed";
                throw new InvalidOperationException(errorMsg);
            }

            var outputs = promptHistory["outputs"];
            if (outputs == null) continue;

            // Find the SaveImage node output (node 9)
            var saveOutput = outputs["9"];
            var images = saveOutput?["images"]?.AsArray();

            if (images == null || images.Count == 0) continue;

            var results = new List<GeneratorResult>();
            var index = 0;

            foreach (var img in images)
            {
                var filename = img?["filename"]?.GetValue<string>();
                var subfolder = img?["subfolder"]?.GetValue<string>() ?? "";
                var imgType = img?["type"]?.GetValue<string>() ?? "output";

                if (string.IsNullOrEmpty(filename)) continue;

                // Build the URL to fetch the image
                var imageUrl = $"{baseUrl}/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={imgType}";

                results.Add(new GeneratorResult(
                    Guid.NewGuid().ToString("N")[..8],
                    imageUrl,
                    "image/png",
                    0,
                    seed,
                    index++,
                    new Dictionary<string, object?>
                    {
                        ["prompt"] = prompt,
                        ["checkpoint"] = checkpoint,
                        ["seed"] = seed,
                        ["provider"] = "comfyui"
                    }));
            }

            if (results.Count > 0)
            {
                _logger.LogInformation("Generated {Count} images with ComfyUI using {Checkpoint}", results.Count, checkpoint);
                return results;
            }
        }

        return null;
    }

    private static (int Width, int Height) ParseImageSize(string size)
    {
        return size.ToLowerInvariant() switch
        {
            "4k" or "4096x4096" => (4096, 4096),
            "2k" or "2048x2048" => (2048, 2048),
            "1792x1024" => (1792, 1024),
            "1024x1792" => (1024, 1792),
            "512x512" => (512, 512),
            "1024x1024" or _ => (1024, 1024)
        };
    }

    public async Task DownloadResultAsync(GeneratorResult result, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (result.SourceUrl.StartsWith("data:"))
        {
            // Handle base64 data URL
            var commaIndex = result.SourceUrl.IndexOf(',');
            if (commaIndex > 0)
            {
                var base64Data = result.SourceUrl[(commaIndex + 1)..];
                var bytes = Convert.FromBase64String(base64Data);
                await File.WriteAllBytesAsync(outputPath, bytes, ct);
            }
        }
        else
        {
            // Download from URL
            using var response = await _http.GetAsync(result.SourceUrl, ct);
            response.EnsureSuccessStatusCode();

            await using var fileStream = File.Create(outputPath);
            await response.Content.CopyToAsync(fileStream, ct);
        }

        _logger.LogDebug("Downloaded image result to {Path}", outputPath);
    }

    public IReadOnlyList<GeneratorCapabilities> GetCapabilities(string? providerId)
    {
        // Return provider-specific capabilities
        return providerId switch
        {
            "google" or "gemini" => new[]
            {
                new GeneratorCapabilities(
                    GeneratorModality.Image,
                    new[]
                    {
                        new GeneratorModeSpec("generate", "Generate", "Generate image from text prompt with Nano Banana 2", new[] { "prompt" }, new[] { "size", "aspectRatio", "count", "thinkingLevel" }),
                        new GeneratorModeSpec("edit", "Edit", "Edit existing image with text instructions", new[] { "image", "prompt" }, new[] { "mask" })
                    },
                    new[] { "png", "jpg", "webp" },
                    4096, // Supports up to 4K
                    null,
                    true,  // supportsTextPrompts
                    true,  // supportsImageInputs
                    true)  // supportsMultipleOutputs
            },
            "comfyui" or "comfy" => new[]
            {
                new GeneratorCapabilities(
                    GeneratorModality.Image,
                    new[]
                    {
                        new GeneratorModeSpec("generate", "Generate", "Generate image with ComfyUI (local)", new[] { "prompt" }, new[] { "negativePrompt", "width", "height", "seed", "steps", "cfgScale", "checkpoint" }),
                        new GeneratorModeSpec("img2img", "Image to Image", "Transform image with prompt", new[] { "image", "prompt" }, new[] { "denoise", "seed" })
                    },
                    new[] { "png", "jpg", "webp" },
                    4096,
                    null,
                    true,  // supportsTextPrompts
                    true,  // supportsImageInputs
                    true)  // supportsMultipleOutputs
            },
            _ => new[]
            {
                new GeneratorCapabilities(
                    GeneratorModality.Image,
                    new[]
                    {
                        new GeneratorModeSpec("generate", "Generate", "Generate image from text prompt", new[] { "prompt" }, new[] { "negativePrompt", "width", "height", "seed", "count", "quality", "style" }),
                        new GeneratorModeSpec("edit", "Edit", "Edit existing image", new[] { "image", "prompt" }, new[] { "mask" }),
                        new GeneratorModeSpec("variations", "Variations", "Create variations of an image", new[] { "image" }, new[] { "count" })
                    },
                    new[] { "png", "jpg", "webp" },
                    4096,
                    null,
                    true,
                    true,
                    true)
            }
        };
    }

    private static string GetStringParam(IReadOnlyDictionary<string, object?>? parameters, string key, string defaultValue)
    {
        if (parameters?.TryGetValue(key, out var value) == true && value != null)
            return value.ToString() ?? defaultValue;
        return defaultValue;
    }

    private static int GetIntParam(IReadOnlyDictionary<string, object?>? parameters, string key, int defaultValue)
    {
        if (parameters?.TryGetValue(key, out var value) == true && value != null)
        {
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static double GetFloatParam(IReadOnlyDictionary<string, object?>? parameters, string key, double defaultValue)
    {
        if (parameters?.TryGetValue(key, out var value) == true && value != null)
        {
            if (value is double d) return d;
            if (value is float f) return f;
            if (double.TryParse(value.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }
}

#endregion

#region Material PBR Modality Adapter

public class MaterialPbrModalityAdapter : IModalityAdapter
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;

    // For PBR materials, we can use image generation to create base textures,
    // then process them into PBR maps using local algorithms or specialized APIs

    public MaterialPbrModalityAdapter(ILogger logger, HttpClient http, ICredentialStore credentials)
    {
        _logger = logger;
        _http = http;
        _credentials = credentials;
    }

    public Task<QuoteResult> GetQuoteAsync(string providerId, string modelId, string mode, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        // PBR generation typically costs more as it generates multiple maps
        return Task.FromResult(new QuoteResult(true, 0.15m, "USD", null));
    }

    public Task<string?> ValidateInputsAsync(IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<ArtifactReference>? references, CancellationToken ct)
    {
        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString();
        if (string.IsNullOrWhiteSpace(prompt) && (references == null || references.Count == 0))
        {
            return Task.FromResult<string?>("Prompt or reference image is required");
        }
        return Task.FromResult<string?>(null);
    }

    public async Task<IReadOnlyList<GeneratorResult>?> GenerateAsync(
        string providerId, string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        // Use OpenAI to generate a base texture, then derive PBR maps
        var apiKey = await _credentials.GetAsync("openai", ct);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key not configured for material generation");
        }

        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString() ?? "seamless texture";
        var seamless = parameters?.GetValueOrDefault("seamless")?.ToString()?.ToLower() == "true";

        // Enhance prompt for seamless tileable texture
        var enhancedPrompt = $"Seamless tileable {prompt} texture, top-down view, uniform lighting, no shadows, material texture";

        var body = new JsonObject
        {
            ["model"] = "dall-e-3",
            ["prompt"] = enhancedPrompt,
            ["size"] = "1024x1024",
            ["n"] = 1,
            ["response_format"] = "url"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Material base texture generation failed: {Status}", response.StatusCode);
            throw new InvalidOperationException($"API error: {response.StatusCode}");
        }

        var json = JsonNode.Parse(responseText);
        var url = json?["data"]?[0]?["url"]?.GetValue<string>();

        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        // Generate results for each PBR map
        // In a full implementation, we would process the albedo to derive other maps
        var baseId = Guid.NewGuid().ToString("N")[..8];
        var results = new List<GeneratorResult>
        {
            new GeneratorResult($"{baseId}_albedo", url, "image/png", 0, Random.Shared.Next(), 0,
                new Dictionary<string, object?> { ["mapType"] = "albedo", ["prompt"] = prompt }),
            // Additional maps would be derived from the albedo
            new GeneratorResult($"{baseId}_normal", $"derived:{baseId}_albedo:normal", "image/png", 0, Random.Shared.Next(), 1,
                new Dictionary<string, object?> { ["mapType"] = "normal", ["derived"] = true }),
            new GeneratorResult($"{baseId}_roughness", $"derived:{baseId}_albedo:roughness", "image/png", 0, Random.Shared.Next(), 2,
                new Dictionary<string, object?> { ["mapType"] = "roughness", ["derived"] = true }),
            new GeneratorResult($"{baseId}_metallic", $"derived:{baseId}_albedo:metallic", "image/png", 0, Random.Shared.Next(), 3,
                new Dictionary<string, object?> { ["mapType"] = "metallic", ["derived"] = true }),
            new GeneratorResult($"{baseId}_ao", $"derived:{baseId}_albedo:ao", "image/png", 0, Random.Shared.Next(), 4,
                new Dictionary<string, object?> { ["mapType"] = "ao", ["derived"] = true })
        };

        return results;
    }

    public async Task DownloadResultAsync(GeneratorResult result, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (result.SourceUrl.StartsWith("derived:"))
        {
            // For derived maps, we would normally process the source image
            // For now, create a placeholder that indicates processing needed
            var grayPixel = new byte[] { 128, 128, 128, 255 }; // Gray pixel
            await File.WriteAllBytesAsync(outputPath, CreatePlaceholderPng(result.Metadata?["mapType"]?.ToString()), ct);
            _logger.LogWarning("Created placeholder for derived map {MapType} - real processing not implemented", result.Metadata?["mapType"]);
        }
        else
        {
            using var response = await _http.GetAsync(result.SourceUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var fileStream = File.Create(outputPath);
            await response.Content.CopyToAsync(fileStream, ct);
        }
    }

    private static byte[] CreatePlaceholderPng(string? mapType)
    {
        // Create a minimal valid PNG with appropriate default color for each map type
        // This is a simplified 1x1 PNG - real implementation would process the albedo
        var color = mapType switch
        {
            "normal" => new byte[] { 128, 128, 255 }, // Normal map default (pointing up)
            "roughness" => new byte[] { 128, 128, 128 }, // Mid roughness
            "metallic" => new byte[] { 0, 0, 0 }, // Non-metallic
            "ao" => new byte[] { 255, 255, 255 }, // Full ambient occlusion
            _ => new byte[] { 128, 128, 128 }
        };

        // Minimal PNG structure
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // PNG signature
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR chunk (13 bytes: width, height, bit depth, color type, compression, filter, interlace)
        WriteChunk(bw, "IHDR", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0 });

        // IDAT chunk (compressed pixel data for 1x1 RGB pixel)
        var scanline = new byte[] { 0, color[0], color[1], color[2] }; // filter byte + RGB
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            deflate.Write(scanline);
        }
        var compressedData = compressed.ToArray();
        var idatData = new byte[compressedData.Length + 6];
        idatData[0] = 0x78; idatData[1] = 0x9C; // zlib header
        Array.Copy(compressedData, 0, idatData, 2, compressedData.Length);
        // Adler-32 checksum (simplified)
        var adler = CalculateAdler32(scanline);
        idatData[^4] = (byte)(adler >> 24);
        idatData[^3] = (byte)(adler >> 16);
        idatData[^2] = (byte)(adler >> 8);
        idatData[^1] = (byte)adler;
        WriteChunk(bw, "IDAT", idatData);

        // IEND chunk
        WriteChunk(bw, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static void WriteChunk(BinaryWriter bw, string type, byte[] data)
    {
        // Length (big-endian)
        bw.Write(BitConverter.IsLittleEndian
            ? BitConverter.GetBytes(data.Length).Reverse().ToArray()
            : BitConverter.GetBytes(data.Length));

        // Type
        var typeBytes = Encoding.ASCII.GetBytes(type);
        bw.Write(typeBytes);

        // Data
        bw.Write(data);

        // CRC (over type + data)
        var crcInput = typeBytes.Concat(data).ToArray();
        var crc = CalculateCrc32(crcInput);
        bw.Write(BitConverter.IsLittleEndian
            ? BitConverter.GetBytes(crc).Reverse().ToArray()
            : BitConverter.GetBytes(crc));
    }

    private static uint CalculateCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
        }
        return ~crc;
    }

    private static uint CalculateAdler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    public IReadOnlyList<GeneratorCapabilities> GetCapabilities(string? providerId)
    {
        return new[]
        {
            new GeneratorCapabilities(
                GeneratorModality.MaterialPbr,
                new[]
                {
                    new GeneratorModeSpec("generate", "Generate", "Generate PBR material from text", new[] { "prompt" }, new[] { "style", "seamless" }),
                    new GeneratorModeSpec("from_image", "From Image", "Generate PBR maps from base image", new[] { "image" }, new[] { "mapTypes" })
                },
                new[] { "png" },
                4096,
                null,
                true,
                true,
                true)
        };
    }
}

#endregion

#region Mesh Modality Adapter - Meshy / Tripo AI

public class MeshModalityAdapter : IModalityAdapter
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;

    private const string MeshyBaseUrl = "https://api.meshy.ai/v2";
    private const string DefaultHunyuan3DUrl = "http://localhost:7860"; // Default Gradio port
    private const string DefaultKaoUrl = "http://localhost:11435";       // Kao REST API (ollama-style)

    public MeshModalityAdapter(ILogger logger, HttpClient http, ICredentialStore credentials)
    {
        _logger = logger;
        _http = http;
        _credentials = credentials;
    }

    public Task<QuoteResult> GetQuoteAsync(string providerId, string modelId, string mode, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        // Local providers (Hunyuan3D, Kao) have no API costs
        if (providerId is "hunyuan3d" or "hunyuan" or "kao")
        {
            return Task.FromResult(new QuoteResult(true, 0.00m, "USD", null));
        }

        var cost = mode switch
        {
            "generate" => 0.20m,
            "from_image" => 0.15m,
            "retopology" => 0.10m,
            "texture" => 0.08m,
            _ => 0.20m
        };
        return Task.FromResult(new QuoteResult(true, cost, "USD", null));
    }

    public Task<string?> ValidateInputsAsync(IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<ArtifactReference>? references, CancellationToken ct)
    {
        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString();
        if (string.IsNullOrWhiteSpace(prompt) && (references == null || references.Count == 0))
        {
            return Task.FromResult<string?>("Prompt or reference image/mesh is required");
        }
        return Task.FromResult<string?>(null);
    }

    public async Task<IReadOnlyList<GeneratorResult>?> GenerateAsync(
        string providerId, string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        // Route to the appropriate provider
        if (providerId is "hunyuan3d" or "hunyuan")
        {
            return await GenerateWithHunyuan3DAsync(modelId, mode, parameters, references, ct);
        }

        if (providerId == "kao")
        {
            return await GenerateWithKaoAsync(modelId, mode, parameters, references, ct);
        }

        var apiKey = await _credentials.GetAsync("meshy", ct);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Meshy API key not configured");
        }

        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString() ?? "";
        var artStyle = parameters?.GetValueOrDefault("style")?.ToString() ?? "realistic";

        // Submit text-to-3D task
        var submitBody = new JsonObject
        {
            ["mode"] = "preview",
            ["prompt"] = prompt,
            ["art_style"] = artStyle,
            ["negative_prompt"] = parameters?.GetValueOrDefault("negativePrompt")?.ToString() ?? ""
        };

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{MeshyBaseUrl}/text-to-3d");
        submitRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        submitRequest.Content = new StringContent(submitBody.ToJsonString(), Encoding.UTF8, "application/json");

        using var submitResponse = await _http.SendAsync(submitRequest, ct);
        var submitText = await submitResponse.Content.ReadAsStringAsync(ct);

        if (!submitResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Meshy task submission failed: {Status} - {Response}", submitResponse.StatusCode, submitText);
            throw new InvalidOperationException($"Meshy API error: {submitResponse.StatusCode}");
        }

        var submitJson = JsonNode.Parse(submitText);
        var taskId = submitJson?["result"]?.GetValue<string>();

        if (string.IsNullOrEmpty(taskId))
        {
            throw new InvalidOperationException("Failed to get task ID from Meshy");
        }

        // Poll for completion
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct); // Poll every 5 seconds

            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{MeshyBaseUrl}/text-to-3d/{taskId}");
            statusRequest.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var statusResponse = await _http.SendAsync(statusRequest, ct);
            var statusText = await statusResponse.Content.ReadAsStringAsync(ct);

            if (!statusResponse.IsSuccessStatusCode)
            {
                continue;
            }

            var statusJson = JsonNode.Parse(statusText);
            var status = statusJson?["status"]?.GetValue<string>();

            if (status == "SUCCEEDED")
            {
                var modelUrls = statusJson?["model_urls"];
                var glbUrl = modelUrls?["glb"]?.GetValue<string>();
                var fbxUrl = modelUrls?["fbx"]?.GetValue<string>();
                var objUrl = modelUrls?["obj"]?.GetValue<string>();

                var results = new List<GeneratorResult>();

                if (!string.IsNullOrEmpty(glbUrl))
                {
                    results.Add(new GeneratorResult(
                        $"{taskId}_glb",
                        glbUrl,
                        "model/gltf-binary",
                        0,
                        Random.Shared.Next(),
                        0,
                        new Dictionary<string, object?> { ["format"] = "glb", ["prompt"] = prompt }));
                }

                if (!string.IsNullOrEmpty(fbxUrl))
                {
                    results.Add(new GeneratorResult(
                        $"{taskId}_fbx",
                        fbxUrl,
                        "model/fbx",
                        0,
                        Random.Shared.Next(),
                        1,
                        new Dictionary<string, object?> { ["format"] = "fbx", ["prompt"] = prompt }));
                }

                return results.Count > 0 ? results : null;
            }
            else if (status == "FAILED")
            {
                var errorMessage = statusJson?["message"]?.GetValue<string>() ?? "Generation failed";
                throw new InvalidOperationException(errorMessage);
            }
            else if (status is "EXPIRED" or "CANCELLED")
            {
                throw new InvalidOperationException($"Task {status.ToLowerInvariant()}");
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<GeneratorResult>?> GenerateWithHunyuan3DAsync(
        string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        // Hunyuan3D-2 runs locally via Gradio interface
        // Default: http://localhost:7860 (Gradio default port)
        var baseUrl = await _credentials.GetAsync("hunyuan3d_url", ct) ?? DefaultHunyuan3DUrl;
        baseUrl = baseUrl.TrimEnd('/');

        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString() ?? "";
        var artStyle = parameters?.GetValueOrDefault("style")?.ToString() ?? "realistic";
        var steps = 30;
        if (parameters?.TryGetValue("steps", out var stepsObj) == true && stepsObj is int s) steps = s;
        var seed = Random.Shared.Next();
        if (parameters?.TryGetValue("seed", out var seedObj) == true && seedObj is int sd) seed = sd;

        // Determine which Hunyuan3D variant to use
        var variant = modelId?.ToLowerInvariant() switch
        {
            "hunyuan3d-mini" or "mini" => "mini",
            "hunyuan3d-fast" or "fast" or "turbo" => "fast",
            "hunyuan3d-2.1" or "v2.1" => "v2.1",
            _ => "standard" // Default v2.0
        };

        // Check if Hunyuan3D is running
        try
        {
            using var healthCheck = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/");
            using var healthResponse = await _http.SendAsync(healthCheck, ct);
            if (!healthResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Hunyuan3D not responding at {baseUrl}");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Cannot connect to Hunyuan3D at {BaseUrl}", baseUrl);
            throw new InvalidOperationException(
                $"Hunyuan3D-2 not running at {baseUrl}. " +
                "Start Hunyuan3D: cd B:\\workshop\\Hunyuan3D-2 && run_hunyuan3d.bat");
        }

        // Build Gradio API request
        // Hunyuan3D-2 Gradio interface expects specific inputs
        var gradioPayload = new JsonObject
        {
            ["data"] = new JsonArray
            {
                prompt,          // Text prompt
                seed,            // Seed
                steps,           // Inference steps
                7.5,             // CFG scale
                true,            // Generate mesh
                true,            // Generate texture
                variant          // Model variant
            },
            ["fn_index"] = 0     // Main generation function
        };

        // Handle image-to-3D mode
        if (mode == "from_image" && references?.Count > 0)
        {
            var refImage = references[0];
            if (!string.IsNullOrEmpty(refImage.Base64Data))
            {
                // For image input, use a different function index
                gradioPayload["fn_index"] = 1;
                gradioPayload["data"] = new JsonArray
                {
                    $"data:{refImage.MimeType ?? "image/png"};base64,{refImage.Base64Data}", // Input image
                    seed,
                    steps,
                    7.5,
                    true,
                    true
                };
            }
        }

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/predict");
        submitRequest.Content = new StringContent(gradioPayload.ToJsonString(), Encoding.UTF8, "application/json");

        using var submitResponse = await _http.SendAsync(submitRequest, ct);
        var submitText = await submitResponse.Content.ReadAsStringAsync(ct);

        if (!submitResponse.IsSuccessStatusCode)
        {
            // Try the /run endpoint as fallback (older Gradio versions)
            using var runRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/run/predict");
            runRequest.Content = new StringContent(gradioPayload.ToJsonString(), Encoding.UTF8, "application/json");

            using var runResponse = await _http.SendAsync(runRequest, ct);
            if (!runResponse.IsSuccessStatusCode)
            {
                var errorText = await runResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError("Hunyuan3D generation failed: {Status} - {Response}", runResponse.StatusCode, errorText);
                throw new InvalidOperationException($"Hunyuan3D error: {runResponse.StatusCode}");
            }

            submitText = await runResponse.Content.ReadAsStringAsync(ct);
        }

        var responseJson = JsonNode.Parse(submitText);

        // Gradio returns data array with outputs
        var data = responseJson?["data"]?.AsArray();
        if (data == null || data.Count == 0)
        {
            _logger.LogWarning("Hunyuan3D returned no output data");
            return null;
        }

        var results = new List<GeneratorResult>();

        // Parse output - typically returns file paths or base64 data
        for (int i = 0; i < data.Count; i++)
        {
            var output = data[i];
            if (output == null) continue;

            // Handle file output (Gradio returns file info object)
            var fileName = output["name"]?.GetValue<string>() ?? output["path"]?.GetValue<string>();
            var isFile = output["is_file"]?.GetValue<bool>() ?? false;

            if (!string.IsNullOrEmpty(fileName))
            {
                var extension = Path.GetExtension(fileName).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".glb" => "model/gltf-binary",
                    ".gltf" => "model/gltf+json",
                    ".obj" => "model/obj",
                    ".fbx" => "model/fbx",
                    _ => "application/octet-stream"
                };

                // Build URL to fetch the file from Gradio
                var fileUrl = isFile
                    ? $"{baseUrl}/file={Uri.EscapeDataString(fileName)}"
                    : fileName; // May already be a URL or base64

                results.Add(new GeneratorResult(
                    Guid.NewGuid().ToString("N")[..8],
                    fileUrl,
                    contentType,
                    0,
                    seed,
                    i,
                    new Dictionary<string, object?>
                    {
                        ["prompt"] = prompt,
                        ["variant"] = variant,
                        ["seed"] = seed,
                        ["provider"] = "hunyuan3d"
                    }));
            }
            else if (output.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                // Direct string output (possibly base64 or path)
                var outputStr = output.GetValue<string>();
                if (!string.IsNullOrEmpty(outputStr))
                {
                    results.Add(new GeneratorResult(
                        Guid.NewGuid().ToString("N")[..8],
                        outputStr.StartsWith("data:") ? outputStr : $"{baseUrl}/file={Uri.EscapeDataString(outputStr)}",
                        "model/gltf-binary",
                        0,
                        seed,
                        i,
                        new Dictionary<string, object?>
                        {
                            ["prompt"] = prompt,
                            ["provider"] = "hunyuan3d"
                        }));
                }
            }
        }

        if (results.Count > 0)
        {
            _logger.LogInformation("Generated {Count} meshes with Hunyuan3D-2 ({Variant})", results.Count, variant);
        }

        return results.Count > 0 ? results : null;
    }

    private async Task<IReadOnlyList<GeneratorResult>?> GenerateWithKaoAsync(
        string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        // Kao runs locally with a REST API. The port is auto-selected (11435-11550)
        // and the live URL is published to ~/.kao/service.json, so resolve it from
        // discovery rather than assuming a fixed port.
        var (baseUrl, token) = await ResolveKaoEndpointAsync(ct);

        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString() ?? "";
        var steps = 30;
        if (parameters?.TryGetValue("steps", out var stepsObj) == true)
        {
            if (stepsObj is int s) steps = s;
            else if (int.TryParse(stepsObj?.ToString(), out var parsed)) steps = parsed;
        }
        var guidanceScale = 5.0;
        if (parameters?.TryGetValue("guidance_scale", out var cfgObj) == true)
        {
            if (cfgObj is double d) guidanceScale = d;
            else if (double.TryParse(cfgObj?.ToString(), out var parsed)) guidanceScale = parsed;
        }
        var seed = Random.Shared.Next();
        if (parameters?.TryGetValue("seed", out var seedObj) == true)
        {
            if (seedObj is int sd) seed = sd;
            else if (int.TryParse(seedObj?.ToString(), out var parsed)) seed = parsed;
        }
        var octreeResolution = 256;
        if (parameters?.TryGetValue("octree_resolution", out var octreeObj) == true)
        {
            if (octreeObj is int o) octreeResolution = o;
            else if (int.TryParse(octreeObj?.ToString(), out var parsed)) octreeResolution = parsed;
        }
        var generateTexture = true;
        if (parameters?.TryGetValue("generate_texture", out var texObj) == true)
        {
            if (texObj is bool b) generateTexture = b;
            else if (bool.TryParse(texObj?.ToString(), out var parsed)) generateTexture = parsed;
        }
        var outputFormat = parameters?.GetValueOrDefault("output_format")?.ToString() ?? "glb";

        // Determine whether this request is image-driven or text-driven so we can
        // pick the correct Kao backend variant (e.g. point-e-image vs point-e-text).
        var hasImageInput = mode is "from_image" or "from_multiview" && references is { Count: > 0 };
        var isMultiview = mode == "from_multiview" && references is { Count: >= 3 };

        // Map the requested model id onto a registered Kao model name. Kao's model
        // ids changed to turbo variants and split point-e/shap-e by input type, so
        // legacy/friendly aliases are normalized here.
        var requested = modelId?.ToLowerInvariant() ?? "";
        var kaoModel = requested switch
        {
            // Already-valid Kao model identifiers - pass through unchanged.
            "hunyuan3d-2.1-turbo" or "hunyuan3d-2-turbo" or "hunyuan3d-mini-turbo"
                or "hunyuan3d-2mv" or "triposr"
                or "point-e-image" or "point-e-text"
                or "shap-e-image" or "shap-e-text" => requested,
            // Legacy / friendly aliases.
            "hunyuan3d-2.1" or "hunyuan3d-21" or "hunyuan3d-2" or "hunyuan3d" or "hunyuan" => "hunyuan3d-2.1-turbo",
            "hunyuan3d-mini" or "mini" => "hunyuan3d-mini-turbo",
            "multiview" or "hunyuan3d-2mv-turbo" => "hunyuan3d-2mv",
            "tripo" => "triposr",
            "point-e" or "pointe" => hasImageInput ? "point-e-image" : "point-e-text",
            "shap-e" or "shape" => hasImageInput ? "shap-e-image" : "shap-e-text",
            // Default: multiview routes to the MV model, otherwise the flagship turbo.
            _ => isMultiview ? "hunyuan3d-2mv" : "hunyuan3d-2.1-turbo"
        };

        // Check if Kao is running via the lightweight health probe (avoids the
        // heavier /api/status path that touches the CUDA runtime).
        try
        {
            using var healthCheck = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/health");
            ApplyKaoAuth(healthCheck, token);
            using var healthResponse = await _http.SendAsync(healthCheck, ct);
            if (!healthResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Kao not responding at {baseUrl}");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Cannot connect to Kao at {BaseUrl}", baseUrl);
            throw new InvalidOperationException(
                $"Kao not running at {baseUrl}. " +
                "Start Kao: kao serve");
        }

        // Build Kao API request
        var kaoPayload = new JsonObject
        {
            ["model"] = kaoModel,
            ["steps"] = steps,
            ["guidance_scale"] = guidanceScale,
            ["seed"] = seed,
            ["octree_resolution"] = octreeResolution,
            ["remove_background"] = true,
            ["generate_texture"] = generateTexture,
            ["output_format"] = outputFormat
        };

        // Add text prompt if provided
        if (!string.IsNullOrEmpty(prompt))
        {
            kaoPayload["text"] = prompt;
        }

        // Handle image input
        if (mode == "from_image" && references?.Count > 0)
        {
            var refImage = references[0];
            if (!string.IsNullOrEmpty(refImage.Base64Data))
            {
                kaoPayload["image"] = refImage.Base64Data;
            }
        }

        // Handle multi-view input
        if (mode == "from_multiview" && references?.Count >= 3)
        {
            var images = new JsonObject();
            for (int i = 0; i < references.Count && i < 4; i++)
            {
                var viewName = i switch { 0 => "front", 1 => "left", 2 => "back", 3 => "right", _ => $"view{i}" };
                if (!string.IsNullOrEmpty(references[i].Base64Data))
                {
                    images[viewName] = references[i].Base64Data;
                }
            }
            kaoPayload["images"] = images;
        }

        _logger.LogInformation("Submitting to Kao: model={Model}, steps={Steps}, seed={Seed}", kaoModel, steps, seed);

        // Submit as an asynchronous local job. Mesh generation is long-running, so
        // the job API lets us poll progress and cancel cleanly instead of holding a
        // single synchronous request open for minutes.
        string jobId;
        using (var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/jobs"))
        {
            ApplyKaoAuth(submitRequest, token);
            submitRequest.Content = new StringContent(kaoPayload.ToJsonString(), Encoding.UTF8, "application/json");

            using var submitResponse = await _http.SendAsync(submitRequest, ct);
            var submitText = await submitResponse.Content.ReadAsStringAsync(ct);

            if (!submitResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Kao job submission failed: {Status} - {Response}", submitResponse.StatusCode, submitText);
                throw new InvalidOperationException($"Kao error: {submitResponse.StatusCode} - {submitText}");
            }

            var submitJson = JsonNode.Parse(submitText)
                ?? throw new InvalidOperationException("Kao returned an invalid job response");
            jobId = submitJson["job_id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Kao job response missing job_id");
        }

        _logger.LogInformation("Kao job {JobId} queued for model {Model}", jobId, kaoModel);

        // Poll the job to completion. If our caller cancels, ask Kao to cancel too.
        var responseJson = await PollKaoJobAsync(baseUrl, token, jobId, ct);
        if (responseJson == null)
        {
            throw new InvalidOperationException("Kao returned an invalid job result");
        }

        var results = new List<GeneratorResult>();
        var responseSeed = responseJson["seed"]?.GetValue<int>() ?? seed;
        var stats = responseJson["stats"]?.AsObject();

        // Parse mesh output (base64 encoded)
        var meshB64 = responseJson["mesh"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(meshB64))
        {
            // Kao returns base64-encoded mesh data directly
            var contentType = outputFormat switch
            {
                "glb" => "model/gltf-binary",
                "obj" => "model/obj",
                "ply" => "application/x-ply",
                _ => "application/octet-stream"
            };

            results.Add(new GeneratorResult(
                Guid.NewGuid().ToString("N")[..8],
                $"data:{contentType};base64,{meshB64}",
                contentType,
                meshB64.Length * 3 / 4, // Approximate decoded size
                responseSeed,
                0,
                new Dictionary<string, object?>
                {
                    ["prompt"] = prompt,
                    ["model"] = kaoModel,
                    ["seed"] = responseSeed,
                    ["provider"] = "kao",
                    ["format"] = outputFormat,
                    ["stats"] = stats?.ToJsonString()
                }));
        }

        // Parse point cloud output (base64 encoded PLY)
        var pointcloudB64 = responseJson["pointcloud"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(pointcloudB64))
        {
            results.Add(new GeneratorResult(
                Guid.NewGuid().ToString("N")[..8],
                $"data:application/x-ply;base64,{pointcloudB64}",
                "application/x-ply",
                pointcloudB64.Length * 3 / 4,
                responseSeed,
                1,
                new Dictionary<string, object?>
                {
                    ["type"] = "pointcloud",
                    ["provider"] = "kao"
                }));
        }

        // Parse auxiliary image outputs (base64 PNG) emitted by world-mirror models.
        foreach (var (field, label) in new[] { ("depth", "depth"), ("normals", "normals") })
        {
            var pngB64 = responseJson[field]?.GetValue<string>();
            if (string.IsNullOrEmpty(pngB64))
            {
                continue;
            }

            results.Add(new GeneratorResult(
                Guid.NewGuid().ToString("N")[..8],
                $"data:image/png;base64,{pngB64}",
                "image/png",
                pngB64.Length * 3 / 4,
                responseSeed,
                results.Count,
                new Dictionary<string, object?>
                {
                    ["type"] = label,
                    ["provider"] = "kao"
                }));
        }

        if (results.Count > 0)
        {
            _logger.LogInformation("Generated {Count} outputs with Kao ({Model}), seed={Seed}", results.Count, kaoModel, responseSeed);
        }

        return results.Count > 0 ? results : null;
    }

    /// <summary>
    /// Resolves the Kao service base URL and optional bearer token. Resolution order:
    /// explicit credential override, KAO_URL env var, the ~/.kao/service.json discovery
    /// file (honoring KAO_SERVICE_FILE), then the default loopback port.
    /// </summary>
    private async Task<(string BaseUrl, string? Token)> ResolveKaoEndpointAsync(CancellationToken ct)
    {
        // 1. Explicit credential override (e.g. user-configured remote Kao).
        var configured = await _credentials.GetAsync("kao_url", ct);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var overrideToken = await _credentials.GetAsync("kao_token", ct)
                ?? Environment.GetEnvironmentVariable("KAO_TOKEN");
            return (configured.TrimEnd('/'), overrideToken);
        }

        // 2. KAO_URL environment variable.
        var envUrl = Environment.GetEnvironmentVariable("KAO_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            return (envUrl.TrimEnd('/'), Environment.GetEnvironmentVariable("KAO_TOKEN"));
        }

        // 3. Service discovery file written by `kao serve`.
        try
        {
            var serviceFile = Environment.GetEnvironmentVariable("KAO_SERVICE_FILE")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".kao", "service.json");

            if (File.Exists(serviceFile))
            {
                var json = await File.ReadAllTextAsync(serviceFile, ct);
                var node = JsonNode.Parse(json);
                var url = node?["url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var token = Environment.GetEnvironmentVariable("KAO_TOKEN");
                    var authRequired = node?["auth_required"]?.GetValue<bool>() ?? false;
                    if (authRequired && string.IsNullOrEmpty(token))
                    {
                        var tokenPath = node?["token_path"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(tokenPath) && File.Exists(tokenPath))
                        {
                            token = (await File.ReadAllTextAsync(tokenPath, ct)).Trim();
                        }
                    }
                    return (url.TrimEnd('/'), token);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Kao service discovery file; falling back to default");
        }

        // 4. Default loopback endpoint.
        return (DefaultKaoUrl, Environment.GetEnvironmentVariable("KAO_TOKEN"));
    }

    private static void ApplyKaoAuth(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// Polls a Kao local job until it reaches a terminal state and returns the
    /// GenerateResponse payload. Requests cancellation on the Kao side if the
    /// caller's token is cancelled.
    /// </summary>
    private async Task<JsonNode?> PollKaoJobAsync(string baseUrl, string? token, string jobId, CancellationToken ct)
    {
        var pollDelay = TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                await Task.Delay(pollDelay, ct);

                using var statusReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/jobs/{jobId}");
                ApplyKaoAuth(statusReq, token);
                using var statusResp = await _http.SendAsync(statusReq, ct);
                if (!statusResp.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Kao job status error: {statusResp.StatusCode}");
                }

                var statusText = await statusResp.Content.ReadAsStringAsync(ct);
                var statusJson = JsonNode.Parse(statusText);
                var status = statusJson?["status"]?.GetValue<string>() ?? "";
                var progress = statusJson?["progress"]?.GetValue<int>() ?? 0;
                _logger.LogDebug("Kao job {JobId} status={Status} progress={Progress}", jobId, status, progress);

                switch (status)
                {
                    case "completed":
                        // The job record carries the result inline; fall back to the
                        // dedicated result endpoint if it is not present.
                        var inlineResult = statusJson?["result"];
                        if (inlineResult != null)
                        {
                            return inlineResult;
                        }
                        return await FetchKaoJobResultAsync(baseUrl, token, jobId, ct);

                    case "failed":
                        var err = statusJson?["error"]?.GetValue<string>() ?? "unknown error";
                        throw new InvalidOperationException($"Kao job failed: {err}");

                    case "cancelled":
                        throw new OperationCanceledException("Kao job was cancelled");

                    default:
                        // queued / running / cancel_requested - keep polling.
                        continue;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Best-effort: tell Kao to stop working on the job before propagating.
            await TryCancelKaoJobAsync(baseUrl, token, jobId);
            throw;
        }
    }

    private async Task<JsonNode?> FetchKaoJobResultAsync(string baseUrl, string? token, string jobId, CancellationToken ct)
    {
        using var resultReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/jobs/{jobId}/result");
        ApplyKaoAuth(resultReq, token);
        using var resultResp = await _http.SendAsync(resultReq, ct);
        var resultText = await resultResp.Content.ReadAsStringAsync(ct);
        if (!resultResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kao job result error: {resultResp.StatusCode} - {resultText}");
        }
        return JsonNode.Parse(resultText);
    }

    private async Task TryCancelKaoJobAsync(string baseUrl, string? token, string jobId)
    {
        try
        {
            using var cancelReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/jobs/{jobId}/cancel");
            ApplyKaoAuth(cancelReq, token);
            using var cancelResp = await _http.SendAsync(cancelReq, CancellationToken.None);
            _logger.LogDebug("Requested cancellation of Kao job {JobId}: {Status}", jobId, cancelResp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cancel Kao job {JobId}", jobId);
        }
    }

    public async Task DownloadResultAsync(GeneratorResult result, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var response = await _http.GetAsync(result.SourceUrl, ct);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(outputPath);
        await response.Content.CopyToAsync(fileStream, ct);

        _logger.LogDebug("Downloaded mesh result to {Path}", outputPath);
    }

    public IReadOnlyList<GeneratorCapabilities> GetCapabilities(string? providerId)
    {
        // Return provider-specific capabilities
        if (providerId is "hunyuan3d" or "hunyuan")
        {
            return new[]
            {
                new GeneratorCapabilities(
                    GeneratorModality.Mesh,
                    new[]
                    {
                        new GeneratorModeSpec("generate", "Generate", "Generate 3D mesh from text with Hunyuan3D-2", new[] { "prompt" }, new[] { "style", "steps", "seed" }),
                        new GeneratorModeSpec("from_image", "From Image", "Generate 3D mesh from image", new[] { "image" }, new[] { "seed", "steps" })
                    },
                    new[] { "glb", "obj" },
                    null,
                    null,
                    true,  // supportsTextPrompts
                    true,  // supportsImageInputs
                    false) // Hunyuan3D-2 returns one mesh at a time
            };
        }

        if (providerId == "kao")
        {
            return new[]
            {
                new GeneratorCapabilities(
                    GeneratorModality.Mesh,
                    new[]
                    {
                        new GeneratorModeSpec("generate", "Generate", "Generate 3D mesh from text with Kao (local)", new[] { "prompt" }, new[] { "steps", "seed", "guidance_scale", "octree_resolution", "generate_texture" }),
                        new GeneratorModeSpec("from_image", "From Image", "Generate 3D mesh from single image", new[] { "image" }, new[] { "seed", "steps", "octree_resolution" }),
                        new GeneratorModeSpec("from_multiview", "From Multi-View", "Generate 3D mesh from multi-view images", new[] { "images" }, new[] { "seed", "steps" })
                    },
                    new[] { "glb", "obj", "ply" },
                    null,
                    null,
                    true,  // supportsTextPrompts
                    true,  // supportsImageInputs
                    false) // One mesh per request
            };
        }

        return new[]
        {
            new GeneratorCapabilities(
                GeneratorModality.Mesh,
                new[]
                {
                    new GeneratorModeSpec("generate", "Generate", "Generate 3D mesh from text", new[] { "prompt" }, new[] { "style", "polyCount", "negativePrompt" }),
                    new GeneratorModeSpec("from_image", "From Image", "Generate 3D mesh from image", new[] { "image" }, new[] { "angles" }),
                    new GeneratorModeSpec("retopology", "Retopology", "Retopologize existing mesh", new[] { "mesh" }, new[] { "targetPolyCount" }),
                    new GeneratorModeSpec("texture", "Texture", "Generate textures for mesh", new[] { "mesh", "prompt" }, null)
                },
                new[] { "glb", "fbx", "obj" },
                null,
                null,
                true,
                true,
                true)
        };
    }
}

#endregion

#region Sound Modality Adapter - ElevenLabs

public class SoundModalityAdapter : IModalityAdapter
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;

    private const string ElevenLabsBaseUrl = "https://api.elevenlabs.io/v1";

    public SoundModalityAdapter(ILogger logger, HttpClient http, ICredentialStore credentials)
    {
        _logger = logger;
        _http = http;
        _credentials = credentials;
    }

    public Task<QuoteResult> GetQuoteAsync(string providerId, string modelId, string mode, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var durationSeconds = 10;
        if (parameters?.TryGetValue("duration", out var durObj) == true)
        {
            if (durObj is int d) durationSeconds = d;
            else if (int.TryParse(durObj?.ToString(), out var parsed)) durationSeconds = parsed;
        }
        return Task.FromResult(new QuoteResult(true, 0.01m * durationSeconds, "USD", null));
    }

    public Task<string?> ValidateInputsAsync(IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<ArtifactReference>? references, CancellationToken ct)
    {
        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult<string?>("Sound description prompt is required");
        }
        return Task.FromResult<string?>(null);
    }

    public async Task<IReadOnlyList<GeneratorResult>?> GenerateAsync(
        string providerId, string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        var apiKey = await _credentials.GetAsync("elevenlabs", ct);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("ElevenLabs API key not configured");
        }

        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString() ?? "";
        var durationSeconds = 10.0;
        if (parameters?.TryGetValue("duration", out var durObj) == true)
        {
            if (durObj is int d) durationSeconds = d;
            else if (durObj is double dd) durationSeconds = dd;
            else if (double.TryParse(durObj?.ToString(), out var parsed)) durationSeconds = parsed;
        }

        // Use ElevenLabs Sound Effects API
        var body = new JsonObject
        {
            ["text"] = prompt,
            ["duration_seconds"] = Math.Min(durationSeconds, 22.0), // Max 22 seconds
            ["prompt_influence"] = 0.3
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ElevenLabsBaseUrl}/sound-generation");
        request.Headers.Add("xi-api-key", apiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("ElevenLabs sound generation failed: {Status} - {Response}", response.StatusCode, errorText);
            throw new InvalidOperationException($"ElevenLabs API error: {response.StatusCode}");
        }

        // Response is the audio file directly
        var audioBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var base64Audio = Convert.ToBase64String(audioBytes);

        var resultId = Guid.NewGuid().ToString("N")[..8];
        return new[]
        {
            new GeneratorResult(
                resultId,
                $"data:audio/mpeg;base64,{base64Audio}",
                "audio/mpeg",
                audioBytes.Length,
                Random.Shared.Next(),
                0,
                new Dictionary<string, object?> { ["prompt"] = prompt, ["duration"] = durationSeconds })
        };
    }

    public async Task DownloadResultAsync(GeneratorResult result, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (result.SourceUrl.StartsWith("data:"))
        {
            var commaIndex = result.SourceUrl.IndexOf(',');
            if (commaIndex > 0)
            {
                var base64Data = result.SourceUrl[(commaIndex + 1)..];
                var bytes = Convert.FromBase64String(base64Data);
                await File.WriteAllBytesAsync(outputPath, bytes, ct);
            }
        }
        else
        {
            using var response = await _http.GetAsync(result.SourceUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var fileStream = File.Create(outputPath);
            await response.Content.CopyToAsync(fileStream, ct);
        }

        _logger.LogDebug("Downloaded sound result to {Path}", outputPath);
    }

    public IReadOnlyList<GeneratorCapabilities> GetCapabilities(string? providerId)
    {
        return new[]
        {
            new GeneratorCapabilities(
                GeneratorModality.Sound,
                new[]
                {
                    new GeneratorModeSpec("generate", "Generate", "Generate sound effect from text", new[] { "prompt" }, new[] { "duration", "loop" }),
                    new GeneratorModeSpec("from_reference", "From Reference", "Generate similar sound", new[] { "reference" }, new[] { "duration" })
                },
                new[] { "mp3", "wav" },
                null,
                300,
                true,
                true,
                true)
        };
    }
}

#endregion

#region Animation Modality Adapter

public class AnimationModalityAdapter : IModalityAdapter
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;

    public AnimationModalityAdapter(ILogger logger, HttpClient http, ICredentialStore credentials)
    {
        _logger = logger;
        _http = http;
        _credentials = credentials;
    }

    public Task<QuoteResult> GetQuoteAsync(string providerId, string modelId, string mode, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var durationSeconds = 5;
        if (parameters?.TryGetValue("duration", out var durObj) == true)
        {
            if (durObj is int d) durationSeconds = d;
            else if (int.TryParse(durObj?.ToString(), out var parsed)) durationSeconds = parsed;
        }
        return Task.FromResult(new QuoteResult(true, 0.05m * durationSeconds, "USD", null));
    }

    // Unity VideoClip-importable container formats accepted as motion references.
    private static readonly string[] SupportedVideoExtensions =
        { ".mp4", ".mov", ".webm", ".avi", ".m4v", ".ogv", ".mpg", ".mpeg" };

    public Task<string?> ValidateInputsAsync(IReadOnlyDictionary<string, object?>? parameters, IReadOnlyList<ArtifactReference>? references, CancellationToken ct)
    {
        var prompt = parameters?.GetValueOrDefault("prompt")?.ToString();

        // A video reference may arrive inline (base64) or as a project VideoClip path.
        // 2.9 added the project-path form for GenerateHumanoidAnimation in agent/tool flows.
        var videoRef = references?.FirstOrDefault(r => string.Equals(r.Type, "video", StringComparison.OrdinalIgnoreCase));
        var hasInlineVideoData = !string.IsNullOrEmpty(videoRef?.Base64Data);
        var videoPath = videoRef?.Path ?? GetVideoPathParameter(parameters);
        var hasVideo = hasInlineVideoData || !string.IsNullOrWhiteSpace(videoPath);

        if (string.IsNullOrWhiteSpace(prompt) && !hasVideo)
        {
            return Task.FromResult<string?>("Motion description prompt or video reference is required");
        }

        // Validate the video reference up front so we don't submit a paid/long job
        // with an unusable reference.
        if (!string.IsNullOrWhiteSpace(videoPath) && !IsSupportedVideoPath(videoPath))
        {
            return Task.FromResult<string?>(
                $"Unsupported video reference '{videoPath}'. Provide a project VideoClip path " +
                "(.mp4, .mov, .webm, .avi, .m4v, .ogv, .mpg, .mpeg).");
        }

        return Task.FromResult<string?>(null);
    }

    private static string? GetVideoPathParameter(IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters == null) return null;
        foreach (var key in new[] { "videoPath", "videoClipPath", "video", "targetAssetPath" })
        {
            if (parameters.TryGetValue(key, out var value) && value is not null)
            {
                var s = value.ToString();
                // Only treat values that look like a file path as a path reference;
                // inline base64 video is handled separately.
                if (!string.IsNullOrWhiteSpace(s) && (s.Contains('/') || s.Contains('\\') || s.Contains('.')))
                {
                    return s;
                }
            }
        }
        return null;
    }

    private static bool IsSupportedVideoPath(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) &&
            SupportedVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<GeneratorResult>?> GenerateAsync(
        string providerId, string modelId, string mode,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<ArtifactReference>? references,
        CancellationToken ct)
    {
        // Animation generation is complex - currently uses placeholder
        // Real implementation would integrate with motion generation APIs like:
        // - Rokoko (motion capture)
        // - Move.ai
        // - DeepMotion
        // - Plask

        _logger.LogWarning("Animation generation not fully implemented - API integration pending");

        // Animation generation requires external motion capture APIs
        // Return an error result indicating the provider needs configuration
        return null; // Will trigger graceful error handling in caller
    }

    public async Task DownloadResultAsync(GeneratorResult result, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (result.SourceUrl.StartsWith("data:"))
        {
            var commaIndex = result.SourceUrl.IndexOf(',');
            if (commaIndex > 0)
            {
                var base64Data = result.SourceUrl[(commaIndex + 1)..];
                var bytes = Convert.FromBase64String(base64Data);
                await File.WriteAllBytesAsync(outputPath, bytes, ct);
            }
        }
        else
        {
            using var response = await _http.GetAsync(result.SourceUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var fileStream = File.Create(outputPath);
            await response.Content.CopyToAsync(fileStream, ct);
        }
    }

    public IReadOnlyList<GeneratorCapabilities> GetCapabilities(string? providerId)
    {
        return new[]
        {
            new GeneratorCapabilities(
                GeneratorModality.Animation,
                new[]
                {
                    new GeneratorModeSpec("generate", "Generate", "Generate animation from text", new[] { "prompt" }, new[] { "duration", "loop" }),
                    new GeneratorModeSpec("from_video", "From Video", "Extract motion from a video reference or project VideoClip path", new[] { "video" }, new[] { "skeleton", "videoPath" }),
                    new GeneratorModeSpec("retarget", "Retarget", "Retarget animation to skeleton", new[] { "animation", "skeleton" }, null)
                },
                new[] { "fbx", "anim" },
                null,
                60,
                true,
                true,
                true)
        };
    }
}

#endregion
