// Read-only inventory and lookup timing. Run only in ProjectD main Edit Mode.
if (Application.dataPath.Replace('\\', '/') != "D:/Projects/ProjectD/client/Assets" || EditorApplication.isPlaying)
    throw new Exception("Run only in ProjectD main Edit Mode");
var all = new List<Type>();
foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
{
    try { all.AddRange(assembly.GetTypes()); }
    catch (ReflectionTypeLoadException) { }
}
var oldTypes = all.Where(t => t.IsClass && t.GetCustomAttribute<UnityCliConnector.UnityCliToolAttribute>() != null).ToList();
var cacheTypes = UnityEditor.TypeCache.GetTypesWithAttribute<UnityCliConnector.UnityCliToolAttribute>()
    .Where(t => t.IsClass && t.GetCustomAttribute<UnityCliConnector.UnityCliToolAttribute>() != null).ToList();
string Name(Type t) => t.GetCustomAttribute<UnityCliConnector.UnityCliToolAttribute>().Name ?? UnityCliConnector.StringCaseUtility.ToSnakeCase(t.Name);
Dictionary<string, object> Entry(Type t)
{
    var method = t.GetMethod("HandleCommand", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Newtonsoft.Json.Linq.JObject) }, null);
    var assembly = t.Assembly;
    var location = assembly.IsDynamic ? "" : assembly.Location;
    return new Dictionary<string, object> {
        ["name"] = Name(t), ["type"] = t.FullName, ["assembly"] = assembly.GetName().Name,
        ["location"] = location, ["hasHandler"] = method != null
    };
}
var oldNames = oldTypes.Select(t => t.AssemblyQualifiedName).OrderBy(n => n).ToArray();
var cacheNames = cacheTypes.Select(t => t.AssemblyQualifiedName).OrderBy(n => n).ToArray();
var lookup = new List<double>();
for (int i = 0; i < 21; i++)
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    if (UnityCliConnector.ToolDiscovery.FindHandler("console") == null || UnityCliConnector.ToolDiscovery.FindHandler("exec") == null)
        throw new Exception("Missing connector handlers");
    lookup.Add(timer.Elapsed.TotalMilliseconds / 2);
}
var duplicateNames = oldTypes.GroupBy(Name).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
return new Dictionary<string, object> {
    ["scanCount"] = oldTypes.Count, ["typeCacheCount"] = cacheTypes.Count,
    ["sameTypes"] = oldNames.SequenceEqual(cacheNames),
    ["scanOnly"] = oldNames.Except(cacheNames).ToArray(), ["typeCacheOnly"] = cacheNames.Except(oldNames).ToArray(),
    ["duplicates"] = duplicateNames, ["inventory"] = oldTypes.Select(Entry).ToArray(),
    ["scanOrder"] = oldTypes.Select(Name).ToArray(), ["typeCacheOrder"] = cacheTypes.Select(Name).ToArray(),
    ["handlerLookupMeanMs"] = lookup.Skip(1).Average(),
    ["execAssemblies"] = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("__CliDynamic") != null)
};
