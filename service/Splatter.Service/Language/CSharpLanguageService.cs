using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Splatter.Service.Language;

public sealed class CSharpLanguageService : ICSharpLanguageService
{
    private readonly ServiceConfiguration _configuration;
    private readonly ILogger<CSharpLanguageService> _logger;
    private readonly string _projectRoot;
    private readonly string _rootUri;
    private readonly JsonRpcLspClient _client;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _documentGate = new(1, 1);
    private readonly ConcurrentDictionary<string, JsonElement> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _diagnosticWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DocumentState> _documents = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _initialized;
    private JsonElement _capabilities;
    private ResolvedServer? _server;
    private string? _lastError;

    public CSharpLanguageService(ServiceConfiguration configuration, ILogger<CSharpLanguageService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _projectRoot = Path.GetFullPath(configuration.ProjectRoot ?? Environment.CurrentDirectory);
        _rootUri = new Uri(AppendDirectorySeparator(_projectRoot)).AbsoluteUri.TrimEnd('/');
        _client = new JsonRpcLspClient(StartServerAsync);
        _client.Notification += OnNotification;
        _client.Disconnected += error =>
        {
            _initialized = false;
            _documents.Clear();
            _diagnostics.Clear();
            if (error is not null) _lastError = error.Message;
        };
    }

