// Main Thread Dispatcher - Marshals callbacks to Unity main thread

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEditor;

namespace Splatter.Editor
{
    /// <summary>
    /// Dispatches actions to Unity's main thread from background threads.
    /// Essential for WebSocket callbacks and async operations that need Unity API access.
    /// </summary>
    [InitializeOnLoad]
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _actionQueue = new();
        private static readonly ConcurrentQueue<Func<Task>> _asyncActionQueue = new();

        static MainThreadDispatcher()
        {
            EditorApplication.update += ProcessQueue;
        }

        /// <summary>
        /// Enqueue an action to be executed on the main thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            _actionQueue.Enqueue(action);
        }

        /// <summary>
        /// Enqueue an async action to be executed on the main thread.
        /// </summary>
        public static void Enqueue(Func<Task> asyncAction)
        {
            if (asyncAction == null) return;
            _asyncActionQueue.Enqueue(asyncAction);
        }

        /// <summary>
        /// Execute an action on the main thread and wait for completion.
        /// </summary>
        public static Task RunOnMainThreadAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();

            Enqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Execute a function on the main thread and return the result.
        /// </summary>
        public static Task<T> RunOnMainThreadAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();

            Enqueue(() =>
            {
                try
                {
                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Execute an async function on the main thread and return the result.
        /// </summary>
        public static Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> asyncFunc)
        {
            var tcs = new TaskCompletionSource<T>();

            Enqueue(async () =>
            {
                try
                {
                    var result = await asyncFunc();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Check if we're currently on the main thread.
        /// </summary>
        public static bool IsMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == 1;

        private static void ProcessQueue()
        {
            // Process sync actions (limit per frame to avoid blocking)
            int processed = 0;
            while (processed < 100 && _actionQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[Splatter] MainThreadDispatcher error: {ex}");
                }
                processed++;
            }

            // Process async actions (one at a time to avoid overwhelming)
            if (_asyncActionQueue.TryDequeue(out var asyncAction))
            {
                try
                {
                    // Fire and forget - errors are logged inside
                    _ = ExecuteAsyncAction(asyncAction);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[Splatter] MainThreadDispatcher async error: {ex}");
                }
            }
        }

        private static async Task ExecuteAsyncAction(Func<Task> asyncAction)
        {
            try
            {
                await asyncAction();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Splatter] MainThreadDispatcher async action error: {ex}");
            }
        }
    }
}
