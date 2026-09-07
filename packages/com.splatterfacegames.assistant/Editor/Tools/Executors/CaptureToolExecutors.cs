// Capture Tool Executors - Unity view capture operations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.Tools
{
    /// <summary>
    /// Captures the Scene View as an image.
    /// </summary>
    public class CaptureSceneViewExecutor : IToolExecutor
    {
        public string ToolId => "capture.scene";

        private const int MinDimension = 64;
        private const int MaxDimension = 4096;

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Parse and validate dimensions
                var width = context.Arguments.TryGetValue("width", out var w) ? Convert.ToInt32(w) : 1280;
                var height = context.Arguments.TryGetValue("height", out var h) ? Convert.ToInt32(h) : 720;
                var outputPath = context.Arguments.TryGetValue("outputPath", out var op) ? op?.ToString() : null;

                if (width < MinDimension || width > MaxDimension)
                {
                    return Task.FromResult(ToolExecutionResult.Failed(
                        $"Width must be between {MinDimension} and {MaxDimension}. Got: {width}"));
                }

                if (height < MinDimension || height > MaxDimension)
                {
                    return Task.FromResult(ToolExecutionResult.Failed(
                        $"Height must be between {MinDimension} and {MaxDimension}. Got: {height}"));
                }

                // Cap the long edge to keep image tokens reasonable for the model (~1k/img).
                var maxDim = context.Arguments.TryGetValue("max_dimension", out var mdArg) ? Convert.ToInt32(mdArg) : 1024;
                var fit = Mathf.Min(1f, (float)Mathf.Max(MinDimension, maxDim) / Mathf.Max(width, height));
                width = Mathf.Max(MinDimension, Mathf.RoundToInt(width * fit));
                height = Mathf.Max(MinDimension, Mathf.RoundToInt(height * fit));

                // Get the Scene View
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed(
                        "No active Scene View found. Please ensure a Scene View window is open."));
                }

                var camera = sceneView.camera;
                if (camera == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed(
                        "Scene View camera is not available."));
                }

                // Generate output path if not specified
                if (string.IsNullOrEmpty(outputPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    outputPath = Path.Combine(Application.temporaryCachePath, $"SceneCapture_{timestamp}.png");
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Create RenderTexture
                var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.antiAliasing = 1;
                renderTexture.Create();

                // Store original render texture
                var originalTargetTexture = camera.targetTexture;
                var originalActiveRenderTexture = RenderTexture.active;

                try
                {
                    // Render scene to our texture
                    camera.targetTexture = renderTexture;
                    camera.Render();

                    // Read pixels from RenderTexture
                    RenderTexture.active = renderTexture;
                    var texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
                    texture2D.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    texture2D.Apply();

                    // Encode to PNG and save
                    var pngData = texture2D.EncodeToPNG();
                    File.WriteAllBytes(outputPath, pngData);

                    // Cleanup texture2D
                    UnityEngine.Object.DestroyImmediate(texture2D);

                    return Task.FromResult(ToolExecutionResult.Succeeded(new
                    {
                        // Inline base64 so the MCP layer can hand it to the model as an image.
                        image = Convert.ToBase64String(pngData),
                        mimeType = "image/png",
                        path = outputPath,
                        width,
                        height
                    }, new List<string> { outputPath }));
                }
                finally
                {
                    // Restore original state
                    camera.targetTexture = originalTargetTexture;
                    RenderTexture.active = originalActiveRenderTexture;

                    // Cleanup RenderTexture
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }
    }

    /// <summary>
    /// Captures the Game View as an image.
    /// </summary>
    public class CaptureGameViewExecutor : IToolExecutor
    {
        public string ToolId => "capture.game";

        private const int MinDimension = 64;
        private const int MaxDimension = 4096;

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                // Parse and validate dimensions
                var width = context.Arguments.TryGetValue("width", out var w) ? Convert.ToInt32(w) : 1280;
                var height = context.Arguments.TryGetValue("height", out var h) ? Convert.ToInt32(h) : 720;
                var outputPath = context.Arguments.TryGetValue("outputPath", out var op) ? op?.ToString() : null;

                if (width < MinDimension || width > MaxDimension)
                {
                    return Task.FromResult(ToolExecutionResult.Failed(
                        $"Width must be between {MinDimension} and {MaxDimension}. Got: {width}"));
                }

                if (height < MinDimension || height > MaxDimension)
                {
                    return Task.FromResult(ToolExecutionResult.Failed(
                        $"Height must be between {MinDimension} and {MaxDimension}. Got: {height}"));
                }

                // Cap the long edge to keep image tokens reasonable for the model (~1k/img).
                var maxDim = context.Arguments.TryGetValue("max_dimension", out var mdArg) ? Convert.ToInt32(mdArg) : 1024;
                var fit = Mathf.Min(1f, (float)Mathf.Max(MinDimension, maxDim) / Mathf.Max(width, height));
                width = Mathf.Max(MinDimension, Mathf.RoundToInt(width * fit));
                height = Mathf.Max(MinDimension, Mathf.RoundToInt(height * fit));

                // Generate output path if not specified
                if (string.IsNullOrEmpty(outputPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    outputPath = Path.Combine(Application.temporaryCachePath, $"GameCapture_{timestamp}.png");
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Try to get a camera to render from
                Camera camera = null;

                // First try Camera.main
                camera = Camera.main;

                // If no main camera, try to find any enabled camera
                if (camera == null)
                {
                    var cameras = Camera.allCameras;
                    foreach (var cam in cameras)
                    {
                        if (cam.enabled && cam.gameObject.activeInHierarchy)
                        {
                            camera = cam;
                            break;
                        }
                    }
                }

                // If still no camera, try to get from Game View window using reflection
                if (camera == null)
                {
                    camera = TryGetGameViewCamera();
                }

                if (camera == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed(
                        "No camera available for Game View capture. Ensure there is a Main Camera or an enabled camera in the scene."));
                }

                // Create RenderTexture
                var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.antiAliasing = 1;
                renderTexture.Create();

                // Store original render texture
                var originalTargetTexture = camera.targetTexture;
                var originalActiveRenderTexture = RenderTexture.active;

                try
                {
                    // Render to our texture
                    camera.targetTexture = renderTexture;
                    camera.Render();

                    // Read pixels from RenderTexture
                    RenderTexture.active = renderTexture;
                    var texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
                    texture2D.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    texture2D.Apply();

                    // Encode to PNG and save
                    var pngData = texture2D.EncodeToPNG();
                    File.WriteAllBytes(outputPath, pngData);

                    // Cleanup texture2D
                    UnityEngine.Object.DestroyImmediate(texture2D);

                    return Task.FromResult(ToolExecutionResult.Succeeded(new
                    {
                        // Inline base64 so the MCP layer can hand it to the model as an image.
                        image = Convert.ToBase64String(pngData),
                        mimeType = "image/png",
                        path = outputPath,
                        width,
                        height
                    }, new List<string> { outputPath }));
                }
                finally
                {
                    // Restore original state
                    camera.targetTexture = originalTargetTexture;
                    RenderTexture.active = originalActiveRenderTexture;

                    // Cleanup RenderTexture
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        /// <summary>
        /// Attempts to get the camera rendering to the Game View using reflection.
        /// This is a fallback when Camera.main is not available.
        /// </summary>
        private Camera TryGetGameViewCamera()
        {
            try
            {
                // Try to access GameView through reflection
                var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
                if (gameViewType == null)
                {
                    return null;
                }

                var gameViewWindow = EditorWindow.GetWindow(gameViewType, false, null, false);
                if (gameViewWindow == null)
                {
                    return null;
                }

                // The GameView doesn't directly expose its camera,
                // so we fall back to finding scene cameras
                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Captures an object from multiple angles for AI analysis or documentation.
    /// </summary>
    public class CaptureMultiAngleExecutor : IToolExecutor
    {
        public string ToolId => "capture.multi_angle";

        private const int MinDimension = 64;
        private const int MaxDimension = 2048; // Lower max for multi-angle to manage memory

        // Preset angles for common capture scenarios
        private static readonly Dictionary<string, Vector3[]> AnglePresets = new()
        {
            ["front_back"] = new[] { new Vector3(0, 0, 0), new Vector3(0, 180, 0) },
            ["cardinal"] = new[] { new Vector3(0, 0, 0), new Vector3(0, 90, 0), new Vector3(0, 180, 0), new Vector3(0, 270, 0) },
            ["isometric"] = new[] { new Vector3(30, 45, 0), new Vector3(30, 135, 0), new Vector3(30, 225, 0), new Vector3(30, 315, 0) },
            ["top_down"] = new[] { new Vector3(90, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 90, 0), new Vector3(0, 180, 0), new Vector3(0, 270, 0) },
            ["full"] = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 45, 0), new Vector3(0, 90, 0), new Vector3(0, 135, 0),
                new Vector3(0, 180, 0), new Vector3(0, 225, 0), new Vector3(0, 270, 0), new Vector3(0, 315, 0),
                new Vector3(30, 45, 0), new Vector3(30, 135, 0), new Vector3(30, 225, 0), new Vector3(30, 315, 0),
                new Vector3(90, 0, 0), new Vector3(-30, 0, 0)
            }
        };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            var createdFiles = new List<string>();
            RenderTexture renderTexture = null;

            try
            {
                // Parse arguments
                var instanceId = context.Arguments.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0;
                var objectPath = context.Arguments.TryGetValue("objectPath", out var op) ? op?.ToString() : null;
                var width = context.Arguments.TryGetValue("width", out var w) ? Convert.ToInt32(w) : 512;
                var height = context.Arguments.TryGetValue("height", out var h) ? Convert.ToInt32(h) : 512;
                var outputDir = context.Arguments.TryGetValue("outputDir", out var od) ? od?.ToString() : null;
                var preset = context.Arguments.TryGetValue("preset", out var p) ? p?.ToString()?.ToLowerInvariant() : "cardinal";
                var distance = context.Arguments.TryGetValue("distance", out var d) ? Convert.ToSingle(d) : 0f;
                var backgroundColor = context.Arguments.TryGetValue("backgroundColor", out var bg) ? bg as List<object> : null;

                // Validate dimensions
                width = Math.Clamp(width, MinDimension, MaxDimension);
                height = Math.Clamp(height, MinDimension, MaxDimension);

                // Find the target object
                GameObject target = null;

                if (instanceId != 0)
                {
                    target = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                }

                if (target == null && !string.IsNullOrEmpty(objectPath))
                {
                    target = GameObject.Find(objectPath);
                }

                if (target == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Target GameObject not found"));
                }

                // Get angles from preset or custom angles
                Vector3[] angles;
                if (context.Arguments.TryGetValue("customAngles", out var customAngles) && customAngles is List<object> anglesList)
                {
                    angles = new Vector3[anglesList.Count];
                    for (int i = 0; i < anglesList.Count; i++)
                    {
                        if (anglesList[i] is List<object> angleVec && angleVec.Count >= 3)
                        {
                            angles[i] = new Vector3(
                                Convert.ToSingle(angleVec[0]),
                                Convert.ToSingle(angleVec[1]),
                                Convert.ToSingle(angleVec[2]));
                        }
                    }
                }
                else if (!AnglePresets.TryGetValue(preset, out angles))
                {
                    angles = AnglePresets["cardinal"];
                }

                // Limit angles to prevent excessive captures
                if (angles.Length > 16)
                {
                    angles = angles.Take(16).ToArray();
                }

                // Set up output directory
                if (string.IsNullOrEmpty(outputDir))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    outputDir = Path.Combine(Application.temporaryCachePath, $"MultiAngle_{target.name}_{timestamp}");
                }

                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // Calculate bounding sphere for camera positioning
                var bounds = CalculateBounds(target);
                var center = bounds.center;
                var radius = bounds.extents.magnitude;

                // Use provided distance or calculate from bounds
                if (distance <= 0f)
                {
                    distance = radius * 2.5f; // Default to 2.5x the radius
                }

                // Create temporary camera for rendering
                var cameraGo = new GameObject("_MultiAngleCaptureCamera");
                var camera = cameraGo.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;

                // Set background color
                if (backgroundColor != null && backgroundColor.Count >= 3)
                {
                    camera.backgroundColor = new Color(
                        Convert.ToSingle(backgroundColor[0]),
                        Convert.ToSingle(backgroundColor[1]),
                        Convert.ToSingle(backgroundColor[2]),
                        backgroundColor.Count >= 4 ? Convert.ToSingle(backgroundColor[3]) : 1f);
                }
                else
                {
                    camera.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
                }

                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = radius * 10f;

                // Create render texture
                renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                renderTexture.antiAliasing = 2;
                renderTexture.Create();

                var captures = new List<object>();

                try
                {
                    for (int i = 0; i < angles.Length; i++)
                    {
                        if (ct.IsCancellationRequested) break;

                        var angle = angles[i];

                        // Position camera around the target
                        var rotation = Quaternion.Euler(angle.x, angle.y, angle.z);
                        var cameraPosition = center - (rotation * Vector3.forward) * distance;
                        camera.transform.position = cameraPosition;
                        camera.transform.LookAt(center);

                        // Render
                        camera.targetTexture = renderTexture;
                        camera.Render();

                        // Read pixels
                        RenderTexture.active = renderTexture;
                        var texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
                        texture2D.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        texture2D.Apply();

                        // Save to file
                        var fileName = $"angle_{i:D2}_x{angle.x:F0}_y{angle.y:F0}_z{angle.z:F0}.png";
                        var filePath = Path.Combine(outputDir, fileName);
                        var pngData = texture2D.EncodeToPNG();
                        File.WriteAllBytes(filePath, pngData);

                        createdFiles.Add(filePath);

                        captures.Add(new
                        {
                            index = i,
                            angle = new { x = angle.x, y = angle.y, z = angle.z },
                            // Inline base64 so the MCP layer hands each angle to the model as an image.
                            image = Convert.ToBase64String(pngData),
                            mimeType = "image/png",
                            label = $"{angle.x:F0}/{angle.y:F0}/{angle.z:F0}",
                            path = filePath,
                            fileName
                        });

                        // Cleanup texture
                        UnityEngine.Object.DestroyImmediate(texture2D);

                        RenderTexture.active = null;
                    }

                    return Task.FromResult(ToolExecutionResult.Succeeded(new
                    {
                        targetName = target.name,
                        preset,
                        captureCount = captures.Count,
                        outputDir,
                        width,
                        height,
                        captures,
                        bounds = new
                        {
                            center = new { x = center.x, y = center.y, z = center.z },
                            size = new { x = bounds.size.x, y = bounds.size.y, z = bounds.size.z }
                        }
                    }, createdFiles));
                }
                finally
                {
                    // Cleanup camera and render texture
                    UnityEngine.Object.DestroyImmediate(cameraGo);

                    if (renderTexture != null)
                    {
                        RenderTexture.active = null;
                        renderTexture.Release();
                        UnityEngine.Object.DestroyImmediate(renderTexture);
                    }
                }
            }
            catch (Exception ex)
            {
                // Cleanup on failure
                foreach (var file in createdFiles)
                {
                    try { File.Delete(file); } catch { }
                }

                if (renderTexture != null)
                {
                    RenderTexture.active = null;
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }

                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Bounds CalculateBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(go.transform.position, Vector3.one);
            }

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }
    }

    /// <summary>
    /// Captures a specific region of the Scene View at higher resolution.
    /// </summary>
    public class CaptureRegionExecutor : IToolExecutor
    {
        public string ToolId => "capture.region";

        private const int MinDimension = 64;
        private const int MaxDimension = 4096;

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("No active Scene View found"));
                }

                var camera = sceneView.camera;
                if (camera == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("Scene View camera not available"));
                }

                // Parse region arguments (normalized 0-1 coordinates)
                var regionX = context.Arguments.TryGetValue("regionX", out var rx) ? Convert.ToSingle(rx) : 0f;
                var regionY = context.Arguments.TryGetValue("regionY", out var ry) ? Convert.ToSingle(ry) : 0f;
                var regionWidth = context.Arguments.TryGetValue("regionWidth", out var rw) ? Convert.ToSingle(rw) : 1f;
                var regionHeight = context.Arguments.TryGetValue("regionHeight", out var rh) ? Convert.ToSingle(rh) : 1f;

                var outputWidth = context.Arguments.TryGetValue("width", out var w) ? Convert.ToInt32(w) : 1920;
                var outputHeight = context.Arguments.TryGetValue("height", out var h) ? Convert.ToInt32(h) : 1080;
                var outputPath = context.Arguments.TryGetValue("outputPath", out var op) ? op?.ToString() : null;
                var superSample = context.Arguments.TryGetValue("superSample", out var ss) ? Convert.ToInt32(ss) : 1;

                // Validate
                outputWidth = Math.Clamp(outputWidth, MinDimension, MaxDimension);
                outputHeight = Math.Clamp(outputHeight, MinDimension, MaxDimension);
                superSample = Math.Clamp(superSample, 1, 4);

                regionX = Math.Clamp(regionX, 0f, 1f);
                regionY = Math.Clamp(regionY, 0f, 1f);
                regionWidth = Math.Clamp(regionWidth, 0.1f, 1f);
                regionHeight = Math.Clamp(regionHeight, 0.1f, 1f);

                // Generate output path if not specified
                if (string.IsNullOrEmpty(outputPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    outputPath = Path.Combine(Application.temporaryCachePath, $"RegionCapture_{timestamp}.png");
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Calculate render dimensions with supersampling
                var renderWidth = outputWidth * superSample;
                var renderHeight = outputHeight * superSample;

                // Create render texture
                var renderTexture = new RenderTexture(renderWidth, renderHeight, 24, RenderTextureFormat.ARGB32);
                renderTexture.antiAliasing = superSample > 1 ? 1 : 2;
                renderTexture.Create();

                var originalTargetTexture = camera.targetTexture;
                var originalActiveRenderTexture = RenderTexture.active;
                var originalRect = camera.rect;
                var originalAspect = camera.aspect;

                try
                {
                    // Adjust camera to render only the specified region
                    camera.rect = new Rect(regionX, regionY, regionWidth, regionHeight);
                    camera.aspect = (float)outputWidth / outputHeight;
                    camera.targetTexture = renderTexture;
                    camera.Render();

                    // Read pixels
                    RenderTexture.active = renderTexture;
                    var texture2D = new Texture2D(renderWidth, renderHeight, TextureFormat.RGB24, false);
                    texture2D.ReadPixels(new Rect(0, 0, renderWidth, renderHeight), 0, 0);
                    texture2D.Apply();

                    // Downsample if supersampled
                    Texture2D finalTexture;
                    if (superSample > 1)
                    {
                        finalTexture = DownsampleTexture(texture2D, outputWidth, outputHeight);
                        UnityEngine.Object.DestroyImmediate(texture2D);
                    }
                    else
                    {
                        finalTexture = texture2D;
                    }

                    // Save
                    var pngData = finalTexture.EncodeToPNG();
                    File.WriteAllBytes(outputPath, pngData);

                    UnityEngine.Object.DestroyImmediate(finalTexture);

                    return Task.FromResult(ToolExecutionResult.Succeeded(new
                    {
                        image = Convert.ToBase64String(pngData),
                        mimeType = "image/png",
                        path = outputPath,
                        width = outputWidth,
                        height = outputHeight,
                        region = new { x = regionX, y = regionY, width = regionWidth, height = regionHeight },
                        superSampled = superSample > 1
                    }, new List<string> { outputPath }));
                }
                finally
                {
                    camera.rect = originalRect;
                    camera.aspect = originalAspect;
                    camera.targetTexture = originalTargetTexture;
                    RenderTexture.active = originalActiveRenderTexture;

                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Texture2D DownsampleTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
            var scaleFactor = source.width / targetWidth;

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    // Average the pixels in the source region
                    var colorSum = Color.black;
                    for (int sy = 0; sy < scaleFactor; sy++)
                    {
                        for (int sx = 0; sx < scaleFactor; sx++)
                        {
                            colorSum += source.GetPixel(x * scaleFactor + sx, y * scaleFactor + sy);
                        }
                    }
                    result.SetPixel(x, y, colorSum / (scaleFactor * scaleFactor));
                }
            }

            result.Apply();
            return result;
        }
    }

    /// <summary>
    /// Captures the Unity Editor window layout as an image.
    /// </summary>
    public class CaptureEditorLayoutExecutor : IToolExecutor
    {
        public string ToolId => "capture.editor_layout";

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var outputPath = context.Arguments.TryGetValue("outputPath", out var op) ? op?.ToString() : null;
                var windowType = context.Arguments.TryGetValue("windowType", out var wt) ? wt?.ToString() : null;

                // Generate output path if not specified
                if (string.IsNullOrEmpty(outputPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    outputPath = Path.Combine(Application.temporaryCachePath, $"EditorCapture_{timestamp}.png");
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                EditorWindow targetWindow = null;

                // Find the target window
                if (!string.IsNullOrEmpty(windowType))
                {
                    var type = Type.GetType($"UnityEditor.{windowType},UnityEditor");
                    if (type != null)
                    {
                        targetWindow = EditorWindow.GetWindow(type, false, null, false);
                    }
                }

                if (targetWindow == null)
                {
                    // Default to the focused window
                    targetWindow = EditorWindow.focusedWindow;
                }

                if (targetWindow == null)
                {
                    return Task.FromResult(ToolExecutionResult.Failed("No target window found"));
                }

                // Get window position and size
                var position = targetWindow.position;
                var width = (int)position.width;
                var height = (int)position.height;

                // Read the window's pixels (note: this captures what's currently rendered)
                var colors = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(
                    new Vector2(position.x, position.y), width, height);

                // Create texture and apply pixels
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.SetPixels(colors);
                texture.Apply();

                // Flip vertically (screen coordinates are bottom-up)
                var flippedTexture = FlipTextureVertically(texture);
                UnityEngine.Object.DestroyImmediate(texture);

                // Save to file
                var pngData = flippedTexture.EncodeToPNG();
                File.WriteAllBytes(outputPath, pngData);

                UnityEngine.Object.DestroyImmediate(flippedTexture);

                return Task.FromResult(ToolExecutionResult.Succeeded(new
                {
                    path = outputPath,
                    width,
                    height,
                    windowTitle = targetWindow.titleContent.text,
                    windowType = targetWindow.GetType().Name
                }, new List<string> { outputPath }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message));
            }
        }

        private Texture2D FlipTextureVertically(Texture2D original)
        {
            var flipped = new Texture2D(original.width, original.height, original.format, false);
            var width = original.width;
            var height = original.height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    flipped.SetPixel(x, height - 1 - y, original.GetPixel(x, y));
                }
            }

            flipped.Apply();
            return flipped;
        }
    }
}
