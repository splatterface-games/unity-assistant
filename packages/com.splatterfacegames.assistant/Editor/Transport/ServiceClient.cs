// Service Client - WebSocket transport for Unity <-> local service.
//
// Speaks the shared wire protocol (Splatter.Protocol.MessageEnvelope) using
// Newtonsoft.Json with the SAME conventions as the service's ProtocolJson.Options:
//   - camelCase property names
//   - enum values as member names ("Request"/"Response"/"Event")
//   - payload is a JSON object (carried as the envelope's object Payload)
// Responses are correlated by CorrelationId. Errors arrive as a Response whose
// Type is "error" and whose payload is a NormalizedError.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Splatter.Editor;
using Splatter.Protocol;
using UnityEngine;

namespace Splatter.Editor.Transport
{
    /// <summary>
    /// WebSocket client for the Splatter local service. Singleton; survives across
    /// calls. Handles connection, request/response correlation, and event dispatch.
    /// </summary>
    public sealed class ServiceClient : IDisposable
    {
        private ClientWebSocket _socket;
        private CancellationTokenSource _receiveCts;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pendingRequests =
            new ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>>();
        private readonly ConcurrentDictionary<string, Action<MessageEnvelope>> _eventSubscriptions =
            new ConcurrentDictionary<string, Action<MessageEnvelope>>();
        // Global (all-event) subscribers. A list, NOT a single slot: both the tool
        // executor and the conversation manager subscribe, and a single slot would let
        // one silently clobber the other (dropping tool.execute -> tool calls time out).
        private readonly List<Action<MessageEnvelope>> _globalHandlers =
            new List<Action<MessageEnvelope>>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // Mirrors the service's ProtocolJson.Options. Property names go to camelCase
        // on the wire via SnakeToCamelNamingStrategy, which maps BOTH the envelope's
        // PascalCase members (Protocol -> protocol) and the DTOs' snake_case members
        // (workspace_id -> workspaceId), so a single strategy aligns every payload
        // with the service in both directions. Enums serialize as member names.
        private static readonly JsonSerializerSettings WireSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeToCamelNamingStrategy() },
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.DateTimeOffset,
            DateFormatHandling = DateFormatHandling.IsoDateFormat
        };
        private static readonly JsonSerializer WireSerializer = JsonSerializer.Create(WireSettings);

        public event Action OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<MessageEnvelope> OnEventReceived;
        public event Action<Exception> OnError;

        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        private static ServiceClient _instance;
        public static ServiceClient Instance => _instance ?? (_instance = new ServiceClient());

        private ServiceClient() { }

        /// <summary>Connects to the Splatter service over an authenticated loopback WebSocket.</summary>
        public async Task<bool> ConnectAsync(string url, string authToken, CancellationToken ct = default)
        {
            if (IsConnected)
                return true;

            try
            {
                _socket?.Dispose();
                _socket = new ClientWebSocket();

                if (!string.IsNullOrEmpty(authToken))
                    _socket.Options.SetRequestHeader("Authorization", $"Bearer {authToken}");

                var wsUrl = url.Replace("http://", "ws://").Replace("https://", "wss://");
                if (!wsUrl.EndsWith("/ws"))
                    wsUrl = wsUrl.TrimEnd('/') + "/ws";

                await _socket.ConnectAsync(new Uri(wsUrl), ct);

                _receiveCts = new CancellationTokenSource();
                _ = ReceiveLoopAsync(_receiveCts.Token);

                OnConnected?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Failed to connect: {ex.Message}");
                OnError?.Invoke(ex);
                return false;
            }
        }

        /// <summary>Disconnects from the service and fails any pending requests.</summary>
        public async Task DisconnectAsync()
        {
            _receiveCts?.Cancel();

            if (_socket != null && _socket.State == WebSocketState.Open)
            {
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
                }
                catch { /* best effort */ }
            }

            _socket?.Dispose();
            _socket = null;

            foreach (var kvp in _pendingRequests)
                kvp.Value.TrySetCanceled();
            _pendingRequests.Clear();
        }

        /// <summary>Sends a request and awaits the correlated response, typed as TResponse.</summary>
        public async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
            string method, TRequest request, TimeSpan? timeout = null, CancellationToken ct = default)
            where TRequest : class
            where TResponse : class
        {
            var envelope = MessageEnvelope.Request(method, request);
            var tcs = new TaskCompletionSource<MessageEnvelope>();
            _pendingRequests[envelope.Id] = tcs;

            try
            {
                await SendEnvelopeAsync(envelope, ct);

                var timeoutMs = (int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
                using (var timeoutCts = new CancellationTokenSource(timeoutMs))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    // netstandard2.1-safe timeout (no Task.WaitAsync which is .NET 6+).
                    var delayTask = Task.Delay(Timeout.Infinite, linkedCts.Token);
                    var completed = await Task.WhenAny(tcs.Task, delayTask);
                    if (completed != tcs.Task)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new TimeoutException($"Request '{method}' timed out after {timeoutMs} ms");
                    }
                }

                var response = await tcs.Task;

                if (response.Type == "error")
                {
                    var err = PayloadAs<NormalizedError>(response.Payload);
                    throw new ServiceException(err?.Code ?? "error", err?.Message ?? "Unknown service error");
                }

                return PayloadAs<TResponse>(response.Payload);
            }
            finally
            {
                _pendingRequests.TryRemove(envelope.Id, out _);
            }
        }

        /// <summary>Sends a one-way message (no response awaited).</summary>
        public async Task SendNotificationAsync<T>(string method, T payload, CancellationToken ct = default) where T : class
        {
            var envelope = MessageEnvelope.Request(method, payload);
            await SendEnvelopeAsync(envelope, ct);
        }

        public IDisposable SubscribeToSession(string sessionId, Action<MessageEnvelope> handler)
        {
            _eventSubscriptions[sessionId] = handler;
            return new Subscription(() => _eventSubscriptions.TryRemove(sessionId, out _));
        }

        public IDisposable SubscribeToAllEvents(Action<MessageEnvelope> handler)
        {
            lock (_globalHandlers)
            {
                _globalHandlers.Add(handler);
            }
            return new Subscription(() =>
            {
                lock (_globalHandlers)
                {
                    _globalHandlers.Remove(handler);
                }
            });
        }

        /// <summary>Deserializes an envelope payload (a JToken) into T.</summary>
        public static T PayloadAs<T>(object payload) where T : class
        {
            if (payload == null)
                return null;
            if (payload is JToken token)
                return token.ToObject<T>(WireSerializer);
            // Fallback: round-trip through JSON.
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(payload, WireSettings), WireSettings);
        }

        private async Task SendEnvelopeAsync(MessageEnvelope envelope, CancellationToken ct)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to service");

            var json = JsonConvert.SerializeObject(envelope, WireSettings);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var messageBuffer = new System.Collections.Generic.List<byte>();

            try
            {
                while (!ct.IsCancellationRequested && _socket != null && _socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            OnDisconnected?.Invoke(result.CloseStatusDescription ?? "Server closed connection");
                            return;
                        }

                        for (int i = 0; i < result.Count; i++)
                            messageBuffer.Add(buffer[i]);
                    }
                    while (!result.EndOfMessage);

                    if (messageBuffer.Count > 0)
                    {
                        var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();

                        try
                        {
                            ProcessMessage(json);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[Splatter] Error processing message: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                OnDisconnected?.Invoke(ex.Message);
                OnError?.Invoke(ex);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }

        private void ProcessMessage(string json)
        {
            var envelope = JsonConvert.DeserializeObject<MessageEnvelope>(json, WireSettings);
            if (envelope == null)
                return;

            switch (envelope.Kind)
            {
                case MessageKind.Response:
                    // Correlate by the request id we sent (correlationId on the response).
                    if (!string.IsNullOrEmpty(envelope.CorrelationId) &&
                        _pendingRequests.TryGetValue(envelope.CorrelationId, out var tcs))
                    {
                        tcs.TrySetResult(envelope);
                    }
                    break;

                case MessageKind.Event:
                    if (!string.IsNullOrEmpty(envelope.SessionId) &&
                        _eventSubscriptions.TryGetValue(envelope.SessionId, out var sessionHandler))
                    {
                        MainThreadDispatcher.Enqueue(() => sessionHandler(envelope));
                    }
                    Action<MessageEnvelope>[] globals;
                    lock (_globalHandlers)
                    {
                        globals = _globalHandlers.ToArray();
                    }
                    foreach (var globalHandler in globals)
                    {
                        var h = globalHandler;
                        MainThreadDispatcher.Enqueue(() => h(envelope));
                    }
                    MainThreadDispatcher.Enqueue(() => OnEventReceived?.Invoke(envelope));
                    break;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _socket?.Dispose();
            _sendLock?.Dispose();
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose?.Invoke();
        }
    }

    /// <summary>Exception thrown when the service returns an error response.</summary>
    public class ServiceException : Exception
    {
        public string Code { get; }
        public ServiceException(string code, string message) : base(message) => Code = code;
    }

    /// <summary>
    /// Maps C# member names to the camelCase wire names the service expects, handling
    /// both PascalCase envelope members (Protocol -> protocol) and snake_case DTO
    /// members (workspace_id -> workspaceId). Applied for both serialization and
    /// deserialization, so a single strategy keeps every payload aligned.
    /// </summary>
    internal sealed class SnakeToCamelNamingStrategy : NamingStrategy
    {
        protected override string ResolvePropertyName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var sb = new StringBuilder(name.Length);
            foreach (var part in name.Split('_'))
            {
                if (part.Length == 0)
                    continue;
                if (sb.Length == 0)
                    sb.Append(char.ToLowerInvariant(part[0])).Append(part.Substring(1));
                else
                    sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1));
            }
            return sb.Length == 0 ? name : sb.ToString();
        }
    }
}
