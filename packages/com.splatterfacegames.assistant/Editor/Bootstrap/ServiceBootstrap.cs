// Service Bootstrap - Manages local service lifecycle

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Splatter.Editor.Settings;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Splatter.Editor.Bootstrap
{
    /// <summary>
    /// Manages the lifecycle of the Splatter local service process.
    /// Handles starting, stopping, and monitoring the service.
    /// </summary>
    [InitializeOnLoad]
    public static class ServiceBootstrap
    {
        private static Process _serviceProcess;
        private static string _serviceUrl;
        private static string _authToken;
        private static bool _isStarting;
        private static readonly object _lock = new object();

        public static event Action<string> OnServiceStarted;
        public static event Action OnServiceStopped;
        public static event Action<string> OnServiceError;

        public static bool IsRunning => _serviceProcess != null && !_serviceProcess.HasExited;
        public static string ServiceUrl => _serviceUrl;
        public static string AuthToken => _authToken;

        static ServiceBootstrap()
        {
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // Restore connection if service was running before domain reload
            RestoreServiceConnection();
        }

        /// <summary>
        /// Starts the Splatter local service if not already running.
        /// </summary>
        public static async Task<bool> StartServiceAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (IsRunning || _isStarting)
                {
                    return IsRunning;
                }
                _isStarting = true;
            }

            try
            {
                var servicePath = FindServiceExecutable();
                if (string.IsNullOrEmpty(servicePath))
                {
                    Debug.LogError("[Splatter] Service executable not found. Please ensure the service is installed.");
                    OnServiceError?.Invoke("Service executable not found");
                    return false;
                }

                // Generate auth token for this session
                _authToken = GenerateAuthToken();

                // Determine data directory
                var dataDir = GetDataDirectory();
                Directory.CreateDirectory(dataDir);

                // The Unity project root (parent of Assets/) so path-scoped tools resolve.
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

                // Start the service process
                var startInfo = new ProcessStartInfo
                {
                    FileName = servicePath,
                    Arguments = $"--port 0 --auth-token \"{_authToken}\" --data-dir \"{dataDir}\" --project-root \"{projectRoot}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(servicePath)
                };

                _serviceProcess = new Process { StartInfo = startInfo };
                _serviceProcess.EnableRaisingEvents = true;
                _serviceProcess.Exited += OnProcessExited;
                _serviceProcess.OutputDataReceived += OnOutputDataReceived;
                _serviceProcess.ErrorDataReceived += OnErrorDataReceived;

                if (!_serviceProcess.Start())
                {
                    Debug.LogError("[Splatter] Failed to start service process");
                    OnServiceError?.Invoke("Failed to start process");
                    return false;
                }

                _serviceProcess.BeginOutputReadLine();
                _serviceProcess.BeginErrorReadLine();

                // Wait for service to report its URL
                var urlReceived = await WaitForServiceUrlAsync(ct);
                if (!urlReceived)
                {
                    Debug.LogError("[Splatter] Service failed to start within timeout");
                    StopService();
                    return false;
                }

                // Store state for domain reload recovery
                SaveServiceState();

                Debug.Log($"[Splatter] Service started at {_serviceUrl}");
                OnServiceStarted?.Invoke(_serviceUrl);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Failed to start service: {ex.Message}");
                OnServiceError?.Invoke(ex.Message);
                return false;
            }
            finally
            {
                _isStarting = false;
            }
        }

        /// <summary>
        /// Stops the Splatter local service.
        /// </summary>
        public static void StopService()
        {
            lock (_lock)
            {
                if (_serviceProcess == null)
                    return;

                try
                {
                    if (!_serviceProcess.HasExited)
                    {
                        // Try graceful shutdown first
                        _serviceProcess.CloseMainWindow();
                        if (!_serviceProcess.WaitForExit(3000))
                        {
                            _serviceProcess.Kill();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Splatter] Error stopping service: {ex.Message}");
                }
                finally
                {
                    _serviceProcess.Dispose();
                    _serviceProcess = null;
                    _serviceUrl = null;
                    ClearServiceState();
                    OnServiceStopped?.Invoke();
                }
            }
        }

        /// <summary>
        /// Restarts the service.
        /// </summary>
        public static async Task<bool> RestartServiceAsync(CancellationToken ct = default)
        {
            StopService();
            await Task.Delay(500, ct);
            return await StartServiceAsync(ct);
        }

        private static string FindServiceExecutable()
        {
            // Look in standard locations
            var possiblePaths = new[]
            {
                // Relative to Unity project
                Path.Combine(Application.dataPath, "..", "LocalPackages", "com.splatterfacegames.assistant-service", "Splatter.Service.exe"),
                Path.Combine(Application.dataPath, "..", "Packages", "com.splatterfacegames.assistant", "Service~", "Splatter.Service.exe"),

                // User app data
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Splatterface Games", "Assistant", "Service", "Splatter.Service.exe"),

                // Development paths
                Path.Combine(Application.dataPath, "..", "..", "service", "Splatter.Service", "bin", "Release", "net8.0", "Splatter.Service.exe"),
                Path.Combine(Application.dataPath, "..", "..", "service", "Splatter.Service", "bin", "Debug", "net8.0", "Splatter.Service.exe"),
            };

            // Also check for dotnet run scenario
            var csprojPath = FindServiceCsproj();

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            // If we found the csproj, we can use dotnet run
            if (!string.IsNullOrEmpty(csprojPath))
            {
                return "dotnet";
            }

            return null;
        }

        private static string FindServiceCsproj()
        {
            var possiblePaths = new[]
            {
                Path.Combine(Application.dataPath, "..", "..", "service", "Splatter.Service", "Splatter.Service.csproj"),
                Path.Combine(Application.dataPath, "..", "Packages", "com.splatterfacegames.assistant", "Service~", "Splatter.Service.csproj"),
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private static string GetDataDirectory()
        {
            // Use project-specific data directory
            var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var projectName = Path.GetFileName(projectPath);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Splatterface Games", "Assistant",
                "Projects",
                projectName);
        }

        private static string GenerateAuthToken()
        {
            var bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes);
        }

        private static async Task<bool> WaitForServiceUrlAsync(CancellationToken ct)
        {
            var timeout = TimeSpan.FromSeconds(30);
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
            {
                if (!string.IsNullOrEmpty(_serviceUrl))
                    return true;

                if (_serviceProcess == null || _serviceProcess.HasExited)
                    return false;

                await Task.Delay(100, ct);
            }

            return !string.IsNullOrEmpty(_serviceUrl);
        }

        private static void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data))
                return;

            // Parse service URL from output
            if (e.Data.StartsWith("SPLATTER_URL="))
            {
                _serviceUrl = e.Data.Substring("SPLATTER_URL=".Length).Trim();
            }
            else if (SplatterSettings.instance.VerboseLogging)
            {
                Debug.Log($"[Splatter Service] {e.Data}");
            }
        }

        private static void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Debug.LogWarning($"[Splatter Service] {e.Data}");
            }
        }

        private static void OnProcessExited(object sender, EventArgs e)
        {
            lock (_lock)
            {
                var exitCode = _serviceProcess?.ExitCode ?? -1;
                _serviceProcess?.Dispose();
                _serviceProcess = null;
                _serviceUrl = null;
                ClearServiceState();

                if (exitCode != 0)
                {
                    Debug.LogWarning($"[Splatter] Service exited with code {exitCode}");
                }

                OnServiceStopped?.Invoke();
            }
        }

        private static void OnEditorQuitting()
        {
            StopService();
        }

        private static void OnBeforeAssemblyReload()
        {
            // Save state before domain reload
            if (IsRunning)
            {
                SaveServiceState();
            }
        }

        private static void SaveServiceState()
        {
            if (_serviceProcess != null && !_serviceProcess.HasExited)
            {
                SessionState.SetInt("Splatter_ServicePID", _serviceProcess.Id);
                SessionState.SetString("Splatter_ServiceUrl", _serviceUrl ?? "");
                SessionState.SetString("Splatter_AuthToken", _authToken ?? "");
            }
        }

        private static void ClearServiceState()
        {
            SessionState.EraseInt("Splatter_ServicePID");
            SessionState.EraseString("Splatter_ServiceUrl");
            SessionState.EraseString("Splatter_AuthToken");
        }

        private static void RestoreServiceConnection()
        {
            var pid = SessionState.GetInt("Splatter_ServicePID", 0);
            if (pid == 0)
                return;

            try
            {
                var process = Process.GetProcessById(pid);
                if (process != null && !process.HasExited && process.ProcessName.Contains("Splatter"))
                {
                    _serviceProcess = process;
                    _serviceUrl = SessionState.GetString("Splatter_ServiceUrl", "");
                    _authToken = SessionState.GetString("Splatter_AuthToken", "");

                    _serviceProcess.EnableRaisingEvents = true;
                    _serviceProcess.Exited += OnProcessExited;

                    Debug.Log($"[Splatter] Restored connection to service at {_serviceUrl}");
                    OnServiceStarted?.Invoke(_serviceUrl);
                }
            }
            catch
            {
                // Process no longer exists
                ClearServiceState();
            }
        }
    }
}
