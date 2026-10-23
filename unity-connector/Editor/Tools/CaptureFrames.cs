using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "capture", Description = "Save every Play Mode frame of the Game view as an image. Actions: frames (start), status, stop.")]
    public static class CaptureFrames
    {
        const string Usage = "Usage: unity-cli capture <frames|status|stop|wait> [--seconds N | --frames N] [--out dir] [--width N] [--crop x,y,w,h] [--start-when expr] [--wait-timeout N] [--log expr]... [--sheet CxR] [--format jpg|png] [--video]";
        const string LastResultKey = "UnityCliConnector.CaptureFrames.LastResult";
        const int MaxSheetWidth = 4000;

        static Session s_Session;
        static object s_LastResult;

        public class Parameters
        {
            [ToolParameter("Action: start (alias frames), status, stop", Required = true)]
            public string Action { get; set; }
            [ToolParameter("Capture length in real seconds from the first captured frame (default 2)")]
            public float Seconds { get; set; }
            [ToolParameter("Capture exactly this many player frames instead of --seconds")]
            public int Frames { get; set; }
            [ToolParameter("Output directory on the Editor host; relative to project root. Default: Temp/CaptureFrames/<timestamp>")]
            public string Out { get; set; }
            [ToolParameter("Saved image width (default 640, or the crop width with crop). Never upscales.")]
            public int Width { get; set; }
            [ToolParameter("Crop x,y,w,h in Game view render pixels, origin top-left. Saved at native size unless width is smaller.")]
            public string Crop { get; set; }
            [ToolParameter("C# bool expression evaluated every frame; capture starts on the first frame it is true")]
            public string StartWhen { get; set; }
            [ToolParameter("Seconds to wait for start_when before failing (default 60)")]
            public float WaitTimeout { get; set; }
            [ToolParameter("C# expressions logged per captured frame into frames.csv (array or single string)")]
            public string[] Log { get; set; }
            [ToolParameter("Extra using directives for start_when/log expressions (comma-separated)")]
            public string[] Usings { get; set; }
            [ToolParameter("Contact sheets CxR (e.g. 6x6) covering every saved frame, labelled with frameCount")]
            public string Sheet { get; set; }
            [ToolParameter("Image format: jpg (default) or png")]
            public string Format { get; set; }
            [ToolParameter("JPG quality 1-100 (default 90)")]
            public int Quality { get; set; }
            [ToolParameter("Also write frames.mp4 with one video frame per saved game frame (needs ffmpeg)")]
            public bool Video { get; set; }
            [ToolParameter("Video frame rate (default 60). Playback speed only; frames are not retimed.")]
            public int VideoFps { get; set; }
            [ToolParameter("ffmpeg executable (default: ffmpeg on PATH)")]
            public string Ffmpeg { get; set; }
            [ToolParameter("Delete previous capture outputs in out before starting")]
            public bool Overwrite { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            var action = (p.Get("action") ?? (p.GetRaw("args") as JArray)?.First?.ToString() ?? "status").ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "start":
                    case "frames":
                        return Start(p);
                    case "status":
                    case "wait":
                        return Status();
                    case "stop":
                        if (s_Session == null)
                            return new ErrorResponse("No capture is running.");
                        s_Session.RequestStop();
                        return s_Session.Snapshot();
                    default:
                        return new ErrorResponse(Usage);
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"capture: {ex.GetBaseException().Message}");
            }
        }

        static object Status()
        {
            if (s_Session != null)
                return s_Session.Snapshot();
            if (s_LastResult != null)
                return s_LastResult;
            // A domain reload drops the static result; the interrupted session left it here.
            var json = SessionState.GetString(LastResultKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                var token = JObject.Parse(json);
                return token.Value<bool>("success")
                    ? new SuccessResponse(token.Value<string>("message"), token["data"])
                    : new ErrorResponse(token.Value<string>("message"), token["data"]);
            }
            return new SuccessResponse("idle", new { state = "idle" });
        }

        static object Start(ToolParams p)
        {
            if (s_Session != null)
                return new ErrorResponse("A capture is already running. Use 'capture stop' or 'capture status'.", s_Session.SnapshotData());
            // Outside Play Mode the capture is armed and starts with the first
            // player frame. Entering Play Mode with a domain reload ends it.
            bool keepsDomain = EditorSettings.enterPlayModeOptionsEnabled
                && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0;
            if (!EditorApplication.isPlaying && !keepsDomain)
                return new ErrorResponse("capture frames outside Play Mode needs Enter Play Mode Options with domain reload disabled. Enter Play Mode first ('unity-cli editor play --wait').");

            var config = Config.Parse(p);
            var gameView = EditorScreenshot.FindGameView();
            if (gameView == null)
                return new ErrorResponse("No Game view is open.");
            EditorScreenshot.EnsureGameViewVisible(gameView, false);
            var target = EditorScreenshot.GetGameTarget(gameView);
            if (target == null)
                return new ErrorResponse("The Game view has no render target yet. Keep it visible and retry.");
            if (config.Crop.HasValue)
            {
                var c = config.Crop.Value;
                if (c.x < 0 || c.y < 0 || c.width <= 0 || c.height <= 0 || c.xMax > target.width || c.yMax > target.height)
                    return new ErrorResponse($"--crop {c.x},{c.y},{c.width},{c.height} is outside the Game view render size {target.width}x{target.height}.");
            }

            PrepareOutput(config);
            var expressions = Expressions.Compile(config);

            s_LastResult = null;
            SessionState.EraseString(LastResultKey);
            s_Session = new Session(config, expressions, gameView);
            s_Session.Begin();
            return s_Session.Snapshot();
        }

        static readonly Regex OutputPattern = new Regex(@"^(f\d+\.(jpg|png)|sheet_\d+\.jpg|frames\.csv|frames\.mp4|capture\.json)$", RegexOptions.IgnoreCase);

        static void PrepareOutput(Config config)
        {
            Directory.CreateDirectory(config.Out);
            var previous = Directory.GetFiles(config.Out).Where(f => OutputPattern.IsMatch(Path.GetFileName(f))).ToArray();
            if (previous.Length == 0)
                return;
            if (!config.Overwrite)
                throw new InvalidOperationException($"{config.Out} already holds {previous.Length} capture files. Pass --overwrite or use a new --out.");
            foreach (var f in previous)
                File.Delete(f);
        }

        static void Finished(Session session, object result)
        {
            if (s_Session != session)
                return;
            s_Session = null;
            s_LastResult = result;
            try { SessionState.SetString(LastResultKey, JsonConvert.SerializeObject(result)); } catch { }
        }

        sealed class Config
        {
            public string Out;
            public double Seconds;
            public int Frames;
            public int? Width;
            public RectInt? Crop;
            public string StartWhen;
            public double WaitTimeout;
            public string[] Logs;
            public string[] Usings;
            public int SheetCols, SheetRows;
            public bool Png;
            public int Quality;
            public bool Video;
            public int VideoFps;
            public string Ffmpeg;
            public bool Overwrite;

            public string Extension => Png ? "png" : "jpg";

            public static Config Parse(ToolParams p)
            {
                var c = new Config();
                var root = Path.GetDirectoryName(Application.dataPath);
                var output = p.Get("out") ?? Path.Combine("Temp", "CaptureFrames", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                c.Out = Path.GetFullPath(Path.IsPathRooted(output) ? output : Path.Combine(root, output));

                c.Frames = p.GetInt("frames") ?? 0;
                c.Seconds = p.GetFloat("seconds") ?? 2f;
                if (c.Frames < 0 || c.Seconds <= 0)
                    throw new ArgumentException("--seconds and --frames must be positive.");
                if (p.Get("frames") != null && p.Get("seconds") != null)
                    throw new ArgumentException("Pass --seconds or --frames, not both.");

                c.Width = p.GetInt("width");
                if (c.Width.HasValue && c.Width.Value <= 0)
                    throw new ArgumentException("--width must be positive.");
                var crop = p.Get("crop");
                if (!string.IsNullOrEmpty(crop))
                {
                    var parts = crop.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : int.MinValue).ToArray();
                    if (parts.Length != 4 || parts.Any(v => v == int.MinValue))
                        throw new ArgumentException("--crop must be x,y,w,h in integer pixels.");
                    c.Crop = new RectInt(parts[0], parts[1], parts[2], parts[3]);
                }

                c.StartWhen = p.Get("start_when");
                c.WaitTimeout = p.GetFloat("wait_timeout") ?? 60f;
                c.Logs = Strings(p.GetRaw("log"));
                c.Usings = Strings(p.GetRaw("usings")).SelectMany(u => u.Split(',')).ToArray();

                var sheet = p.Get("sheet");
                if (!string.IsNullOrEmpty(sheet) && !sheet.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    if (sheet.Equals("true", StringComparison.OrdinalIgnoreCase))
                        sheet = "6x6";
                    var m = Regex.Match(sheet, @"^(\d+)x(\d+)$", RegexOptions.IgnoreCase);
                    if (!m.Success || int.Parse(m.Groups[1].Value) <= 0 || int.Parse(m.Groups[2].Value) <= 0)
                        throw new ArgumentException("--sheet must be CxR, e.g. 6x6.");
                    c.SheetCols = int.Parse(m.Groups[1].Value);
                    c.SheetRows = int.Parse(m.Groups[2].Value);
                }

                var format = (p.Get("format") ?? "jpg").ToLowerInvariant();
                if (format != "jpg" && format != "jpeg" && format != "png")
                    throw new ArgumentException("--format must be jpg or png.");
                c.Png = format == "png";
                c.Quality = Mathf.Clamp(p.GetInt("quality") ?? 90, 1, 100);
                c.Video = p.GetBool("video");
                c.VideoFps = Math.Max(1, p.GetInt("video_fps") ?? 60);
                c.Ffmpeg = p.Get("ffmpeg") ?? "ffmpeg";
                c.Overwrite = p.GetBool("overwrite");
                return c;
            }

            static string[] Strings(JToken token)
            {
                if (token == null || token.Type == JTokenType.Null)
                    return Array.Empty<string>();
                if (token.Type == JTokenType.Array)
                    return token.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                var s = token.ToString();
                return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : new[] { s };
            }
        }

        sealed class Expressions
        {
            public Func<bool> StartWhen;
            public Func<object>[] Logs = Array.Empty<Func<object>>();

            public static Expressions Compile(Config config)
            {
                var result = new Expressions();
                if (string.IsNullOrEmpty(config.StartWhen) && config.Logs.Length == 0)
                    return result;

                var sb = new StringBuilder();
                sb.Append(ExecuteCsharp.DefaultUsingLines(config.Usings));
                sb.AppendLine("public static class __CliCapture {");
                if (!string.IsNullOrEmpty(config.StartWhen))
                    sb.AppendLine($"  public static bool StartWhen() {{\n{Body(config.StartWhen)}\n  }}");
                for (int i = 0; i < config.Logs.Length; i++)
                    sb.AppendLine($"  public static object Log{i}() {{\n{Body(config.Logs[i])}\n  }}");
                sb.AppendLine("}");

                var assembly = ExecuteCsharp.CompileSource(sb.ToString(), out var error);
                if (assembly == null)
                    throw new ArgumentException($"start-when/log expression failed to compile: {error}");
                var type = assembly.GetType("__CliCapture");
                if (!string.IsNullOrEmpty(config.StartWhen))
                    result.StartWhen = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), type.GetMethod("StartWhen"));
                result.Logs = Enumerable.Range(0, config.Logs.Length)
                    .Select(i => (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), type.GetMethod($"Log{i}")))
                    .ToArray();
                return result;
            }

            // A bare expression is returned; statements with their own return are kept.
            static string Body(string code)
            {
                var trimmed = code.Trim().TrimEnd(';');
                return Regex.IsMatch(code, @"\breturn\b") ? code : $"return ({trimmed});";
            }
        }

        sealed class Row
        {
            public int Frame;
            public double Realtime;
            public float UnscaledDeltaTime;
            public string[] Values;
            public string Status;
            public FrameData Data;
        }

        sealed class FrameData
        {
            public int Frame;
            public int Width, Height;
            public byte[] Pixels; // RGBA32, bottom row first
            public bool GapBefore;
            public string Path;
            public volatile bool Saved;
            public string Error;
        }

        struct Plan
        {
            public int SourceWidth, SourceHeight;
            public Vector2 Scale, Offset;
            public int CropWidth, CropHeight;
            public int OutWidth, OutHeight;
        }

        enum State { Waiting, Capturing, Finishing, Done }

        sealed class Session
        {
            const int MaxInFlight = 12;
            const long MaxPendingBytes = 512L * 1024 * 1024;

            readonly Config config;
            readonly Expressions expressions;
            EditorWindow gameView;
            CancellationTokenSource runner;
            bool runnerAlive, inPlayMode;
            volatile State state = State.Waiting;
            Plan plan;
            bool hasPlan;

            double armedAt, startTime = -1, finishingSince;
            int startFrame = -1, lastFrame = -1, ticks, inFlight;
            int skippedBacklog, skippedNoTarget, skippedReadback, skippedGaps, skippedUnfinished;
            int savedCount, encodeFailed;
            long pendingBytes;
            int startWhenErrors, logErrors;
            string startWhenError, logError;
            string stopReason;
            bool failed;
            string failMessage;
            bool detached;

            readonly List<Row> rows = new();
            readonly List<FrameData> ordered = new();
            readonly BlockingCollection<FrameData> encodeQueue = new();
            readonly BlockingCollection<FrameData> sheetQueue = new();
            readonly List<Thread> workers = new();
            readonly List<string> sheets = new();
            Task finishTask;
            string videoPath, videoError;

            public Session(Config config, Expressions expressions, EditorWindow gameView)
            {
                this.config = config;
                this.expressions = expressions;
                this.gameView = gameView;
            }

            public void Begin()
            {
                armedAt = Time.realtimeSinceStartupAsDouble;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                EditorApplication.quitting += OnBeforeAssemblyReload;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                EditorApplication.update += Update;

                int encoders = Mathf.Clamp(Environment.ProcessorCount / 2, 1, 4);
                for (int i = 0; i < encoders; i++)
                    StartWorker($"unity-cli capture encode {i}", EncodeLoop);
                if (config.SheetCols > 0)
                    StartWorker("unity-cli capture sheet", SheetLoop);

                if (expressions.StartWhen == null)
                    state = State.Capturing;
                inPlayMode = EditorApplication.isPlaying;
                if (inPlayMode)
                    CreateRunner();
            }

            void StartWorker(string name, ThreadStart body)
            {
                var thread = new Thread(body) { IsBackground = true, Name = name };
                workers.Add(thread);
                thread.Start();
            }

            // Awaitable.EndOfFrameAsync resumes where WaitForEndOfFrame does: after
            // the Game view has rendered the player frame. Editor assemblies
            // cannot add MonoBehaviours in Play Mode, so no coroutine host is used.
            void CreateRunner()
            {
                runner = new CancellationTokenSource();
                RunEndOfFrameLoop(runner.Token);
            }

            async void RunEndOfFrameLoop(CancellationToken token)
            {
                runnerAlive = true;
                try
                {
                    while (!token.IsCancellationRequested && EditorApplication.isPlaying)
                    {
                        await Awaitable.EndOfFrameAsync(token);
                        if (token.IsCancellationRequested)
                            break;
                        try { OnEndOfFrame(); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Debug.LogException(e); }
                finally
                {
                    if (!token.IsCancellationRequested)
                        runnerAlive = false;
                }
            }

            void DestroyRunner()
            {
                runner?.Cancel();
                runner?.Dispose();
                runner = null;
                runnerAlive = false;
            }

            public void RequestStop() => BeginFinish(state == State.Waiting ? "stopped before start" : "stopped", false);

            void OnPlayModeChanged(PlayModeStateChange change)
            {
                if (change == PlayModeStateChange.EnteredPlayMode)
                    inPlayMode = true;
                if (change == PlayModeStateChange.ExitingPlayMode)
                    BeginFinish("play mode exited", state == State.Waiting);
            }

            void Fail(string message)
            {
                failMessage = message;
                BeginFinish("failed", true);
            }

            // Runs after the Game view has rendered this player frame.
            void OnEndOfFrame()
            {
                if (state != State.Waiting && state != State.Capturing)
                    return;
                int frame = Time.frameCount;
                double now = Time.realtimeSinceStartupAsDouble;

                if (state == State.Waiting)
                {
                    bool start = false;
                    try { start = expressions.StartWhen(); }
                    catch (Exception e)
                    {
                        startWhenErrors++;
                        startWhenError ??= Describe(e);
                    }
                    if (!start)
                        return;
                    state = State.Capturing;
                }

                if (startTime < 0)
                {
                    startTime = now;
                    startFrame = frame;
                }
                if (config.Frames > 0 ? ticks >= config.Frames : now - startTime >= config.Seconds)
                {
                    BeginFinish(null, false);
                    return;
                }

                bool gapBefore = lastFrame >= 0 && frame > lastFrame + 1;
                if (gapBefore)
                    skippedGaps += frame - lastFrame - 1;
                lastFrame = frame;
                ticks++;

                var row = new Row
                {
                    Frame = frame,
                    Realtime = now,
                    UnscaledDeltaTime = Time.unscaledDeltaTime,
                    Values = EvaluateLogs(),
                };
                rows.Add(row);
                row.Status = Issue(row, gapBefore);
            }

            string[] EvaluateLogs()
            {
                var values = new string[expressions.Logs.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    try { values[i] = Format(expressions.Logs[i]()); }
                    catch (Exception e)
                    {
                        logErrors++;
                        logError ??= Describe(e);
                        values[i] = "ERR: " + e.GetType().Name;
                    }
                }
                return values;
            }

            string Issue(Row row, bool gapBefore)
            {
                if (inFlight >= MaxInFlight || Interlocked.Read(ref pendingBytes) > MaxPendingBytes)
                {
                    skippedBacklog++;
                    return "skipped: backlog";
                }
                if (gameView == null)
                    gameView = EditorScreenshot.FindGameView();
                var source = EditorScreenshot.GetGameTarget(gameView);
                if (source == null)
                {
                    skippedNoTarget++;
                    return "skipped: no game view target";
                }
                if (!hasPlan || plan.SourceWidth != source.width || plan.SourceHeight != source.height)
                {
                    if (!MakePlan(source.width, source.height, out var error))
                    {
                        Fail(error);
                        return "skipped: " + error;
                    }
                }

                var previous = RenderTexture.active;
                RenderTexture current = null;
                try
                {
                    // Copy (and crop) at full resolution, flipped upright like the
                    // screenshot tool, then halve repeatedly so the final bilinear
                    // step never skips source pixels.
                    current = RenderTexture.GetTemporary(plan.CropWidth, plan.CropHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(source, current, plan.Scale, plan.Offset);
                    while (current.width >= plan.OutWidth * 2 && current.height >= 2)
                        current = Downscale(current, current.width / 2, current.height / 2);
                    if (current.width != plan.OutWidth || current.height != plan.OutHeight)
                        current = Downscale(current, plan.OutWidth, plan.OutHeight);

                    int width = current.width, height = current.height;
                    AsyncGPUReadback.Request(current, 0, TextureFormat.RGBA32, request => OnReadback(request, row, gapBefore, width, height));
                    inFlight++;
                    return "pending";
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (current != null)
                        RenderTexture.ReleaseTemporary(current);
                }
            }

            static RenderTexture Downscale(RenderTexture source, int width, int height)
            {
                var next = RenderTexture.GetTemporary(Math.Max(1, width), Math.Max(1, height), 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, next);
                RenderTexture.ReleaseTemporary(source);
                return next;
            }

            bool MakePlan(int width, int height, out string error)
            {
                error = null;
                var crop = config.Crop ?? new RectInt(0, 0, width, height);
                if (crop.x < 0 || crop.y < 0 || crop.width <= 0 || crop.height <= 0 || crop.xMax > width || crop.yMax > height)
                {
                    error = $"crop {crop.x},{crop.y},{crop.width},{crop.height} is outside the Game view render size {width}x{height}";
                    return false;
                }

                // Crop is given top-left; UV origin is bottom-left.
                float u0 = (float)crop.x / width, su = (float)crop.width / width;
                float v0 = 1f - (float)crop.yMax / height, sv = (float)crop.height / height;
                plan.Scale = SystemInfo.graphicsUVStartsAtTop ? new Vector2(su, -sv) : new Vector2(su, sv);
                plan.Offset = SystemInfo.graphicsUVStartsAtTop ? new Vector2(u0, 1f - v0) : new Vector2(u0, v0);
                plan.SourceWidth = width;
                plan.SourceHeight = height;
                plan.CropWidth = crop.width;
                plan.CropHeight = crop.height;
                int defaultWidth = config.Crop.HasValue ? crop.width : 640;
                plan.OutWidth = Math.Min(config.Width ?? defaultWidth, crop.width);
                plan.OutHeight = Math.Max(1, (int)Math.Round((double)plan.OutWidth * crop.height / crop.width));
                hasPlan = true;
                return true;
            }

            void OnReadback(AsyncGPUReadbackRequest request, Row row, bool gapBefore, int width, int height)
            {
                inFlight--;
                if (detached)
                    return;
                if (request.hasError)
                {
                    skippedReadback++;
                    row.Status = "skipped: readback error";
                    return;
                }

                var data = new FrameData
                {
                    Frame = row.Frame,
                    Width = width,
                    Height = height,
                    Pixels = request.GetData<byte>().ToArray(),
                    GapBefore = gapBefore,
                    Path = Path.Combine(config.Out, $"f{row.Frame:D7}.{config.Extension}"),
                };
                row.Data = data;
                row.Status = "queued";
                ordered.Add(data);
                Interlocked.Add(ref pendingBytes, data.Pixels.Length);
                encodeQueue.Add(data);
                if (config.SheetCols > 0)
                    sheetQueue.Add(data);
            }

            void EncodeLoop()
            {
                foreach (var data in encodeQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        byte[] encoded;
                        if (config.Png)
                        {
                            // The view target may carry partial alpha; images should be opaque.
                            var pixels = (byte[])data.Pixels.Clone();
                            for (int i = 3; i < pixels.Length; i += 4)
                                pixels[i] = 255;
                            encoded = ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)data.Width, (uint)data.Height);
                        }
                        else
                        {
                            encoded = ImageConversion.EncodeArrayToJPG(data.Pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)data.Width, (uint)data.Height, 0, config.Quality);
                        }
                        File.WriteAllBytes(data.Path, encoded);
                        data.Saved = true;
                        Interlocked.Increment(ref savedCount);
                    }
                    catch (Exception e)
                    {
                        data.Error = e.Message;
                        Interlocked.Increment(ref encodeFailed);
                    }
                    finally
                    {
                        Interlocked.Add(ref pendingBytes, -data.Pixels.Length);
                    }
                }
            }

            void SheetLoop()
            {
                var builder = new SheetBuilder(config.SheetCols, config.SheetRows, config.Out);
                foreach (var data in sheetQueue.GetConsumingEnumerable())
                {
                    try { builder.Add(data); }
                    catch (Exception e) { Debug.LogException(e); }
                }
                try { builder.Flush(); }
                catch (Exception e) { Debug.LogException(e); }
                lock (sheets) sheets.AddRange(builder.Written);
            }

            void BeginFinish(string reason, bool asFailure)
            {
                if (state == State.Finishing || state == State.Done)
                    return;
                state = State.Finishing;
                stopReason = reason;
                failed |= asFailure;
                finishingSince = Time.realtimeSinceStartupAsDouble;
                DestroyRunner();
            }

            void Update()
            {
                try
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    switch (state)
                    {
                        case State.Waiting:
                            if (now - armedAt >= config.WaitTimeout)
                            {
                                failMessage = $"start-when stayed false for {config.WaitTimeout:0.#}s (--wait-timeout)."
                                    + (startWhenError != null ? $" It threw {startWhenErrors}x: {startWhenError}" : "");
                                BeginFinish("wait timeout", true);
                            }
                            else if (!runnerAlive && inPlayMode && EditorApplication.isPlaying)
                                CreateRunner();
                            return;
                        case State.Capturing:
                            // A paused or stalled player loop must not hold the capture open.
                            if (config.Frames == 0 && startTime >= 0 && now - startTime >= config.Seconds + 2)
                                BeginFinish(null, false);
                            else if (!runnerAlive && inPlayMode && EditorApplication.isPlaying)
                                CreateRunner();
                            return;
                        case State.Finishing:
                            if (finishTask == null)
                            {
                                if (inFlight > 0 && now - finishingSince < 5)
                                    return;
                                Detach();
                                finishTask = Task.Run(FinishInBackground);
                            }
                            else if (finishTask.IsCompleted)
                            {
                                Complete(BuildResult());
                            }
                            return;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    failMessage = Describe(e);
                    failed = true;
                    if (state != State.Done)
                    {
                        Detach();
                        Complete(BuildResult());
                    }
                }
            }

            // Stops accepting readbacks; frames still in flight count as skipped.
            void Detach()
            {
                if (detached)
                    return;
                detached = true;
                skippedUnfinished += Math.Max(0, inFlight);
                foreach (var row in rows)
                    if (row.Status == "pending")
                        row.Status = "skipped: readback not finished";
                encodeQueue.CompleteAdding();
                sheetQueue.CompleteAdding();
            }

            void FinishInBackground()
            {
                foreach (var worker in workers)
                    worker.Join();
                WriteCsv();
                if (config.Video && ordered.Count > 0)
                    WriteVideo();
            }

            void OnBeforeAssemblyReload()
            {
                if (state == State.Done)
                    return;
                if (state != State.Finishing)
                {
                    stopReason = "interrupted by domain reload or Editor quit";
                    failed |= state == State.Waiting;
                    state = State.Finishing;
                }
                DestroyRunner();
                try { AsyncGPUReadback.WaitAllRequests(); } catch { }
                Detach();
                foreach (var worker in workers)
                    worker.Join(3000);
                if (finishTask == null)
                    try { WriteCsv(); } catch { }
                Complete(BuildResult());
            }

            void Complete(object result)
            {
                state = State.Done;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                EditorApplication.quitting -= OnBeforeAssemblyReload;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                EditorApplication.update -= Update;
                DestroyRunner();
                try { File.WriteAllText(Path.Combine(config.Out, "capture.json"), JsonConvert.SerializeObject(result, Formatting.Indented)); } catch { }
                Finished(this, result);
            }

            void WriteCsv()
            {
                var sb = new StringBuilder();
                sb.Append("frame,t,realtime,unscaled_delta_time,status,file");
                foreach (var expr in config.Logs)
                    sb.Append(',').Append(Csv(expr));
                sb.AppendLine();
                foreach (var row in rows)
                {
                    var status = row.Data == null ? row.Status : row.Data.Saved ? "saved" : row.Data.Error != null ? "skipped: encode error" : "skipped: not encoded";
                    sb.Append(row.Frame.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append((row.Realtime - startTime).ToString("0.000000", CultureInfo.InvariantCulture)).Append(',')
                        .Append(row.Realtime.ToString("0.000000", CultureInfo.InvariantCulture)).Append(',')
                        .Append(row.UnscaledDeltaTime.ToString("0.000000", CultureInfo.InvariantCulture)).Append(',')
                        .Append(Csv(status)).Append(',')
                        .Append(row.Data?.Saved == true ? Path.GetFileName(row.Data.Path) : "");
                    foreach (var value in row.Values)
                        sb.Append(',').Append(Csv(value));
                    sb.AppendLine();
                }
                File.WriteAllText(Path.Combine(config.Out, "frames.csv"), sb.ToString(), new UTF8Encoding(false));
            }

            // One video frame per saved game frame, in frame order; never retimed.
            void WriteVideo()
            {
                var output = Path.Combine(config.Out, "frames.mp4");
                var psi = new ProcessStartInfo
                {
                    FileName = config.Ffmpeg,
                    Arguments = $"-y -hide_banner -loglevel error -f image2pipe -framerate {config.VideoFps} -c:v {(config.Png ? "png" : "mjpeg")} -i - " +
                        "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" -fps_mode passthrough -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p " +
                        $"\"{output}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                try
                {
                    using var process = Process.Start(psi);
                    var stderr = process.StandardError.ReadToEndAsync();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    using (var input = process.StandardInput.BaseStream)
                    {
                        foreach (var data in ordered)
                        {
                            if (!data.Saved)
                                continue;
                            var bytes = File.ReadAllBytes(data.Path);
                            input.Write(bytes, 0, bytes.Length);
                        }
                    }
                    if (!process.WaitForExit(300000))
                    {
                        try { process.Kill(); } catch { }
                        videoError = "ffmpeg did not finish within 300s";
                        return;
                    }
                    if (process.ExitCode != 0)
                    {
                        videoError = $"ffmpeg exited {process.ExitCode}: {stderr.Result.Trim()}";
                        return;
                    }
                    videoPath = output;
                }
                catch (Exception e)
                {
                    videoError = $"ffmpeg failed ({config.Ffmpeg}): {e.Message}";
                }
            }

            public object Snapshot()
            {
                var data = SnapshotData();
                return new SuccessResponse(data.Value<string>("state"), data);
            }

            public JObject SnapshotData() => JObject.FromObject(new
            {
                state = state.ToString().ToLowerInvariant(),
                out_dir = config.Out,
                start_frame = startFrame >= 0 ? startFrame : (int?)null,
                frames_seen = ticks,
                frames_saved = Volatile.Read(ref savedCount),
                frames_skipped = SkippedTotal,
                waiting_seconds = state == State.Waiting ? Time.realtimeSinceStartupAsDouble - armedAt : (double?)null,
                start_when_error = startWhenError,
            });

            int SkippedTotal => skippedBacklog + skippedNoTarget + skippedReadback + skippedGaps + skippedUnfinished
                + Volatile.Read(ref encodeFailed);

            object BuildResult()
            {
                var first = ordered.FirstOrDefault();
                var saved = ordered.Where(d => d.Saved).ToList();
                string[] sheetList;
                lock (sheets) sheetList = sheets.ToArray();
                var data = new
                {
                    state = "done",
                    out_dir = config.Out,
                    files = saved.Count > 0 ? $"f<frameCount:7 digits>.{config.Extension}" : null,
                    csv = File.Exists(Path.Combine(config.Out, "frames.csv")) ? Path.Combine(config.Out, "frames.csv") : null,
                    sheets = sheetList,
                    video = videoPath,
                    video_error = videoError,
                    frames_saved = saved.Count,
                    frames_skipped = SkippedTotal,
                    skipped = new
                    {
                        frame_gaps = skippedGaps,
                        backlog = skippedBacklog,
                        no_target = skippedNoTarget,
                        readback_error = skippedReadback,
                        readback_unfinished = skippedUnfinished,
                        encode_error = Volatile.Read(ref encodeFailed),
                    },
                    frames_seen = ticks,
                    start_frame = startFrame >= 0 ? startFrame : (int?)null,
                    first_saved_frame = saved.FirstOrDefault()?.Frame,
                    last_saved_frame = saved.LastOrDefault()?.Frame,
                    duration_seconds = rows.Count > 1 ? Math.Round(rows[rows.Count - 1].Realtime - rows[0].Realtime, 4) : 0,
                    width = first?.Width,
                    height = first?.Height,
                    source_width = hasPlan ? plan.SourceWidth : (int?)null,
                    source_height = hasPlan ? plan.SourceHeight : (int?)null,
                    crop = config.Crop.HasValue ? $"{config.Crop.Value.x},{config.Crop.Value.y},{config.Crop.Value.width},{config.Crop.Value.height}" : null,
                    stop_reason = stopReason,
                    start_when_error = startWhenError,
                    start_when_errors = startWhenErrors,
                    log_error = logError,
                    log_errors = logErrors,
                };
                var message = failMessage ?? (failed ? $"Capture ended without frames: {stopReason}." : $"Saved {saved.Count} frames to {config.Out}");
                return failed ? new ErrorResponse(message, data) : new SuccessResponse(message, data);
            }
        }

        // Pages every saved frame, in order, onto CxR contact sheets no wider
        // than MaxSheetWidth. Each cell is labelled with its frameCount; a red
        // label marks a cell whose preceding frames were not captured.
        sealed class SheetBuilder
        {
            const int Gap = 2;
            readonly int cols, rows;
            readonly string dir;
            int cellWidth, cellHeight, index, page;
            byte[] canvas;
            public readonly List<string> Written = new();

            public SheetBuilder(int cols, int rows, string dir)
            {
                this.cols = cols;
                this.rows = rows;
                this.dir = dir;
            }

            int SheetWidth => cols * cellWidth + (cols - 1) * Gap;
            int SheetHeight(int usedRows) => usedRows * cellHeight + (usedRows - 1) * Gap;

            public void Add(FrameData data)
            {
                if (cellWidth == 0)
                {
                    cellWidth = Math.Max(16, Math.Min(data.Width, (MaxSheetWidth - (cols - 1) * Gap) / cols));
                    cellHeight = Math.Max(9, (int)Math.Round((double)cellWidth * data.Height / data.Width));
                }
                if (canvas == null)
                {
                    canvas = new byte[SheetWidth * SheetHeight(rows) * 4];
                    for (int i = 0; i < canvas.Length; i += 4)
                    {
                        canvas[i] = canvas[i + 1] = canvas[i + 2] = 40;
                        canvas[i + 3] = 255;
                    }
                }

                int col = index % cols, row = index / cols;
                Paste(data, col * (cellWidth + Gap), row * (cellHeight + Gap));
                index++;
                if (index == cols * rows)
                    Flush();
            }

            // Area-averages the frame into the cell. Rows are stored bottom-up.
            void Paste(FrameData data, int left, int top)
            {
                int width = SheetWidth, height = SheetHeight(rows);
                var src = data.Pixels;
                for (int y = 0; y < cellHeight; y++)
                {
                    int sy0 = y * data.Height / cellHeight, sy1 = Math.Max(sy0 + 1, (y + 1) * data.Height / cellHeight);
                    int dstRow = height - 1 - (top + y);
                    for (int x = 0; x < cellWidth; x++)
                    {
                        int sx0 = x * data.Width / cellWidth, sx1 = Math.Max(sx0 + 1, (x + 1) * data.Width / cellWidth);
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int sy = sy0; sy < sy1; sy++)
                        {
                            int srcRow = data.Height - 1 - sy;
                            for (int sx = sx0; sx < sx1; sx++)
                            {
                                int i = (srcRow * data.Width + sx) * 4;
                                r += src[i];
                                g += src[i + 1];
                                b += src[i + 2];
                                n++;
                            }
                        }
                        int o = (dstRow * width + left + x) * 4;
                        canvas[o] = (byte)(r / n);
                        canvas[o + 1] = (byte)(g / n);
                        canvas[o + 2] = (byte)(b / n);
                    }
                }
                DrawLabel(data.Frame.ToString(CultureInfo.InvariantCulture), left, top, data.GapBefore);
            }

            // 3x5 digit glyphs, rows top to bottom, 3 bits per row.
            static readonly int[] Digits = { 0x7B6F, 0x2C97, 0x73E7, 0x73CF, 0x5BC9, 0x79CF, 0x79EF, 0x7249, 0x7BEF, 0x7BCF };

            void DrawLabel(string text, int left, int top, bool alert)
            {
                int scale = Mathf.Clamp(cellWidth / 100, 3, 6);
                int pad = scale;
                int boxWidth = Math.Min(cellWidth, text.Length * 4 * scale - scale + pad * 2);
                int boxHeight = Math.Min(cellHeight, 5 * scale + pad * 2);
                for (int y = 0; y < boxHeight; y++)
                    for (int x = 0; x < boxWidth; x++)
                        SetPixel(left + x, top + y, alert ? (byte)200 : (byte)0, 0, 0);
                for (int c = 0; c < text.Length; c++)
                {
                    int glyph = Digits[text[c] - '0'];
                    for (int gy = 0; gy < 5; gy++)
                        for (int gx = 0; gx < 3; gx++)
                        {
                            if ((glyph >> (14 - (gy * 3 + gx)) & 1) == 0)
                                continue;
                            for (int sy = 0; sy < scale; sy++)
                                for (int sx = 0; sx < scale; sx++)
                                {
                                    int x = pad + (c * 4 + gx) * scale + sx, y = pad + gy * scale + sy;
                                    if (x < boxWidth && y < boxHeight)
                                        SetPixel(left + x, top + y, 255, 255, 255);
                                }
                        }
                }
            }

            void SetPixel(int x, int y, byte r, byte g, byte b)
            {
                int o = ((SheetHeight(rows) - 1 - y) * SheetWidth + x) * 4;
                canvas[o] = r;
                canvas[o + 1] = g;
                canvas[o + 2] = b;
            }

            public void Flush()
            {
                if (canvas == null || index == 0)
                    return;
                int usedRows = (index + cols - 1) / cols;
                int width = SheetWidth, fullHeight = SheetHeight(rows), height = SheetHeight(usedRows);
                // Rows are bottom-up, so the top usedRows of the sheet are the buffer's tail.
                var pixels = canvas;
                if (height != fullHeight)
                {
                    pixels = new byte[width * height * 4];
                    Buffer.BlockCopy(canvas, (fullHeight - height) * width * 4, pixels, 0, pixels.Length);
                }
                var path = Path.Combine(dir, $"sheet_{page:D3}.jpg");
                File.WriteAllBytes(path, ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height, 0, 85));
                Written.Add(path);
                page++;
                index = 0;
                canvas = null;
            }
        }

        static string Format(object value)
        {
            switch (value)
            {
                case null: return "";
                case bool b: return b ? "true" : "false";
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        static string Describe(Exception e)
        {
            var inner = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
            return $"{inner.GetType().Name}: {inner.Message}";
        }

        static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
