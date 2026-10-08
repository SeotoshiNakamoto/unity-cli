// Standalone managed regression fixture. No Unity process or GPU is used.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityCliConnector;
using UnityCliConnector.Tools;
using Utility = UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility;

namespace UnityEngine
{
    public class Object
    {
        public string name = "Fixture";
        [Obsolete("Use EntityId", true)] public int GetInstanceID() => 7;
        public EntityId GetEntityId() => new EntityId();
    }
    public struct EntityId { public ulong GetRawData() => 9007199254740993; }
    public static class FrameDebugger { public static bool enabled { get; set; } }
    public static class Application { public static string dataPath = Path.Combine(Path.GetTempPath(), "Fixture", "Assets"); public static string unityVersion = "fixture"; }
    public static class Resources
    {
        public static Object[] FindObjectsOfTypeAll(Type t) => UnityEditor.EditorWindow.current == null ? new Object[0] : new Object[] { UnityEditor.EditorWindow.current };
        public static T[] FindObjectsOfTypeAll<T>() where T : Object => FindObjectsOfTypeAll(typeof(T)).OfType<T>().ToArray();
    }
    public class Component : Object { public GameObject gameObject; }
    public class Transform { public Transform parent; public string name; }
    public class GameObject : Object { public Transform transform = new Transform(); public Scene scene; }
    public struct Scene { public string path; }
    public class Texture : Object { public int width = 64, height = 32; }
    public struct Vector4 { public float x, y, z, w; }
    public struct Matrix4x4 { public float this[int r, int c] => r == c ? 1 : 0; }
}
namespace UnityEngine.Experimental.Rendering { public enum GraphicsFormat { None, R8G8B8A8_UNorm } }
namespace UnityEngine.Rendering { public enum RenderBufferLoadAction { Load, Clear, DontCare } public enum RenderBufferStoreAction { Store, DontCare } }
namespace UnityEditorInternal
{
    public static class InternalEditorUtility { public static void RepaintAllViews() { } }
    public static class ProfilerDriver { public static int connectedProfiler => -1; }
}
namespace UnityEditor
{
    public static class AssetDatabase { public static string GetAssetPath(UnityEngine.Object o) => "Assets/Fixture.asset"; }
    public class EditorWindow : UnityEngine.Object
    {
        public static EditorWindow current;
        public void Repaint() { }
        public void Close() { current = null; UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility.SetEnabled(false, 0); }
    }
    public static class EditorApplication
    {
        public static event Action update, quitting;
        public static double timeSinceStartup;
        static bool paused;
        public static bool isPlaying;
        public static bool isPaused
        {
            get => paused;
            set { if (paused != value) Utility.SetEnabled(false, 0); paused = value; }
        }
        public static void QueuePlayerLoopUpdate() { }
        public static void Tick(double seconds = 0.1) { timeSinceStartup += seconds; Utility.ticks++; update?.Invoke(); }
        public static void Quit() => quitting?.Invoke();
    }
    public static class AssemblyReloadEvents { public static event Action beforeAssemblyReload; public static void Reload() => beforeAssemblyReload?.Invoke(); }
    public class FrameDebuggerWindow : EditorWindow
    {
        public static bool failEnable;
        public static FrameDebuggerWindow OpenWindow() => throw new Exception("Focus-taking OpenWindow path called");
        void EnableFrameDebugger() => throw new Exception("Focus-taking ShowTab enable path called");
        void DisableFrameDebugger() { Utility.SetEnabled(false, 0); }
        void ChangeFrameEventLimit(int value) { Utility.limit = value; }
        void RepaintOnLimitChange() { }
    }
}
namespace UnityEditorInternal.FrameDebuggerInternal
{
    public struct ShaderFloat { public string m_Name; public float m_Value; }
    public struct ShaderInfo { public ShaderFloat[] m_Floats; public ShaderFloat[] m_Keywords; public ShaderFloat[] m_Buffers; }
    public struct BlendState { public bool m_DepthClip; public string m_SrcBlend; }
#if STRUCT_DATA
    public struct FrameDebuggerEventData
#else
    public class FrameDebuggerEventData
#endif
    {
        public int m_FrameEventIndex, m_VertexCount, m_IndexCount, m_InstanceCount, m_DrawCallCount;
        public string m_OriginalShaderName, m_RealShaderName, m_PassName, m_PassLightMode;
        public int m_ShaderPassIndex, m_SubShaderIndex, m_BatchBreakCause;
        #if STRUCT_DATA
        public int m_ShaderInstanceID, m_ComponentInstanceID;
#else
        public UnityEngine.EntityId m_ShaderEntityId, m_ComponentEntityId;
#endif
        public string m_RenderTargetName;
        public int m_RenderTargetWidth, m_RenderTargetHeight, m_RenderTargetFormat, m_RenderTargetLoadAction, m_RenderTargetStoreAction;
        public BlendState m_BlendState, m_RasterState, m_DepthState, m_StencilState;
        public int m_StencilRef;
        public ShaderInfo m_ShaderInfo;
        public int m_ComputeShaderThreadGroupsX;
    }
    public struct FrameDebuggerEvent { public string m_Type; public UnityEngine.Object m_Obj; }
    public static class FrameDebuggerUtility
    {
        public static int ticks, selectedAt, failIndex = -1, reads;
        public static bool noEvents, staleData, changeOnceOnRead, changeAlwaysOnRead;
        public static string failureMode, failureChange;
        public static bool failureTriggered, changedCount, changeOnFailedIdentity;
        public static double selectedTime;
        public static string lastType = "ComputeDispatch";
        public static void SetEnabled(bool value, int connection)
        {
            UnityEngine.FrameDebugger.enabled = value;
            if (value && UnityEditor.FrameDebuggerWindow.failEnable) throw new Exception("enable failed");
            if (!value) limit = 0;
        }
        public static int GetRemotePlayerGUID() => 0;
        public static int count => noEvents ? 0 : changedCount ? 4 : 3;
        static int currentLimit;
        public static int limit { get => currentLimit; set { currentLimit = value; selectedAt = ticks; selectedTime = UnityEditor.EditorApplication.timeSinceStartup; } }
        public static int eventsHash { get; set; } = 123;
        public static FrameDebuggerEvent[] GetFrameEvents() => Enumerable.Range(0, count)
            .Select(i => new FrameDebuggerEvent { m_Type = i == 2 ? lastType : "Mesh" }).ToArray();
        public static string[] GetBatchBreakCauseStrings() => new[] { "None", "Different material" };
        static void ChangeFailureFrame()
        {
            if (failureChange == "hash") eventsHash++;
            else changedCount = true;
            failureTriggered = true;
        }
        public static string GetFrameEventInfoName(int i)
        {
            if (changeOnFailedIdentity && i == 2 && !failureTriggered) ChangeFailureFrame();
            return failureMode == null ? "Scope/Fixture" + i : "Scope/Frame" + eventsHash + "/" + count + "/Fixture" + i;
        }
        public static UnityEngine.Object GetFrameEventObject(int i) => new UnityEngine.Texture
        {
            name = failureMode == null ? "Fixture" : "Frame" + eventsHash + "/" + count
        };
#if STRUCT_DATA
        public static bool GetFrameEventData(int index, ref FrameDebuggerEventData data)
#else
        public static bool GetFrameEventData(int index, FrameDebuggerEventData data)
#endif
        {
            if (ticks <= selectedAt) throw new Exception("Read before redraw");
            if (UnityEditor.EditorApplication.isPlaying && !UnityEditor.EditorApplication.isPaused) throw new Exception("Game not paused during capture");
            reads++;
            if (changeAlwaysOnRead || changeOnceOnRead) { eventsHash++; changeOnceOnRead = false; }
            if (index == failIndex) return false;
            if (index == 2 && failureMode != null && !failureTriggered)
            {
                if (failureMode == "timeout" && UnityEditor.EditorApplication.timeSinceStartup - selectedTime < 2) return false;
                if (!changeOnFailedIdentity) ChangeFailureFrame();
                if (failureMode == "throw") throw new Exception("Last event read failed during frame change");
                return false;
            }
            data.m_FrameEventIndex = staleData ? index - 1 : index;
            data.m_OriginalShaderName = data.m_RealShaderName = "Fixture/Shader";
            data.m_PassName = "Forward"; data.m_PassLightMode = "UniversalForward";
            data.m_DrawCallCount = 2; data.m_VertexCount = 36; data.m_BatchBreakCause = 1;
            data.m_RenderTargetName = index == 2 ? "TargetB" : "TargetA";
            data.m_RenderTargetWidth = 1920; data.m_RenderTargetHeight = 1080;
            data.m_RenderTargetFormat = 1; data.m_RenderTargetLoadAction = 1;
            data.m_BlendState = new BlendState { m_SrcBlend = "One", m_DepthClip = true };
            data.m_ShaderInfo = new ShaderInfo { m_Floats = new[] { new ShaderFloat { m_Name = "_Glossiness", m_Value = 0.5f } } };
            return true;
        }
    }
}

