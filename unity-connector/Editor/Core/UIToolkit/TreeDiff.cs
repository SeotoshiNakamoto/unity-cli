using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace UnityCliConnector.UIToolkit
{
    internal struct LiteElement
    {
        public string Id;
        public string Label;
        public string TypeName;
        public bool Visible;
        public bool Enabled;
    }

    internal static class TreeDiff
    {
        /// <summary>
        /// Lightweight snapshot: ID + label + visible + enabled only. No rect, no path.
        /// </summary>
        internal static Dictionary<string, LiteElement> SnapshotLite(List<PanelInfo> panels)
        {
            var map = new Dictionary<string, LiteElement>();
            foreach (var panel in panels)
                CollectLite(panel.Root, panel.WindowTitle, "", map);
            return map;
        }

        static void CollectLite(VisualElement element, string windowTitle, string parentPath,
            Dictionary<string, LiteElement> map)
        {
            string nodeName = !string.IsNullOrEmpty(element.name)
                ? element.name
                : element.GetType().Name;
            string path = string.IsNullOrEmpty(parentPath) ? nodeName : parentPath + "/" + nodeName;
            string label = VisualElementTraverser.ExtractLabel(element);
            string id = StableIdGenerator.GenerateId(element, windowTitle, path, label);

            bool visible = element.resolvedStyle.display != DisplayStyle.None
                && element.resolvedStyle.visibility != Visibility.Hidden
                && element.resolvedStyle.opacity > 0f;

            map[id] = new LiteElement
            {
                Id = id,
                Label = label,
                TypeName = element.GetType().Name,
                Visible = visible,
                Enabled = element.enabledInHierarchy,
            };

            for (int i = 0; i < element.childCount; i++)
                CollectLite(element[i], windowTitle, path, map);
        }

        internal static JObject ComputeDiff(Dictionary<string, LiteElement> before,
            Dictionary<string, LiteElement> after)
        {
            var added = new JArray();
            var removed = new JArray();
            var changed = new JArray();

            // Removed: in before but not in after
            foreach (var kv in before)
            {
                if (!after.ContainsKey(kv.Key))
                {
                    removed.Add(new JObject
                    {
                        ["id"] = kv.Value.Id,
                        ["label"] = kv.Value.Label,
                        ["type"] = kv.Value.TypeName,
                    });
                }
            }

            // Added + Changed
            foreach (var kv in after)
            {
                if (!before.ContainsKey(kv.Key))
                {
                    added.Add(new JObject
                    {
                        ["id"] = kv.Value.Id,
                        ["label"] = kv.Value.Label,
                        ["type"] = kv.Value.TypeName,
                    });
                }
                else
                {
                    var prev = before[kv.Key];
                    var curr = kv.Value;

                    if (prev.Visible != curr.Visible)
                    {
                        changed.Add(new JObject
                        {
                            ["id"] = curr.Id,
                            ["field"] = "visible",
                            ["from"] = prev.Visible,
                            ["to"] = curr.Visible,
                        });
                    }
                    if (prev.Enabled != curr.Enabled)
                    {
                        changed.Add(new JObject
                        {
                            ["id"] = curr.Id,
                            ["field"] = "enabled",
                            ["from"] = prev.Enabled,
                            ["to"] = curr.Enabled,
                        });
                    }
                    if (!string.Equals(prev.Label, curr.Label, StringComparison.Ordinal))
                    {
                        changed.Add(new JObject
                        {
                            ["id"] = curr.Id,
                            ["field"] = "label",
                            ["from"] = prev.Label,
                            ["to"] = curr.Label,
                        });
                    }
                }
            }

            return new JObject
            {
                ["added"] = added,
                ["removed"] = removed,
                ["changed"] = changed,
            };
        }
    }
}
