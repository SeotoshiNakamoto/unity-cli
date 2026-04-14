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
    /// Monitors UIDocument additions/removals each frame and writes events
    /// to a JSONL status file. No Harmony, no reflection — plain Unity APIs.
    /// </summary>
    [InitializeOnLoad]
    internal static class UIEventMonitor
    {
        static readonly string s_StatusDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "status");

        // Last known UIDocument fingerprint: name → childCount
        static Dictionary<string, int> s_LastFingerprint = new Dictionary<string, int>();
        static double s_LastCheck;
        const double CHECK_INTERVAL = 0.25; // 4 checks/sec, not every frame

        static UIEventMonitor()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += _ => Reset();
            AssemblyReloadEvents.beforeAssemblyReload += () => EditorApplication.update -= Tick;
            AssemblyReloadEvents.afterAssemblyReload += () =>
            {
                Reset();
                EditorApplication.update += Tick;
            };
        }

        static void Reset()
        {
            s_LastFingerprint.Clear();
            s_LastCheck = 0;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - s_LastCheck < CHECK_INTERVAL) return;
            s_LastCheck = now;

            var current = BuildFingerprint();
            if (s_LastFingerprint.Count == 0 && current.Count > 0)
            {
                // First observation — just record, don't emit events
                s_LastFingerprint = current;
                return;
            }

            var events = new List<string>();

            // Detect removed
            foreach (var kv in s_LastFingerprint)
            {
                if (!current.ContainsKey(kv.Key))
                {
                    events.Add(FormatEvent("screen_removed", kv.Key, 0));
                }
            }

            // Detect added
            foreach (var kv in current)
            {
                if (!s_LastFingerprint.ContainsKey(kv.Key))
                {
                    events.Add(FormatEvent("screen_added", kv.Key, kv.Value));
                }
            }

            s_LastFingerprint = current;

            if (events.Count > 0)
                AppendEvents(events);
        }

        static Dictionary<string, int> BuildFingerprint()
        {
            var fp = new Dictionary<string, int>();

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
                var name = doc.gameObject.name;

                // Disambiguate duplicate names (§ won't appear in real GameObject names)
                if (fp.ContainsKey(name))
                {
                    int suffix = 2;
                    while (fp.ContainsKey(name + "§" + suffix)) suffix++;
                    name = name + "§" + suffix;
                }

                fp[name] = childCount;
            }

            return fp;
        }

        static string FormatEvent(string type, string name, int elementCount)
        {
            var ts = DateTime.Now.ToString("o");
            // Manual JSON — avoid allocating JObject for a 3-field line
            return $"{{\"ts\":\"{ts}\",\"type\":\"{type}\",\"name\":\"{EscapeJson(name)}\",\"element_count\":{elementCount}}}";
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
