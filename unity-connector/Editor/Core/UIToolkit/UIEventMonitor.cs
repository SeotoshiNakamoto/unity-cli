using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCliConnector.UIToolkit
{
    /// <summary>
    /// Opt-in monitor for UIDocument additions/removals. While enabled it writes
    /// events to a JSONL status file. No Harmony, no reflection — plain Unity APIs.
    /// </summary>
    internal static class UIEventMonitor
    {
        static readonly string s_StatusDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "status");

        sealed class DocumentFingerprint
        {
            internal string Name;
            internal int ChildCount;
        }

        // Instance ID distinguishes replacement documents that reuse the same GameObject name.
        static Dictionary<int, DocumentFingerprint> s_LastFingerprint =
            new Dictionary<int, DocumentFingerprint>();
        static bool s_HasBaseline;
        static double s_LastCheck;
        static double s_ExpiresAt;
        static bool s_IsMonitoring;
        const double CHECK_INTERVAL = 0.25; // 4 checks/sec, not every frame
        internal const int MONITOR_TIMEOUT_SECONDS = 300;

        internal static bool IsMonitoring
        {
            get
            {
                StopIfExpired();
                return s_IsMonitoring;
            }
        }

        internal static double ExpiresInSeconds
        {
            get
            {
                StopIfExpired();
                return s_IsMonitoring
                    ? Math.Max(0, s_ExpiresAt - EditorApplication.timeSinceStartup)
                    : 0;
            }
        }

        internal static void Start()
        {
            if (!s_IsMonitoring)
            {
                EditorApplication.update += Tick;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                AssemblyReloadEvents.beforeAssemblyReload += Stop;
                s_IsMonitoring = true;
            }

            ClearPending();
            ResetFingerprint();
            s_ExpiresAt = EditorApplication.timeSinceStartup + MONITOR_TIMEOUT_SECONDS;
        }

        internal static void Stop()
        {
            if (s_IsMonitoring)
            {
                EditorApplication.update -= Tick;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                AssemblyReloadEvents.beforeAssemblyReload -= Stop;
                s_IsMonitoring = false;
            }

            s_ExpiresAt = 0;
            ResetFingerprint(captureCurrent: false);
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                ResetFingerprint();
            else if (state == PlayModeStateChange.ExitingPlayMode)
                Stop();
        }

        static void ResetFingerprint(bool captureCurrent = true)
        {
            s_LastFingerprint = captureCurrent && EditorApplication.isPlaying
                ? BuildFingerprint()
                : new Dictionary<int, DocumentFingerprint>();
            s_HasBaseline = captureCurrent && EditorApplication.isPlaying;
            s_LastCheck = EditorApplication.timeSinceStartup;
        }

        static void StopIfExpired()
        {
            if (s_IsMonitoring && EditorApplication.timeSinceStartup >= s_ExpiresAt)
                Stop();
        }

        static void Tick()
        {
            StopIfExpired();
            if (!s_IsMonitoring) return;
            if (!EditorApplication.isPlaying) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - s_LastCheck < CHECK_INTERVAL) return;
            s_LastCheck = now;

            var current = BuildFingerprint();
            if (!s_HasBaseline)
            {
                // First observation — just record, don't emit events
                s_LastFingerprint = current;
                s_HasBaseline = true;
                return;
            }

            var events = new List<string>();

            // Detect removed
            foreach (var kv in s_LastFingerprint)
            {
                if (!current.ContainsKey(kv.Key))
                {
                    events.Add(FormatEvent(
                        "screen_removed", kv.Value.Name, kv.Key, 0));
                }
            }

            // Detect added
            foreach (var kv in current)
            {
                if (!s_LastFingerprint.ContainsKey(kv.Key))
                {
                    events.Add(FormatEvent(
                        "screen_added", kv.Value.Name, kv.Key, kv.Value.ChildCount));
                }
            }

            s_LastFingerprint = current;

            if (events.Count > 0)
                AppendEvents(events);
        }

        static Dictionary<int, DocumentFingerprint> BuildFingerprint()
        {
            var fp = new Dictionary<int, DocumentFingerprint>();

#if UNITY_2023_1_OR_NEWER
            var documents = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
#else
            var documents = UnityEngine.Object.FindObjectsOfType<UIDocument>();
#endif

            foreach (var doc in documents)
            {
                if (doc == null || !doc.gameObject.activeInHierarchy) continue;
                var root = doc.rootVisualElement;
                int childCount = root != null ? root.childCount : 0;
                var instanceId = doc.GetInstanceID();
                fp[instanceId] = new DocumentFingerprint
                {
                    Name = doc.gameObject.name,
                    ChildCount = childCount,
                };
            }

            return fp;
        }

        static string FormatEvent(string type, string name, int instanceId, int elementCount)
        {
            var ts = DateTime.Now.ToString("o");
            // Manual JSON — avoid allocating JObject for a small status line
            return $"{{\"ts\":\"{ts}\",\"type\":\"{type}\",\"name\":\"{EscapeJson(name)}\",\"instance_id\":{instanceId},\"element_count\":{elementCount}}}";
        }

        static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        static void AppendEvents(List<string> events)
        {
            try
            {
                Directory.CreateDirectory(s_StatusDir);
                var path = GetEventsFilePath();
                File.AppendAllLines(path, events);
            }
            catch
            {
                // Silently ignore file I/O errors — don't crash the editor
            }
        }

        internal static string GetEventsFilePath()
        {
            return Path.Combine(s_StatusDir, $"ui-events-{HttpServer.Port}.jsonl");
        }

        static void ClearPending()
        {
            try
            {
                var path = GetEventsFilePath();
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // A stale event file must not prevent monitoring from starting.
            }
        }

        /// <summary>
        /// Read all pending events and clear the file. Called by UISnapshot "events" action.
        /// </summary>
        internal static string[] ReadAndClear()
        {
            var path = GetEventsFilePath();
            if (!File.Exists(path))
                return Array.Empty<string>();

            try
            {
                var lines = File.ReadAllLines(path);
                File.Delete(path);
                return lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
