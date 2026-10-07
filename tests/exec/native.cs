// Run ONLY with: unity-cli --project D:/Projects/ProjectD/client exec --file tests/exec/native.cs --timeout 3600000
// Requires a fresh Editor domain. Bounded synchronous diagnostic, not a Unity asset.
const int repetitions = 600;
const long growthLimit = 256L * 1024 * 1024;
var content = EditorApplication.applicationContentsPath;
var csc = Directory.GetFiles(content, "csc.dll", SearchOption.AllDirectories).First();
var dotnet = Directory.GetFiles(content, "dotnet.exe", SearchOption.AllDirectories).First();
var marker = "unity-cli-exec-cache-native-counter";

object Run(string code, string usings = null)
{
    var p = new Newtonsoft.Json.Linq.JObject { ["code"] = code, ["csc"] = csc, ["dotnet"] = dotnet };
    if (usings != null) p["usings"] = usings;
    return UnityCliConnector.Tools.ExecuteCsharp.HandleCommand(p);
}
object Value(object response)
{
    if (response is UnityCliConnector.SuccessResponse success) return success.data;
    throw new Exception("exec failed: " + ((UnityCliConnector.ErrorResponse)response).message);
}
int Assemblies() => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null);
long MonoBytes() => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
long NativeBytes() => UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

Check(Application.unityVersion == "6000.5.5f1", "Wrong Unity version");
Check(Application.dataPath.Replace('\\', '/') == "D:/Projects/ProjectD/client/Assets", "Wrong project");
Check(!EditorApplication.isPlaying && !EditorApplication.isCompiling, "Run in stable Edit Mode");
var times = new List<double>(repetitions);
var initializationMono = MonoBytes();
var initializationNative = NativeBytes();
var firstTimer = System.Diagnostics.Stopwatch.StartNew();
Value(Run("return -432;"));
Value(Run("return -432;"));
var initializationMs = firstTimer.Elapsed.TotalMilliseconds;
Collect();
var beforeAssemblies = Assemblies();
var beforeMono = MonoBytes();
var beforeNative = NativeBytes();
for (int i = 0; i < repetitions; i++)
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    Check((int)Value(Run("return 432;")) == 432, "Wrong repeat result");
    times.Add(timer.Elapsed.TotalMilliseconds);
    if (i % 25 == 0)
        Check(MonoBytes() - beforeMono < growthLimit && NativeBytes() - beforeNative < growthLimit, "256 MiB memory growth limit exceeded");
}
var preGCMonoDelta = MonoBytes() - beforeMono;
var preGCNativeDelta = NativeBytes() - beforeNative;
Collect();
var postGCMonoDelta = MonoBytes() - beforeMono;
var postGCNativeDelta = NativeBytes() - beforeNative;
var repeatAssemblyDelta = Assemblies() - beforeAssemblies;
Check(repeatAssemblyDelta == 1, "Identical calls did not load exactly one assembly; inspect changed references/cache bypass");

try
{
    AppDomain.CurrentDomain.SetData(marker, 0);
    var counterCode = "var n = (int)AppDomain.CurrentDomain.GetData(\"" + marker + "\") + 1;\nAppDomain.CurrentDomain.SetData(\"" + marker + "\", n);\nreturn n;";
    for (int i = 1; i <= 20; i++) Check((int)Value(Run(counterCode)) == i, "Code was not executed on hit");
    var beforeUnique = Assemblies();
    var uniqueTimer = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 100; i++)
    {
        Value(Run("return " + (5000 + i) + ";"));
        if (i % 10 == 0)
            Check(MonoBytes() - beforeMono < growthLimit && NativeBytes() - beforeNative < growthLimit, "Distinct-source memory limit exceeded");
    }
    var uniqueMeanMs = uniqueTimer.Elapsed.TotalMilliseconds / 100;
    Check(Assemblies() == beforeUnique + 100, "Unexpected distinct-source behavior");
    var uniquePreGCMonoDelta = MonoBytes() - beforeMono;
    var uniquePreGCNativeDelta = NativeBytes() - beforeNative;
    Collect();
    var uniquePostGCMonoDelta = MonoBytes() - beforeMono;
    var uniquePostGCNativeDelta = NativeBytes() - beforeNative;
    Check(Assemblies() == beforeUnique + 100, "GC unexpectedly changed distinct-source assembly count");
    var beforeUsings = Assemblies();
    Value(Run("return 432;", "System.Text"));
    Value(Run("return 432;", "System.Text"));
    Check(Assemblies() == beforeUsings + 1, "Using change/reuse failed");
    for (int i = 0; i < 2; i++)
        Check(Run("return unity_cli_missing_symbol;") is UnityCliConnector.ErrorResponse error && error.message.StartsWith("Compile error:"), "Compile error response changed");
    var beforeExceptions = Assemblies();
    for (int i = 0; i < 3; i++)
        Check(Run("throw new InvalidOperationException(\"native-cache-probe\");\nreturn null;") is UnityCliConnector.ErrorResponse error && error.message.StartsWith("Runtime error: InvalidOperationException:"), "Runtime error response changed");
    Check(Assemblies() == beforeExceptions + 1, "Runtime exception was recompiled on hit");
    return new Dictionary<string, object>
    {
        ["unityVersion"] = Application.unityVersion,
        ["repetitions"] = repetitions,
        ["beforeExecAssemblies"] = beforeAssemblies,
        ["afterRepeatExecAssemblies"] = beforeAssemblies + repeatAssemblyDelta,
        ["repeatAssemblyDelta"] = repeatAssemblyDelta,
        ["counterExecutions"] = 20,
        ["beforeUniqueExecAssemblies"] = beforeUnique,
        ["distinctSourceAssemblies"] = 100,
        ["afterAllExecAssemblies"] = Assemblies(),
        ["distinctSourceMeanMs"] = uniqueMeanMs,
        ["distinctSourcePreGCMonoDeltaBytes"] = uniquePreGCMonoDelta,
        ["distinctSourcePostGCMonoDeltaBytes"] = uniquePostGCMonoDelta,
        ["distinctSourcePreGCNativeDeltaBytes"] = uniquePreGCNativeDelta,
        ["distinctSourcePostGCNativeDeltaBytes"] = uniquePostGCNativeDelta,
        ["initializationMs"] = initializationMs,
        ["initializationMonoPostGCBytes"] = beforeMono - initializationMono,
        ["initializationNativePostGCBytes"] = beforeNative - initializationNative,
        ["beforeMonoBytes"] = beforeMono,
        ["beforeNativeAllocatedBytes"] = beforeNative,
        ["firstCompileMs"] = times[0],
        ["first99HitsMeanMs"] = times.Skip(1).Take(99).Average(),
        ["last100HitsMeanMs"] = times.Skip(500).Average(),
        ["preGCMonoDeltaBytes"] = preGCMonoDelta,
        ["preGCNativeAllocatedDeltaBytes"] = preGCNativeDelta,
        ["postGCMonoDeltaBytes"] = postGCMonoDelta,
        ["postGCNativeAllocatedDeltaBytes"] = postGCNativeDelta,
        ["gcUnloadedAssemblies"] = false
    };
}
finally { AppDomain.CurrentDomain.SetData(marker, null); }
