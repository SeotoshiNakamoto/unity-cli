using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "framedebug", Description = "Capture Frame Debugger events as JSON. Actions: enable, disable, status, dump. Events are not GPU draws; no GPU timings.")]
    public static class ManageFrameDebugger
    {
        const string Usage = "Usage: unity-cli framedebug <enable|disable|status|dump> [--output <path>] [--max-events N] [--capture-timeout seconds] [--async]";
        static Capture s_Capture;

        public class Parameters
        {
            [ToolParameter("Action: enable, disable, status, dump", Required = true)]
            public string Action { get; set; }
            [ToolParameter("JSON output path on the Editor host; relative to project root. Default: Temp/FrameDebugger/<timestamp>.json")]
            public string Output { get; set; }
            [ToolParameter("Maximum events; omit for all. Must be positive.")]
            public int MaxEvents { get; set; }
            [ToolParameter("Overall capture timeout in seconds (default 90). Partial dumps are saved on timeout.")]
            public int CaptureTimeout { get; set; }
        }

        // Task<object> is awaited by CommandRouter. HttpServer's --async transport
        // already registers this task with AsyncJobManager; no second job is needed.
        public static Task<object> HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters);
            var action = (p.Get("action") ?? (p.GetRaw("args") as JArray)?.First?.ToString())?.ToLowerInvariant();
            if (!new[] { "enable", "disable", "status", "dump" }.Contains(action))
                return Task.FromResult<object>(new ErrorResponse(Usage));
            try
            {
                var api = new FrameDebuggerApi();
                if (action == "status")
                    return Task.FromResult<object>(new SuccessResponse("Frame Debugger status", new
                    {
                        enabled = api.Enabled, count = api.Count, limit = api.Limit,
                        eventsHash = api.Hash, capturing = s_Capture != null,
                        unityVersion = Application.unityVersion, api = api.Describe()
                    }));
                if (s_Capture != null)
                    return Task.FromResult<object>(new ErrorResponse("A Frame Debugger dump is already running."));
                if (action == "enable")
                {
                    api.OpenWindow();
                    api.Enable();
                    return Task.FromResult<object>(new SuccessResponse("Frame Debugger enabled."));
                }
                if (action == "disable")
                {
                    api.Disable();
                    return Task.FromResult<object>(new SuccessResponse("Frame Debugger disabled."));
                }

                int max = PositiveInt(p, "max-events", "max_events", int.MaxValue);
                int timeout = PositiveInt(p, "capture-timeout", "capture_timeout", 90);
                string root = Path.GetDirectoryName(Application.dataPath);
                string output = p.Get("output") ?? Path.Combine("Temp", "FrameDebugger", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                output = Path.GetFullPath(Path.IsPathRooted(output) ? output : Path.Combine(root, output));
                s_Capture = new Capture(api, output, max, timeout);
                return s_Capture.Start();
            }
            catch (Exception ex)
            {
                return Task.FromResult<object>(new ErrorResponse($"framedebug: {ex.GetBaseException().Message}. {Usage}"));
            }
        }

        static int PositiveInt(ToolParams p, string flag, string key, int fallback)
        {
            var text = p.Get(flag) ?? p.Get(key);
            if (text == null) return fallback;
            if (!int.TryParse(text, out var value) || value <= 0)
                throw new ArgumentException($"--{flag} must be a positive integer");
            return value;
        }

        sealed class Capture
        {
            readonly FrameDebuggerApi api;
            readonly string output;
            readonly int max;
            readonly double timeout;
            readonly TaskCompletionSource<object> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly JArray events = new();
            readonly HashSet<string> missing = new();
            readonly bool wasEnabled;
            readonly bool wasPaused;
            readonly int oldLimit;
            readonly int connection;
            readonly DateTime startedUtc = DateTime.UtcNow;
            double started, eventStarted;
            int waitUpdates = 4, index, hash, total, selected;
            Array descriptors;
            string[] breakCauses;
            bool finished;

            public Capture(FrameDebuggerApi api, string output, int max, int timeout)
            {
                this.api = api;
                this.output = output;
                this.max = max;
                this.timeout = timeout;
                wasEnabled = api.Enabled;
                wasPaused = EditorApplication.isPaused;
                oldLimit = api.Limit;
                connection = api.Connection;
            }

            public Task<object> Start()
            {
                started = EditorApplication.timeSinceStartup;
                AssemblyReloadEvents.beforeAssemblyReload += Interrupted;
                EditorApplication.quitting += Interrupted;
                try
                {
                    api.OpenWindow();
                    api.Enable();
                    EditorApplication.update += Update;
                    api.Repaint();
                }
                catch (Exception ex) { Finish(ex.GetBaseException().Message); }
                return completion.Task;
            }

            void Interrupted() => Finish("Capture interrupted by Editor shutdown or assembly reload.");

            void Update()
            {
                try
                {
                    double now = EditorApplication.timeSinceStartup;
                    if (now - started >= timeout)
                    {
                        Finish("Overall capture timeout reached.");
                        return;
                    }
                    if (!api.Enabled) { Finish("Frame Debugger was disabled during capture."); return; }
                    if (waitUpdates-- > 0) { api.Repaint(); return; }
                    if (descriptors == null)
                    {
                        // Enabling and rendering are deferred by Unity. Never read details
                        // in the same update that changes the event limit.
                        var frame = api.Events();
                        if (!api.WindowReady || api.Count <= 0 || frame == null || frame.Length == 0)
                        {
                            if (now - started >= 10) Finish("No ready captured frame. Make a Game view visible and render a frame; Play Mode may be required.");
                            else api.Repaint();
                            return;
                        }
                        total = api.Count;
                        if (frame.Length != total) { api.Repaint(); return; }
                        descriptors = frame;
                        hash = api.Hash;
                        selected = Math.Min(total, max);
                        breakCauses = api.BreakCauses();
                        if (breakCauses == null) missing.Add("GetBatchBreakCauseStrings");
                        Select(now);
                        return;
                    }
                    if (api.Hash != hash || api.Count != total)
                    {
                        Finish("Captured frame changed during traversal; refusing to mix frames.");
                        return;
                    }
                    try
                    {
                        if (api.Limit != index + 1)
                            throw new InvalidOperationException("Event limit changed externally.");
                        if (!api.TryData(index, out var data))
                        {
                            if (now - eventStarted < 2) { api.Repaint(); return; }
                            throw new InvalidOperationException("Event data unavailable after 2 seconds.");
                        }
                        events.Add(FrameDebuggerJson.Event(api, descriptors.GetValue(index), data, index, breakCauses, missing));
                    }
                    catch (Exception ex)
                    {
                        events.Add(FrameDebuggerJson.FailedEvent(api, descriptors.GetValue(index), index, ex.GetBaseException().Message));
                    }
                    index++;
                    if (index >= selected) Finish(null);
                    else Select(now);
                }
                catch (Exception ex) { Finish(ex.GetBaseException().Message); }
            }

            void Select(double now)
            {
                api.Limit = index + 1;
                api.ChangeLimit(index + 1);
                eventStarted = now;
                waitUpdates = 2;
                api.Repaint();
            }

            void Finish(string error)
            {
                if (finished) return;
                finished = true;
                EditorApplication.update -= Update;
                AssemblyReloadEvents.beforeAssemblyReload -= Interrupted;
                EditorApplication.quitting -= Interrupted;
                // Restore before publishing a result, including setup/write/timeout errors.
                try
                {
                    if (!wasEnabled) api.Disable();
                    api.CloseOwnedWindow();
                    if (wasEnabled)
                    {
                        if (!api.Enabled) api.SetEnabled(true, connection);
                        api.Limit = oldLimit;
                        api.ChangeLimit(oldLimit);
                    }
                    EditorApplication.isPaused = wasPaused;
                    api.Repaint();
                }
                catch (Exception ex)
                {
                    error = (error == null ? "" : error + " ") + "State restoration failed: " + ex.GetBaseException().Message;
                    // Best effort if the window's cleanup failed.
                    try { api.SetEnabled(wasEnabled, connection); api.Limit = oldLimit; } catch { }
                    EditorApplication.isPaused = wasPaused;
                }
                try
                {
                    var summary = FrameDebuggerJson.Summary(events, total, selected);
                    var result = new JObject
                    {
                        ["output"] = output, ["summary"] = summary, ["complete"] = error == null,
                        ["truncated"] = selected < total, ["error"] = error,
                        ["missingFields"] = new JArray(missing.OrderBy(x => x))
                    };
                    // Do not leave an empty artifact when no frame could be captured.
                    if (descriptors != null)
                    {
                        var dump = new JObject
                        {
                            ["schemaVersion"] = 1, ["unityVersion"] = Application.unityVersion,
                            ["projectPath"] = Path.GetDirectoryName(Application.dataPath),
                            ["startedUtc"] = startedUtc.ToString("O"), ["eventsHash"] = hash,
                            ["durationSeconds"] = EditorApplication.timeSinceStartup - started,
                            ["complete"] = error == null, ["truncated"] = selected < total,
                            ["error"] = error, ["missingFields"] = result["missingFields"].DeepClone(),
                            ["api"] = api.Describe(), ["summary"] = summary.DeepClone(), ["events"] = events
                        };
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        File.WriteAllText(output, dump.ToString(Formatting.Indented));
                    }
                    else result["output"] = null;
                    completion.TrySetResult(error == null
                        ? new SuccessResponse("Frame Debugger dump saved.", result)
                        : (object)new ErrorResponse(error, result));
                }
                catch (Exception ex) { completion.TrySetResult(new ErrorResponse("Dump write failed: " + ex.GetBaseException().Message)); }
                finally { s_Capture = null; }
            }
        }
    }

    // Only actual runtime types cross Unity's native boundary. In particular Unity 6
    // EventData is a class, while older versions may require a boxed ref struct.
    internal sealed class FrameDebuggerApi
    {
        const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        readonly Type utility, dataType, windowType, engine;
        readonly MethodInfo getData, setEnabled;
        readonly PropertyInfo limit;
        EditorWindow window;
        bool ownsWindow;

        public FrameDebuggerApi()
        {
            var editor = typeof(EditorWindow).Assembly;
            utility = editor.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility")
                ?? editor.GetType("UnityEditorInternal.FrameDebuggerUtility")
                ?? throw new NotSupportedException("FrameDebuggerUtility type not found");
            windowType = editor.GetType("UnityEditor.FrameDebuggerWindow")
                ?? throw new NotSupportedException("FrameDebuggerWindow type not found");
            engine = typeof(Object).Assembly.GetType("UnityEngine.FrameDebugger")
                ?? throw new NotSupportedException("UnityEngine.FrameDebugger type not found");
            setEnabled = RequiredMethod(utility, "SetEnabled", typeof(bool), typeof(int));
            getData = utility.GetMethods(Static).FirstOrDefault(m => m.Name == "GetFrameEventData" && m.GetParameters().Length == 2)
                ?? throw new NotSupportedException("GetFrameEventData method not found");
            var parameter = getData.GetParameters()[1].ParameterType;
            dataType = parameter.IsByRef ? parameter.GetElementType() : parameter;
            limit = utility.GetProperty("limit", Static);
            if (limit == null || !limit.CanRead || !limit.CanWrite)
                throw new NotSupportedException("FrameDebuggerUtility.limit is unavailable");
            foreach (var name in new[] { "count", "eventsHash" })
                if (utility.GetProperty(name, Static) == null) throw new NotSupportedException(name + " is unavailable");
            if (engine.GetProperty("enabled", Static) == null) throw new NotSupportedException("FrameDebugger.enabled is unavailable");
            RequiredMethod(utility, "GetFrameEvents");
        }

        static MethodInfo RequiredMethod(Type type, string name, params Type[] args) =>
            type.GetMethod(name, Static, null, args, null) ?? throw new NotSupportedException(type.Name + "." + name + " is unavailable");
        object Call(string name, params object[] args) => RequiredMethod(utility, name, args.Select(x => x.GetType()).ToArray()).Invoke(null, args);
        public bool Enabled => (bool)engine.GetProperty("enabled", Static).GetValue(null);
        public int Count => (int)utility.GetProperty("count", Static).GetValue(null);
        public int Hash => (int)utility.GetProperty("eventsHash", Static).GetValue(null);
        public int Limit { get => (int)limit.GetValue(null); set => limit.SetValue(null, value); }
        public int Connection => (int)(utility.GetMethod("GetRemotePlayerGUID", Static)?.Invoke(null, null) ?? 0);
        public bool WindowReady => window == null || !(windowType.GetProperty("IsEnablingFrameDebugger", Instance)?.GetValue(window) is bool enabling && enabling);
        public Array Events() => Call("GetFrameEvents") as Array;
        public string[] BreakCauses() => utility.GetMethod("GetBatchBreakCauseStrings", Static)?.Invoke(null, null) as string[];
        public string Name(int index) => utility.GetMethod("GetFrameEventInfoName", Static)?.Invoke(null, new object[] { index }) as string;
        public Object EventObject(int index) => utility.GetMethod("GetFrameEventObject", Static)?.Invoke(null, new object[] { index }) as Object;
        public void SetEnabled(bool enabled, int connection) => setEnabled.Invoke(null, new object[] { enabled, connection });

        public void OpenWindow()
        {
            window = Resources.FindObjectsOfTypeAll(windowType).OfType<EditorWindow>().FirstOrDefault();
            if (window != null) return;
            if (Enabled) throw new NotSupportedException("Enabled Frame Debugger has no window; open its window before dumping");
            window = RequiredMethod(windowType, "OpenWindow").Invoke(null, null) as EditorWindow;
            ownsWindow = true;
            if (window == null) throw new NotSupportedException("Unable to open Frame Debugger window");
        }

        public void Enable()
        {
            if (Enabled) return;
            var method = windowType.GetMethod("EnableFrameDebugger", Instance, null, Type.EmptyTypes, null)
                ?? throw new NotSupportedException("FrameDebuggerWindow.EnableFrameDebugger is unavailable");
            method.Invoke(window, null);
            if (!Enabled) throw new InvalidOperationException("Frame Debugger could not enable. Make a Game view visible and check graphics API support");
        }

        public void Disable()
        {
            var current = window ?? Resources.FindObjectsOfTypeAll(windowType).OfType<EditorWindow>().FirstOrDefault();
            var method = windowType.GetMethod("DisableFrameDebugger", Instance, null, Type.EmptyTypes, null);
            if (current != null && method != null) method.Invoke(current, null);
            else SetEnabled(false, Connection);
        }

        public void ChangeLimit(int value) => windowType.GetMethod("ChangeFrameEventLimit", Instance, null, new[] { typeof(int) }, null)?.Invoke(window, new object[] { value });
        public void Repaint()
        {
            if (window != null)
            {
                windowType.GetMethod("RepaintOnLimitChange", Instance)?.Invoke(window, null);
                window.Repaint();
            }
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
        public void CloseOwnedWindow() { if (ownsWindow && window != null) { window.Close(); window = null; } }

        public bool TryData(int index, out object data)
        {
            var args = new[] { (object)index, Activator.CreateInstance(dataType, true) };
            // A sentinel prevents accepting a successful call with stale/default data.
            var field = dataType.GetField("m_FrameEventIndex", Instance) ?? dataType.GetField("frameEventIndex", Instance);
            field?.SetValue(args[1], -1);
            var result = getData.Invoke(null, args);
            data = args[1]; // reflection updates this slot for ref/out structs
            return result is bool ok && ok && (field == null || Convert.ToInt32(field.GetValue(data)) == index);
        }

        public JObject Describe() => new JObject
        {
            ["utilityType"] = utility.FullName, ["eventDataType"] = dataType.FullName,
            ["eventDataIsValueType"] = dataType.IsValueType,
            ["utilityMembers"] = new JArray(utility.GetMembers(Static | BindingFlags.DeclaredOnly).Select(x => x.ToString()).OrderBy(x => x)),
            ["eventDataFields"] = new JArray(dataType.GetFields(Instance).Select(x => x.FieldType.FullName + " " + x.Name).OrderBy(x => x))
        };
    }

    internal static class FrameDebuggerJson
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public static object Member(object value, params string[] names)
        {
            if (value == null) return null;
            foreach (string name in names)
            {
                var type = value.GetType();
                var field = type.GetField(name, Instance);
                if (field != null) return field.GetValue(value);
                var property = type.GetProperty(name, Instance);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(value);
            }
            return null;
        }
        static JToken Read(object value, HashSet<string> missing, params string[] names)
        {
            var result = Member(value, names);
            if (result == null && !names.Any(n => value.GetType().GetField(n, Instance) != null)) missing.Add(names[0]);
            return Token(result);
        }
        static string Key(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);
        public static JToken Token(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is Object obj) return ObjectInfo(obj);
            if (value is string || value.GetType().IsPrimitive || value is decimal) return JToken.FromObject(value);
            if (value.GetType().IsEnum) return new JValue(value.ToString());
            if (value.GetType().FullName == "UnityEngine.EntityId")
                return new JValue(value.GetType().GetMethod("GetRawData", Instance)?.Invoke(value, null)?.ToString() ?? value.ToString());
            if (value is Vector4 v) return new JArray(v.x, v.y, v.z, v.w);
            if (value is Matrix4x4 matrix)
            {
                var rows = new JArray();
                for (int r = 0; r < 4; r++) rows.Add(new JArray(matrix[r, 0], matrix[r, 1], matrix[r, 2], matrix[r, 3]));
                return rows;
            }
            if (value is IEnumerable sequence)
            {
                var array = new JArray();
                foreach (var item in sequence) array.Add(Token(item));
                return array;
            }
            var fields = new JObject();
            foreach (var f in value.GetType().GetFields(Instance).Where(f => !f.IsStatic))
                fields[Key(f.Name.StartsWith("m_") ? f.Name.Substring(2) : f.Name)] = Token(f.GetValue(value));
            return fields;
        }

        static JObject ObjectInfo(Object obj)
        {
            if (obj == null) return null;
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            var path = go == null ? AssetDatabase.GetAssetPath(obj) : go.name;
            if (go != null)
                for (var parent = go.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            var info = new JObject
            {
                ["name"] = obj.name, ["path"] = path, ["type"] = obj.GetType().FullName,
                ["entityId"] = Token(typeof(Object).GetMethod("GetEntityId", Instance)?.Invoke(obj, null)),
                ["instanceId"] = typeof(Object).GetMethod("GetEntityId", Instance) == null
                    ? Token(typeof(Object).GetMethod("GetInstanceID", Instance)?.Invoke(obj, null)) : JValue.CreateNull()
            };
            if (go != null) info["scene"] = go.scene.path;
            if (obj is Texture texture) { info["width"] = texture.width; info["height"] = texture.height; }
            return info;
        }

        static JObject Group(object data, string prefix)
        {
            var group = new JObject();
            foreach (var field in data.GetType().GetFields(Instance).Where(f => f.Name.StartsWith(prefix)))
                group[Key(field.Name.Substring(prefix.Length))] = Token(field.GetValue(data));
            return group;
        }

        public static JObject FailedEvent(FrameDebuggerApi api, object descriptor, int index, string error)
        {
            var result = new JObject
            {
                ["index"] = index, ["type"] = Token(Member(descriptor, "m_Type", "type")),
                ["status"] = "failed", ["error"] = error, ["name"] = null, ["object"] = null
            };
            // Keep scope names/objects even when native detail data is unavailable.
            try { result["name"] = api.Name(index); } catch { }
            try { result["object"] = ObjectInfo(api.EventObject(index) ?? Member(descriptor, "m_Obj", "gameObject") as Object); } catch { }
            return result;
        }

        public static JObject Event(FrameDebuggerApi api, object descriptor, object data, int index, string[] causes, HashSet<string> missing)
        {
            var cause = Read(data, missing, "m_BatchBreakCause", "batchBreakCause");
            int code = cause.Type == JTokenType.Integer ? cause.Value<int>() : -1;
            var shaderInfo = Member(data, "m_ShaderInfo", "shaderProperties");
            if (shaderInfo == null) missing.Add("m_ShaderInfo");
            var properties = new JObject();
            foreach (var name in new[] { "Keywords", "Floats", "Ints", "Vectors", "Matrices", "Textures", "Buffers", "CBuffers" })
                properties[Key(name)] = shaderInfo == null ? JValue.CreateNull() : Read(shaderInfo, missing, "m_" + name, Key(name));
            var target = Group(data, "m_RenderTarget");
            // Explicit nulls distinguish unavailable API fields from numeric zero.
            foreach (var name in new[] { "Name", "Width", "Height", "Format", "LoadAction", "StoreAction", "DepthLoadAction", "DepthStoreAction", "ClearColorR", "ClearColorG", "ClearColorB", "ClearColorA", "ClearDepth", "ClearStencil" })
                target[Key(name)] = Read(data, missing, "m_RenderTarget" + name);
            var format = target["format"];
            target["formatName"] = format?.Type == JTokenType.Integer ? new JValue(((UnityEngine.Experimental.Rendering.GraphicsFormat)format.Value<int>()).ToString()) : JValue.CreateNull();
            foreach (var name in new[] { "loadAction", "depthLoadAction" })
                target[name + "Name"] = target[name]?.Type == JTokenType.Integer ? new JValue(((UnityEngine.Rendering.RenderBufferLoadAction)target[name].Value<int>()).ToString()) : JValue.CreateNull();
            foreach (var name in new[] { "storeAction", "depthStoreAction" })
                target[name + "Name"] = target[name]?.Type == JTokenType.Integer ? new JValue(((UnityEngine.Rendering.RenderBufferStoreAction)target[name].Value<int>()).ToString()) : JValue.CreateNull();
            var result = new JObject
            {
                ["index"] = index, ["status"] = "ok", ["type"] = Token(Member(descriptor, "m_Type", "type")),
                ["name"] = api.Name(index), ["object"] = ObjectInfo(api.EventObject(index) ?? Member(descriptor, "m_Obj", "gameObject") as Object),
                ["componentId"] = Read(data, missing, "m_ComponentEntityId", "m_ComponentInstanceID", "componentInstanceID"),
                ["shader"] = new JObject
                {
                    ["original"] = Read(data, missing, "m_OriginalShaderName", "shaderName"),
                    ["real"] = Read(data, missing, "m_RealShaderName", "shaderName"),
                    ["id"] = Read(data, missing, "m_ShaderEntityId", "m_ShaderInstanceID", "shaderInstanceID")
                },
                ["pass"] = new JObject
                {
                    ["name"] = Read(data, missing, "m_PassName", "passName"),
                    ["lightMode"] = Read(data, missing, "m_PassLightMode", "passLightMode"),
                    ["index"] = Read(data, missing, "m_ShaderPassIndex", "shaderPassIndex"),
                    ["subShaderIndex"] = Read(data, missing, "m_SubShaderIndex", "subShaderIndex")
                },
                ["keywords"] = properties["keywords"].DeepClone(), ["shaderProperties"] = properties,
                ["counts"] = new JObject
                {
                    ["vertices"] = Read(data, missing, "m_VertexCount", "vertexCount"),
                    ["indices"] = Read(data, missing, "m_IndexCount", "indexCount"),
                    ["instances"] = Read(data, missing, "m_InstanceCount", "instanceCount"),
                    ["drawCalls"] = Read(data, missing, "m_DrawCallCount", "drawCallCount")
                },
                ["batchBreak"] = new JObject { ["code"] = cause, ["reason"] = causes != null && code >= 0 && code < causes.Length ? causes[code] : null },
                ["renderTarget"] = target, ["clear"] = Group(data, "m_Clear"),
                ["blend"] = Read(data, missing, "m_BlendState", "blendState"),
                ["raster"] = Read(data, missing, "m_RasterState", "rasterState"),
                ["depth"] = Read(data, missing, "m_DepthState", "depthState"),
                ["stencil"] = Read(data, missing, "m_StencilState", "stencilState"),
                ["stencilRef"] = Read(data, missing, "m_StencilRef", "stencilRef"),
                ["compute"] = Group(data, "m_ComputeShader"), ["rayTracing"] = Group(data, "m_RayTracing")
            };
            // Unity's native event cache retains fields from previous events even
            // with a fresh EventData instance and a matching frameEventIndex.
            // Clear/dispatch shader names and draw counts are not current draws.
            string type = (string)result["type"];
            bool isCompute = type == "ComputeDispatch";
            bool isRayTracing = type == "RayTracingDispatch";
            bool isClear = type?.StartsWith("Clear", StringComparison.Ordinal) == true;
            bool isDraw = new[]
            {
                "StaticBatch", "DynamicBatch", "Mesh", "DynamicGeometry", "GLDraw",
                "DrawProcedural", "DrawProceduralIndirect", "DrawProceduralIndexed",
                "DrawProceduralIndexedIndirect", "InstancedMesh",
                "SRPBatch", "HybridBatch"
            }.Contains(type);
            if (!isDraw)
            {
                foreach (var name in new[] { "shader", "pass", "counts", "batchBreak", "blend", "raster", "depth", "stencil", "stencilRef" })
                    result[name] = JValue.CreateNull();
                if (!isCompute && !isRayTracing)
                {
                    result["keywords"] = new JArray();
                    result["shaderProperties"] = JValue.CreateNull();
                }
            }
            if (!isCompute) result["compute"] = JValue.CreateNull();
            if (!isRayTracing) result["rayTracing"] = JValue.CreateNull();
            if (!isClear) result["clear"] = JValue.CreateNull();
            if (isCompute || isRayTracing) result["renderTarget"] = JValue.CreateNull();
            return result;
        }

        public static JObject Summary(JArray events, int total, int selected)
        {
            var ok = events.OfType<JObject>().Where(e => (string)e["status"] == "ok").ToArray();
            JObject Histogram(IEnumerable<string> keys) => JObject.FromObject(keys.GroupBy(x => x).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()));
            var targets = ok.Select(e => e["renderTarget"] as JObject).Where(t => t?["name"]?.Type == JTokenType.String)
                .Select(t => string.Join("|", new[] { "name", "width", "height", "format", "dimension", "cubemapFace", "count" }.Select(k => t[k]?.ToString() ?? ""))).ToArray();
            return new JObject
            {
                ["totalEvents"] = total, ["selectedEvents"] = selected, ["dumpedEvents"] = events.Count,
                ["successfulEvents"] = ok.Length, ["failedEvents"] = events.Count - ok.Length,
                ["unvisitedEvents"] = selected - events.Count,
                ["eventTypes"] = Histogram(events.Select(e => e["type"]?.ToString() ?? "unknown")),
                ["batchBreakReasons"] = Histogram(ok.Where(e => !string.IsNullOrEmpty((string)(e["shader"] as JObject)?["real"]))
                    .Select(e => (string)(e["batchBreak"] as JObject)?["reason"] ?? "unknown:" + (e["batchBreak"] as JObject)?["code"])),
                ["shaders"] = Histogram(ok.Select(e => (string)(e["shader"] as JObject)?["real"]).Where(s => !string.IsNullOrEmpty(s))),
                ["shaderPasses"] = Histogram(ok.Where(e => !string.IsNullOrEmpty((string)(e["shader"] as JObject)?["real"]))
                    .Select(e => e["shader"]["real"] + " / " + e["pass"]["name"] + " / " + e["pass"]["index"])),
                ["computeDispatches"] = Histogram(ok.Where(e => e["compute"] is JObject)
                    .Select(e => e["compute"]["name"] + " / " + e["compute"]["kernelName"])),
                ["rayTracingDispatches"] = Histogram(ok.Where(e => e["rayTracing"] is JObject)
                    .Select(e => e["rayTracing"]["shaderName"] + " / " + e["rayTracing"]["shaderRayGenName"])),
                ["renderTargetTransitions"] = targets.Zip(targets.Skip(1), (a, b) => a != b).Count(changed => changed),
                ["renderTargetTransitionSamples"] = targets.Length,
                ["reportedDrawCalls"] = ok.Sum(e => (long?)(e["counts"] as JObject)?["drawCalls"] ?? 0),
                ["notes"] = "Events include clear/dispatch/scopes, not one-to-one GPU draws. No GPU timings. Shader/pass/batch/draw counts cover draw events only; compute/ray dispatches are separate. Histograms cover successful selected events; render target transitions compare reported target descriptors, not native attachment identities."
            };
        }
    }
}