    public async Task<object> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            if (toolName == "csharp.status") return await StatusAsync(cancellationToken).ConfigureAwait(false);
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var path = RequiredString(arguments, "path");
            var document = await SynchronizeDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            return toolName switch
            {
                "csharp.get_diagnostics" => await DiagnosticsAsync(document, arguments, cancellationToken).ConfigureAwait(false),
                "csharp.get_symbols" => await RequestAsync("textDocument/documentSymbol", document, null, cancellationToken).ConfigureAwait(false),
                "csharp.hover" => await RequestAsync("textDocument/hover", document, Position(arguments), cancellationToken).ConfigureAwait(false),
                "csharp.find_definition" => await RequestAsync("textDocument/definition", document, Position(arguments), cancellationToken).ConfigureAwait(false),
                "csharp.find_references" => await ReferencesAsync(document, arguments, cancellationToken).ConfigureAwait(false),
                "csharp.get_completions" => await RequestAsync("textDocument/completion", document, Position(arguments), cancellationToken).ConfigureAwait(false),
                "csharp.get_signature_help" => await RequestAsync("textDocument/signatureHelp", document, Position(arguments), cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown C# language tool '{toolName}'.")
            };
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            throw new InvalidOperationException($"C# language server: {ex.Message}", ex);
        }
    }

    private async Task<object> StatusAsync(CancellationToken cancellationToken)
    {
        try { await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _lastError = ex.Message; }
        return new
        {
            connected = _client.IsConnected && _initialized,
            transport = "stdio",
            server = _server?.Name,
            command = _server?.Command,
            projectRoot = _projectRoot,
            solution = FindSolution(),
            capabilities = _capabilities.ValueKind == JsonValueKind.Undefined ? null : (object)_capabilities,
            error = _lastError
        };
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized && _client.IsConnected) return;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _client.IsConnected) return;
            var result = await _client.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                rootUri = _rootUri,
                capabilities = new
                {
                    workspace = new { workspaceFolders = true },
                    textDocument = new
                    {
                        synchronization = new { didSave = true, dynamicRegistration = false },
                        diagnostic = new { dynamicRegistration = false },
                        completion = new { completionItem = new { snippetSupport = false } },
                        hover = new { contentFormat = new[] { "markdown", "plaintext" } }
                    }
                },
                workspaceFolders = new[] { new { uri = _rootUri, name = Path.GetFileName(_projectRoot) } },
                clientInfo = new { name = "splatter-unity", version = ServiceConfiguration.ServiceVersion },
                initializationOptions = new { automaticWorkspaceInit = true }
            }, cancellationToken).ConfigureAwait(false);
            _capabilities = result.TryGetProperty("capabilities", out var capabilities) ? capabilities.Clone() : default;
            await _client.NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            _initialized = true;
            _lastError = null;
        }
        finally { _initializeGate.Release(); }
    }

    private async Task<LspConnection> StartServerAsync(CancellationToken cancellationToken)
    {
        _server = ResolveServer() ?? throw new FileNotFoundException(
            "No C# LSP executable was found. Install csharp-ls, install the VS Code C# extension, or launch Splatter with --csharp-lsp-command <path>.");
        var start = new ProcessStartInfo
        {
            FileName = _server.Command,
            Arguments = _server.Arguments,
            WorkingDirectory = _projectRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {_server.Name}.");
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                _logger.LogDebug("C# LSP: {Line}", line);
        }, CancellationToken.None);
        _logger.LogInformation("Started C# language server {Server} for {ProjectRoot}", _server.Name, _projectRoot);
        return new LspConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, new ProcessOwner(process));
    }

    private ResolvedServer? ResolveServer()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.CSharpLspCommand))
            return new ResolvedServer("configured", _configuration.CSharpLspCommand!, _configuration.CSharpLspArguments ?? "");

        var csharpLs = FindOnPath(OperatingSystem.IsWindows() ? "csharp-ls.exe" : "csharp-ls") ??
                       FindOnPath("csharp-ls") ??
                       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", OperatingSystem.IsWindows() ? "csharp-ls.exe" : "csharp-ls");
        if (File.Exists(csharpLs)) return new ResolvedServer("csharp-ls", csharpLs, "");

        var roslyn = FindOnPath(OperatingSystem.IsWindows() ? "Microsoft.CodeAnalysis.LanguageServer.exe" : "Microsoft.CodeAnalysis.LanguageServer") ??
                     FindRoslynInVsCode();
        if (roslyn is not null)
        {
            var logDirectory = Path.Combine(_configuration.DataDirectory, "logs", "csharp-lsp");
            Directory.CreateDirectory(logDirectory);
            return new ResolvedServer("Microsoft.CodeAnalysis.LanguageServer", roslyn,
                $"--logLevel Warning --extensionLogDirectory \"{logDirectory}\" --stdio");
        }
        return null;
    }

    private static string? FindOnPath(string command)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(directory.Trim('"'), command); if (File.Exists(candidate)) return candidate; } catch { }
        }
        return null;
    }

    private static string? FindRoslynInVsCode()
    {
        var extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (!Directory.Exists(extensions)) return null;
        try
        {
            return Directory.EnumerateFiles(extensions, OperatingSystem.IsWindows() ? "Microsoft.CodeAnalysis.LanguageServer.exe" : "Microsoft.CodeAnalysis.LanguageServer", SearchOption.AllDirectories)
                .Where(path => path.Contains("ms-dotnettools.csharp-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private async Task<DocumentState> SynchronizeDocumentAsync(string requestedPath, CancellationToken cancellationToken)
    {
        var fullPath = ResolveProjectPath(requestedPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("C# file not found.", requestedPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("C# language tools only accept .cs files.");
        var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var uri = new Uri(fullPath).AbsoluteUri;

        await _documentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_documents.TryGetValue(uri, out var state))
            {
                state = new DocumentState(uri, 1, hash, text);
                _documents[uri] = state;
                _diagnostics.TryRemove(uri, out _);
                await _client.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "csharp", version = state.Version, text } }, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.Equals(state.Hash, hash, StringComparison.Ordinal))
            {
                var change = UsesIncrementalSync()
                    ? (object)new { range = FullRange(state.Text), rangeLength = state.Text.Length, text }
                    : new { text };
                state = state with { Version = state.Version + 1, Hash = hash, Text = text };
                _documents[uri] = state;
                _diagnostics.TryRemove(uri, out _);
                await _client.NotifyAsync("textDocument/didChange", new { textDocument = new { uri, version = state.Version }, contentChanges = new[] { change } }, cancellationToken).ConfigureAwait(false);
            }
            return state;
        }
        finally { _documentGate.Release(); }
    }

    private async Task<object> DiagnosticsAsync(DocumentState document, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (SupportsPullDiagnostics())
        {
            try
            {
                var pulled = await _client.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri = document.Uri } }, cancellationToken).ConfigureAwait(false);
                var items = pulled.TryGetProperty("items", out var values) ? values.Clone() : JsonSerializer.SerializeToElement(Array.Empty<object>());
                return DiagnosticResult(document, items, true, "pull");
            }
            catch (InvalidOperationException) { }
        }
        if (_diagnostics.TryGetValue(document.Uri, out var cached)) return DiagnosticResult(document, cached, true, "push");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticWaiters[document.Uri] = waiter;
        if (_diagnostics.TryGetValue(document.Uri, out cached)) waiter.TrySetResult(cached);
        await _client.NotifyAsync("textDocument/didSave", new { textDocument = new { uri = document.Uri } }, cancellationToken).ConfigureAwait(false);
        var waitMs = arguments.TryGetProperty("waitMs", out var wait) && wait.TryGetInt32(out var value) ? Math.Clamp(value, 50, 10_000) : 1500;
        JsonElement diagnostics;
        var fresh = true;
        try { diagnostics = await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { fresh = false; diagnostics = _diagnostics.GetValueOrDefault(document.Uri); }
        finally { _diagnosticWaiters.TryRemove(document.Uri, out _); }
        return DiagnosticResult(document, diagnostics, fresh, "push");
    }

    private bool SupportsPullDiagnostics() => _capabilities.ValueKind == JsonValueKind.Object && _capabilities.TryGetProperty("diagnosticProvider", out _);
    private bool UsesIncrementalSync() => _capabilities.ValueKind == JsonValueKind.Object &&
        _capabilities.TryGetProperty("textDocumentSync", out var sync) && sync.ValueKind == JsonValueKind.Object &&
        sync.TryGetProperty("change", out var change) && change.TryGetInt32(out var kind) && kind == 2;
    private static object FullRange(string text)
    {
        var lines = text.Split('\n');
        return new { start = new { line = 0, character = 0 }, end = new { line = lines.Length - 1, character = lines[^1].TrimEnd('\r').Length } };
    }
    private object DiagnosticResult(DocumentState document, JsonElement diagnostics, bool fresh, string mode) => new
    {
        path = ProjectRelativePath(new Uri(document.Uri).LocalPath),
        diagnostics = diagnostics.ValueKind == JsonValueKind.Array ? diagnostics : JsonSerializer.SerializeToElement(Array.Empty<object>()),
        fresh,
        mode,
        version = document.Version
    };

    private Task<object> ReferencesAsync(DocumentState document, JsonElement arguments, CancellationToken cancellationToken) =>
        RequestAsync("textDocument/references", document, new ReferenceRequest(Position(arguments), arguments.TryGetProperty("includeDeclaration", out var include) && include.ValueKind == JsonValueKind.True), cancellationToken);

    private async Task<object> RequestAsync(string method, DocumentState document, object? value, CancellationToken cancellationToken)
    {
        object parameters = value switch
        {
            null => new { textDocument = new { uri = document.Uri } },
            ReferenceRequest reference => new { textDocument = new { uri = document.Uri }, position = reference.Position, context = new { includeDeclaration = reference.IncludeDeclaration } },
            _ => new { textDocument = new { uri = document.Uri }, position = value }
        };
        var result = await _client.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return new { path = ProjectRelativePath(new Uri(document.Uri).LocalPath), result };
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method != "textDocument/publishDiagnostics" || parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("uri", out var uriElement)) return;
        var uri = uriElement.GetString();
        if (string.IsNullOrEmpty(uri)) return;
        var values = parameters.TryGetProperty("diagnostics", out var diagnostics) ? diagnostics.Clone() : JsonSerializer.SerializeToElement(Array.Empty<object>());
        _diagnostics[uri] = values;
        if (_diagnosticWaiters.TryGetValue(uri, out var waiter)) waiter.TrySetResult(values);
    }

    private string ResolveProjectPath(string path)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(AppendDirectorySeparator(_projectRoot), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Language tools reject paths outside the Unity project.");
        return fullPath;
    }
    private string ProjectRelativePath(string fullPath) => Path.GetRelativePath(_projectRoot, fullPath).Replace('\\', '/');
    private string? FindSolution() => Directory.EnumerateFiles(_projectRoot, "*.sln*").OrderBy(path => path.Length).FirstOrDefault();
    private static string AppendDirectorySeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    private static string RequiredString(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new ArgumentException($"'{name}' is required.");
    private static object Position(JsonElement arguments) => new { line = Math.Max(0, arguments.GetProperty("line").GetInt32() - 1), character = Math.Max(0, arguments.GetProperty("column").GetInt32() - 1) };

    public async ValueTask DisposeAsync()
    {
        if (_initialized && _client.IsConnected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await _client.RequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false); await _client.NotifyAsync("exit", null, timeout.Token).ConfigureAwait(false); } catch { }
        }
        await _client.DisposeAsync().ConfigureAwait(false);
        _initializeGate.Dispose();
        _documentGate.Dispose();
    }

    private sealed record DocumentState(string Uri, int Version, string Hash, string Text);
    private sealed record ReferenceRequest(object Position, bool IncludeDeclaration);
    private sealed record ResolvedServer(string Name, string Command, string Arguments);
    private sealed class ProcessOwner(Process process) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
