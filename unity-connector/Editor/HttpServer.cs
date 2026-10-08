using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliConnector
{
    /// <summary>
    /// Lightweight HTTP server on localhost. Receives CLI commands as POST /command,
    /// dispatches via CommandRouter, returns JSON responses.
    /// Uses ConcurrentQueue + EditorApplication.update for main-thread marshaling
    /// so commands execute even when Unity is unfocused.
    /// Survives domain reloads via InitializeOnLoad.
    /// </summary>
    [InitializeOnLoad]
    public static class HttpServer
    {
        const int DEFAULT_PORT = 8090;
        const int MAX_PORT_ATTEMPTS = 10;
        const double AUTO_RESTART_INTERVAL = 1.0;
        const double FAILURE_LOG_INTERVAL = 5.0;

        static HttpListener s_Listener;
        static ListenerSession s_Session;
        static readonly ConcurrentDictionary<ListenerSession, byte> s_Retiring = new();
        const string NotExecuted = "Command was not executed: Unity is reloading/restarting the listener. Retry after the editor is ready.";
        const string OutcomeUnknown = "Command execution started, but its outcome is unknown: Unity is reloading/restarting the listener. Do not automatically retry commands with side effects.";

        sealed class Reply
        {
            public readonly byte[] Buffer;
            public readonly int Status;
            public Reply(byte[] buffer, int status = 200) { Buffer = buffer; Status = status; }
        }

        sealed class RequestState
        {
            public readonly TaskCompletionSource<Reply> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int execution; // 0 pending, 1 started, 2 cancelled before start
            public void BeginExecution()
            {
                if (Interlocked.CompareExchange(ref execution, 1, 0) != 0)
                    throw new OperationCanceledException();
            }
            public ErrorResponse CancelError()
            {
                var prior = Interlocked.CompareExchange(ref execution, 2, 0);
                return new ErrorResponse(prior == 1 ? OutcomeUnknown : NotExecuted,
                    new { execution_state = prior == 1 ? "started" : "not_started" });
            }
            public Reply CancelReply() => new(SerializeResponse(CancelError()), 503);
        }

        // A listener generation owns all background I/O. No Unity objects or APIs
        // are used by its tasks; StopListener cancels and joins them before reload.
        sealed class ListenerSession
        {
            public readonly HttpListener Listener;
            public readonly CancellationTokenSource Cts = new();
            public readonly object Gate = new();
            public readonly ConcurrentDictionary<HttpListenerContext, Task> Requests = new();
            public readonly List<Task> Tasks = new();
            public Task Loop;
            public Task Retirement;
            public byte[] Health;
            public volatile bool RestartPending;

            public ListenerSession(HttpListener listener) { Listener = listener; }
        }
        static int s_Port;
        static SynchronizationContext s_MainContext;
        static double s_NextStartAttemptTime;
        static string s_LastFailureMessage;
        static double s_LastFailureLogTime;
        static string s_ProjectPath;
        static int s_Pid;
        static string s_UnityVersion;
        static string s_ConnectorVersion;

        static readonly ConcurrentQueue<WorkItem> s_Queue = new();

        struct WorkItem
        {
            public string Command;
            public JObject Parameters;
            public RequestState Request;
            public CancellationToken Token;
            public bool Async;
        }

        static HttpServer()
        {
            if (!EditorProcessGuard.IsPrimaryEditorProcess)
                return;

            s_MainContext = SynchronizationContext.Current;
            s_ProjectPath = Application.dataPath.Replace("/Assets", "");
            s_Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            s_UnityVersion = Application.unityVersion;
            try
            {
                s_ConnectorVersion = UnityEditor.PackageManager.PackageInfo
                    .FindForAssembly(typeof(HttpServer).Assembly)?.version;
            }
            catch
            {
                s_ConnectorVersion = null;
            }

            Start();
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += StopListener;
            AssemblyReloadEvents.afterAssemblyReload += Start;
            EditorApplication.update += ProcessQueue;
        }

        public static int Port => s_Port;
        public static bool IsRunning => s_Listener != null && s_Listener.IsListening;
        public static string LastFailure => s_LastFailureMessage;
        public static string ConnectorVersion => s_ConnectorVersion;

        static object HealthSnapshot()
        {
            return new
            {
                listening = IsRunning,
                port = s_Port,
                pid = s_Pid,
                projectPath = s_ProjectPath,
                unityVersion = s_UnityVersion,
                connectorVersion = s_ConnectorVersion,
                lastFailure = s_LastFailureMessage,
                retryScheduled = s_NextStartAttemptTime > 0,
            };
        }

        static void Start()
        {
            if (IsRunning) return;
            if (s_Listener != null)
                StopListener();

            for (var attempt = 0; attempt < MAX_PORT_ATTEMPTS; attempt++)
            {
                var port = DEFAULT_PORT + attempt;
                if (TryStartOnPort(port))
                    return;
            }

            ScheduleRetry();
            LogStartFailure("[UnityCliConnector] Failed to start HTTP server — no available port", true);
        }

        static bool TryStartOnPort(int port)
        {
            HttpListener listener = null;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();

                var session = new ListenerSession(listener);
                s_Listener = listener;
                s_Port = port;
                s_Session = session;
                s_NextStartAttemptTime = 0;
                s_LastFailureMessage = null;
                s_LastFailureLogTime = 0;

                session.Health = SerializeResponse(new SuccessResponse("ok", HealthSnapshot()));
                session.Loop = Task.Run(() => ListenLoop(session));

                Debug.Log($"[UnityCliConnector] HTTP server started on port {port}");
                return true;
            }
            catch (HttpListenerException)
            {
                CloseListener(listener);
                return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Windows/Mono throws SocketException instead of HttpListenerException.
                CloseListener(listener);
                return false;
            }
            catch (Exception ex)
            {
                CloseListener(listener);
                ScheduleRetry();
                LogStartFailure($"[UnityCliConnector] Failed to start HTTP server: {ex.Message}", true);
                return false;
            }
        }

        static void CloseListener(HttpListener listener)
        {
            if (listener == null) return;
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
            }
        }

        static void ScheduleRetry()
        {
            s_NextStartAttemptTime = EditorApplication.timeSinceStartup + AUTO_RESTART_INTERVAL;
        }

        static void LogStartFailure(string message, bool error)
        {
            var now = EditorApplication.timeSinceStartup;
            if (s_LastFailureMessage == message && now - s_LastFailureLogTime < FAILURE_LOG_INTERVAL)
                return;

            s_LastFailureMessage = message;
            s_LastFailureLogTime = now;
            if (error) Debug.LogError(message);
            else Debug.LogWarning(message);
        }

        static void StopListener()
        {
            s_NextStartAttemptTime = 0;
            var session = s_Session;
            s_Session = null;
            s_Listener = null;
            if (session == null) return;

            s_Retiring.TryAdd(session, 0);
            Task[] requests;
            session.Cts.Cancel();
            lock (session.Gate)
                requests = session.Tasks.ToArray();
            while (s_Queue.TryDequeue(out var item))
                item.Request.Completion.TrySetResult(item.Request.CancelReply());

            // Give small 503 replies a bounded opportunity to reach clients before
            // Mono's listener.Stop tears down their sockets. No main continuations.
            try { Task.WhenAll(requests).Wait(TimeSpan.FromMilliseconds(250)); }
            catch (AggregateException) { }
            CloseListener(session.Listener);
            var tasks = new List<Task>(requests) { session.Loop };

            // All I/O awaits are context-free; command waits are cancelled above.
            // Never wait here for a handler's UnitySynchronizationContext continuation.
            var all = Task.WhenAll(tasks);
            try
            {
                if (!all.Wait(TimeSpan.FromMilliseconds(4750)))
                    Debug.LogWarning("[UnityCliConnector] Background HTTP shutdown exceeded 5 seconds; retaining generation until all I/O ends.");
            }
            catch (AggregateException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnityCliConnector] HTTP shutdown: {ex.Message}");
            }
            // This finalizer is itself tracked by the retiring generation. Dispose
            // only after *all* tasks finish, even after the bounded wait expires.
            session.Retirement = all.ContinueWith(t =>
            {
                if (t.IsFaulted) System.Diagnostics.Debug.WriteLine(t.Exception);
                session.Requests.Clear();
                session.Cts.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        static void Stop()
        {
            var port = s_Port;
            StopListener();
            Debug.Log($"[UnityCliConnector] HTTP server stopped (was port {port})");
        }

        static void ForceEditorUpdate(ListenerSession session)
        {
            s_MainContext?.Post(_ =>
            {
                if (!ReferenceEquals(s_Session, session)) return;
                try { EditorApplication.QueuePlayerLoopUpdate(); }
                catch { }
                try { UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); }
                catch { }
            }, null);
        }

        static void ProcessQueue()
        {
            foreach (var retired in s_Retiring.Keys)
                if (retired.Retirement?.IsCompleted == true)
                    s_Retiring.TryRemove(retired, out _);
            if (s_Session != null)
                lock (s_Session.Gate) s_Session.Tasks.RemoveAll(task => task.IsCompleted);
            if (s_Session?.RestartPending == true)
            {
                StopListener();
                ScheduleRetry();
            }

            if (!IsRunning && s_NextStartAttemptTime > 0 &&
                EditorApplication.timeSinceStartup >= s_NextStartAttemptTime)
            {
                Start();
            }

            while (s_Queue.TryDequeue(out var item))
                if (!item.Token.IsCancellationRequested)
                    ProcessItem(item);
        }

        static byte[] SerializeResponse(object result) =>
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result));

        // Only called by EditorApplication.update. Both async handler continuations
        // and serialization retain UnitySynchronizationContext, including job results.
        static async void ProcessItem(WorkItem item)
        {
            TaskCompletionSource<object> job = null;
            try
            {
                if (item.Command == "job_status")
                {
                    item.Request.Completion.TrySetResult(new Reply(SerializeResponse(AsyncJobManager.GetJobStatus(item.Parameters?["job_id"]?.ToString()))));
                    return;
                }
                if (item.Async)
                {
                    job = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var id = AsyncJobManager.CreateJob(item.Command, job);
                    item.Request.Completion.TrySetResult(new Reply(SerializeResponse(new SuccessResponse("job_created", new { job_id = id }))));
                }
                using var cancellation = item.Token.Register(() => job?.TrySetResult(item.Request.CancelError()));
                var result = await CommandRouter.Dispatch(item.Command, item.Parameters, item.Token, item.Request.BeginExecution);
                if (item.Token.IsCancellationRequested) return;
                if (job != null) job.TrySetResult(result);
                else item.Request.Completion.TrySetResult(new Reply(SerializeResponse(result)));
            }
            catch (Exception ex)
            {
                if (item.Token.IsCancellationRequested) return;
                var error = new ErrorResponse(ex.Message);
                if (job != null) job.TrySetResult(error);
                else item.Request.Completion.TrySetResult(new Reply(SerializeResponse(error)));
            }
        }

        static async Task ListenLoop(ListenerSession session)
        {
            var ct = session.Cts.Token;
            try
            {
                while (!ct.IsCancellationRequested && session.Listener.IsListening)
                {
                    var context = await session.Listener.GetContextAsync().ConfigureAwait(false);
                    lock (session.Gate)
                    {
                        context.Response.StatusCode = 503; // No default empty 200 during teardown.
                        if (ct.IsCancellationRequested) { context.Response.Abort(); break; }
                        var task = HandleRequest(context, session, ct);
                        session.Requests[context] = task;
                        session.Tasks.Add(task);
                        if (task.IsCompleted) session.Requests.TryRemove(context, out _);
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnityCliConnector] ListenLoop crashed: {ex.Message}");
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    session.RestartPending = true;
                    ForceEditorUpdate(session);
                }
            }
        }

        static async Task HandleRequest(HttpListenerContext context, ListenerSession session, CancellationToken ct)
        {
            var request = context.Request;
            var response = context.Response;

            var state = new RequestState();
            using var cancellation = ct.Register(() =>
            {
                state.Completion.TrySetResult(state.CancelReply());
                try { request.InputStream.Close(); } catch { }
            });
            bool written = false;
            try
            {
                response.ContentType = "application/json";

                // Block browser cross-origin requests — CLI is not subject to CORS.
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    written = true;
                    return;
                }

                var origin = request.Headers["Origin"];
                if (origin != null)
                {
                    response.StatusCode = 403;
                    var buf = Encoding.UTF8.GetBytes("{\"error\":\"Browser requests are not allowed\"}");
                    response.ContentLength64 = buf.Length;
                    await response.OutputStream.WriteAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    response.Close();
                    written = true;
                    return;
                }

                byte[] buffer;
                int status = 200;
                try
                {
                    if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/health")
                    {
                        // Immutable bytes prepared on the main thread, never a Unity object.
                        buffer = Volatile.Read(ref session.Health);
                    }
                    else if (request.HttpMethod != "POST" || request.Url.AbsolutePath != "/command")
                    {
                        buffer = SerializeResponse(new ErrorResponse($"Expected GET /health or POST /command, got {request.HttpMethod} {request.Url.AbsolutePath}"));
                        status = 400;
                    }
                    else
                    {
                        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        var json = JObject.Parse(body);
                        var command = json["command"]?.ToString();
                        var parameters = json["params"] as JObject;

                        if (string.IsNullOrEmpty(command))
                        {
                            buffer = SerializeResponse(new ErrorResponse("Missing 'command' field"));
                            status = 400;
                        }
                        else
                        {
                            var isAsync = command != "job_status" && parameters?["async"]?.Value<bool>() == true;
                            s_Queue.Enqueue(new WorkItem
                            {
                                Command = command,
                                Parameters = parameters,
                                Request = state,
                                Token = ct,
                                Async = isAsync,
                            });
                            ForceEditorUpdate(session);
                            var reply = await state.Completion.Task.ConfigureAwait(false);
                            buffer = reply.Buffer;
                            status = reply.Status;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    var reply = state.CancelReply();
                    buffer = reply.Buffer;
                    status = reply.Status;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        var reply = state.CancelReply();
                        buffer = reply.Buffer;
                        status = 503;
                    }
                    else
                    {
                        // Protocol-only errors have no user/Unity data to serialize.
                        buffer = SerializeResponse(new ErrorResponse($"Request error: {ex.Message}"));
                        status = 500;
                    }
                }

                if (ct.IsCancellationRequested && status == 200)
                {
                    var cancelled = state.CancelReply();
                    buffer = cancelled.Buffer;
                    status = 503;
                }
                response.StatusCode = status;
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                response.Close();
                written = true;
            }
            catch (Exception ex)
            {
                // Disconnects and shutdown are transport failures, not Unity errors.
                System.Diagnostics.Debug.WriteLine($"[UnityCliConnector] Request ended: {ex.Message}");
            }
            finally
            {
                if (!written)
                {
                    try { response.StatusCode = 503; } catch { }
                    try { response.Abort(); } catch { }
                }
                // Removal is part of the tracked request itself, not a new task.
                session.Requests.TryRemove(context, out _);
            }
        }
    }
}
