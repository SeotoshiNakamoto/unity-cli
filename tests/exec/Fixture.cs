using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using UnityCliConnector;
using UnityCliConnector.Tools;

// Managed doubles only: this fixture does not claim Unity-native validation.
namespace UnityEngine
{
    public enum RuntimePlatform { WindowsEditor }
    public static class Application
    {
        public static RuntimePlatform platform = RuntimePlatform.WindowsEditor;
        public static string unityVersion = "fixture";
        public static string dataPath = "fixture";
    }
}
namespace UnityEngine.SceneManagement { public class Stub { } }
namespace UnityEngine.Profiling
{
    public static class Profiler
    {
        public static long GetMonoUsedSizeLong() => 0;
        public static long GetTotalAllocatedMemoryLong() => 0;
    }
}
namespace UnityEditor.SceneManagement { public class Stub { } }
namespace UnityEditorInternal { public class Stub { } }
namespace UnityEditor
{
    public static class EditorApplication
    {
        public static string applicationContentsPath;
        public static bool isCompiling, isPlaying;
    }
    public static class AssemblyReloadEvents
    {
        public static event Action beforeAssemblyReload;
        public static void Reload() => beforeAssemblyReload?.Invoke();
    }
}
namespace UnityEditor.Compilation
{
    public class Assembly { }
    public static class CompilationPipeline
    {
        public static event Action<object> compilationStarted;
        public static event Action<object> compilationFinished;
        public static void Start() => compilationStarted?.Invoke(null);
        public static void Finish() => compilationFinished?.Invoke(null);
    }
}

