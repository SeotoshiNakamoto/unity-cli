using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityCliConnector;

namespace UnityEditor
{
    public static class TypeCache
    {
        public static Type[] Types;
        public static IEnumerable<Type> GetTypesWithAttribute<T>() => Types;
    }
}
namespace UnityEngine
{
    public static class Debug
    {
        public static readonly List<string> Errors = new List<string>();
        public static void LogError(object message) => Errors.Add(message.ToString());
    }
}
[UnityCliTool(Name = "explicit", Description = "first", Group = "fixture")]
public static class FirstTool
{
    public static object HandleCommand(JObject p) => null;
    public class Parameters { [ToolParameter("required", Required = true)] public string SomeValue { get; set; } }
}
[UnityCliTool(Name = "explicit", Description = "second")]
public static class DuplicateTool { public static object HandleCommand(JObject p) => null; }
[UnityCliTool]
public static class MixedHTTPTool { public static object HandleCommand(JObject p) => null; }
[UnityCliTool(Name = "bad_signature")]
public static class BadTool { public static object HandleCommand(string p) => null; }
public static class Fixture
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main()
    {
        try
        {
            UnityEditor.TypeCache.Types = new[] { typeof(int), typeof(FirstTool), typeof(DuplicateTool), typeof(MixedHTTPTool), typeof(BadTool) };
            Check(ToolDiscovery.FindHandler("explicit").DeclaringType == typeof(FirstTool), "First duplicate not retained");
            Check(UnityEngine.Debug.Errors.Count == 1, "Handler duplicate not logged");
            Check(ToolDiscovery.FindHandler("mixed_http_tool").DeclaringType == typeof(MixedHTTPTool), "snake_case fallback");
            Check(ToolDiscovery.FindHandler("bad_signature") == null, "Signature filter");
            Check(ToolDiscovery.FindHandler("missing") == null, "Unknown tool");
            UnityEngine.Debug.Errors.Clear();
            var schemas = JArray.FromObject(ToolDiscovery.GetToolSchemas());
            Check(schemas.Count == 3 && UnityEngine.Debug.Errors.Count == 1, "Schema class/duplicate filtering");
            var first = schemas.Single(x => x["name"].ToString() == "explicit");
            Check(first["description"].ToString() == "first" && first["group"].ToString() == "fixture", "Schema first duplicate");
            Check(first["parameters"][0]["name"].ToString() == "some_value" && (bool)first["parameters"][0]["required"], "Parameter schema changed");
            UnityEditor.TypeCache.Types = new[] { typeof(DuplicateTool) };
            Check(ToolDiscovery.FindHandler("explicit").DeclaringType == typeof(DuplicateTool), "Added an own cache");
            Check(ToolDiscovery.GetToolSchemas().Count == 1, "Schemas did not use current TypeCache candidates");
            Console.WriteLine("PASS: names, JObject signature, IsClass, duplicates/first selection, schemas, no own cache");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
