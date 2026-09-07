// Mock Service Client - Simulates WebSocket communication for testing
// Allows tests to verify tool execution without a real service connection

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Splatter.Editor.Handlers;
using Splatter.Editor.Transport;
using UnityEngine;
using Splatter.Protocol;

namespace Splatter.Tests.Editor.Integration
{
    /// <summary>
    /// Mock implementation of service client for testing.
    /// Simulates WebSocket communication without requiring a real server.
    /// </summary>
    public class MockServiceClient : IDisposable
    {
        private readonly ConcurrentQueue<MessageEnvelope> _sentMessages = new();
        private readonly ConcurrentQueue<MessageEnvelope> _receivedResponses = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pendingRequests = new();
        private readonly ConcurrentDictionary<string, Action<MessageEnvelope>> _eventHandlers = new();
        private readonly List<ToolExecuteRequest> _toolExecutionRequests = new();
        private readonly List<ToolExecuteResponse> _toolResponses = new();

        private bool _isConnected;
        private bool _autoRespond = true;
        private Func<ToolExecuteRequest, ToolExecuteResponse> _customResponseHandler;
        private bool _disposed;

        public event Action<MessageEnvelope> OnMessageSent;
        public event Action<ToolExecuteRequest> OnToolExecuteRequested;

        /// <summary>
        /// Gets or sets whether the mock client is connected.
        /// </summary>
        public bool IsConnected
        {
            get => _isConnected;
            set => _isConnected = value;
        }

        /// <summary>
        /// Gets all messages that were sent through this client.
        /// </summary>
        public IReadOnlyList<MessageEnvelope> SentMessages
        {
            get
            {
                var list = new List<MessageEnvelope>();
                while (_sentMessages.TryDequeue(out var msg))
                {
                    list.Add(msg);
                }
                foreach (var msg in list)
                {
                    _sentMessages.Enqueue(msg);
                }
                return list;
            }
        }

        /// <summary>
        /// Gets all tool execution requests that were received.
        /// </summary>
        public IReadOnlyList<ToolExecuteRequest> ToolExecutionRequests => _toolExecutionRequests;

        /// <summary>
        /// Gets all tool responses that were sent.
        /// </summary>
        public IReadOnlyList<ToolExecuteResponse> ToolResponses => _toolResponses;

        /// <summary>
        /// Gets or sets whether the mock client should auto-respond to requests.
        /// </summary>
        public bool AutoRespond
        {
            get => _autoRespond;
            set => _autoRespond = value;
        }

        /// <summary>
        /// Sets a custom response handler for tool execution requests.
        /// </summary>
        public void SetCustomResponseHandler(Func<ToolExecuteRequest, ToolExecuteResponse> handler)
        {
            _customResponseHandler = handler;
        }

        /// <summary>
        /// Simulates connecting to the service.
        /// </summary>
        public Task<bool> ConnectAsync(string url = null, string authToken = null, CancellationToken ct = default)
        {
            _isConnected = true;
            return Task.FromResult(true);
        }

