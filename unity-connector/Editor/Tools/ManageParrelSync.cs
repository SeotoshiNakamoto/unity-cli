using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "manage_parrel_sync", Description = "Lists, creates, and opens ParrelSync clone projects without a compile-time ParrelSync dependency.")]
    public static class ManageParrelSync
    {
        private const string ManagerTypeName = "ParrelSync.ClonesManager";

        public class Parameters
        {
            [ToolParameter("Action to perform: list, ensure, open", Required = true)]
            public string Action { get; set; }

            [ToolParameter("Desired clone count for ensure", DefaultValue = "1")]
            public int Count { get; set; }

            [ToolParameter("Clone index for open", DefaultValue = "0")]
            public int Index { get; set; }

            [ToolParameter("Open ensured clones after creation")]
            public bool Open { get; set; }

            [ToolParameter("Open every existing clone")]
            public bool All { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var managerType = FindManagerType();
            if (managerType == null)
                return new ErrorResponse("ParrelSync is not installed or its editor assembly is not loaded.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            try
            {
                switch (actionResult.Value.ToLowerInvariant())
                {
                    case "list":
                        return BuildSnapshot(managerType, "ParrelSync clone status.");

                    case "ensure":
                        return EnsureClones(managerType, p);

                    case "open":
                        return OpenClones(managerType, p);

                    default:
                        return new ErrorResponse($"Unknown ParrelSync action: '{actionResult.Value}'. Available: list, ensure, open.");
                }
            }
            catch (TargetInvocationException ex)
            {
                return new ErrorResponse(ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                return new ErrorResponse(ex.Message);
            }
        }

        private static object EnsureClones(Type managerType, ToolParams p)
        {
            if (Invoke<bool>(managerType, "IsClone"))
                return new ErrorResponse("Cannot create ParrelSync clones from a clone editor.");

            int maxCount = GetMaxCloneCount(managerType);
            int desiredCount = p.GetInt("count", 1) ?? 1;
            if (desiredCount < 1 || desiredCount > maxCount)
                return new ErrorResponse($"Clone count must be between 1 and {maxCount}.");

            var paths = GetClonePaths(managerType);
            while (paths.Count < desiredCount)
            {
                var clone = Invoke(managerType, "CreateCloneFromCurrent");
                if (clone == null)
                    return new ErrorResponse("ParrelSync failed to create a clone.");
                paths = GetClonePaths(managerType);
            }

            if (p.GetBool("open"))
            {
                foreach (var path in paths.Take(desiredCount))
                    OpenIfNeeded(managerType, path);
            }

            return BuildSnapshot(managerType, $"Ensured {desiredCount} ParrelSync clone(s).");
        }

        private static object OpenClones(Type managerType, ToolParams p)
        {
            var paths = GetClonePaths(managerType);
            if (paths.Count == 0)
                return new ErrorResponse("No ParrelSync clones exist. Run 'parrelsync ensure' first.");

            if (p.GetBool("all"))
            {
                foreach (var path in paths)
                    OpenIfNeeded(managerType, path);
                return BuildSnapshot(managerType, $"Requested open for {paths.Count} ParrelSync clone(s).");
            }

            int index = p.GetInt("index", 0) ?? 0;
            if (index < 0 || index >= paths.Count)
                return new ErrorResponse($"Clone index {index} is out of range. Existing clone count: {paths.Count}.");

            OpenIfNeeded(managerType, paths[index]);
            return BuildSnapshot(managerType, $"Requested open for clone {index}.");
        }

        private static object BuildSnapshot(Type managerType, string message)
        {
            var clones = GetClonePaths(managerType)
                .Select((path, index) => new
                {
                    index,
                    path,
                    running = Invoke<bool>(managerType, "IsCloneProjectRunning", path)
                })
                .ToArray();

            return new SuccessResponse(message, new
            {
                installed = true,
                isClone = Invoke<bool>(managerType, "IsClone"),
                maxCloneCount = GetMaxCloneCount(managerType),
                clones
            });
        }

        private static void OpenIfNeeded(Type managerType, string path)
        {
            if (!Invoke<bool>(managerType, "IsCloneProjectRunning", path))
                Invoke(managerType, "OpenProject", path);
        }

        private static List<string> GetClonePaths(Type managerType)
        {
            var result = Invoke(managerType, "GetCloneProjectsPath") as IEnumerable;
            if (result == null)
                return new List<string>();

            var paths = new List<string>();
            foreach (var item in result)
            {
                if (item is string path)
                    paths.Add(path);
            }
            return paths;
        }

        private static int GetMaxCloneCount(Type managerType)
        {
            var field = managerType.GetField("MaxCloneProjectCount", BindingFlags.Public | BindingFlags.Static);
            return field?.GetRawConstantValue() is int value ? value : 10;
        }

        private static Type FindManagerType()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(ManagerTypeName, false))
                .FirstOrDefault(type => type != null);
        }

        private static object Invoke(Type managerType, string methodName, params object[] args)
        {
            var argumentTypes = args.Select(arg => arg.GetType()).ToArray();
            var method = managerType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                argumentTypes,
                null);
            if (method == null)
                throw new MissingMethodException(managerType.FullName, methodName);
            return method.Invoke(null, args);
        }

        private static T Invoke<T>(Type managerType, string methodName, params object[] args)
        {
            var result = Invoke(managerType, methodName, args);
            return result is T value ? value : default;
        }
    }
}
