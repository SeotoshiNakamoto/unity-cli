using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCliConnector.UIToolkit
{
    /// <summary>
    /// Lightweight window screenshot capture, reusing the GrabPixels reflection
    /// pattern from EditorScreenshot.
    /// </summary>
    internal static class ScreenshotHelper
    {
        const BindingFlags kFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static bool CaptureWindow(PanelInfo panel, string outputPath)
        {
            if (panel.IsRuntime)
                return CaptureGameView(outputPath);

            var window = FindEditorWindow(panel.WindowTitle, panel.WindowType);
            if (window == null)
                return false;

            return CaptureEditorWindow(window, outputPath);
        }

        static EditorWindow FindEditorWindow(string title, string typeName)
        {
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (var w in allWindows)
            {
                if (w.GetType().Name == typeName && w.titleContent.text == title)
                    return w;
            }
            return null;
        }

        static bool CaptureEditorWindow(EditorWindow window, string outputPath)
        {
            var pos = window.position;
            if (pos.width <= 0 || pos.height <= 0)
                return false;

            var parentField = typeof(EditorWindow).GetField("m_Parent", kFlags);
            var hostView = parentField?.GetValue(window);
            if (hostView == null)
                return false;

            var guiViewType = hostView.GetType();
            while (guiViewType != null && guiViewType.Name != "GUIView")
                guiViewType = guiViewType.BaseType;

            var grabMethod = guiViewType?.GetMethod("GrabPixels", kFlags);
            if (grabMethod == null)
                return false;

            window.Focus();
            window.ShowTab();
            window.Repaint();
            InternalEditorUtility.RepaintAllViews();

            var sendEventMethod = guiViewType.GetMethod("SendEvent", kFlags);
            if (sendEventMethod != null)
            {
                var repaintEvent = new Event { type = EventType.Repaint };
                try { sendEventMethod.Invoke(hostView, new object[] { repaintEvent }); } catch { }
            }

            int w = (int)pos.width;
            int h = (int)pos.height;

            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 24);
            desc.sRGB = false;
            var rt = new RenderTexture(desc);
            Texture2D tex = null;

            try
            {
                grabMethod.Invoke(hostView, new object[] { rt, new Rect(0, 0, w, h) });

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                // GrabPixels returns Y-flipped
                var pixels = tex.GetPixels();
                var flipped = new Color[pixels.Length];
                for (int y = 0; y < h; y++)
                    Array.Copy(pixels, y * w, flipped, (h - 1 - y) * w, w);
                tex.SetPixels(flipped);
                tex.Apply();

                File.WriteAllBytes(outputPath, tex.EncodeToPNG());
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        static bool CaptureGameView(string outputPath)
        {
            var camera = Camera.main;
            if (camera == null)
            {
#if UNITY_2023_1_OR_NEWER
                camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
#else
                camera = UnityEngine.Object.FindObjectOfType<Camera>();
#endif
                if (camera == null)
                    return false;
            }

            var previousRT = camera.targetTexture;
            RenderTexture rt = null;
            Texture2D tex = null;

            try
            {
                int w = Screen.width > 0 ? Screen.width : 1920;
                int h = Screen.height > 0 ? Screen.height : 1080;

                rt = new RenderTexture(w, h, 24);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                File.WriteAllBytes(outputPath, tex.EncodeToPNG());
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                camera.targetTexture = previousRT;
                RenderTexture.active = null;
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
