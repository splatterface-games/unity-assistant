using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Splatter.Editor.Tools;
using Splatter.Editor.Transport;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Splatter.Editor.Handlers
{
    /// <summary>
    /// Completes tool calls that span a domain reload.
    ///
    /// A tool that triggers a recompile (e.g. script.compile_check with recompile=true) returns
    /// ToolExecutionResult.Defer() and records the call here on the MAIN THREAD before the reload.
    /// The editor process and the harness survive the reload; only the editor's managed state (the
    /// in-flight call + socket) is torn down. After the reload settles and the socket reconnects,
    /// this reads the FRESH result and sends tool.result for the original call id. If the recompile
    /// was a no-op (no reload), an in-domain watchdog completes it instead.
    /// </summary>
    [InitializeOnLoad]
    public static class ReloadSurvival
    {
        private const string Key = "Splatter_DeferredTools";
        private const double GiveUpSeconds = 120.0;

        [Serializable] private class Pending { public string callId; public string toolId; }
        [Serializable] private class PendingList { public List<Pending> items = new List<Pending>(); }

        private static double _graceUntil;   // no-reload watchdog: let the compile start first
        private static double _deadline;      // give up waiting for compile/reconnect after this

        static ReloadSurvival()
        {
            // Runs on every domain load. If a call was deferred across the reload that just
            // happened, complete it once compilation settles and the socket reconnects.
            var pending = Load().items.Count;
            if (pending > 0)
            {
                Debug.Log($"[Splatter] Reload-survival: {pending} deferred tool call(s) to complete after reload.");
                _deadline = EditorApplication.timeSinceStartup + GiveUpSeconds;
                EditorApplication.update += Pump;
            }
        }

        /// <summary>Records a tool call to complete after the domain reload it triggered. Main-thread only.</summary>
        public static void Defer(string callId, string toolId)
        {
            if (string.IsNullOrEmpty(callId)) return;

            var list = Load();
            list.items.Add(new Pending { callId = callId, toolId = toolId });
            Save(list);
            Debug.Log($"[Splatter] Reload-survival: deferred {toolId} ({callId}) across an expected reload.");

            // No-reload safety net: if the recompile is a no-op, nothing post-reload fires.
            _graceUntil = EditorApplication.timeSinceStartup + 3.0;
            _deadline = EditorApplication.timeSinceStartup + GiveUpSeconds;
            EditorApplication.update -= Watchdog;
            EditorApplication.update += Watchdog;
        }

        private static void Watchdog()
        {
            if (EditorApplication.timeSinceStartup < _graceUntil) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return; // a reload may still follow
            EditorApplication.update -= Watchdog;
            Pump();
        }

        private static void Pump()
        {
            var client = ServiceClient.Instance;
            var ready = !EditorApplication.isCompiling && !EditorApplication.isUpdating
                        && client != null && client.IsConnected;

            if (!ready)
            {
                if (EditorApplication.timeSinceStartup > _deadline)
                {
                    Debug.LogWarning($"[Splatter] Reload-survival: gave up waiting " +
                        $"(compiling={EditorApplication.isCompiling}, connected={(client != null && client.IsConnected)}). " +
                        "Clearing deferred calls; the service-side timeout will surface the failure.");
                    Stop();
                    Save(new PendingList());
                }
                return;
            }

            var list = Load();
            Stop();
            if (list.items.Count == 0) return;
            Save(new PendingList()); // clear first so neither path double-sends

            Debug.Log($"[Splatter] Reload-survival: completing {list.items.Count} deferred call(s) after reload.");
            foreach (var p in list.items)
                _ = Complete(client, p);
        }

        private static void Stop()
        {
            EditorApplication.update -= Pump;
            EditorApplication.update -= Watchdog;
        }

        private static async Task Complete(ServiceClient client, Pending p)
        {
            object output;
            try { output = ComputeResult(p.toolId); }
            catch (Exception ex) { output = new { error = ex.Message }; }

            var response = new ToolExecuteResponse
            {
                tool_call_id = p.callId,
                success = true,
                output = JsonConvert.SerializeObject(output)
            };
            try
            {
                await client.SendNotificationAsync("tool.result", response);
                Debug.Log($"[Splatter] Reload-survival: sent deferred result for {p.toolId} ({p.callId}).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Splatter] Reload-survival: failed to send deferred result for {p.callId}: {ex.Message}");
            }
        }

        // Post-reload result for a deferred tool. Extend as more reload-triggering tools opt in.
        private static object ComputeResult(string toolId)
        {
            switch (toolId)
            {
                case "script.compile_check":
                    return ScriptCompileCheckExecutor.ReadCompileResult();
                default:
                    return new { ok = true, note = "completed after reload" };
            }
        }

        private static PendingList Load()
        {
            var json = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(json)) return new PendingList();
            try { return JsonUtility.FromJson<PendingList>(json) ?? new PendingList(); }
            catch { return new PendingList(); }
        }

        private static void Save(PendingList list) => SessionState.SetString(Key, JsonUtility.ToJson(list));
    }
}
