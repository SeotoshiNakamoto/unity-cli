using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace UnityCliConnector
{
    /// <summary>
    /// Runtime method tracer using Harmony (loaded dynamically to avoid Burst scanner issues).
    /// 0Harmony.dll is loaded from ~/.unity-cli/plugins/ on first use.
    /// </summary>
    public static class HarmonyTracer
    {
        public struct HookInfo
        {
            public string HookId;
            public string TypeName;
            public string MethodName;
            public string AssemblyName;
            public MethodInfo Original;
            public bool LogParams;
            public bool LogReturn;
            public bool LogStack;
            public string InstanceFilter;
            public int CallCount;
            public DateTime HookedAt;
        }

        public struct TraceEntry
        {
            public string HookId;
            public DateTime Timestamp;
            public string Direction; // "call" or "return"
            public string[] Parameters;
            public string ReturnValue;
            public string StackTrace;
            public string Instance;
        }

        static readonly Dictionary<string, HookInfo> s_Hooks = new Dictionary<string, HookInfo>();
        static readonly ConcurrentQueue<TraceEntry> s_Buffer = new ConcurrentQueue<TraceEntry>();
        static int s_BufferCount;
        const int MaxBufferSize = 1000;

        // Map original method -> hookId for prefix/postfix lookup
        static readonly ConcurrentDictionary<MethodBase, string> s_MethodToHook = new ConcurrentDictionary<MethodBase, string>();

        // --- Harmony reflection cache (loaded once) ---
        static Assembly s_HarmonyAsm;
        static Type s_HarmonyType;        // HarmonyLib.Harmony
        static Type s_HarmonyMethodType;  // HarmonyLib.HarmonyMethod
        static ConstructorInfo s_HarmonyCtor;      // Harmony(string id)
        static ConstructorInfo s_HarmonyMethodCtor; // HarmonyMethod(MethodInfo)
        static MethodInfo s_PatchMethod;           // harmony.Patch(...)
        static MethodInfo s_UnpatchAllMethod;      // harmony.UnpatchAll(string)
        static bool s_Loaded;
        static string s_LoadError;

        static string s_HarmonyDllHint; // path hint from CLI

        /// <summary>Set DLL path hint before calling Hook (called by TraceMethod).</summary>
        public static void SetHarmonyPath(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                s_HarmonyDllHint = path;
        }

        static string FindHarmonyDll()
        {
            // 1) Hint from CLI (next to unity-cli.exe)
            if (!string.IsNullOrEmpty(s_HarmonyDllHint) && File.Exists(s_HarmonyDllHint))
                return s_HarmonyDllHint;

            // 2) ~/.unity-cli/plugins/0Harmony.dll
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidate = Path.Combine(home, ".unity-cli", "plugins", "0Harmony.dll");
            if (File.Exists(candidate)) return candidate;

            return null;
        }

        static string EnsureHarmonyLoaded()
        {
            if (s_Loaded) return s_LoadError;

            // Check if already loaded in AppDomain
            s_HarmonyAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "0Harmony");

            if (s_HarmonyAsm == null)
            {
                var dllPath = FindHarmonyDll();
                if (dllPath == null)
                {
                    s_LoadError = "0Harmony.dll not found. Place it at: " +
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".unity-cli", "plugins", "0Harmony.dll");
                    s_Loaded = true;
                    return s_LoadError;
                }

                try
                {
                    s_HarmonyAsm = Assembly.LoadFrom(dllPath);
                }
                catch (Exception ex)
                {
                    s_LoadError = $"Failed to load 0Harmony.dll: {ex.Message}";
                    s_Loaded = true;
                    return s_LoadError;
                }
            }

            // Cache reflection handles
            try
            {
                s_HarmonyType = s_HarmonyAsm.GetType("HarmonyLib.Harmony");
                s_HarmonyMethodType = s_HarmonyAsm.GetType("HarmonyLib.HarmonyMethod");

                s_HarmonyCtor = s_HarmonyType.GetConstructor(new[] { typeof(string) });
                s_HarmonyMethodCtor = s_HarmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });

                s_PatchMethod = s_HarmonyType.GetMethod("Patch", new[]
                {
                    typeof(MethodBase), s_HarmonyMethodType, s_HarmonyMethodType,
                    s_HarmonyMethodType, s_HarmonyMethodType
                });

                s_UnpatchAllMethod = s_HarmonyType.GetMethod("UnpatchAll", new[] { typeof(string) });

                if (s_HarmonyCtor == null || s_HarmonyMethodCtor == null ||
                    s_PatchMethod == null || s_UnpatchAllMethod == null)
                {
                    s_LoadError = "Harmony API mismatch. Expected Harmony 2.x.";
                }
            }
            catch (Exception ex)
            {
                s_LoadError = $"Failed to resolve Harmony API: {ex.Message}";
            }

            s_Loaded = true;
            return s_LoadError;
        }

        public static (string hookId, string error) Hook(string typeName, string methodName, string assemblyHint,
            bool logParams, bool logReturn, bool logStack, string instanceFilter = null)
        {
            var loadError = EnsureHarmonyLoaded();
            if (loadError != null) return (null, loadError);

            // Resolve type
            Type targetType = ResolveType(typeName, assemblyHint);
            if (targetType == null)
                return (null, $"Type '{typeName}' not found." +
                    (string.IsNullOrEmpty(assemblyHint) ? " Try specifying --assembly." : ""));

            // Resolve method (handle overloads — pick first match)
            var candidates = targetType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == methodName)
                .ToArray();
            if (candidates.Length == 0)
                return (null, $"Method '{methodName}' not found on type '{targetType.FullName}'.");
            var original = candidates[0];

            // Generate hook ID
            string shortId = Guid.NewGuid().ToString("N").Substring(0, 6);
            string hookId = $"{targetType.Name}.{methodName}_{shortId}";

            // Check if same method already hooked
            if (s_MethodToHook.ContainsKey(original))
            {
                var existingId = s_MethodToHook[original];
                return (null, $"Method already hooked as '{existingId}'. Unhook first.");
            }

            // Patch with Harmony (via reflection)
            try
            {
                var harmonyInstance = s_HarmonyCtor.Invoke(new object[] { hookId });

                var prefix = typeof(HarmonyTracer).GetMethod(nameof(TracerPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                // void methods can't use __result in postfix
                bool isVoid = original.ReturnType == typeof(void);
                var postfixName = isVoid ? nameof(TracerPostfixVoid) : nameof(TracerPostfix);
                var postfix = typeof(HarmonyTracer).GetMethod(postfixName,
                    BindingFlags.Static | BindingFlags.NonPublic);

                var prefixHm = s_HarmonyMethodCtor.Invoke(new object[] { prefix });
                var postfixHm = s_HarmonyMethodCtor.Invoke(new object[] { postfix });

                s_PatchMethod.Invoke(harmonyInstance, new object[] { original, prefixHm, postfixHm, null, null });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return (null, $"Failed to patch: {inner.Message}");
            }

            var info = new HookInfo
            {
                HookId = hookId,
                TypeName = targetType.FullName,
                MethodName = methodName,
                AssemblyName = targetType.Assembly.GetName().Name,
                Original = original,
                LogParams = logParams,
                LogReturn = logReturn,
                LogStack = logStack,
                InstanceFilter = instanceFilter,
                CallCount = 0,
                HookedAt = DateTime.UtcNow,
            };

            s_Hooks[hookId] = info;
            s_MethodToHook[original] = hookId;
            return (hookId, null);
        }

        public static string Unhook(string hookId)
        {
            if (!s_Hooks.TryGetValue(hookId, out var info))
                return $"Hook '{hookId}' not found.";

            var loadError = EnsureHarmonyLoaded();
            if (loadError != null) return loadError;

            try
            {
                var harmonyInstance = s_HarmonyCtor.Invoke(new object[] { hookId });
                s_UnpatchAllMethod.Invoke(harmonyInstance, new object[] { hookId });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return $"Failed to unpatch: {inner.Message}";
            }

            s_MethodToHook.TryRemove(info.Original, out _);
            s_Hooks.Remove(hookId);
            return null;
        }

        public static List<object> ListHooks()
        {
            return s_Hooks.Values.Select(h => (object)new
            {
                hookId = h.HookId,
                typeName = h.TypeName,
                methodName = h.MethodName,
                assemblyName = h.AssemblyName,
                callCount = h.CallCount,
                logParams = h.LogParams,
                logReturn = h.LogReturn,
                logStack = h.LogStack,
                instanceFilter = h.InstanceFilter,
                hookedAt = h.HookedAt.ToString("o"),
            }).ToList();
        }

        public static (List<object> entries, int bufferSize) ReadBuffer(string hookId, int count)
        {
            if (count <= 0) count = 50;

            var all = s_Buffer.ToArray();
            IEnumerable<TraceEntry> filtered = all;
            if (!string.IsNullOrEmpty(hookId))
                filtered = filtered.Where(e => e.HookId == hookId);

            var entries = filtered
                .Reverse()
                .Take(count)
                .Reverse()
                .Select(e => (object)new
                {
                    hookId = e.HookId,
                    timestamp = e.Timestamp.ToString("HH:mm:ss.fff"),
                    direction = e.Direction,
                    instance = e.Instance,
                    parameters = e.Parameters,
                    returnValue = e.ReturnValue,
                    stackTrace = e.StackTrace,
                })
                .ToList();

            return (entries, all.Length);
        }

        public static void ClearBuffer(string hookId)
        {
            if (string.IsNullOrEmpty(hookId))
            {
                while (s_Buffer.TryDequeue(out _)) { }
                Interlocked.Exchange(ref s_BufferCount, 0);
            }
            else
            {
                var keep = s_Buffer.Where(e => e.HookId != hookId).ToArray();
                while (s_Buffer.TryDequeue(out _)) { }
                foreach (var entry in keep) s_Buffer.Enqueue(entry);
                Interlocked.Exchange(ref s_BufferCount, keep.Length);
            }
        }

        public static string ClearAll()
        {
            var hookIds = s_Hooks.Keys.ToList();
            foreach (var id in hookIds) Unhook(id);
            ClearBuffer(null);
            return null;
        }

        // --- Harmony Callbacks (parameter names are Harmony conventions) ---

        static void TracerPrefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (!s_MethodToHook.TryGetValue(__originalMethod, out var hookId)) return;
            if (!s_Hooks.TryGetValue(hookId, out var info)) return;

            // Resolve instance identity (GameObject name for Unity objects)
            string instanceId = ResolveInstanceId(__instance);

            // Instance filter: skip if filter set and instance doesn't match
            if (info.InstanceFilter != null)
            {
                if (instanceId == null || !instanceId.Contains(info.InstanceFilter)) return;
            }

            info.CallCount++;
            s_Hooks[hookId] = info;

            var entry = new TraceEntry
            {
                HookId = hookId,
                Timestamp = DateTime.Now,
                Direction = "call",
                Instance = instanceId,
            };

            if (info.LogParams && __args != null && __args.Length > 0)
            {
                entry.Parameters = new string[__args.Length];
                for (int i = 0; i < __args.Length; i++)
                    entry.Parameters[i] = SafeToString(__args[i]);
            }

            if (info.LogStack)
                entry.StackTrace = Environment.StackTrace;

            EnqueueEntry(entry);
        }

        static void TracerPostfix(MethodBase __originalMethod, object __result)
        {
            if (!s_MethodToHook.TryGetValue(__originalMethod, out var hookId)) return;
            if (!s_Hooks.TryGetValue(hookId, out var info)) return;

            if (!info.LogReturn) return;

            var entry = new TraceEntry
            {
                HookId = hookId,
                Timestamp = DateTime.Now,
                Direction = "return",
                ReturnValue = SafeToString(__result),
            };

            EnqueueEntry(entry);
        }

        static void TracerPostfixVoid(MethodBase __originalMethod)
        {
            // no-op for void methods (call is already logged by prefix)
        }

        // --- Helpers ---

        static void EnqueueEntry(TraceEntry entry)
        {
            s_Buffer.Enqueue(entry);
            if (Interlocked.Increment(ref s_BufferCount) > MaxBufferSize)
            {
                if (s_Buffer.TryDequeue(out _))
                    Interlocked.Decrement(ref s_BufferCount);
            }
        }

        static Type ResolveType(string typeName, string assemblyHint)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyHint) &&
                    !asm.GetName().Name.Equals(assemblyHint, StringComparison.OrdinalIgnoreCase))
                    continue;

                var type = asm.GetType(typeName, false, true);
                if (type != null) return type;
            }

            // Fallback: search by simple name
            if (!typeName.Contains("."))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.IsNullOrEmpty(assemblyHint) &&
                        !asm.GetName().Name.Equals(assemblyHint, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        foreach (var type in asm.GetTypes())
                        {
                            if (type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                                return type;
                        }
                    }
                    catch { /* Skip assemblies that fail to enumerate types */ }
                }
            }

            return null;
        }

        static string ResolveInstanceId(object instance)
        {
            if (instance == null) return null;
            try
            {
                // Try to get gameObject.name via reflection (works for Component, MonoBehaviour, BT Tasks)
                var type = instance.GetType();

                // 1) If it has an "agent" property (NodeCanvas Task) → agent.gameObject.name
                //    Walk up to find the base Task.agent (Component) to avoid ambiguity with generic overrides
                var current = type;
                while (current != null && current != typeof(object))
                {
                    var agentProp = current.GetProperty("agent",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (agentProp != null && typeof(UnityEngine.Component).IsAssignableFrom(agentProp.PropertyType))
                    {
                        var agent = agentProp.GetValue(instance) as UnityEngine.Component;
                        if (agent != null && agent.gameObject != null)
                            return agent.gameObject.name + " [" + agent.gameObject.GetInstanceID() + "]";
                        break;
                    }
                    current = current.BaseType;
                }

                // 2) If it's a Component → gameObject.name
                if (instance is UnityEngine.Component comp && comp.gameObject != null)
                    return comp.gameObject.name + " [" + comp.gameObject.GetInstanceID() + "]";

                // 3) If it's a GameObject
                if (instance is UnityEngine.GameObject go)
                    return go.name + " [" + go.GetInstanceID() + "]";

                // 4) Fallback
                return SafeToString(instance);
            }
            catch
            {
                return SafeToString(instance);
            }
        }

        static string SafeToString(object obj)
        {
            if (obj == null) return "null";
            try { return obj.ToString(); }
            catch { return $"<{obj.GetType().Name}>"; }
        }
    }
}
