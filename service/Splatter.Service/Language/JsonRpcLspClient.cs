using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Splatter.Service.Language;

internal sealed class JsonRpcLspClient : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<LspConnection>> _connect;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private LspConnection? _connection;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private long _nextId;

    public JsonRpcLspClient(Func<CancellationToken, Task<LspConnection>> connect) => _connect = connect;
    public bool IsConnected => _connection is not null;
    public event Action<string, JsonElement>? Notification;
    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null) return;
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null) return;
            _connection = await _connect(cancellationToken).ConfigureAwait(false);
            _readerCancellation = new CancellationTokenSource();
            _readerTask = ReadLoopAsync(_connection.Input, _readerCancellation.Token);
        }
        finally { _connectionGate.Release(); }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var output = _connection?.Output ?? throw new IOException("The C# language server is disconnected.");
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync(Stream input, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var length = await ReadContentLengthAsync(input, cancellationToken).ConfigureAwait(false);
                var bytes = new byte[length];
                await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                Dispatch(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            foreach (var completion in _pending.Values)
                completion.TrySetException(failure ?? new IOException("The C# language server disconnected."));
            Disconnected?.Invoke(failure);
        }
    }

    private void Dispatch(JsonElement message)
    {
        if (!message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id) && _pending.TryRemove(id, out var completion))
        {
            if (message.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidOperationException(error.GetRawText()));
            else completion.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement<object?>(null));
            return;
        }
        if (message.TryGetProperty("method", out var method))
        {
            var methodName = method.GetString() ?? "";
            var parameters = message.TryGetProperty("params", out var values) ? values.Clone() : default;
            if (message.TryGetProperty("id", out var serverId)) _ = RespondToServerRequestAsync(serverId.Clone(), methodName, parameters);
            else Notification?.Invoke(methodName, parameters);
        }
    }

    private async Task RespondToServerRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            object? result = method switch
            {
                "workspace/configuration" when parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                    => Enumerable.Repeat<object?>(null, items.GetArrayLength()).ToArray(),
                "workspace/workspaceFolders" => Array.Empty<object>(),
                "workspace/applyEdit" => new { applied = false, failureReason = "Splatter C# language tools are read-only." },
                _ => null
            };
            await WriteAsync(new { jsonrpc = "2.0", id, result }, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task<int> ReadContentLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(128);
        var tail = 0;
        while (tail != 0x0d0a0d0a)
        {
            var one = new byte[1];
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            header.Add(one[0]);
            tail = (tail << 8) | one[0];
            if (header.Count > 16_384) throw new InvalidDataException("LSP header exceeded 16 KiB.");
        }
        var text = Encoding.ASCII.GetString(header.ToArray());
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[15..].Trim(), out var length) && length >= 0)
                return length;
        throw new InvalidDataException("LSP response omitted Content-Length.");
    }

    public async ValueTask DisposeAsync()
    {
        _readerCancellation?.Cancel();
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        if (_readerTask is not null) try { await _readerTask.ConfigureAwait(false); } catch { }
        _readerCancellation?.Dispose();
        _connectionGate.Dispose();
        _writeGate.Dispose();
    }
}

internal sealed record LspConnection(Stream Input, Stream Output, IAsyncDisposable Owner) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Owner.DisposeAsync();
}
