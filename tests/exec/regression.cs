if (Application.dataPath.Replace('\\', '/') != "D:/Projects/ProjectD/client/Assets" || EditorApplication.isPlaying)
    throw new Exception("Run only in ProjectD main Edit Mode");
var marker = "unity-cli-discovery-regression-counter";
var before = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null);
var code = "var n = (int)AppDomain.CurrentDomain.GetData(\"" + marker + "\") + 1;\nAppDomain.CurrentDomain.SetData(\"" + marker + "\", n);\nreturn n;";
AppDomain.CurrentDomain.SetData(marker, 0);
try
{
    for (int i = 1; i <= 20; i++)
    {
        var response = UnityCliConnector.Tools.ExecuteCsharp.HandleCommand(new Newtonsoft.Json.Linq.JObject { ["code"] = code }) as UnityCliConnector.SuccessResponse;
        if (response == null || (int)response.data != i) throw new Exception("Cached code did not execute");
    }
    var after = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null);
    if (after - before > 1) throw new Exception("Duplicate execution assemblies");
    return new Dictionary<string, object> { ["counterExecutions"] = 20, ["assemblyDelta"] = after - before, ["playing"] = EditorApplication.isPlaying };
}
finally { AppDomain.CurrentDomain.SetData(marker, null); }