        /// <summary>
        /// Simulates disconnecting from the service.
        /// </summary>
        public Task DisconnectAsync()
        {
            _isConnected = false;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Simulates sending a request and receiving a response.
        /// </summary>
        public async Task<TResponse> SendRequestAsync<TRequest, TResponse>(string method, TRequest request, TimeSpan? timeout = null, CancellationToken ct = default)
            where TRequest : class
            where TResponse : class
        {
            if (!_isConnected)
            {
                throw new InvalidOperationException("Not connected to service");
            }

            var requestId = Guid.NewGuid().ToString("N");
            var envelope = new MessageEnvelope
            {
                Id = requestId,
                Type = "request",
                Method = method,
                Payload = JsonUtility.ToJson(request)
            };

            _sentMessages.Enqueue(envelope);
            OnMessageSent?.Invoke(envelope);

            // Create a pending request
            var tcs = new TaskCompletionSource<MessageEnvelope>();
            _pendingRequests[requestId] = tcs;

            try
            {
                // Wait for response
                var actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
                using var timeoutCts = new CancellationTokenSource(actualTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                var response = await tcs.Task.WaitAsync(linkedCts.Token);
                return JsonUtility.FromJson<TResponse>(response.Payload);
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        /// <summary>
        /// Simulates sending a notification (no response expected).
        /// </summary>
        public Task SendNotificationAsync<T>(string method, T payload, CancellationToken ct = default) where T : class
        {
            if (!_isConnected)
            {
                throw new InvalidOperationException("Not connected to service");
            }

            var envelope = new MessageEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "notification",
                Method = method,
                Payload = JsonUtility.ToJson(payload)
            };

            _sentMessages.Enqueue(envelope);
            OnMessageSent?.Invoke(envelope);

            // Handle tool.result notifications
            if (method == "tool.result")
            {
                var response = JsonUtility.FromJson<ToolExecuteResponse>(envelope.Payload);
                _toolResponses.Add(response);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Simulates receiving a tool execution request from the service.
        /// </summary>
        public void SimulateToolExecuteRequest(ToolExecuteRequest request)
        {
            _toolExecutionRequests.Add(request);
            OnToolExecuteRequested?.Invoke(request);

            var envelope = new MessageEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "event",
                Method = "tool.execute",
                Payload = JsonUtility.ToJson(request),
                SessionId = request.session_id
            };

            // Notify event handlers
            foreach (var handler in _eventHandlers.Values)
            {
                handler?.Invoke(envelope);
            }
        }

        /// <summary>
        /// Simulates receiving a response from the service.
        /// </summary>
        public void SimulateResponse(string requestId, object payload, MessageError error = null)
        {
            var envelope = new MessageEnvelope
            {
                Id = requestId,
                Type = "response",
                Payload = payload != null ? JsonUtility.ToJson(payload) : null,
                Error = error
            };

            _receivedResponses.Enqueue(envelope);

            if (_pendingRequests.TryGetValue(requestId, out var tcs))
            {
                tcs.TrySetResult(envelope);
            }
        }

        /// <summary>
        /// Simulates receiving an event from the service.
        /// </summary>
        public void SimulateEvent(string method, object payload, string sessionId = null)
        {
            var envelope = new MessageEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "event",
                Method = method,
                Payload = payload != null ? JsonUtility.ToJson(payload) : null,
                SessionId = sessionId
            };

            foreach (var handler in _eventHandlers.Values)
            {
                handler?.Invoke(envelope);
            }
        }

        /// <summary>
        /// Subscribes to events.
        /// </summary>
        public IDisposable SubscribeToAllEvents(Action<MessageEnvelope> handler)
        {
            var subscriptionId = Guid.NewGuid().ToString("N");
            _eventHandlers[subscriptionId] = handler;
            return new Subscription(() => _eventHandlers.TryRemove(subscriptionId, out _));
        }

        /// <summary>
        /// Subscribes to session events.
        /// </summary>
        public IDisposable SubscribeToSession(string sessionId, Action<MessageEnvelope> handler)
        {
            _eventHandlers[sessionId] = handler;
            return new Subscription(() => _eventHandlers.TryRemove(sessionId, out _));
        }

        /// <summary>
        /// Waits for a tool response with the specified tool call ID.
        /// </summary>
        public async Task<ToolExecuteResponse> WaitForToolResponseAsync(string toolCallId, TimeSpan? timeout = null)
        {
            var actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < actualTimeout)
            {
                foreach (var response in _toolResponses)
                {
                    if (response.tool_call_id == toolCallId)
                    {
                        return response;
                    }
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timeout waiting for tool response: {toolCallId}");
        }

        /// <summary>
        /// Clears all recorded messages and requests.
        /// </summary>
        public void Clear()
        {
            while (_sentMessages.TryDequeue(out _)) { }
            while (_receivedResponses.TryDequeue(out _)) { }
            _toolExecutionRequests.Clear();
            _toolResponses.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _isConnected = false;

            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
            _eventHandlers.Clear();
        }

        private class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose?.Invoke();
        }
    }

    /// <summary>
    /// Mock permission handler for testing tool execution that requires user permission.
    /// </summary>
    public class MockPermissionHandler
    {
        private bool _autoApprove = true;
        private readonly Dictionary<string, bool> _permissionOverrides = new();
        private readonly List<PermissionRequest> _requestHistory = new();

        /// <summary>
        /// Gets or sets whether to auto-approve permission requests.
        /// </summary>
        public bool AutoApprove
        {
            get => _autoApprove;
            set => _autoApprove = value;
        }

        /// <summary>
        /// Gets the history of permission requests.
        /// </summary>
        public IReadOnlyList<PermissionRequest> RequestHistory => _requestHistory;

        /// <summary>
        /// Sets a specific permission override for a tool.
        /// </summary>
        public void SetPermissionOverride(string toolId, bool approved)
        {
            _permissionOverrides[toolId] = approved;
        }

        /// <summary>
        /// Handles a permission request.
        /// </summary>
        public Task<bool> HandlePermissionRequestAsync(string toolId, string description, Dictionary<string, object> args)
        {
            var request = new PermissionRequest
            {
                ToolId = toolId,
                Description = description,
                Arguments = args,
                Timestamp = DateTime.UtcNow
            };

            _requestHistory.Add(request);

            if (_permissionOverrides.TryGetValue(toolId, out var overrideValue))
            {
                request.WasApproved = overrideValue;
                return Task.FromResult(overrideValue);
            }

            request.WasApproved = _autoApprove;
            return Task.FromResult(_autoApprove);
        }

        /// <summary>
        /// Clears the permission history.
        /// </summary>
        public void Clear()
        {
            _requestHistory.Clear();
            _permissionOverrides.Clear();
        }
    }

    /// <summary>
    /// Records a permission request.
    /// </summary>
    public class PermissionRequest
    {
        public string ToolId { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Arguments { get; set; }
        public DateTime Timestamp { get; set; }
        public bool WasApproved { get; set; }
    }
}
