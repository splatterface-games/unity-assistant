// Splatter Service - Entry Point

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Splatter.Service;
using Splatter.Service.Storage;
using Splatter.Service.Protocol;
using Splatter.Service.Permissions;
using Splatter.Service.Context;
using Splatter.Service.Tools;
using Splatter.Service.Generators;
using Splatter.Service.Recovery;
using Splatter.Service.Diagnostics;
using Splatter.Service.Security;
using Splatter.Service.Language;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Core services
// Parse launch args (Unity passes --port, --auth-token, --data-dir, --project-root).
var configuration = BuildServiceConfiguration(args);
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton<ICredentialStore, CredentialStore>();
builder.Services.AddSingleton<IPersistenceStore, SqlitePersistenceStore>();
builder.Services.AddSingleton<IEventJournal, EventJournal>();

// Protocol/Transport
builder.Services.AddSingleton<InteractiveSessionRegistry>();
builder.Services.AddSingleton<McpHttpHandler>();
builder.Services.AddSingleton<ITransportServer, WebSocketTransportServer>();
builder.Services.AddSingleton<IMessageRouter, MessageRouter>();
builder.Services.AddSingleton<ApiMessageHandler>();

// Tools & Permissions
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton<IPermissionEngine, PermissionEngine>();
builder.Services.AddSingleton<ICheckpointManager, CheckpointManager>();
builder.Services.AddSingleton<ICSharpLanguageService, CSharpLanguageService>();

// Context
builder.Services.AddSingleton<IContextService, ContextService>();
builder.Services.AddSingleton<ISkillRegistry, SkillRegistry>();

// Generators
builder.Services.AddSingleton<IGeneratorService, GeneratorService>();

// M6: Recovery & Diagnostics
builder.Services.AddSingleton<ITimingMetrics, TimingMetrics>();
builder.Services.AddSingleton<ICrashRecoveryManager, CrashRecoveryManager>();
builder.Services.AddSingleton<IDiagnosticsExporter, DiagnosticsExporter>();
builder.Services.AddSingleton<ISecurityPolicyManager, SecurityPolicyManager>();

// Hosted service
builder.Services.AddHostedService<SplatterServiceHost>();

var host = builder.Build();

// Run
await host.RunAsync();

// Builds the service configuration from command-line args. Recognized flags:
//   --port <int>            transport port (0 = auto-assign)
//   --auth-token <string>   per-editor bearer token required on connections
//   --data-dir <path>       storage directory for this project's state
//   --project-root <path>   the Unity project root (for path-scoped tools)
//   --host <ip>             bind address (defaults to loopback)
//   --csharp-lsp-command    optional csharp-ls or Roslyn LSP executable
//   --csharp-lsp-arguments  optional raw command-line arguments for that executable
static ServiceConfiguration BuildServiceConfiguration(string[] args)
{
    var config = new ServiceConfiguration();

    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        var key = args[i];
        var value = args[i + 1];
        switch (key)
        {
            case "--port":
                if (int.TryParse(value, out var port)) config.Port = port;
                break;
            case "--auth-token":
                config.AuthToken = string.IsNullOrWhiteSpace(value) ? null : value;
                break;
            case "--data-dir":
                if (!string.IsNullOrWhiteSpace(value)) config.DataDirectory = value;
                break;
            case "--project-root":
                if (!string.IsNullOrWhiteSpace(value)) config.ProjectRoot = value;
                break;
            case "--host":
                if (!string.IsNullOrWhiteSpace(value)) config.Host = value;
                break;
            case "--csharp-lsp-command":
                if (!string.IsNullOrWhiteSpace(value)) config.CSharpLspCommand = value;
                break;
            case "--csharp-lsp-arguments":
                config.CSharpLspArguments = value;
                break;
        }
    }

    return config;
}
