using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "screenshot", Description = "Capture a screenshot of the Unity editor. Views: scene, game, window. Actions: capture, list_windows.")]
    public static class EditorScreenshot
    {
        private const int DefaultWidth = 1920;
        private const int DefaultHeight = 1080;

        public class Parameters
        {
            [ToolParameter("View to capture: scene (default), game, window", Required = false)]
            public string View { get; set; }

            [ToolParameter("Action: capture (default), list_windows", Required = false)]
            public string Action { get; set; }

            [ToolParameter("EditorWindow type name to capture (e.g. InspectorWindow, ConsoleWindow)", Required = false)]
            public string WindowType { get; set; }

            [ToolParameter("Find window by title text (substring match)", Required = false)]
            public string WindowTitle { get; set; }

            [ToolParameter("Override width (default 1920 for scene/game, window actual size for window)", Required = false)]
            public int Width { get; set; }

            [ToolParameter("Override height (default 1080 for scene/game, window actual size for window)", Required = false)]
            public int Height { get; set; }

            [ToolParameter("Output file path, absolute or relative to project root (default: Screenshots/screenshot.png)", Required = false)]
            public string OutputPath { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                @params = new JObject();

            var p = new ToolParams(@params);
            var action = p.Get("action", "capture").ToLowerInvariant();

            if (action == "list_windows")
                return ListWindows();

            var view = p.Get("view", "scene").ToLowerInvariant();
            var outputPath = ResolveOutputPath(p.Get("output_path"));

            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                switch (view)
                {
                    case "scene":
                        @params["window_type"] = "SceneView";
                        return CaptureEditorWindow(p, outputPath);
                    case "game":
                        @params["window_type"] = "GameView";
                        return CaptureEditorWindow(p, outputPath);
                    case "window":
                        return CaptureEditorWindow(p, outputPath);
                    default:
                        return new ErrorResponse($"Unknown view '{view}'. Valid: scene, game, window.");
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Screenshot failed: {e.Message}");
            }
        }

        private static object ListWindows()
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var list = windows.Select(w => new
            {
                type = w.GetType().Name,
                full_type = w.GetType().FullName,
                title = w.titleContent.text,
                width = (int)w.position.width,
                height = (int)w.position.height
            }).OrderBy(w => w.type).ToList();

            return new SuccessResponse($"Found {list.Count} open editor windows.", list);
        }

        private static EditorWindow FindEditorWindow(ToolParams p)
        {
            var windowType = p.Get("window_type");
            var windowTitle = p.Get("window_title");

            if (string.IsNullOrEmpty(windowType) && string.IsNullOrEmpty(windowTitle))
                return null;

            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            if (!string.IsNullOrEmpty(windowType))
            {
                var matches = allWindows.Where(w =>
                    w.GetType().Name.Equals(windowType, StringComparison.OrdinalIgnoreCase) ||
                    w.GetType().FullName.Equals(windowType, StringComparison.OrdinalIgnoreCase)
                ).ToArray();

                if (matches.Length == 0) return null;
                return matches.FirstOrDefault(w => w.hasFocus) ?? matches[0];
            }

            var titleMatches = allWindows.Where(w =>
                w.titleContent.text.IndexOf(windowTitle, StringComparison.OrdinalIgnoreCase) >= 0
            ).ToArray();

            if (titleMatches.Length == 0) return null;
            return titleMatches.FirstOrDefault(w => w.hasFocus) ?? titleMatches[0];
        }

        private static object CaptureEditorWindow(ToolParams p, string outputPath)
        {
            var window = FindEditorWindow(p);
            if (window == null)
            {
                var windowType = p.Get("window_type");
                var windowTitle = p.Get("window_title");
                if (string.IsNullOrEmpty(windowType) && string.IsNullOrEmpty(windowTitle))
                    return new ErrorResponse("view=window requires window_type or window_title parameter.");

                var identifier = !string.IsNullOrEmpty(windowType) ? $"type '{windowType}'" : $"title '{windowTitle}'";
                return new ErrorResponse(
                    $"No open EditorWindow found matching {identifier}. Use --action list_windows to see available windows.");
            }

            var pos = window.position;
            if (pos.width <= 0 || pos.height <= 0)
                return new ErrorResponse($"Window '{window.GetType().Name}' is minimized or has zero size.");

            const BindingFlags kFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var parentField = typeof(EditorWindow).GetField("m_Parent", kFlags);
            var hostView = parentField?.GetValue(window);
            if (hostView == null)
                return new ErrorResponse("Cannot access window's host view for buffer capture.");

            var guiViewType = hostView.GetType();
            while (guiViewType != null && guiViewType.Name != "GUIView")
                guiViewType = guiViewType.BaseType;

            var grabMethod = guiViewType?.GetMethod("GrabPixels", kFlags);
            if (grabMethod == null)
                return new ErrorResponse("GrabPixels method not available in this Unity version.");

            window.Focus();
            window.ShowTab();
            window.Repaint();
            InternalEditorUtility.RepaintAllViews();

            // Force synchronous repaint via SendEvent so the buffer is populated
            var sendEventMethod = guiViewType.GetMethod("SendEvent", kFlags);
            if (sendEventMethod != null)
            {
                var repaintEvent = new Event { type = EventType.Repaint };
                try { sendEventMethod.Invoke(hostView, new object[] { repaintEvent }); } catch { }
            }

            int captureWidth = (int)pos.width;
            int captureHeight = (int)pos.height;

            var desc = new RenderTextureDescriptor(captureWidth, captureHeight, RenderTextureFormat.ARGB32, 24);
            desc.sRGB = false;
            var rt = new RenderTexture(desc);
            Texture2D tex = null;

            try
            {
                grabMethod.Invoke(hostView, new object[] { rt, new Rect(0, 0, captureWidth, captureHeight) });

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex = new Texture2D(captureWidth, captureHeight, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                // GrabPixels returns Y-flipped image — flip it back
                var pixels = tex.GetPixels();
                var flipped = new Color[pixels.Length];
                for (int y = 0; y < captureHeight; y++)
                    Array.Copy(pixels, y * captureWidth, flipped, (captureHeight - 1 - y) * captureWidth, captureWidth);
                tex.SetPixels(flipped);
                tex.Apply();

                var userWidth = p.GetInt("width");
                var userHeight = p.GetInt("height");
                if (userWidth.HasValue || userHeight.HasValue)
                {
                    int targetW = userWidth ?? captureWidth;
                    int targetH = userHeight ?? captureHeight;
                    if (targetW != captureWidth || targetH != captureHeight)
                        tex = ResizeTexture(tex, targetW, targetH);
                }

                File.WriteAllBytes(outputPath, tex.EncodeToPNG());

                return new SuccessResponse($"Screenshot saved to {outputPath}", new
                {
                    path = outputPath,
                    width = tex.width,
                    height = tex.height,
                    window_type = window.GetType().Name,
                    window_title = window.titleContent.text
                });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static Texture2D ResizeTexture(Texture2D source, int newWidth, int newHeight)
        {
            RenderTexture rt = null;
            try
            {
                rt = RenderTexture.GetTemporary(newWidth, newHeight);
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(newWidth, newHeight, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = null;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static string ResolveOutputPath(string userPath)
        {
            if (string.IsNullOrEmpty(userPath))
                userPath = "Screenshots/screenshot.png";

            if (Path.IsPathRooted(userPath))
                return Path.GetFullPath(userPath);

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, userPath));
        }

        private static object CaptureSceneView(int width, int height, string outputPath)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (!sceneView)
                return new ErrorResponse("No active SceneView found.");

            var camera = sceneView.camera;
            if (!camera)
                return new ErrorResponse("SceneView camera is null.");

            return CaptureCamera(camera, width, height, outputPath);
        }

        private static object CaptureGameView(int width, int height, string outputPath)
        {
            var camera = Camera.main;
            if (!camera)
            {
#if UNITY_2023_1_OR_NEWER
                camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
#else
                camera = UnityEngine.Object.FindObjectOfType<Camera>();
#endif
                if (!camera)
                    return new ErrorResponse("No camera found in scene.");
            }

            return CaptureCamera(camera, width, height, outputPath);
        }

        private static object CaptureCamera(Camera camera, int width, int height, string outputPath)
        {
            var previousRT = camera.targetTexture;
            RenderTexture rt = null;
            Texture2D tex = null;

            try
            {
                rt = new RenderTexture(width, height, 24);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                File.WriteAllBytes(outputPath, tex.EncodeToPNG());

                return new SuccessResponse($"Screenshot saved to {outputPath}",
                    new { path = outputPath, width, height });
            }
            finally
            {
                camera.targetTexture = previousRT;
                RenderTexture.active = null;
                if (rt) UnityEngine.Object.DestroyImmediate(rt);
                if (tex) UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
