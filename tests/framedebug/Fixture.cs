// Standalone managed regression fixture. No Unity process or GPU is used.
using System;
using System.IO;
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
    public static class Resources { public static Object[] FindObjectsOfTypeAll(Type t) => UnityEditor.EditorWindow.current == null ? new Object[0] : new Object[] { UnityEditor.EditorWindow.current }; }
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
namespace UnityEditorInternal { public static class InternalEditorUtility { public static void RepaintAllViews() { } } }
namespace UnityEditor
{
    public static class AssetDatabase { public static string GetAssetPath(UnityEngine.Object o) => "Assets/Fixture.asset"; }
    public class EditorWindow : UnityEngine.Object
    {
        public static EditorWindow current;
        public void Repaint() { }
        public void Close() { current = null; UnityEngine.FrameDebugger.enabled = false; }
    }
    public static class EditorApplication
    {
        public static event Action update, quitting;
        public static double timeSinceStartup;
        public static bool isPaused;
        public static void QueuePlayerLoopUpdate() { }
        public static void Tick(double seconds = 0.1) { timeSinceStartup += seconds; Utility.ticks++; update?.Invoke(); }
        public static void Quit() => quitting?.Invoke();
    }
    public static class AssemblyReloadEvents { public static event Action beforeAssemblyReload; public static void Reload() => beforeAssemblyReload?.Invoke(); }
    public class FrameDebuggerWindow : EditorWindow
    {
        public static bool failEnable;
        public static FrameDebuggerWindow OpenWindow() { var w = new FrameDebuggerWindow(); current = w; return w; }
        void EnableFrameDebugger() { UnityEngine.FrameDebugger.enabled = true; EditorApplication.isPaused = true; if (failEnable) throw new Exception("enable failed"); }
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
        public static int ticks, selectedAt, failIndex = -1;
        public static bool noEvents, staleData;
        public static void SetEnabled(bool value, int connection) { UnityEngine.FrameDebugger.enabled = value; }
        public static int GetRemotePlayerGUID() => 0;
        public static int count => noEvents ? 0 : 3;
        static int currentLimit;
        public static int limit { get => currentLimit; set { currentLimit = value; selectedAt = ticks; } }
        public static int eventsHash { get; set; } = 123;
        public static FrameDebuggerEvent[] GetFrameEvents() => noEvents ? new FrameDebuggerEvent[0] : new[]
        {
            new FrameDebuggerEvent { m_Type = "Mesh" }, new FrameDebuggerEvent { m_Type = "Mesh" }, new FrameDebuggerEvent { m_Type = "ComputeDispatch" }
        };
        public static string[] GetBatchBreakCauseStrings() => new[] { "None", "Different material" };
        public static string GetFrameEventInfoName(int i) => "Scope/Fixture" + i;
        public static UnityEngine.Object GetFrameEventObject(int i) => new UnityEngine.Texture();
#if STRUCT_DATA
        public static bool GetFrameEventData(int index, ref FrameDebuggerEventData data)
#else
        public static bool GetFrameEventData(int index, FrameDebuggerEventData data)
#endif
        {
            if (ticks <= selectedAt) throw new Exception("Read before redraw");
            if (index == failIndex) return false;
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
        UnityEngine.FrameDebugger.enabled = enabled;
        UnityEditor.EditorApplication.isPaused = false;
        UnityEditor.EditorWindow.current = enabled ? new UnityEditor.FrameDebuggerWindow() : null;
        Utility.limit = 2; Utility.eventsHash = 123; Utility.failIndex = -1; Utility.noEvents = Utility.staleData = false;
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
    static void Main()
    {
        Reset(); var task = Start(); Assert(!task.IsCompleted, "Must yield across updates");
        Assert(Complete(task) is SuccessResponse, "Full capture failed"); var d = Dump();
        Assert((int)d["summary"]["dumpedEvents"] == 3, "Off-by-one traversal");
        Assert((int)d["summary"]["renderTargetTransitions"] == 1, "Target transition count");
        Assert((int)d["summary"]["reportedDrawCalls"] == 6, "Draw counts");
        Assert((int)d["summary"]["shaderPasses"]["Fixture/Shader / Forward / 0"] == 3, "Shader/pass histogram");
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
        Reset(true); var window = UnityEditor.EditorWindow.current;
        Assert(Complete(Start(new JObject { ["max_events"] = 1 })) is SuccessResponse, "Prefix capture failed"); d = Dump();
        Assert((bool)d["truncated"] && (int)d["summary"]["dumpedEvents"] == 1, "Truncation not explicit");
        Assert(UnityEngine.FrameDebugger.enabled && Utility.limit == 2 && UnityEditor.EditorWindow.current == window, "Enabled/window/limit state not restored");
        Reset(); Utility.failIndex = 1; Assert(Complete(Start()) is SuccessResponse, "Individual failure should continue"); d = Dump();
        Assert((int)d["summary"]["failedEvents"] == 1 && (int)d["events"][2]["index"] == 2, "Failure continuation");
        Assert((string)d["events"][1]["name"] == "Scope/Fixture1" && d["events"][1]["object"].Type == JTokenType.Object, "Failure identity lost");
        Reset(); task = Start(new JObject { ["capture-timeout"] = 1 }); UnityEditor.EditorApplication.Tick(2);
        Assert(Complete(task) is ErrorResponse && !File.Exists(output) && !UnityEngine.FrameDebugger.enabled, "Timeout restoration before frame");
        Reset(); task = Start(); for (int i = 0; i < 10; i++) UnityEditor.EditorApplication.Tick(); UnityEditor.EditorApplication.Tick(100);
        Assert(Complete(task) is ErrorResponse, "Overall timeout missing"); d = Dump(); Assert(!(bool)d["complete"], "Timeout partial file missing");
        Reset(); task = Start(); for (int i = 0; i < 6; i++) UnityEditor.EditorApplication.Tick(); Utility.eventsHash++;
        Assert(Complete(task) is ErrorResponse, "Changed hash should fail"); d = Dump(); Assert(!(bool)d["complete"], "Mixed-frame guard");
        Reset(); Utility.staleData = true; Complete(Start()); d = Dump(); Assert((int)d["summary"]["failedEvents"] == 3, "Stale data accepted");
        Reset(); Utility.noEvents = true; Assert(Complete(Start()) is ErrorResponse && !File.Exists(output), "No frame should not write a dump");
        Reset(); UnityEditor.FrameDebuggerWindow.failEnable = true;
        Assert(Complete(Start()) is ErrorResponse && !UnityEngine.FrameDebugger.enabled && !UnityEditor.EditorApplication.isPaused, "Setup failure restoration");
        Reset(); task = Start(); UnityEditor.AssemblyReloadEvents.Reload();
        Assert(Complete(task) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Reload cleanup");
        Reset(); task = Start(); UnityEditor.EditorApplication.Quit();
        Assert(Complete(task) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Quit cleanup");
        Reset(); Assert(Complete(Start(new JObject { ["max-events"] = 0 })) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Invalid max accepted");
        Reset(); Directory.CreateDirectory(output); Assert(Complete(Start()) is ErrorResponse && !UnityEngine.FrameDebugger.enabled, "Write failure restoration"); Directory.Delete(output);
        var matrix = FrameDebuggerJson.Token(new UnityEngine.Matrix4x4()); Assert((int)matrix[0][0] == 1 && (int)matrix[0][1] == 0, "Matrix layout");
        Console.WriteLine("PASS: " + checks + " checks; EventData value type: " + typeof(UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData).IsValueType);
    }
}
