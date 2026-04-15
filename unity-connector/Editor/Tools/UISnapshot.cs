using Newtonsoft.Json.Linq;
using UnityCliConnector.UIToolkit;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "ui_snapshot", Description = "UIToolkit UI observation and interaction. Actions: snapshot, tree, query, click.")]
    public static class UISnapshot
    {
        public class Parameters
        {
            [ToolParameter("Action: snapshot (default), tree, query, click", Required = false)]
            public string Action { get; set; }

            [ToolParameter("Target window name or type (substring match)", Required = false)]
            public string Window { get; set; }

            [ToolParameter("Selector string (e.g. id=save-btn, label=Save, type=Button)", Required = false)]
            public string Selector { get; set; }

            [ToolParameter("Max tree depth. 0 = unlimited (default)", Required = false)]
            public int MaxDepth { get; set; }

            [ToolParameter("Only include visible elements (default false)", Required = false)]
            public bool VisibleOnly { get; set; }

            [ToolParameter("Filter elements by text substring", Required = false)]
            public string Filter { get; set; }

            [ToolParameter("Output file path prefix (produces <prefix>.json + <prefix>.png)", Required = false)]
            public string Output { get; set; }

            [ToolParameter("Include screenshot (default true)", Required = false)]
            public bool Screenshot { get; set; }

            [ToolParameter("Only include interactive/readable elements: Button, TextField, Toggle, Label, etc. (default false)", Required = false)]
            public bool Interactive { get; set; }

            [ToolParameter("Panel source: all (default), runtime (UIDocument only — game UI), editor (EditorWindow only)", Required = false)]
            public string Source { get; set; }
        }

        static UIToolkit.PanelSource ParseSource(ToolParams p)
        {
            var src = p.Get("source", "all").ToLowerInvariant();
            switch (src)
            {
                case "runtime": return UIToolkit.PanelSource.Runtime;
                case "editor":  return UIToolkit.PanelSource.Editor;
                default:        return UIToolkit.PanelSource.All;
            }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                @params = new JObject();

            var p = new ToolParams(@params);
            var action = p.Get("action", "snapshot").ToLowerInvariant();

            switch (action)
            {
                case "snapshot": return DoSnapshot(p);
                case "tree":    return DoTree(p);
                case "query":   return DoQuery(p);
                case "click":   return DoClick(p);
                case "type":    return DoType(p);
                case "events":  return DoEvents();
                default:
                    return new ErrorResponse($"Unknown action '{action}'. Valid: snapshot, tree, query, click, type, events.");
            }
        }

        static object DoSnapshot(ToolParams p)
        {
            var windowFilter = p.Get("window");
            var panels = PanelDiscovery.FindPanels(windowFilter, ParseSource(p));

            if (panels.Count == 0)
                return new ErrorResponse(string.IsNullOrEmpty(windowFilter)
                    ? "No UIToolkit panels found."
                    : $"No panels matching '{windowFilter}' found.");

            var maxDepth = p.GetInt("max_depth", 0).Value;
            var visibleOnly = p.GetBool("visible_only", false);
            var interactiveOnly = p.GetBool("interactive", false);
            var filter = p.Get("filter");
            var outputPrefix = p.Get("output");
            var includeScreenshot = p.GetBool("screenshot", true);

            var allElements = new JArray();
            int totalCount = 0;
            bool truncated = false;
            PanelInfo targetPanel = panels[0];

            foreach (var panel in panels)
            {
                var result = VisualElementTraverser.Traverse(
                    panel.Root, panel.WindowTitle, panel.WindowType,
                    panel.WindowRect, panel.IsRuntime,
                    maxDepth, visibleOnly, filter, interactiveOnly);

                totalCount += result.TotalCount;
                truncated = truncated || result.Truncated;

                foreach (var elem in result.Elements)
                    allElements.Add(elem.ToJObject());
            }

            // Build JSON schema v3
            var snapshot = new JObject
            {
                ["snapshot_version"] = "3",
                ["captured_at"] = System.DateTime.Now.ToString("o"),
                ["window"] = new JObject
                {
                    ["name"] = targetPanel.WindowTitle,
                    ["type"] = targetPanel.WindowType,
                    ["rect"] = new JArray(
                        (int)targetPanel.WindowRect.x,
                        (int)targetPanel.WindowRect.y,
                        (int)targetPanel.WindowRect.width,
                        (int)targetPanel.WindowRect.height),
                    ["focused"] = targetPanel.Focused,
                },
                ["elements"] = allElements,
                ["element_count_total"] = totalCount,
                ["element_count_returned"] = allElements.Count,
                ["truncated"] = truncated,
            };

            // Write files
            var prefix = ResolveOutputPrefix(outputPrefix);
            var jsonPath = prefix + ".json";

            try
            {
                var dir = System.IO.Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir))
                    System.IO.Directory.CreateDirectory(dir);

                System.IO.File.WriteAllText(jsonPath, snapshot.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (System.Exception e)
            {
                return new ErrorResponse($"Failed to write JSON: {e.Message}");
            }

            string pngPath = null;
            if (includeScreenshot)
            {
                pngPath = prefix + ".png";
                ScreenshotHelper.CaptureWindow(targetPanel, pngPath);
            }

            return new SuccessResponse(
                $"Snapshot saved. {allElements.Count} elements" + (truncated ? " (truncated)" : ""),
                new
                {
                    json_path = jsonPath,
                    png_path = pngPath,
                    element_count = allElements.Count,
                    truncated,
                });
        }

        static object DoTree(ToolParams p)
        {
            var windowFilter = p.Get("window");
            var panels = PanelDiscovery.FindPanels(windowFilter, ParseSource(p));

            if (panels.Count == 0)
                return new ErrorResponse(string.IsNullOrEmpty(windowFilter)
                    ? "No UIToolkit panels found."
                    : $"No panels matching '{windowFilter}' found.");

            var maxDepth = p.GetInt("max_depth", 0).Value;
            var interactiveOnly = p.GetBool("interactive", false);

            var sb = new System.Text.StringBuilder();
            foreach (var panel in panels)
            {
                sb.AppendLine($"[{panel.WindowType}] {panel.WindowTitle}");
                VisualElementTraverser.BuildTreeText(panel.Root, sb, maxDepth, indent: 1, interactiveOnly: interactiveOnly);
                sb.AppendLine();
            }

            return new SuccessResponse(sb.ToString().TrimEnd(), null);
        }

        static object DoQuery(ToolParams p)
        {
            var selectorStr = p.Get("selector");
            if (string.IsNullOrEmpty(selectorStr))
                return new ErrorResponse("'selector' is required for query action.");

            var query = SelectorParser.Parse(selectorStr);
            var windowFilter = p.Get("window");
            var panels = PanelDiscovery.FindPanels(windowFilter, ParseSource(p));

            foreach (var panel in panels)
            {
                var result = VisualElementTraverser.Traverse(
                    panel.Root, panel.WindowTitle, panel.WindowType,
                    panel.WindowRect, panel.IsRuntime,
                    maxDepth: 0, visibleOnly: false, textFilter: null);

                var matches = SelectorParser.FindMatches(query, result.Elements);
                if (matches.Count > 0)
                {
                    var target = query.Index >= 0 && query.Index < matches.Count
                        ? matches[query.Index]
                        : matches[0];
                    return new SuccessResponse("Found", target.ToJObject());
                }
            }

            return new ErrorResponse($"No element matching '{selectorStr}' found.");
        }

        static object DoClick(ToolParams p)
        {
            var selectorStr = p.Get("selector");
            if (string.IsNullOrEmpty(selectorStr))
                return new ErrorResponse("'selector' is required for click action.");

            var query = SelectorParser.Parse(selectorStr);
            var windowFilter = p.Get("window");
            var source = ParseSource(p);
            var panels = PanelDiscovery.FindPanels(windowFilter, source);

            // Pre-click snapshot for diff (same source scope as the click target)
            var allPanels = PanelDiscovery.FindPanels(null, source);
            var before = TreeDiff.SnapshotLite(allPanels);

            foreach (var panel in panels)
            {
                var result = VisualElementTraverser.Traverse(
                    panel.Root, panel.WindowTitle, panel.WindowType,
                    panel.WindowRect, panel.IsRuntime,
                    maxDepth: 0, visibleOnly: false, textFilter: null);

                var matches = SelectorParser.FindMatches(query, result.Elements);
                if (matches.Count > 0)
                {
                    var target = query.Index >= 0 && query.Index < matches.Count
                        ? matches[query.Index]
                        : matches[0];

                    if (target.SourceElement == null)
                        return new ErrorResponse($"Element '{target.Id}' is no longer available.");

                    var clickResult = ClickExecutor.Execute(target.SourceElement);

                    // Post-click snapshot + diff (synchronous — handler already executed)
                    var afterPanels = PanelDiscovery.FindPanels(null, source);
                    var after = TreeDiff.SnapshotLite(afterPanels);
                    var diff = TreeDiff.ComputeDiff(before, after);

                    if (clickResult.Success)
                    {
                        return new SuccessResponse(
                            $"Clicked '{target.Label ?? target.Id}' via {clickResult.Strategy}",
                            new Newtonsoft.Json.Linq.JObject
                            {
                                ["id"] = target.Id,
                                ["label"] = target.Label,
                                ["strategy"] = clickResult.Strategy,
                                ["diff"] = diff,
                            });
                    }
                    return new ErrorResponse($"Click failed on '{target.Id}': {clickResult.Error}");
                }
            }

            return new ErrorResponse($"No element matching '{selectorStr}' found.");
        }

        static object DoType(ToolParams p)
        {
            var selectorStr = p.Get("selector");
            if (string.IsNullOrEmpty(selectorStr))
                return new ErrorResponse("'selector' is required for type action.");

            var text = p.Get("text");
            if (text == null)
                return new ErrorResponse("'text' is required for type action.");

            var query = SelectorParser.Parse(selectorStr);
            var windowFilter = p.Get("window");
            var source = ParseSource(p);
            var panels = PanelDiscovery.FindPanels(windowFilter, source);

            var allPanels = PanelDiscovery.FindPanels(null, source);
            var before = TreeDiff.SnapshotLite(allPanels);

            foreach (var panel in panels)
            {
                var result = VisualElementTraverser.Traverse(
                    panel.Root, panel.WindowTitle, panel.WindowType,
                    panel.WindowRect, panel.IsRuntime,
                    maxDepth: 0, visibleOnly: false, textFilter: null);

                var matches = SelectorParser.FindMatches(query, result.Elements);
                if (matches.Count > 0)
                {
                    var target = query.Index >= 0 && query.Index < matches.Count
                        ? matches[query.Index]
                        : matches[0];

                    if (target.SourceElement == null)
                        return new ErrorResponse($"Element '{target.Id}' is no longer available.");

                    var element = target.SourceElement;

                    // Set value — .value setter auto-dispatches ChangeEvent
                    if (element is UnityEngine.UIElements.TextField tf)
                    {
                        tf.value = text;
                    }
                    else if (element is UnityEngine.UIElements.BaseField<string> sf)
                    {
                        sf.value = text;
                    }
                    else
                    {
                        return new ErrorResponse(
                            $"Element '{target.Id}' is {target.TypeName}, not a text field.");
                    }

                    var afterPanels = PanelDiscovery.FindPanels(null, source);
                    var after = TreeDiff.SnapshotLite(afterPanels);
                    var diff = TreeDiff.ComputeDiff(before, after);

                    return new SuccessResponse(
                        $"Typed '{text}' into '{target.Label ?? target.Id}'",
                        new Newtonsoft.Json.Linq.JObject
                        {
                            ["id"] = target.Id,
                            ["label"] = target.Label,
                            ["text"] = text,
                            ["diff"] = diff,
                        });
                }
            }

            return new ErrorResponse($"No element matching '{selectorStr}' found.");
        }

        static object DoEvents()
        {
            var lines = UIEventMonitor.ReadAndClear();
            if (lines.Length == 0)
                return new SuccessResponse("No pending events.", new Newtonsoft.Json.Linq.JArray());

            var events = new Newtonsoft.Json.Linq.JArray();
            foreach (var line in lines)
            {
                try
                {
                    events.Add(Newtonsoft.Json.Linq.JObject.Parse(line));
                }
                catch
                {
                    // skip malformed lines
                }
            }

            return new SuccessResponse($"{events.Count} event(s)", events);
        }

        static string ResolveOutputPrefix(string userPrefix)
        {
            if (string.IsNullOrEmpty(userPrefix))
                userPrefix = "Screenshots/ui-snapshot";

            if (System.IO.Path.IsPathRooted(userPrefix))
                return System.IO.Path.GetFullPath(userPrefix);

            var projectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, userPrefix));
        }
    }
}
