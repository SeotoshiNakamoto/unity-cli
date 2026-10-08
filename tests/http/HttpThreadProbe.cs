// Temporary native fixture: precompile, copy into the main package Editor folder,
// then remove this source and its generated .meta after validation. Never ship it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector.HttpTests
{
    public class EditModeSmoke
    {
        [NUnit.Framework.Test]
        public void PureMainThreadSmoke()
        {
            NUnit.Framework.Assert.That(Application.isPlaying, NUnit.Framework.Is.False);
            NUnit.Framework.Assert.That(Application.dataPath, NUnit.Framework.Is.EqualTo("D:/Projects/ProjectD/client/Assets"));
            NUnit.Framework.Assert.That(2 + 2, NUnit.Framework.Is.EqualTo(4));
        }
    }

    [InitializeOnLoad]
    [UnityCliTool(Name = "_http_thread_probe", Description = "Temporary HTTP thread/lifetime regression fixture")]
    public static class ThreadProbe
    {
        static readonly int MainThread = Thread.CurrentThread.ManagedThreadId;
        static readonly string Epoch = Guid.NewGuid().ToString("N");
        static readonly List<string> Events = new();
        static readonly CancellationTokenSource Reload = new();
        static int active, maximum, entered, exited;
        static object session;
        static string shutdownPath;
        static ThreadProbe()
        {
            session = typeof(HttpServer).GetField("s_Session", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                Reload.Cancel();
                if (session == null || shutdownPath == null) return; // Original connector has no session type.
                var type = session.GetType();
                var loop = (Task)type.GetField("Loop").GetValue(session);
                var tasksField = type.GetField("Tasks");
                var tasks = tasksField != null ? (IEnumerable<Task>)tasksField.GetValue(session)
                    : ((System.Collections.Concurrent.ConcurrentDictionary<System.Net.HttpListenerContext, Task>)type.GetField("Requests").GetValue(session)).Values;
                int unfinished = loop.IsCompleted ? 0 : 1;
                foreach (var task in tasks) if (!task.IsCompleted) unfinished++;
                var report = Newtonsoft.Json.JsonConvert.SerializeObject(new { epoch = Epoch, unfinished, loopCompleted = loop.IsCompleted });
                System.IO.File.AppendAllText(shutdownPath, report + "\n");
            };
        }

        sealed class MainOnlyResult
        {
            public string id;
            public int enteredThread, resumedThread;
            public string UnityPath
            {
                get
                {
                    AssertMain();
                    return Application.dataPath;
                }
            }
            public int GetterThread { get { AssertMain(); return Thread.CurrentThread.ManagedThreadId; } }
        }

        static void AssertMain()
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThread)
                throw new InvalidOperationException("HTTP regression: Unity getter/handler left main thread");
            if (Application.dataPath != "D:/Projects/ProjectD/client/Assets")
                throw new InvalidOperationException("HTTP regression: wrong project");
        }

        public static async Task<object> HandleCommand(JObject p)
        {
            AssertMain();
            session = typeof(HttpServer).GetField("s_Session", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            var action = p?["action"]?.ToString() ?? "work";
            if (action == "configure")
            {
                shutdownPath = p["shutdown_path"]?.ToString();
                return new SuccessResponse("configured");
            }
            if (action == "forced_retirement")
            {
                var type = session.GetType();
                var cts = (CancellationTokenSource)type.GetField("Cts").GetValue(session);
                var tasks = (List<Task>)type.GetField("Tasks").GetValue(session);
                tasks.Add(Task.Delay(6800)); // Synthetic unfinished I/O, no blocking socket/UI.
                var timer = Stopwatch.StartNew();
                typeof(HttpServer).GetMethod("StopListener", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var stopMs = timer.Elapsed.TotalMilliseconds;
                var retire = (Task)type.GetField("Retirement").GetValue(session);
                bool beforeDisposed;
                try { var token = cts.Token; beforeDisposed = false; } catch (ObjectDisposedException) { beforeDisposed = true; }
                typeof(HttpServer).GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var retiring = typeof(HttpServer).GetField("s_Retiring", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                bool tracked = (bool)retiring.GetType().GetMethod("ContainsKey").Invoke(retiring, new[] { session });
                var pending = Newtonsoft.Json.JsonConvert.SerializeObject(new { stopMs, beforeDisposed, tracked, ended = retire?.IsCompleted });
                System.IO.File.WriteAllText(p["record"].ToString(), pending);
                await Task.Delay(2300);
                bool afterDisposed;
                try { var token = cts.Token; afterDisposed = false; } catch (ObjectDisposedException) { afterDisposed = true; }
                System.IO.File.AppendAllText(p["record"].ToString(), "\n" + Newtonsoft.Json.JsonConvert.SerializeObject(new { afterDisposed, ended = retire.IsCompleted }));
                return new SuccessResponse("retirement checked");
            }
            if (action == "state")
                return new SuccessResponse("state", new { epoch = Epoch, mainThread = MainThread, active, maximum, entered, exited, events = Events.ToArray(), threads = Process.GetCurrentProcess().Threads.Count });
            if (action == "kill_listener")
            {
                var listener = (System.Net.HttpListener)typeof(HttpServer).GetField("s_Listener", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                listener.Stop();
                return new SuccessResponse("listener stopped");
            }
            if (action == "reload")
            {
                EditorUtility.RequestScriptReload();
                return new SuccessResponse("reload requested", new { epoch = Epoch });
            }
            if (action == "reset")
            {
                if (active != 0) throw new InvalidOperationException("reset while active");
                maximum = entered = exited = 0;
                Events.Clear();
                return new SuccessResponse("reset");
            }
            var id = p?["id"]?.ToString() ?? "probe";
            var record = p?["record"]?.ToString();
            if (record != null) System.IO.File.AppendAllText(record, "enter:" + id + "\n");
            if (p?["stop_after"] != null || p?["reload_after"] != null)
            {
                bool reloadInstead = p?["reload_after"] != null;
                var stopAt = EditorApplication.timeSinceStartup + (p["reload_after"] ?? p["stop_after"]).Value<int>() / 1000.0;
                EditorApplication.CallbackFunction restart = null;
                restart = () =>
                {
                    if (EditorApplication.timeSinceStartup < stopAt) return;
                    EditorApplication.update -= restart;
                    if (reloadInstead) { EditorUtility.RequestScriptReload(); return; }
                    typeof(HttpServer).GetMethod("StopListener", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                    typeof(HttpServer).GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                };
                EditorApplication.update += restart;
            }
            int delay = p?["delay"]?.Value<int>() ?? 10;
            var tid = Thread.CurrentThread.ManagedThreadId;
            active++;
            maximum = Math.Max(maximum, active);
            entered++;
            Events.Add("enter:" + id);
            try
            {
                if (p?["compile"]?.Value<bool>() == true)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                }
                await Task.Delay(delay, Reload.Token);
                AssertMain();
                return new SuccessResponse("probe", new MainOnlyResult { id = id, enteredThread = tid, resumedThread = Thread.CurrentThread.ManagedThreadId });
            }
            catch (OperationCanceledException) when (Reload.IsCancellationRequested)
            {
                return new ErrorResponse("Probe interrupted by assembly reload.");
            }
            finally
            {
                active--; exited++; Events.Add("exit:" + id);
                if (record != null) System.IO.File.AppendAllText(record, "exit:" + id + "\n");
            }
        }
    }
}
