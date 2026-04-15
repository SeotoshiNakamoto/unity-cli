using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCliConnector.UIToolkit
{
    internal struct PanelInfo
    {
        public VisualElement Root;
        public string WindowTitle;
        public string WindowType;
        public Rect WindowRect;
        public bool Focused;
        public bool IsRuntime;
    }

    internal enum PanelSource
    {
        All,
        Runtime,  // UIDocument only (game UI)
        Editor,   // EditorWindow only (Inspector, GameView, custom tools, etc.)
    }

    internal static class PanelDiscovery
    {
        internal static List<PanelInfo> FindPanels(string windowFilter = null, PanelSource source = PanelSource.All)
        {
            var panels = new List<PanelInfo>();

            // Editor windows
            if (source == PanelSource.All || source == PanelSource.Editor)
            {
            var focusedWindow = EditorWindow.focusedWindow;
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            foreach (var window in allWindows)
            {
                var root = window.rootVisualElement;
                if (root == null || root.childCount == 0)
                    continue;

                var title = window.titleContent.text;
                var typeName = window.GetType().Name;

                if (!string.IsNullOrEmpty(windowFilter))
                {
                    bool matchTitle = title.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchType = typeName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchTitle && !matchType)
                        continue;
                }

                panels.Add(new PanelInfo
                {
                    Root = root,
                    WindowTitle = title,
                    WindowType = typeName,
                    WindowRect = window.position,
                    Focused = focusedWindow == window,
                    IsRuntime = false,
                });
            }
            } // end Editor block

            // Runtime UIDocuments (play mode only)
            if ((source == PanelSource.All || source == PanelSource.Runtime) && EditorApplication.isPlaying)
            {
                var documents =
#if UNITY_2023_1_OR_NEWER
                    UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
#else
                    UnityEngine.Object.FindObjectsOfType<UIDocument>();
#endif

                foreach (var doc in documents)
                {
                    var root = doc.rootVisualElement;
                    if (root == null)
                        continue;

                    var title = doc.gameObject.name;

                    if (!string.IsNullOrEmpty(windowFilter))
                    {
                        if (title.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }

                    panels.Add(new PanelInfo
                    {
                        Root = root,
                        WindowTitle = title,
                        WindowType = "UIDocument",
                        WindowRect = new Rect(0, 0, Screen.width, Screen.height),
                        Focused = false,
                        IsRuntime = true,
                    });
                }
            }

            // Sort: focused first, then by title
            panels.Sort((a, b) =>
            {
                if (a.Focused != b.Focused) return a.Focused ? -1 : 1;
                return string.Compare(a.WindowTitle, b.WindowTitle, StringComparison.Ordinal);
            });

            return panels;
        }
    }
}
