// Run with explicit --csc and --dotnet so the probe sees cold discovery fields.
var tool = typeof(UnityCliConnector.Tools.ExecuteCsharp);
var flags = BindingFlags.Static | BindingFlags.NonPublic;
var findCsc = tool.GetMethod("FindCsc", flags);
var findDotnet = tool.GetMethod("FindDotnet", flags);
var cscField = tool.GetField("AutoCscPath", flags);
var dotnetField = tool.GetField("AutoDotnetPath", flags);
var beforeCsc = cscField?.GetValue(null);
var beforeDotnet = dotnetField?.GetValue(null);
var cscTimes = new List<double>();
var dotnetTimes = new List<double>();
string csc = null, dotnet = null;
for (int i = 0; i < 5; i++)
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    csc = (string)findCsc.Invoke(null, new object[] { null });
    cscTimes.Add(timer.Elapsed.TotalMilliseconds);
    timer.Restart();
    dotnet = (string)findDotnet.Invoke(null, new object[] { null });
    dotnetTimes.Add(timer.Elapsed.TotalMilliseconds);
}
var referenceTimes = new List<double>();
var keyTimes = new List<double>();
int referenceCount = 0;
for (int i = 0; i < 20; i++)
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var references = new List<Assembly>();
    var added = new HashSet<string>();
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        try
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            if (!added.Add(asm.GetName().Name)) continue;
            references.Add(asm);
        }
        catch { }
    }
    referenceTimes.Add(timer.Elapsed.TotalMilliseconds);
    referenceCount = references.Count;
    timer.Restart();
    tool.GetMethod("BuildCacheKey", flags).Invoke(null, new object[] { "return 432;", references, csc, dotnet });
    keyTimes.Add(timer.Elapsed.TotalMilliseconds);
}
// This is an intentionally copied measurement of the hit path, not new production logic.
var p = new Newtonsoft.Json.Linq.JObject { ["code"] = "return -432123;", ["csc"] = csc, ["dotnet"] = dotnet };
UnityCliConnector.Tools.ExecuteCsharp.HandleCommand(p);
UnityCliConnector.Tools.ExecuteCsharp.HandleCommand(p);
var hitTimer = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 20; i++)
{
    var r = UnityCliConnector.Tools.ExecuteCsharp.HandleCommand(p) as UnityCliConnector.SuccessResponse;
    if (r == null || (int)r.data != -432123) throw new Exception("Cache-hit regression");
}
return new Dictionary<string, object> {
    ["cscBefore"] = beforeCsc,
    ["dotnetBefore"] = beforeDotnet,
    ["cscAfter"] = cscField?.GetValue(null),
    ["dotnetAfter"] = dotnetField?.GetValue(null),
    ["resolvedCsc"] = csc,
    ["resolvedDotnet"] = dotnet,
    ["cscFirstMs"] = cscTimes[0],
    ["dotnetFirstMs"] = dotnetTimes[0],
    ["cscSubsequentMeanMs"] = cscTimes.Skip(1).Average(),
    ["dotnetSubsequentMeanMs"] = dotnetTimes.Skip(1).Average(),
    ["referenceCount"] = referenceCount,
    ["referenceSelectionMeanMs"] = referenceTimes.Skip(1).Average(),
    ["keyMeanMs"] = keyTimes.Skip(1).Average(),
    ["internalHitMeanMs"] = hitTimer.Elapsed.TotalMilliseconds / 20
};
