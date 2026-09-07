// Tracks interactive harness sessions launched from this editor.
//
// SessionState-backed (survives domain reloads, cleared on editor restart) and
// reconciled against the service's mcp.session.list, which is the source of truth -
// terminal processes outlive reloads and the service knows which tokens are live.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Splatter.Editor.Launch
{
    [Serializable]
    public class TrackedSession
    {
        public string sessionId;
        public string providerId;
        public string mode;
        public string label;
        public long startedAtMs;
        public int terminalPid; // best-effort: the terminal wrapper process
    }

    public static class ActiveSessionTracker
    {
        private const string StateKey = "Splatter_ActiveSessions";

        [Serializable]
        private class State { public List<TrackedSession> sessions = new List<TrackedSession>(); }

        public static IReadOnlyList<TrackedSession> Sessions => Load().sessions;

        public static void Add(TrackedSession session)
        {
            var state = Load();
            state.sessions.RemoveAll(s => s.sessionId == session.sessionId);
            state.sessions.Add(session);
            Save(state);
        }

        public static void Remove(string sessionId)
        {
            var state = Load();
            state.sessions.RemoveAll(s => s.sessionId == sessionId);
            Save(state);
        }

        /// <summary>Drops local records the service no longer knows about.</summary>
        public static void Reconcile(IEnumerable<string> liveSessionIds)
        {
            var live = new HashSet<string>(liveSessionIds);
            var state = Load();
            var removed = state.sessions.RemoveAll(s => !live.Contains(s.sessionId));
            if (removed > 0)
                Save(state);
        }

        private static State Load()
        {
            var json = SessionState.GetString(StateKey, "");
            if (string.IsNullOrEmpty(json)) return new State();
            try
            {
                return JsonUtility.FromJson<State>(json) ?? new State();
            }
            catch
            {
                return new State();
            }
        }

        private static void Save(State state) =>
            SessionState.SetString(StateKey, JsonUtility.ToJson(state));
    }
}
