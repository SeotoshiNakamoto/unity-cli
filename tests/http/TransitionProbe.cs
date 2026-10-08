// Temporary native observation fixture. Never deploy outside a validation run.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityCliConnector.HttpTests
{
    [InitializeOnLoad]
    [UnityCliTool(Name = "_http_transition_probe", Description = "Temporary transition observation")]
    public static class TransitionProbe
    {
        static object session;
        static string report;
        static TransitionProbe()
        {
            // Force HttpServer's static reload registration before our observer.
            session = typeof(HttpServer).GetField("s_Session", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (session == null || report == null) return;
                var type = session.GetType();
                var loop = (Task)type.GetField("Loop").GetValue(session);
                var tasks = (System.Collections.Generic.List<Task>)type.GetField("Tasks").GetValue(session);
                File.AppendAllText(report, JsonConvert.SerializeObject(new { loopCompleted = loop.IsCompleted,
                    unfinished = tasks.Count(t => !t.IsCompleted), taskCount = tasks.Count }) + "\n");
            };
        }
        public static object HandleCommand(JObject p)
        {
            session = typeof(HttpServer).GetField("s_Session", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            report = p?["report"]?.ToString();
            if (p?["reload_ms"] != null)
            {
                double due = EditorApplication.timeSinceStartup + p["reload_ms"].Value<int>() / 1000.0;
                EditorApplication.CallbackFunction callback = null;
                callback = () =>
                {
                    if (EditorApplication.timeSinceStartup < due) return;
                    bool running = typeof(UnityCliConnector.Tools.ManageFrameDebugger).GetField("s_Capture", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) != null;
                    if (p["wait_capture"]?.Value<bool>() == true && !running)
                    {
                        if (EditorApplication.timeSinceStartup < due + 5) return;
                        EditorApplication.update -= callback;
                        File.AppendAllText(p["marker"].ToString(), JsonConvert.SerializeObject(new { captureRunning = false, reloadRequested = false }) + "\n");
                        return;
                    }
                    EditorApplication.update -= callback;
                    if (p["marker"] != null)
                        File.AppendAllText(p["marker"].ToString(), JsonConvert.SerializeObject(new { captureRunning = running, reloadRequested = true,
                            enabled = UnityEngine.FrameDebugger.enabled, paused = EditorApplication.isPaused }) + "\n");
                    EditorUtility.RequestScriptReload();
                };
                EditorApplication.update += callback;
            }
            var capture = typeof(UnityCliConnector.Tools.ManageFrameDebugger).GetField("s_Capture", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            return new SuccessResponse("armed", new { captureRunning = capture != null });
        }
    }
}
