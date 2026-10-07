// Fixed 200 distinct snippets, with a 256 MiB Mono/native growth limit.
if (Application.dataPath.Replace('\\', '/') != "D:/Projects/ProjectD/client/Assets" || EditorApplication.isPlaying)
    throw new Exception("Run only in ProjectD main Edit Mode");
const int repetitions = 200;
const long limit = 256L * 1024 * 1024;
int Count() => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null);
long MonoBytes() => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
long NativeBytes() => UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var before = Count();
var beforeMono = MonoBytes();
var beforeNative = NativeBytes();
var times = new List<double>();
var marker = Guid.NewGuid().ToString("N");
for (int i = 0; i < repetitions; i++)
{
    if (i % 10 == 0 && (MonoBytes() - beforeMono >= limit || NativeBytes() - beforeNative >= limit))
        throw new Exception("256 MiB growth limit exceeded");
    var p = new Newtonsoft.Json.Linq.JObject { ["code"] = "return " + (43200000 + i) + "; // " + marker };
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var response = UnityCliConnector.Tools.ExecuteCsharp.HandleCommand(p) as UnityCliConnector.SuccessResponse;
    if (response == null || (int)response.data != 43200000 + i) throw new Exception("exec failed");
    times.Add(timer.Elapsed.TotalMilliseconds);
}
var preGCMono = MonoBytes() - beforeMono;
var preGCNative = NativeBytes() - beforeNative;
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var after = Count();
if (after - before != repetitions) throw new Exception("Unexpected assembly delta");
return new Dictionary<string, object> {
    ["repetitions"] = repetitions, ["before"] = before, ["after"] = after, ["delta"] = after - before,
    ["meanCompileMs"] = times.Average(), ["preGCMonoDelta"] = preGCMono, ["postGCMonoDelta"] = MonoBytes() - beforeMono,
    ["preGCNativeDelta"] = preGCNative, ["postGCNativeDelta"] = NativeBytes() - beforeNative
};
