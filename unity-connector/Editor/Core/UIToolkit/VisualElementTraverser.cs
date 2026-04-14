using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCliConnector.UIToolkit
{
    internal struct ElementData
    {
        public string Id;
        public int[] Rect;         // [x, y, w, h] screen pixels
        public string Label;
        public string TypeName;
        public string Path;
        public bool Enabled;
        public bool Visible;
        public int ChildrenCount;
        public VisualElement SourceElement;

        public JObject ToJObject()
        {
            return new JObject
            {
                ["id"] = Id,
                ["rect"] = new JArray(Rect[0], Rect[1], Rect[2], Rect[3]),
                ["label"] = Label,
                ["type"] = TypeName,
                ["path"] = Path,
                ["enabled"] = Enabled,
                ["visible"] = Visible,
                ["children_count"] = ChildrenCount,
            };
        }
    }

    internal struct TraversalResult
    {
        public List<ElementData> Elements;
        public int TotalCount;
        public bool Truncated;
    }

    internal static class VisualElementTraverser
    {
        const int HardCap = 500;

        internal static TraversalResult Traverse(
            VisualElement root,
            string windowTitle,
            string windowType,
            Rect windowRect,
            bool isRuntime,
            int maxDepth,
            bool visibleOnly,
            string textFilter)
        {
            var elements = new List<ElementData>();
            int totalCount = 0;
            bool truncated = false;

            float ppp = EditorGUIUtility.pixelsPerPoint;

            TraverseRecursive(
                root, windowTitle, windowRect, isRuntime, ppp,
                "", maxDepth, 0, visibleOnly, textFilter,
                elements, ref totalCount, ref truncated);

            return new TraversalResult
            {
                Elements = elements,
                TotalCount = totalCount,
                Truncated = truncated,
            };
        }

        static void TraverseRecursive(
            VisualElement element,
            string windowTitle,
            Rect windowRect,
            bool isRuntime,
            float pixelsPerPoint,
            string parentPath,
            int maxDepth,
            int currentDepth,
            bool visibleOnly,
            string textFilter,
            List<ElementData> elements,
            ref int totalCount,
            ref bool truncated)
        {
            if (truncated) return;

            bool visible = IsVisible(element);
            if (visibleOnly && !visible) return;

            string nodeName = BuildNodeName(element, parentPath);
            string path = string.IsNullOrEmpty(parentPath) ? nodeName : parentPath + "/" + nodeName;
            string label = ExtractLabel(element);
            string typeName = element.GetType().Name;

            // Text filter
            if (!string.IsNullOrEmpty(textFilter))
            {
                bool matchLabel = label != null && label.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchName = element.name != null && element.name.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchType = typeName.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!matchLabel && !matchName && !matchType)
                {
                    // Still traverse children — a child might match
                    TraverseChildren(element, windowTitle, windowRect, isRuntime, pixelsPerPoint,
                        path, maxDepth, currentDepth, visibleOnly, textFilter, elements, ref totalCount, ref truncated);
                    return;
                }
            }

            totalCount++;

            if (elements.Count >= HardCap)
            {
                truncated = true;
                return;
            }

            string id = StableIdGenerator.GenerateId(element, windowTitle, path, label);
            int[] rect = ComputeScreenRect(element, windowRect, isRuntime, pixelsPerPoint);

            elements.Add(new ElementData
            {
                Id = id,
                Rect = rect,
                Label = label,
                TypeName = typeName,
                Path = path,
                Enabled = element.enabledInHierarchy,
                Visible = visible,
                ChildrenCount = element.childCount,
                SourceElement = element,
            });

            TraverseChildren(element, windowTitle, windowRect, isRuntime, pixelsPerPoint,
                path, maxDepth, currentDepth, visibleOnly, textFilter, elements, ref totalCount, ref truncated);
        }

        static void TraverseChildren(
            VisualElement element,
            string windowTitle,
            Rect windowRect,
            bool isRuntime,
            float pixelsPerPoint,
            string path,
            int maxDepth,
            int currentDepth,
            bool visibleOnly,
            string textFilter,
            List<ElementData> elements,
            ref int totalCount,
            ref bool truncated)
        {
            if (maxDepth > 0 && currentDepth >= maxDepth) return;

            for (int i = 0; i < element.childCount; i++)
            {
                if (truncated) return;
                TraverseRecursive(
                    element[i], windowTitle, windowRect, isRuntime, pixelsPerPoint,
                    path, maxDepth, currentDepth + 1, visibleOnly, textFilter,
                    elements, ref totalCount, ref truncated);
            }
        }

        internal static void BuildTreeText(VisualElement element, StringBuilder sb, int maxDepth, int indent)
        {
            string prefix = new string(' ', indent * 2);
            string typeName = element.GetType().Name;
            string name = !string.IsNullOrEmpty(element.name) ? $" id:{element.name}" : "";
            string label = ExtractLabel(element);
            string labelStr = !string.IsNullOrEmpty(label) ? $" \"{label}\"" : "";
            bool visible = IsVisible(element);
            string visStr = visible ? "" : " [hidden]";

            sb.AppendLine($"{prefix}[{typeName}]{name}{labelStr}{visStr}");

            if (maxDepth > 0 && indent >= maxDepth) return;

            for (int i = 0; i < element.childCount; i++)
                BuildTreeText(element[i], sb, maxDepth, indent + 1);
        }

        static bool IsVisible(VisualElement element)
        {
            var style = element.resolvedStyle;
            if (style.display == DisplayStyle.None) return false;
            if (style.visibility == Visibility.Hidden) return false;
            if (style.opacity <= 0f) return false;

            var wb = element.worldBound;
            if (wb.width <= 0 || wb.height <= 0) return false;

            return true;
        }

        static string BuildNodeName(VisualElement element, string parentPath)
        {
            if (!string.IsNullOrEmpty(element.name))
                return element.name;

            string typeName = element.GetType().Name;
            var parent = element.parent;
            if (parent == null)
                return typeName;

            // Disambiguate by sibling index among same-type children
            int sameTypeIndex = 0;
            int sameTypeCount = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent[i].GetType().Name == typeName)
                {
                    if (parent[i] == element)
                        sameTypeIndex = sameTypeCount;
                    sameTypeCount++;
                }
            }

            return sameTypeCount > 1 ? $"{typeName}[{sameTypeIndex}]" : typeName;
        }

        internal static string ExtractLabel(VisualElement element)
        {
            // TextElement subclasses (Label, Button text)
            if (element is TextElement te && !string.IsNullOrEmpty(te.text))
                return te.text;

            // TextField value
            if (element is TextField tf && !string.IsNullOrEmpty(tf.value))
                return tf.value;

            // Check first child TextElement
            for (int i = 0; i < element.childCount; i++)
            {
                if (element[i] is TextElement childTe && !string.IsNullOrEmpty(childTe.text))
                    return childTe.text;
            }

            return null;
        }

        static int[] ComputeScreenRect(VisualElement element, Rect windowRect, bool isRuntime, float pixelsPerPoint)
        {
            var wb = element.worldBound;
            if (wb.width <= 0 || wb.height <= 0)
                return new[] { 0, 0, 0, 0 };

            if (isRuntime)
            {
                // Runtime: worldBound is panel-local, use as-is
                return new[]
                {
                    (int)wb.x, (int)wb.y,
                    (int)wb.width, (int)wb.height
                };
            }

            // Editor: offset by window position, scale by pixelsPerPoint
            float x = (windowRect.x + wb.x) * pixelsPerPoint;
            float y = (windowRect.y + wb.y) * pixelsPerPoint;
            float w = wb.width * pixelsPerPoint;
            float h = wb.height * pixelsPerPoint;

            return new[] { (int)x, (int)y, (int)w, (int)h };
        }
    }
}
