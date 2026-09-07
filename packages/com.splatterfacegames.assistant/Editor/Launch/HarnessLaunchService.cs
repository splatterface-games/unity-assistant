// Launches interactive harness terminals (Claude Code, Codex) wired to Splatter.
//
// Productizes the old debug launcher: ensure the bridge is connected, validate the
// CLI (Preferences path override wins), mint a token-scoped session carrying the
// permission mode the user chose, write the context managed-blocks, then open a real
// terminal at the project root with the CLI pointed at our MCP endpoint. The user
// drives the genuine first-party TUI; Splatter serves tools, gates mutations in the
// editor, and renders the activity feed.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Splatter.Editor.Bridge;
using Splatter.Protocol;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Splatter.Editor.Launch
{
    public static class HarnessLaunchService
    {
        public const string ClaudeProviderId = "claude-code";
        public const string CodexProviderId = "codex";
        public const string GrokProviderId = "grok-build";

        /// <summary>EditorPrefs key for a machine-scoped harness path override.</summary>
        public static string PathOverrideKey(string providerId) => $"Splatter_HarnessPath_{providerId}";

        public static string GetPathOverride(string providerId)
        {
            var value = EditorPrefs.GetString(PathOverrideKey(providerId), "");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static async Task<bool> LaunchAsync(string providerId, AgentPermissionMode mode)
        {
            var bridge = SplatterBridge.Instance;
            if (!bridge.IsConnected && !await bridge.InitializeAsync())
            {
                EditorUtility.DisplayDialog("Splatterface Games Assistant",
                    "Couldn't start or connect to the Splatter service.", "OK");
                return false;
            }

            // Validate the CLI with the same resolution the launch will use.
            HarnessCheckResult check;
            try
            {
                check = await bridge.CheckHarnessAsync(providerId, GetPathOverride(providerId));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Splatterface Games Assistant", $"Harness check failed: {ex.Message}", "OK");
                return false;
            }
            if (check == null || !check.ok || string.IsNullOrEmpty(check.path))
            {
                EditorUtility.DisplayDialog("Splatterface Games Assistant",
                    $"{SplatterBridge.ProviderLabel(providerId)} isn't available: {check?.message ?? "not found"}\n\n" +
                    "Set its path in Preferences > Splatter AI.", "OK");
                return false;
            }

            // Mint the token-scoped session that carries the chosen permission mode.
            McpSessionCreated minted;
            try
            {
                minted = await bridge.MintSessionAsync(providerId, mode);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Splatterface Games Assistant", $"Couldn't create a session: {ex.Message}", "OK");
                return false;
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            LaunchContextWriter.EnsureManagedBlocks(projectRoot);

            var mcpUrl = string.IsNullOrEmpty(minted.mcp_url) ? SplatterBridge.GetMcpUrl() : minted.mcp_url;
            CleanupStaleTempConfigs();

            try
            {
                var command = providerId switch
                {
                    ClaudeProviderId => BuildClaudeCommand(check.path, mcpUrl, minted),
                    CodexProviderId => BuildCodexCommand(check.path, mcpUrl, minted),
                    GrokProviderId => BuildGrokCommand(check.path, mcpUrl, minted, projectRoot),
                    _ => throw new NotSupportedException($"No launcher for '{providerId}'.")
                };

                var pid = LaunchTerminal(projectRoot, command);
                ActiveSessionTracker.Add(new TrackedSession
                {
                    sessionId = minted.session_id,
                    providerId = providerId,
                    mode = mode.ToString(),
                    startedAtMs = minted.created_at_ms,
                    terminalPid = pid
                });
                Debug.Log($"[Splatter] Launched {SplatterBridge.ProviderLabel(providerId)} " +
                          $"(session {minted.session_id}, mode {mode}, MCP {mcpUrl})");
                return true;
            }
            catch (Exception ex)
            {
                // Don't leave a dangling token for a terminal that never opened.
                _ = bridge.CloseSessionAsync(minted.session_id);
                Debug.LogError($"[Splatter] Failed to launch {providerId}: {ex.Message}");
                EditorUtility.DisplayDialog("Splatterface Games Assistant", $"Couldn't launch a terminal: {ex.Message}", "OK");
                return false;
            }
        }

        public static async Task EndSessionAsync(string sessionId)
        {
            try
            {
                await SplatterBridge.Instance.CloseSessionAsync(sessionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Splatter] mcp.session.close failed: {ex.Message}");
            }
            ActiveSessionTracker.Remove(sessionId);
        }

        // Claude Code: MCP config via a temp --mcp-config file carrying the session token
        // header (proven pattern from the headless runner / debug launcher). The whole
        // splatter-unity server is pre-approved CLI-side: the service's mutation gate in
        // the Unity editor is the authoritative prompt, so users are never asked twice.
        // Claude's own tools (Bash/Edit/Write) keep their native TUI prompts.
        private static string BuildClaudeCommand(string exePath, string mcpUrl, McpSessionCreated minted)
        {
            var configJson =
                "{\"mcpServers\":{\"splatter-unity\":{\"type\":\"http\",\"url\":\"" + mcpUrl +
                "\",\"headers\":{\"X-Splatter-Session\":\"" + minted.token + "\"}}}}";
            var configPath = Path.Combine(
                Path.GetTempPath(), $"splatter-mcp-{minted.session_id}.json");
            File.WriteAllText(configPath, configJson);

            return $"{Quote(exePath)} --mcp-config {Quote(configPath)} --allowedTools mcp__splatter-unity";
        }

        // Codex: config via -c CLI overrides (no global config mutation). The session
        // token rides as a URL query param - the service accepts ?session= exactly so we
        // don't have to squeeze an inline-TOML header table through cmd quoting.
        // tool_timeout_sec must exceed the editor's permission-gate wait (default 60s
        // would abandon a prompt). VERIFY on the installed codex version: quoted-string
        // -c values and query-string preservation.
        private static string BuildCodexCommand(string exePath, string mcpUrl, McpSessionCreated minted)
        {
            var url = $"{mcpUrl}?session={minted.token}";
            return $"{Quote(exePath)}" +
                   $" -c mcp_servers.splatter_unity.url=\\\"{url}\\\"" +
                   " -c mcp_servers.splatter_unity.startup_timeout_sec=30" +
                   " -c mcp_servers.splatter_unity.tool_timeout_sec=600";
        }

        // Grok Build: MCP config via a managed section in the project's .grok/config.toml
        // (project configs may contribute [mcp_servers] per xAI's docs), rewritten at each
        // launch with the current endpoint + session token; grok reads config at startup,
        // so concurrent sessions each keep the token they launched with. The whole
        // splatter_unity server is pre-approved CLI-side via --allow (the editor gate is
        // the authoritative prompt). Grok Build reads AGENTS.md, which LaunchContextWriter
        // already maintains. VERIFY on the installed grok version: --allow in interactive
        // mode and the MCPTool(server__*) pattern.
        private static string BuildGrokCommand(string exePath, string mcpUrl, McpSessionCreated minted, string projectRoot)
        {
            WriteGrokProjectConfig(projectRoot, mcpUrl, minted.token);
            return $"{Quote(exePath)} --allow \"MCPTool(splatter_unity__*)\"";
        }

        private const string GrokConfigBegin = "# BEGIN SPLATTER MANAGED (rewritten at each launch - do not edit)";
        private const string GrokConfigEnd = "# END SPLATTER MANAGED";

        private static void WriteGrokProjectConfig(string projectRoot, string mcpUrl, string token)
        {
            var dir = Path.Combine(projectRoot, ".grok");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "config.toml");

            // Session tokens are ephemeral and loopback-only; a stale token just falls
            // back to a read-only session, so leaving the section in place is safe.
            var section =
                GrokConfigBegin + "\n" +
                "[mcp_servers.splatter_unity]\n" +
                $"url = \"{mcpUrl}\"\n" +
                "headers = { \"X-Splatter-Session\" = \"" + token + "\" }\n" +
                "enabled = true\n" +
                "startup_timeout_sec = 30\n" +
                // Must exceed the editor's permission-gate wait + reload-barrier holds.
                "tool_timeout_sec = 600\n" +
                GrokConfigEnd;

            if (!File.Exists(path))
            {
                File.WriteAllText(path, section + "\n");
                return;
            }

            var existing = File.ReadAllText(path);
            var begin = existing.IndexOf(GrokConfigBegin, StringComparison.Ordinal);
            if (begin < 0)
            {
                var separator = existing.EndsWith("\n") ? "\n" : "\n\n";
                File.WriteAllText(path, existing + separator + section + "\n");
                return;
            }

            var end = existing.IndexOf(GrokConfigEnd, begin, StringComparison.Ordinal);
            File.WriteAllText(path, end < 0
                ? existing.Substring(0, begin) + section + "\n"
                : existing.Substring(0, begin) + section + existing.Substring(end + GrokConfigEnd.Length));
        }

        // Opens a real terminal at the project root running the command, and returns the
        // terminal wrapper's PID (best effort; -1 when unknown).
        private static int LaunchTerminal(string workingDir, string command)
        {
            ProcessStartInfo psi;

#if UNITY_EDITOR_WIN
            // cmd /k keeps the window open after the CLI exits so transcripts stay readable.
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k " + command,
                WorkingDirectory = workingDir,
                UseShellExecute = true,
                CreateNoWindow = false
            };
#elif UNITY_EDITOR_OSX
            var inner = $"cd '{workingDir}' && {command.Replace("\"", "\\\"")}";
            psi = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e 'tell application \"Terminal\" to do script \"{inner}\"' -e 'tell application \"Terminal\" to activate'",
                UseShellExecute = false
            };
#else
            psi = new ProcessStartInfo
            {
                FileName = "x-terminal-emulator",
                Arguments = "-e " + command,
                WorkingDirectory = workingDir,
                UseShellExecute = false
            };
#endif
            var process = Process.Start(psi);
            try { return process?.Id ?? -1; } catch { return -1; }
        }

        private static string Quote(string s) => s.Contains(" ") ? $"\"{s}\"" : s;

        // Temp MCP configs are one-per-launch; sweep anything older than a day.
        private static void CleanupStaleTempConfigs()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-24);
                foreach (var file in Directory.GetFiles(Path.GetTempPath(), "splatter-mcp-*.json"))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
            }
            catch
            {
                // best effort
            }
        }
    }
}