class Fixture
{
    static int checks;
    static string output;
    static void Assert(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Reset(bool enabled = false)
    {
        UnityEditor.EditorApplication.isPaused = false;
        UnityEngine.FrameDebugger.enabled = enabled;
        UnityEditor.EditorApplication.isPlaying = true;
        Utility.reads = 0; Utility.changeOnceOnRead = Utility.changeAlwaysOnRead = false;
        Utility.failureMode = Utility.failureChange = null;
        Utility.failureTriggered = Utility.changedCount = Utility.changeOnFailedIdentity = false;
        UnityEditor.EditorWindow.current = enabled ? new UnityEditor.FrameDebuggerWindow() : null;
        Utility.limit = 2; Utility.eventsHash = 123; Utility.failIndex = -1; Utility.noEvents = Utility.staleData = false; Utility.lastType = "ComputeDispatch";
        UnityEditor.FrameDebuggerWindow.failEnable = false;
        output = Path.Combine(Path.GetTempPath(), "framedebug-fixture-" + Guid.NewGuid() + ".json");
    }
    static Task<object> Start(JObject extra = null)
    {
        var p = extra ?? new JObject(); p["action"] = "dump"; p["output"] = output;
        return ManageFrameDebugger.HandleCommand(p);
    }
    static object Complete(Task<object> task)
    {
        for (int i = 0; !task.IsCompleted && i < 1500; i++) UnityEditor.EditorApplication.Tick();
        Assert(task.IsCompleted, "Capture never completed"); return task.GetAwaiter().GetResult();
    }
    static JObject Dump() { var dump = JObject.Parse(File.ReadAllText(output)); File.Delete(output); return dump; }
    static void UntilRead(Task<object> task)
    {
        for (int i = 0; Utility.reads == 0 && !task.IsCompleted && i < 200; i++) UnityEditor.EditorApplication.Tick();
        Assert(Utility.reads > 0 && !task.IsCompleted, "Traversal not started");
    }
    static void Main()
    {
        Reset(); var task = Start(); Assert(!task.IsCompleted, "Must yield across updates");
        Assert(Complete(task) is SuccessResponse, "Full capture failed"); var d = Dump();
        Assert((int)d["summary"]["dumpedEvents"] == 3, "Off-by-one traversal");
        Assert((int)d["summary"]["renderTargetTransitions"] == 0, "Stale compute target counted");
        Assert((int)d["summary"]["reportedDrawCalls"] == 4, "Stale compute draw count included");
        Assert((int)d["summary"]["shaderPasses"]["Fixture/Shader / Forward / 0"] == 2, "Stale compute shader counted");
        Assert(d["events"][2]["shader"].Type == JTokenType.Null && d["events"][2]["blend"].Type == JTokenType.Null, "Compute stale graphics state retained");
        Assert(d["events"][2]["compute"].Type == JTokenType.Object && d["events"][0]["compute"].Type == JTokenType.Null, "Dispatch applicability");
        Assert((string)d["events"][0]["pass"]["lightMode"] == "UniversalForward", "Pass mapping");
        Assert((string)d["events"][0]["blend"]["srcBlend"] == "One", "Render state mapping");
        Assert((float)d["events"][0]["shaderProperties"]["floats"][0]["value"] == 0.5f, "Property mapping");
        Assert((string)d["events"][0]["object"]["entityId"] == "9007199254740993", "EntityId precision");
#if STRUCT_DATA
        Assert(d["events"][0]["shader"]["id"].Type == JTokenType.Integer, "InstanceID fallback");
#else
        Assert((string)d["events"][0]["shader"]["id"] == "9007199254740993", "Shader EntityId precision");
#endif
        Assert(d["events"][0]["renderTarget"]["clearDepth"].Type == JTokenType.Null, "Missing fields must be null");
        Assert(d["missingFields"].ToString().Contains("m_RenderTargetClearDepth"), "Missing field diagnostic");
        Assert(!UnityEngine.FrameDebugger.enabled && !UnityEditor.EditorApplication.isPaused && UnityEditor.EditorWindow.current == null, "Initial disabled state not restored");
        Reset(); task = Start(); Assert(UnityEditor.EditorWindow.current == null, "Capture opened a window"); Complete(task); Dump();
        Reset(true); UnityEditor.EditorWindow.current = null;
        Assert(Complete(Start()) is SuccessResponse, "Enabled windowless capture failed"); Dump();
        Assert(UnityEngine.FrameDebugger.enabled && UnityEditor.EditorWindow.current == null, "Enabled windowless state not restored");
        Reset(); Utility.lastType = "ClearAll"; Complete(Start()); d = Dump();
        Assert(d["events"][2]["shaderProperties"].Type == JTokenType.Null && d["events"][2]["pass"].Type == JTokenType.Null, "Clear stale shader data retained");
        Assert(d["events"][2]["clear"].Type == JTokenType.Object && d["events"][0]["clear"].Type == JTokenType.Null, "Clear applicability");
        Assert((int)d["summary"]["reportedDrawCalls"] == 4 && (int)d["summary"]["renderTargetTransitions"] == 1, "Clear summary normalization");
        Reset(true); var window = UnityEditor.EditorWindow.current;
        Assert(Complete(Start(new JObject { ["max_events"] = 1 })) is SuccessResponse, "Prefix capture failed"); d = Dump();
        Assert((bool)d["truncated"] && (int)d["summary"]["dumpedEvents"] == 1, "Truncation not explicit");
        Assert(UnityEngine.FrameDebugger.enabled && Utility.limit == 2 && UnityEditor.EditorWindow.current == window, "Enabled/window/limit state not restored");
        Reset(); Utility.failIndex = 1; Assert(Complete(Start()) is SuccessResponse, "Individual failure should continue"); d = Dump();
        Assert((int)d["summary"]["failedEvents"] == 1 && (int)d["events"][2]["index"] == 2, "Failure continuation");
        Assert((string)d["events"][1]["name"] == "Scope/Fixture1" && d["events"][1]["object"].Type == JTokenType.Object, "Failure identity lost");
        Reset(); task = Start(new JObject { ["capture-timeout"] = 1 }); UnityEditor.EditorApplication.Tick(2);
        Assert(Complete(task) is ErrorResponse && !File.Exists(output) && !UnityEngine.FrameDebugger.enabled, "Timeout restoration before frame");
        Reset(); task = Start(); UntilRead(task); UnityEditor.EditorApplication.Tick(100);
        Assert(Complete(task) is ErrorResponse, "Overall timeout missing"); d = Dump(); Assert(!(bool)d["complete"], "Timeout partial file missing");
        Reset(); task = Start(); for (int i = 0; i < 6; i++) { Utility.eventsHash++; UnityEditor.EditorApplication.Tick(); }
        Assert(Utility.reads == 0, "Traversal started before stable samples");
        Assert(Complete(task) is SuccessResponse, "Startup instability should settle"); d = Dump(); Assert((int)d["retryCount"] == 0, "Startup should not spend traversal retries");
        Reset(); task = Start(); UntilRead(task); Utility.eventsHash++;
        Assert(Complete(task) is SuccessResponse, "Changed frame should restart"); d = Dump();
        Assert((int)d["retryCount"] == 1 && d["events"].Count() == 3 && (int)d["events"][0]["index"] == 0, "Restart mixed frames or duplicated indices");
        Reset(); Utility.changeOnceOnRead = true; Assert(Complete(Start()) is SuccessResponse, "Change during data read should restart"); d = Dump();
        Assert((int)d["retryCount"] == 1 && (int)d["summary"]["failedEvents"] == 0, "Post-read fence missing");
        foreach (var change in new[] { "hash", "count" })
        foreach (var mode in new[] { "throw", "false", "timeout", "failed-identity" })
        {
            Reset(); Utility.failureChange = change;
            Utility.failureMode = mode == "failed-identity" ? "throw" : mode;
            Utility.changeOnFailedIdentity = mode == "failed-identity";
            task = Start();
            if (mode == "false")
            {
                // First false result arrives after the detail deadline on the last event.
                for (int i = 0; Utility.limit != 3 && !task.IsCompleted && i < 200; i++) UnityEditor.EditorApplication.Tick();
                Assert(Utility.limit == 3 && !task.IsCompleted, "Last event not selected");
                UnityEditor.EditorApplication.Tick(); UnityEditor.EditorApplication.Tick();
                UnityEditor.EditorApplication.Tick(2.1);
            }
            Assert(Complete(task) is SuccessResponse, "Last-event " + mode + "/" + change + " did not settle"); d = Dump();
            Assert(Utility.failureTriggered && (int)d["retryCount"] == 1, "Last-event failure fence missing: " + mode + "/" + change);
            Assert((int)d["frameChanges"][0]["index"] == 2, "Failure not exercised on the last event");
            Assert((bool)d["complete"] && (int)d["summary"]["failedEvents"] == 0 && d["events"].Count() == Utility.count, "Mixed failed event was published");
            Assert(d["events"].Select((e, i) => (int)e["index"] == i).All(x => x), "Restart did not traverse from zero");
            Assert(d["events"].All(e => (string)e["name"] == "Scope/Frame" + Utility.eventsHash + "/" + Utility.count + "/Fixture" + (int)e["index"]), "Names mixed across frames");
            Assert(d["events"].All(e => (string)e["object"]["name"] == "Frame" + Utility.eventsHash + "/" + Utility.count), "Objects mixed across frames");
            Assert(!UnityEngine.FrameDebugger.enabled && !UnityEditor.EditorApplication.isPaused && Utility.limit == 2, "Failure restart did not restore state");
        }
        Reset(); Utility.changeAlwaysOnRead = true; Assert(Complete(Start()) is ErrorResponse, "Unstable frame should exhaust retry bound"); d = Dump();
        Assert((int)d["retryCount"] == 3 && d["frameChanges"].Count() == 4 && !(bool)d["complete"], "Retry bound missing");
        Assert(!UnityEngine.FrameDebugger.enabled && !UnityEditor.EditorApplication.isPaused && Utility.limit == 2, "Retry exhaustion did not restore state");
        Reset(true); Utility.limit = 3; task = Start(); Assert(UnityEditor.EditorApplication.isPaused, "Already-enabled unpaused capture not paused");
        Assert(Complete(task) is SuccessResponse, "Already-enabled unpaused capture failed"); d = Dump();
        Assert((bool)d["pausedDuringCapture"] && !UnityEditor.EditorApplication.isPaused && UnityEngine.FrameDebugger.enabled && Utility.limit == 3, "Unpaused initial enabled/limit state not restored after pause transition");
        Reset(true); UnityEditor.EditorApplication.isPaused = true; Utility.SetEnabled(true, 0); Complete(Start()); Dump();
        Assert(UnityEditor.EditorApplication.isPaused, "Originally paused capture resumed game");
        Reset(); task = Start(new JObject { ["capture-timeout"] = 1 });
        for (int i = 0; !task.IsCompleted && i < 30; i++) { Utility.eventsHash++; UnityEditor.EditorApplication.Tick(); }
        Assert(Complete(task) is ErrorResponse && !File.Exists(output), "Startup stability escaped overall timeout");
        Reset(); Utility.staleData = true; Complete(Start()); d = Dump(); Assert((int)d["summary"]["failedEvents"] == 3, "Stale data accepted");
        foreach (var captureTimeout in new[] { 90, 300 })
        {
            Reset(); Utility.noEvents = true; double noFrameStarted = UnityEditor.EditorApplication.timeSinceStartup;
            Assert(Complete(Start(new JObject { ["capture-timeout"] = captureTimeout })) is ErrorResponse && !File.Exists(output), "No frame should not write a dump");
            double elapsed = UnityEditor.EditorApplication.timeSinceStartup - noFrameStarted;
            Assert(elapsed >= 10 && elapsed < 11, "No-frame wait must remain 10 seconds independently of overall timeout");
            Assert(!UnityEngine.FrameDebugger.enabled && !UnityEditor.EditorApplication.isPaused && Utility.limit == 2, "No-frame timeout did not restore state");
        }
        Reset(); UnityEditor.FrameDebuggerWindow.failEnable = true;
        Assert(Complete(Start()) is ErrorResponse && !UnityEngine.FrameDebugger.enabled && !UnityEditor.EditorApplication.isPaused, "Setup failure restoration");
        Reset(); task = Start(); UnityEditor.AssemblyReloadEvents.Reload();
        Assert(Complete(task) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Reload cleanup");
        Reset(); task = Start(); UnityEditor.EditorApplication.Quit();
        Assert(Complete(task) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Quit cleanup");
        Reset(); Assert(Complete(Start(new JObject { ["max-events"] = 0 })) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Invalid max accepted");
        Reset(); Directory.CreateDirectory(output); Assert(Complete(Start()) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Write failure restoration"); Directory.Delete(output);
        foreach (var initialLimit in new[] { -1, 0, 2 })
        {
            Reset(); Utility.limit = initialLimit;
            Assert(Complete(Start()) is SuccessResponse, "Disabled-limit capture failed"); Dump();
            Assert(Utility.limit == initialLimit, "Disabled limit not restored after success");
            Reset(); Utility.limit = initialLimit; UnityEditor.FrameDebuggerWindow.failEnable = true;
            Assert(Complete(Start()) is ErrorResponse && Utility.limit == initialLimit, "Disabled limit not restored after setup failure");
            Reset(); Utility.limit = initialLimit; task = Start(); UnityEditor.AssemblyReloadEvents.Reload();
            Assert(Complete(task) is ErrorResponse && Utility.limit == initialLimit, "Disabled limit not restored after reload");
            Reset(); Utility.limit = initialLimit; task = Start(new JObject { ["capture-timeout"] = 1 }); UnityEditor.EditorApplication.Tick(2);
            Assert(Complete(task) is ErrorResponse && Utility.limit == initialLimit, "Disabled limit not restored after timeout");
            Reset(); Utility.limit = initialLimit; Directory.CreateDirectory(output);
            Assert(Complete(Start()) is ErrorResponse && Utility.limit == initialLimit, "Disabled limit not restored after write failure"); Directory.Delete(output);
        }
        var matrix = FrameDebuggerJson.Token(new UnityEngine.Matrix4x4()); Assert((int)matrix[0][0] == 1 && (int)matrix[0][1] == 0, "Matrix layout");
        Console.WriteLine("PASS: " + checks + " checks; EventData value type: " + typeof(UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData).IsValueType);
    }
}
