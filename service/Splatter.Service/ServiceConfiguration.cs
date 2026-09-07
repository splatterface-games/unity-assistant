// Splatter Service Configuration

namespace Splatter.Service;

public sealed class ServiceConfiguration
{
    public const string ProtocolVersion = "splatter.v1";
    public const string ServiceVersion = "0.1.0";

    // Transport
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 0; // 0 = auto-assign
    public bool UseNamedPipe { get; set; } = false;
    public string? NamedPipeName { get; set; }

    // Storage
    public string DataDirectory { get; set; } = GetDefaultDataDirectory();
    public string DatabaseFileName { get; set; } = "splatter.db";

    // Security
    public TimeSpan TokenExpiry { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan PermissionRequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-editor bearer token required on WebSocket connections. When set,
    /// unauthenticated connections are rejected. When null/empty (e.g. standalone
    /// dev runs on loopback), no token is required.
    /// </summary>
    public string? AuthToken { get; set; }

    // Harness CLIs (path or PATH-resolvable name). The editor launches these as
    // interactive terminals wired to our MCP endpoint; the CLI owns auth and model.
    public string ClaudeCodePath { get; set; } = "claude";
    public string CodexPath { get; set; } = "codex";
    public string GrokBuildPath { get; set; } = "grok";

    // Optional C# LSP override. When unset, Splatter discovers csharp-ls or the
    // Roslyn server shipped by the VS Code C# extension.
    public string? CSharpLspCommand { get; set; }
    public string? CSharpLspArguments { get; set; }

    // How long an MCP tools/call blocks on the editor Allow/Deny prompt before
    // failing safe to a denial. Kept below typical CLI MCP client timeouts.
    public TimeSpan McpGateTimeout { get; set; } = TimeSpan.FromMinutes(2);

    // The service's own MCP HTTP endpoint (set after the transport starts). Harness
    // CLIs are pointed here so they can call Splatter's Unity/project tools.
    public string? McpUrl { get; set; }

    // Indexing
    public int MaxIndexFileSizeBytes { get; set; } = 1024 * 1024; // 1MB
    public int MaxSkillFileSizeBytes { get; set; } = 1024 * 1024; // 1MB

    // Project/Workspace
    public string? ProjectRoot { get; set; }
    public string? CheckpointsPath { get; set; }
    public int MaxCheckpointSnapshots { get; set; } = 50;
    public TimeSpan CheckpointMaxAge { get; set; } = TimeSpan.FromDays(7);

    // Generators
    public string? GeneratedAssetsPath { get; set; }
    public int MaxConcurrentGeneratorJobs { get; set; } = 3;

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    private static string GetDefaultDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Splatterface Games", "Assistant");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "logs"));
    }
}
