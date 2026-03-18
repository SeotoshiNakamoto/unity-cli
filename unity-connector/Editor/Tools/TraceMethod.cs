using Newtonsoft.Json.Linq;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Description = "Hook methods at runtime for call tracing. Actions: hook, unhook, list, read, clear.")]
    public static class TraceMethod
    {
        public class Parameters
        {
            [ToolParameter("Action: hook, unhook, list, read, clear", Required = true)]
            public string Action { get; set; }

            [ToolParameter("Full type name (e.g. UnityEngine.Transform, or just Transform)")]
            public string TypeName { get; set; }

            [ToolParameter("Method name to hook")]
            public string MethodName { get; set; }

            [ToolParameter("Assembly name hint (e.g. UnityEngine.CoreModule). Optional.")]
            public string Assembly { get; set; }

            [ToolParameter("Hook ID (from hook action). Required for unhook.")]
            public string HookId { get; set; }

            [ToolParameter("Max trace entries to return. Default 50.")]
            public int Count { get; set; }

            [ToolParameter("Log parameters on call. Default true.")]
            public bool LogParams { get; set; }

            [ToolParameter("Log return values. Default true.")]
            public bool LogReturn { get; set; }

            [ToolParameter("Log stack traces. Default false (expensive).")]
            public bool LogStack { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            var p = new ToolParams(parameters);
            var action = p.Get("action")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
                return new ErrorResponse("'action' required. Valid: hook, unhook, list, read, clear.");

            switch (action)
            {
                case "hook":    return DoHook(p);
                case "unhook":  return DoUnhook(p);
                case "list":    return DoList();
                case "read":    return DoRead(p);
                case "clear":   return DoClear(p);
                default:
                    return new ErrorResponse($"Unknown action: '{action}'. Valid: hook, unhook, list, read, clear.");
            }
        }

        static object DoHook(ToolParams p)
        {
            var typeName = p.Get("typeName") ?? p.Get("type_name") ?? p.Get("type");
            if (string.IsNullOrEmpty(typeName))
                return new ErrorResponse("'type_name' is required for hook action.");

            var methodName = p.Get("methodName") ?? p.Get("method_name") ?? p.Get("method");
            if (string.IsNullOrEmpty(methodName))
                return new ErrorResponse("'method_name' is required for hook action.");

            var assembly = p.Get("assembly");
            var logParams = p.GetBool("logParams", true);
            var logReturn = p.GetBool("logReturn", true);
            var logStack = p.GetBool("logStack", false);

            var (hookId, error) = HarmonyTracer.Hook(typeName, methodName, assembly, logParams, logReturn, logStack);
            if (error != null)
                return new ErrorResponse(error);

            var hooks = HarmonyTracer.ListHooks();
            var info = hooks.Find(h => ((dynamic)h).hookId == hookId);
            return new SuccessResponse($"Hooked {typeName}.{methodName}", new
            {
                hookId,
                typeName = ((dynamic)info).typeName,
                methodName,
                assemblyName = ((dynamic)info).assemblyName,
            });
        }

        static object DoUnhook(ToolParams p)
        {
            var hookId = p.Get("hookId") ?? p.Get("hook_id") ?? p.Get("id");
            if (string.IsNullOrEmpty(hookId))
                return new ErrorResponse("'hook_id' is required for unhook action.");

            var error = HarmonyTracer.Unhook(hookId);
            if (error != null)
                return new ErrorResponse(error);

            return new SuccessResponse($"Unhooked '{hookId}'.");
        }

        static object DoList()
        {
            var hooks = HarmonyTracer.ListHooks();
            return new SuccessResponse($"{hooks.Count} active hook(s)", hooks);
        }

        static object DoRead(ToolParams p)
        {
            var hookId = p.Get("hookId") ?? p.Get("hook_id") ?? p.Get("id");
            var count = p.GetInt("count", 50).Value;

            var (entries, bufferSize) = HarmonyTracer.ReadBuffer(hookId, count);
            return new SuccessResponse($"{entries.Count} trace entries", new
            {
                entries,
                bufferSize,
                maxBufferSize = 1000,
            });
        }

        static object DoClear(ToolParams p)
        {
            var hookId = p.Get("hookId") ?? p.Get("hook_id") ?? p.Get("id");

            if (string.IsNullOrEmpty(hookId))
            {
                HarmonyTracer.ClearAll();
                return new SuccessResponse("All hooks removed and buffer cleared.");
            }

            HarmonyTracer.ClearBuffer(hookId);
            return new SuccessResponse($"Buffer cleared for '{hookId}'.");
        }
    }
}