public static class ExecFixture
{
    public static int Counter;
    private static string csc, dotnet;
    private static readonly FieldInfo CacheField = typeof(ExecuteCsharp).GetField("CompiledMethods", BindingFlags.NonPublic | BindingFlags.Static);
    private static int CacheCount => ((System.Collections.IDictionary)CacheField.GetValue(null)).Count;
    private static int AssemblyCount => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null);

    private static object Run(string code, string usings = null, string compiler = null, string host = null)
    {
        var p = new JObject { ["code"] = code, ["csc"] = compiler ?? csc, ["dotnet"] = host ?? dotnet };
        if (usings != null) p["usings"] = usings;
        return ExecuteCsharp.HandleCommand(p);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static object Value(object response)
    {
        var success = response as SuccessResponse;
        Check(success != null, "Expected success: " + (response as ErrorResponse)?.message);
        return success.data;
    }
    private static void Error(object response, string prefix)
    {
        Check(response is ErrorResponse e && e.message.StartsWith(prefix), "Expected " + prefix);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb, pageFaultCount;
        public UIntPtr peakWorkingSet, workingSet, quotaPeakPaged, quotaPaged, quotaPeakNonPaged, quotaNonPaged, pagefile, peakPagefile, privateBytes;
    }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
    private static long PrivateBytes()
    {
        var counters = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
        Check(GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb), "Cannot measure process private bytes");
        return (long)counters.privateBytes.ToUInt64();
    }

    private static long GCBytes()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(true);
    }
    private static string Key(List<Assembly> refs)
    {
        return (string)typeof(ExecuteCsharp).GetMethod("BuildCacheKey", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { "source", refs, csc, dotnet });
    }

    private static void CheckDiscovery(string temp)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var cscField = typeof(ExecuteCsharp).GetField("AutoCscPath", flags);
        var dotnetField = typeof(ExecuteCsharp).GetField("AutoDotnetPath", flags);
        var findCsc = typeof(ExecuteCsharp).GetMethod("FindCsc", flags);
        var findDotnet = typeof(ExecuteCsharp).GetMethod("FindDotnet", flags);
        var originalContent = UnityEditor.EditorApplication.applicationContentsPath;
        var originalCsc = cscField.GetValue(null);
        var originalDotnet = dotnetField.GetValue(null);
        var root = Path.Combine(temp, "discovery");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        string Find(MethodInfo method, string path = null) => (string)method.Invoke(null, new object[] { path });
        try
        {
            UnityEditor.EditorApplication.applicationContentsPath = root;
            cscField.SetValue(null, null);
            dotnetField.SetValue(null, null);
            Check(Find(findCsc) == null && Find(findDotnet) == "dotnet.exe", "Discovery failure/fallback changed");
            Check(cscField.GetValue(null) == null && dotnetField.GetValue(null) == null, "Failed discovery was cached");
            var exe = Path.Combine(root, "csc.exe");
            File.WriteAllText(exe, "fixture");
            Check(Find(findCsc) == exe, "Windows csc.exe fallback");
            var dll1 = Path.Combine(first, "csc.dll");
            var host1 = Path.Combine(first, "dotnet.exe");
            File.WriteAllText(dll1, "fixture");
            File.WriteAllText(host1, "fixture");
            cscField.SetValue(null, null);
            Check(Find(findCsc) == dll1, "csc.dll must precede csc.exe on discovery");
            Check(Find(findDotnet) == host1, "Dotnet discovery");
            UnityEditor.EditorApplication.applicationContentsPath = Path.Combine(root, "missing");
            Check(Find(findCsc) == dll1 && Find(findDotnet) == host1, "Cached paths should avoid traversal");
            Check(Find(findCsc, "manual-csc") == "manual-csc" && Find(findDotnet, "manual-dotnet") == "manual-dotnet", "Override paths changed");
            Check((string)cscField.GetValue(null) == dll1 && (string)dotnetField.GetValue(null) == host1, "Overrides overwrote auto paths");
            UnityEditor.EditorApplication.applicationContentsPath = root;
            var dll2 = Path.Combine(second, "csc.dll");
            var host2 = Path.Combine(second, "dotnet.exe");
            File.WriteAllText(dll2, "fixture");
            File.WriteAllText(host2, "fixture");
            File.Delete(dll1); File.Delete(host1);
            Check(Find(findCsc) == dll2 && Find(findDotnet) == host2, "Missing cached paths were not rediscovered");
            File.Delete(dll2); File.Delete(host2); File.Delete(exe);
            Check(Find(findCsc) == null && Find(findDotnet) == "dotnet.exe", "Removed-tool fallback changed");
            File.WriteAllText(dll2, "fixture"); File.WriteAllText(host2, "fixture");
            Check(Find(findCsc) == dll2 && Find(findDotnet) == host2, "Failed lookup was not retried");
            Console.WriteLine("PASS: discovery reuse, dll/exe priority, overrides, removed files, failure retry and PATH fallback");
        }
        finally
        {
            UnityEditor.EditorApplication.applicationContentsPath = originalContent;
            cscField.SetValue(null, originalCsc);
            dotnetField.SetValue(null, originalDotnet);
            Directory.Delete(root, true);
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            csc = args[0]; dotnet = args[1];
            UnityEditor.EditorApplication.applicationContentsPath = args[2];
            Console.WriteLine("Warm-up: {0:F2} ms, private bytes={1}, assemblies={2}", Stopwatch.StartNew().Elapsed.TotalMilliseconds, PrivateBytes(), AssemblyCount);
            GCBytes();
            Key(new List<Assembly>());
            Value(ExecuteCsharp.HandleCommand(new JObject { ["code"] = "return 0;", ["usings"] = new JArray("System.Text"), ["csc"] = csc, ["dotnet"] = dotnet }));
            Value(Run("return 0;")); // Warm fingerprint/JIT/JSON dependencies, not the tested source.
            Value(Run("return 0;"));
            Value(Run("return 0;"));
            var beforeAssemblies = AssemblyCount;
            var beforeBytes = GCBytes();
            var beforePrivate = PrivateBytes();
            var times = new List<double>();
            var repetitions = int.Parse(args[5]);
            for (int i = 0; i < repetitions; i++)
            {
                var timer = Stopwatch.StartNew();
                Check((int)Value(Run("return 432;")) == 432, "Wrong repeat result");
                times.Add(timer.Elapsed.TotalMilliseconds);
                if (i < 3 || (i + 1) % 100 == 0) Console.WriteLine("Call {0}: {1:F2} ms, assembly delta={2}, cache={3}", i + 1, times[i], AssemblyCount - beforeAssemblies, CacheCount);
                if (i % 25 == 0)
                    Check(PrivateBytes() - beforePrivate < 256L * 1024 * 1024 && GC.GetTotalMemory(false) - beforeBytes < 256L * 1024 * 1024, "256 MiB safety limit");
            }
            var preGCBytes = GC.GetTotalMemory(false);
            var afterBytes = GCBytes();
            Check(AssemblyCount == beforeAssemblies + 1, "Identical code loaded duplicate assemblies");
            Console.WriteLine("{7} identical calls: assembly delta={0}, cold={1:F2} ms, first100 hits={2:F2} ms, last100={3:F2} ms, managed preGC delta={4}, postGC delta={5}, process private delta={6}",
                AssemblyCount - beforeAssemblies, times[0], times.Skip(1).Take(99).Average(), times.Skip(Math.Max(1, repetitions - 100)).Average(), preGCBytes - beforeBytes, afterBytes - beforeBytes, PrivateBytes() - beforePrivate, repetitions);
            for (int i = 1; i <= 20; i++) Check((int)Value(Run("return ++ExecFixture.Counter;")) == i, "Cached result instead of execution");
            var count = AssemblyCount;
            Value(Run("return 433;"));
            Check(AssemblyCount == count + 1, "Different source did not compile");
            count = AssemblyCount;
            Value(Run("return 432;", "System.Text"));
            Check(AssemblyCount == count + 1, "Different usings did not compile");
            count = AssemblyCount;
            Value(Run("return 432;", "System.Text"));
            Check(AssemblyCount == count, "Usings cache miss");
            Value(ExecuteCsharp.HandleCommand(new JObject { ["code"] = "return 432;", ["usings"] = new JArray("System.Text"), ["csc"] = csc, ["dotnet"] = dotnet }));
            Check(AssemblyCount == count, "Array usings did not reuse identical source");
            Error(Run("return nonexistent_symbol;"), "Compile error:");
            Error(Run("return nonexistent_symbol;"), "Compile error:");
            Check(AssemblyCount == count, "Compile failures loaded assemblies");
            for (int i = 0; i < 3; i++) Error(Run("throw new InvalidOperationException(\"fixture\");\nreturn null;"), "Runtime error: InvalidOperationException: fixture");
            Check(AssemblyCount == count + 1, "Runtime failure recompiled on hit");
            Check(Value(Run("return null;")) == null, "Null serialization");
            Check(((List<object>)Value(Run("return new[] { 1, 2 };"))).Count == 2, "Collection serialization");
            var positional = new JObject { ["args"] = new JArray("return 432;"), ["csc"] = csc, ["dotnet"] = dotnet };
            Check((int)Value(ExecuteCsharp.HandleCommand(positional)) == 432, "Positional dispatch");
            Error(ExecuteCsharp.HandleCommand(new JObject()), "'code' required");

            var refPath = args[3];
            var oldKey = Key(new List<Assembly>());
            var reference = Assembly.LoadFrom(refPath);
            Check(Key(new List<Assembly> { reference }) != oldKey, "Added reference key unchanged");
            Check((int)Value(Run("return CacheReference.Value;")) == 1, "Reference v1");
            count = AssemblyCount;
            Value(Run("return 432;"));
            Check(AssemblyCount == count + 1, "Added reference did not invalidate cached source");
            var reference2 = Assembly.ReflectionOnlyLoadFrom(args[4]);
            Check(reference.FullName == reference2.FullName && reference.ManifestModule.ModuleVersionId != reference2.ManifestModule.ModuleVersionId, "Reference fixture identities");
            Check(Key(new List<Assembly> { reference }) != Key(new List<Assembly> { reference2 }), "Changed MVID/location key unchanged");
            Check(Key(new List<Assembly> { reference, reference2 }) != Key(new List<Assembly> { reference2, reference }), "Reference order ignored");
            var compilerCopy = Path.Combine(Path.GetDirectoryName(refPath), "csc.dll");
            File.Copy(csc, compilerCopy);
            var keyMethod = typeof(ExecuteCsharp).GetMethod("BuildCacheKey", BindingFlags.NonPublic | BindingFlags.Static);
            var copyKey = (string)keyMethod.Invoke(null, new object[] { "source", new List<Assembly>(), compilerCopy, dotnet });
            Check(copyKey != Key(new List<Assembly>()), "Compiler path key unchanged");
            var compilerBytes = File.ReadAllBytes(compilerCopy);
            compilerBytes[compilerBytes.Length - 1] ^= 1;
            File.WriteAllBytes(compilerCopy, compilerBytes);
            Check(copyKey == (string)keyMethod.Invoke(null, new object[] { "source", new List<Assembly>(), compilerCopy, dotnet }), "Compiler contents must not be hashed");
            var configPath = Path.Combine(Path.GetDirectoryName(refPath), "csc.runtimeconfig.json");
            var noConfigKey = (string)keyMethod.Invoke(null, new object[] { "source", new List<Assembly>(), compilerCopy, dotnet });
            File.WriteAllText(configPath, "{}");
            Check(noConfigKey == (string)keyMethod.Invoke(null, new object[] { "source", new List<Assembly>(), compilerCopy, dotnet }), "Compiler folder must not be scanned");
            var rspPath = Path.Combine(Path.GetDirectoryName(refPath), "csc.rsp");
            File.WriteAllText(rspPath, "-define:FIXTURE");
            Check(keyMethod.Invoke(null, new object[] { "source", new List<Assembly>(), compilerCopy, dotnet }) == null, "Ambient response file must bypass cache");
            File.Delete(rspPath);
            var hostCopy = Path.Combine(Path.GetDirectoryName(refPath), "dotnet.exe");
            File.Copy(dotnet, hostCopy);
            Check(Key(new List<Assembly>()) != (string)typeof(ExecuteCsharp).GetMethod("BuildCacheKey", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { "source", new List<Assembly>(), csc, hostCopy }), "Host path key unchanged");
            Check((string)typeof(ExecuteCsharp).GetMethod("BuildCacheKey", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { "source", new List<Assembly>(), csc, "dotnet" }) == null, "Unknown PATH host must bypass cache");
            var environmentKey = Key(new List<Assembly>());
            Environment.SetEnvironmentVariable("EXEC_CACHE_FIXTURE", "changed");
            Check(environmentKey == Key(new List<Assembly>()), "Environment must not be in the key");
            Environment.SetEnvironmentVariable("EXEC_CACHE_FIXTURE", null);
            var cwd = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = Path.GetDirectoryName(refPath);
                Check(environmentKey == Key(new List<Assembly>()), "Working directory must not be in the key");
                File.WriteAllText(rspPath, "-define:FIXTURE");
                Check(Key(new List<Assembly>()) == null, "Current-directory response file must bypass cache");
            }
            finally { File.Delete(rspPath); Environment.CurrentDirectory = cwd; }

            Check(CacheCount > 0, "Empty cache");
            count = AssemblyCount;
            UnityEditor.Compilation.CompilationPipeline.Start();
            Check(CacheCount == 0 && AssemblyCount == count, "Invalidation must not claim unload");
            UnityEditor.EditorApplication.isCompiling = true;
            Value(Run("return 432;"));
            Check(CacheCount == 0, "Cached during compilation");
            UnityEditor.EditorApplication.isCompiling = false;
            Value(Run("return 432;"));
            Check(CacheCount > 0, "Cache not restored after compilation");
            UnityEditor.Compilation.CompilationPipeline.Finish();
            Check(CacheCount == 0, "Compilation finished did not clear cache");
            Value(Run("return 432;"));
            UnityEditor.AssemblyReloadEvents.Reload();
            Check(CacheCount == 0, "Reload callback did not clear cache");
            count = AssemblyCount;
            Value(Run("return 432;")); Value(Run("return 432;"));
            Check(AssemblyCount == count + 1, "After reload callback cache not rebuilt exactly once");
            CheckDiscovery(Path.GetDirectoryName(refPath));
            Value(ExecuteCsharp.HandleCommand(new JObject { ["code"] = "return 432;" }));
            // Compile the retained native diagnostic; its version guard must prevent it
            // from running an Editor experiment against these managed doubles.
            Error(Run(File.ReadAllText(args[6])), "Runtime error: Exception: Wrong Unity version");
            Console.WriteLine("PASS: real csc/Mono compilation and load; counter, source, usings, reference identity/order, compiler/host paths, excluded file contents/environment/cwd, errors, serialization, auto-discovery, simulated Unity invalidation events. Not a Unity Editor integration test.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
