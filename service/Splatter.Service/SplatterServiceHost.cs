// Splatter Service Host - Background Service

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Splatter.Service.Protocol;
using Splatter.Service.Storage;
using Splatter.Service.Recovery;
using Splatter.Service.Diagnostics;
using Splatter.Service.Security;
using Splatter.Protocol;

namespace Splatter.Service;

public sealed class SplatterServiceHost : BackgroundService
{
    private readonly ILogger<SplatterServiceHost> _logger;
    private readonly ServiceConfiguration _config;
    private readonly ITransportServer _transport;
    private readonly IPersistenceStore _persistence;
    private readonly ApiMessageHandler _apiHandler;
    private readonly ICrashRecoveryManager _recovery;
    private readonly IDiagnosticsExporter _diagnostics;
    private readonly ISecurityPolicyManager _security;
    private readonly ITimingMetrics _timing;

    public SplatterServiceHost(
        ILogger<SplatterServiceHost> logger,
        ServiceConfiguration config,
        ITransportServer transport,
        IPersistenceStore persistence,
        ApiMessageHandler apiHandler,
        ICrashRecoveryManager recovery,
        IDiagnosticsExporter diagnostics,
        ISecurityPolicyManager security,
        ITimingMetrics timing)
    {
        _logger = logger;
        _config = config;
        _transport = transport;
        _persistence = persistence;
        _apiHandler = apiHandler;
        _recovery = recovery;
        _diagnostics = diagnostics;
        _security = security;
        _timing = timing;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Splatter Service starting...");

        try
        {
            using var startupScope = _timing.StartScope(TimingOperation.RecoveryScan, "startup");

            // Ensure directories
            _config.EnsureDirectoriesExist();

            // Write crash marker (cleared on graceful shutdown)
            await _recovery.WriteCrashMarkerAsync(stoppingToken);

            // Initialize storage
            await _persistence.InitializeAsync(stoppingToken);
            _logger.LogInformation("Storage initialized at {Path}", _config.DatabasePath);

            // Run crash recovery scan
            var recoveryResult = await _recovery.ScanForRecoverableItemsAsync(stoppingToken);
            if (recoveryResult.TotalAbandoned > 0)
            {
                _logger.LogWarning(
                    "Found {Count} recoverable items from previous session ({Recoverable} recoverable, {Unknown} unknown)",
                    recoveryResult.TotalAbandoned, recoveryResult.TotalRecoverable, recoveryResult.TotalUnknown);

                // Auto-resolve simple cases
                await AutoResolveRecoverableItemsAsync(stoppingToken);
            }

            // Register API message handlers (including recovery and diagnostics endpoints)
            _apiHandler.RegisterHandlers();
            RegisterRecoveryHandlers();
            RegisterDiagnosticsHandlers();

            // Start transport
            var endpoint = await _transport.StartAsync(stoppingToken);
            _logger.LogInformation("Transport listening on {Endpoint}", endpoint);

            // Publish the MCP HTTP endpoint (same host/port, /mcp path) so the harness
            // runner can point spawned CLIs at it.
            _config.McpUrl = endpoint.Replace("ws://", "http://").Replace("wss://", "https://").TrimEnd('/') + "/mcp";

            // Announce the endpoint on stdout. The Unity bootstrap watches stdout for
            // this exact line to discover the port; without it the editor times out.
            Console.Out.WriteLine($"SPLATTER_URL={endpoint}");
            Console.Out.Flush();

            // Write connection info for Unity to find
            await WriteConnectionInfoAsync(endpoint, stoppingToken);

            // Log security policy summary
            LogSecurityPolicySummary();

            _logger.LogInformation("Splatter Service ready (v{Version})", ServiceConfiguration.ServiceVersion);
            startupScope.WithTag("status", "success");

            // Keep running until cancelled
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Splatter Service shutting down gracefully...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Splatter Service failed");
            throw;
        }
        finally
        {
            // Clear crash marker on graceful shutdown
            await _recovery.ClearCrashMarkerAsync(CancellationToken.None);

            await _transport.StopAsync(CancellationToken.None);
            _logger.LogInformation("Splatter Service stopped");
        }
    }

    private async Task AutoResolveRecoverableItemsAsync(CancellationToken ct)
    {
        var items = await _recovery.GetRecoverableItemsAsync(null, ct);

        foreach (var item in items)
        {
            // Auto-resolve old checkpoints and partial writes
            if (item.Type == RecoverableItemType.Checkpoint || item.Type == RecoverableItemType.PartialWrite)
            {
                var age = DateTimeOffset.UtcNow - item.LastActivity;
                if (age > TimeSpan.FromDays(7))
                {
                    _logger.LogInformation("Auto-deleting old {Type} item {Id} (age: {Age})",
                        item.Type, item.Id, age);
                    await _recovery.ExecuteRecoveryActionAsync(item.Id, RecoveryAction.Delete, ct);
                }
            }
        }
    }

    private void RegisterRecoveryHandlers()
    {
        // Recovery handlers would be registered with the message router
        // This is a placeholder for the actual implementation
        _logger.LogDebug("Recovery handlers registered");
    }

    private void RegisterDiagnosticsHandlers()
    {
        // Diagnostics handlers would be registered with the message router
        // This is a placeholder for the actual implementation
        _logger.LogDebug("Diagnostics handlers registered");
    }

    private void LogSecurityPolicySummary()
    {
        var ssrf = _security.GetSsrfPolicy();
        var shell = _security.GetShellEnvPolicy();

        _logger.LogInformation(
            "Security policies active: SSRF ({AllowedHosts} hosts), Shell ({AllowedVars} vars, {BlockedVars} blocked)",
            ssrf.AllowedHosts.Count,
            shell.AllowedVariables.Count,
            shell.BlockedVariables.Count);
    }

    private async Task WriteConnectionInfoAsync(string endpoint, CancellationToken ct)
    {
        var connectionInfo = new ConnectionInfo
        {
            Protocol = ServiceConfiguration.ProtocolVersion,
            Version = ServiceConfiguration.ServiceVersion,
            Endpoint = endpoint,
            Pid = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow
        };

        var path = Path.Combine(_config.DataDirectory, "connection.json");
        var json = System.Text.Json.JsonSerializer.Serialize(connectionInfo, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(path, json, ct);
        _logger.LogDebug("Connection info written to {Path}", path);
    }
}

public sealed record ConnectionInfo
{
    public required string Protocol { get; init; }
    public required string Version { get; init; }
    public required string Endpoint { get; init; }
    public required int Pid { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}
