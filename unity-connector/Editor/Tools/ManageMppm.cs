using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "mppm", Description = "Lists, activates, deactivates, and tags Unity Multiplayer Play Mode editor instances without a compile-time MPPM dependency. 'list' and 'status' work from any editor; every action that changes a player must run in the main editor. Tag actions never change activation state.")]
    public static class ManageMppm
    {
        private const string EditorAssemblyName = "UnityEditor.MultiplayerModule";
        private const string RuntimeAssemblyName = "UnityEngine.MultiplayerModule";
        private const string PlaymodeTypeName = "Unity.Multiplayer.PlayMode.Editor.MultiplayerPlaymode";
        private const string CurrentPlayerTypeName = "Unity.Multiplayer.PlayMode.CurrentPlayer";
        private const string VirtualProjectsFolder = "Library/VP";

        /// <summary>
        /// States that mean the player process is up. Anything else — including a state a future
        /// MPPM version adds — counts as not running, so 'count' will launch a replacement
        /// instead of counting a dead player toward the quota.
        /// </summary>
        private static readonly string[] RunningStates = { "Launched", "Launching" };

        private const BindingFlags InstanceMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private const BindingFlags StaticMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public class Parameters
        {
            [ToolParameter("Action: list, status, activate, deactivate, tag, untag, clear-tags (clear_tags is accepted too). Tag actions only edit role tags and never start or stop a player.", Required = true)]
            public string Action { get; set; }

            [ToolParameter("Target one player by name ('Player 2') or by the 1-based index reported by 'list'. The main editor is never a valid target. activate takes 'player' or 'count'; deactivate, tag, untag and clear-tags take 'player' or 'all'. Give exactly one selector.")]
            public string Player { get; set; }

            [ToolParameter("activate only: ensure AT LEAST this many additional editor players are running. Already running players are counted, not restarted, and a surplus is never stopped. Range 1..(number of additional players 'list' reports). Cannot be combined with 'tag' or 'clear_tags'.")]
            public int Count { get; set; }

            [ToolParameter("Apply the action to every additional editor player. Valid for deactivate, tag, untag and clear-tags. activate rejects it so the number of editor processes stays explicit via 'count' or 'player'.")]
            public bool All { get; set; }

            [ToolParameter("activate only: extra command line arguments for the clone editor, split on whitespace, so one argument value cannot contain a space (for example '-e2eIdentity mppm-player2'). Prefer the collision-free key 'player_args'; a bare positional argument on the command line overwrites 'args'.")]
            public string Args { get; set; }

            [ToolParameter("Role tag. Required for 'tag' and 'untag'. With 'activate' it REPLACES the player's tags so a role kept from a previous run cannot leak in.")]
            public string Tag { get; set; }

            [ToolParameter("activate only: launch with no tags at all. Accepted as clear_tags or clear-tags. Use the 'clear-tags' action to clear tags without launching anything.")]
            public bool ClearTags { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var playmodeType = FindType(EditorAssemblyName, PlaymodeTypeName);
            if (playmodeType == null)
            {
                return new ErrorResponse(
                    "Multiplayer Play Mode is not available in this Unity editor. " +
                    $"'{PlaymodeTypeName}' was not found in '{EditorAssemblyName}'.",
                    new { code = "mppm_unavailable", unityVersion = Application.unityVersion });
            }

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage, new { code = "mppm_action_required" });

            var action = actionResult.Value.Trim().ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "list":
                    case "status":
                        return BuildSnapshot(playmodeType, action);

                    case "activate":
                        return Activate(playmodeType, p);

                    case "deactivate":
                        return Deactivate(playmodeType, p);

                    case "tag":
                        return ChangeTags(playmodeType, p, TagMode.Add, action);

                    case "untag":
                        return ChangeTags(playmodeType, p, TagMode.Remove, action);

                    case "clear-tags":
                    case "clear_tags":
                        return ChangeTags(playmodeType, p, TagMode.Clear, "clear-tags");

                    default:
                        return new ErrorResponse(
                            $"Unknown MPPM action: '{actionResult.Value}'. " +
                            "Available: list, status, activate, deactivate, tag, untag, clear-tags.",
                            new { code = "mppm_unknown_action" });
                }
            }
            catch (MissingMemberException ex)
            {
                // Reflection binding is all-or-nothing: if a member is gone, no target could be
                // processed, so there is nothing partial to report.
                return new ErrorResponse(
                    "The installed Multiplayer Play Mode API does not match what this tool expects. " + ex.Message,
                    new { code = "mppm_api_mismatch", action, unityVersion = Application.unityVersion });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                return new ErrorResponse(inner.Message,
                    new { code = "mppm_call_failed", action, players = TryBuildSnapshotData(playmodeType) });
            }
            catch (Exception ex)
            {
                return new ErrorResponse(ex.Message,
                    new { code = "mppm_failed", action, players = TryBuildSnapshotData(playmodeType) });
            }
        }

        private enum TagMode
        {
            Add,
            Remove,
            Clear
        }

        // ---------------------------------------------------------------------
        // Request validation
        // ---------------------------------------------------------------------

        private sealed class Request
        {
            public bool UseAll;
            public int? Count;
            public string Tag;
            public bool ClearTags;
            public List<string> ExtraArgs = new List<string>();
        }

        /// <summary>
        /// Validates the whole parameter set up front. Silent precedence between selectors, or a
        /// parameter that only applies to another action being ignored, would let a caller believe
        /// it asked for something it did not get, so every such combination is an error.
        /// </summary>
        private static object ValidateRequest(ToolParams p, string action, out Request request)
        {
            request = new Request();
            var activate = action == "activate";

            request.UseAll = p.GetBool("all");
            var hasPlayer = !string.IsNullOrWhiteSpace(p.Get("player"));

            var countRaw = p.GetRaw("count");
            var hasCount = countRaw != null && !string.IsNullOrWhiteSpace(countRaw.ToString());

            var tagRaw = p.GetRaw("tag");
            var hasTag = tagRaw != null;
            request.Tag = p.Get("tag")?.Trim();

            request.ClearTags = p.GetBool("clear_tags") || p.GetBool("clear-tags");
            var clearTagsGiven = p.GetRaw("clear_tags") != null || p.GetRaw("clear-tags") != null;

            // Parameters that belong to another action.
            if (hasCount && !activate)
            {
                return new ErrorResponse(
                    "'count' is only supported by the 'activate' action. Use 'player' or 'all' instead.",
                    new { code = "mppm_count_not_supported", action });
            }

            if (clearTagsGiven && !activate)
            {
                return new ErrorResponse(
                    $"'clear_tags' is only supported by the 'activate' action. Use the 'clear-tags' action to clear tags without launching, not '{action}'.",
                    new { code = "mppm_param_not_supported", action, parameter = "clear_tags" });
            }

            if (hasTag && !(activate || action == "tag" || action == "untag"))
            {
                return new ErrorResponse(
                    $"'tag' is not used by the '{action}' action.",
                    new { code = "mppm_param_not_supported", action, parameter = "tag" });
            }

            var argsError = ReadExtraArgs(p, action, activate, request);
            if (argsError != null)
                return argsError;

            if (hasTag && string.IsNullOrWhiteSpace(request.Tag))
            {
                return new ErrorResponse(
                    "'tag' was supplied but is blank. Note that '--tag' followed directly by another flag consumes no value.",
                    new { code = "mppm_tag_blank", action });
            }

            if (activate && hasTag && request.ClearTags)
            {
                return new ErrorResponse(
                    "'tag' and 'clear_tags' contradict each other. 'tag' already replaces the existing tags.",
                    new { code = "mppm_tag_conflict", action });
            }

            // Selectors.
            if (request.UseAll && activate)
            {
                return new ErrorResponse(
                    "'all' is not allowed for activate because each player is another Unity editor process. " +
                    "Use 'count' for how many players should be running, or 'player' for one specific player.",
                    new { code = "mppm_use_count", action });
            }

            var supplied = new List<string>();
            if (hasPlayer) supplied.Add("player");
            if (request.UseAll) supplied.Add("all");
            if (hasCount) supplied.Add("count");

            if (supplied.Count > 1)
            {
                return new ErrorResponse(
                    $"Give exactly one target selector, not {string.Join(" + ", supplied)}.",
                    new { code = "mppm_selector_conflict", action, supplied = supplied.ToArray() });
            }

            if (hasCount)
            {
                var parsed = p.GetInt("count");
                if (parsed == null)
                {
                    return new ErrorResponse(
                        $"'count' must be a whole number, got '{countRaw}'.",
                        new { code = "mppm_count_invalid", action });
                }
                request.Count = parsed.Value;

                // 'count' ensures a number of running players and does not name one, so a role tag
                // would either be dropped (nothing to launch) or applied to several players at once.
                if (hasTag || request.ClearTags)
                {
                    return new ErrorResponse(
                        "'tag' and 'clear_tags' need a specific player. Use 'player' to set a role, or run the 'tag' action after activating.",
                        new { code = "mppm_tag_needs_player", action });
                }
            }

            return null;
        }

        /// <summary>
        /// Reads the clone editor arguments. The CLI writes bare positional arguments into the
        /// 'args' key, so an array value there is a command line accident rather than a request.
        /// </summary>
        private static object ReadExtraArgs(ToolParams p, string action, bool activate, Request request)
        {
            var preferred = p.GetRaw("player_args");
            var legacy = p.GetRaw("args");
            var raw = preferred ?? legacy;

            if (raw == null)
                return null;

            if (!activate)
            {
                return new ErrorResponse(
                    $"Clone editor arguments are only used by the 'activate' action, not '{action}'.",
                    new { code = "mppm_param_not_supported", action, parameter = preferred != null ? "player_args" : "args" });
            }

            if (raw.Type == JTokenType.Array)
            {
                var items = raw.Children().Select(token => token.ToString()).ToArray();

                if (preferred != null)
                {
                    request.ExtraArgs = items.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
                    return null;
                }

                return new ErrorResponse(
                    "'args' received a list, which happens when a bare word is left on the command line " +
                    $"(received: {string.Join(" ", items)}). Quote the arguments and pass them as 'player_args'.",
                    new { code = "mppm_args_positional", action, received = items });
            }

            request.ExtraArgs = SplitArgs(raw.ToString());
            return null;
        }

        private static object RequireMainEditor(string operation, string action)
        {
            if (IsMainEditor())
                return null;

            return new ErrorResponse(
                $"MPPM {operation} must run in the main editor, not in a clone editor.",
                new { code = "mppm_not_main_editor", action });
        }

        /// <summary>
        /// Resolves the additional editor players a 'player' or 'all' request targets. The main
        /// editor is never a valid target, so selecting it is reported instead of silently skipped.
        /// </summary>
        private static object ResolveTargets(
            Type playmodeType,
            ToolParams p,
            Request request,
            string action,
            string operation,
            out List<PlayerHandle> targets)
        {
            targets = null;

            var players = GetPlayers(playmodeType);
            if (players.Count == 0)
            {
                return new ErrorResponse(
                    "MPPM reported no players at all.",
                    new { code = "mppm_registry_empty", action });
            }

            if (request.UseAll)
            {
                targets = players.Where(handle => !handle.IsMain).ToList();
                if (targets.Count == 0)
                {
                    return new ErrorResponse("No additional editor players exist.",
                        new { code = "mppm_no_clone_players", action });
                }
                return null;
            }

            var selector = p.Get("player");
            if (string.IsNullOrWhiteSpace(selector))
            {
                var selectors = action == "activate" ? "'player' or 'count'" : "'player' or 'all'";
                return new ErrorResponse(
                    $"{selectors} is required for {operation}. Use a name like 'Player 2' or the 1-based index from 'list'.",
                    new { code = "mppm_target_required", action });
            }

            var selected = SelectPlayer(players, selector, out var selectionError);
            if (selected == null)
            {
                return new ErrorResponse(selectionError,
                    new { code = "mppm_instance_not_found", action, available = players.Select(h => h.Name).ToArray() });
            }

            if (selected.IsMain)
            {
                return new ErrorResponse(
                    $"'{selected.Name}' is the main editor and is not a valid target for {operation}.",
                    new { code = "mppm_main_editor_target", action });
            }

            targets = new List<PlayerHandle> { selected };
            return null;
        }

        // ---------------------------------------------------------------------
        // Activate / deactivate
        // ---------------------------------------------------------------------

        private static object Activate(Type playmodeType, ToolParams p)
        {
            const string action = "activate";

            var validation = ValidateRequest(p, action, out var request);
            if (validation != null)
                return validation;

            var gate = RequireMainEditor("activation", action);
            if (gate != null)
                return gate;

            var clones = GetPlayers(playmodeType).Where(handle => !handle.IsMain).ToList();
            if (clones.Count == 0)
                return new ErrorResponse("No additional editor players exist.", new { code = "mppm_no_clone_players", action });

            var runningBefore = clones.Where(IsRunning).Select(handle => handle.Name).ToArray();
            List<PlayerHandle> targets;

            if (request.Count != null)
            {
                var count = request.Count.Value;
                if (count < 1 || count > clones.Count)
                {
                    return new ErrorResponse(
                        $"'count' must be between 1 and {clones.Count}.",
                        new { code = "mppm_count_out_of_range", action, maxCount = clones.Count });
                }

                // 'count' guarantees a minimum, never an exact number: stopping a surplus editor
                // could kill a player another test is using, so surplus is reported, not touched.
                var missing = count - runningBefore.Length;
                targets = missing <= 0
                    ? new List<PlayerHandle>()
                    : clones.Where(handle => !IsRunning(handle)).Take(missing).ToList();
            }
            else
            {
                var targetError = ResolveTargets(playmodeType, p, request, action, "activation", out targets);
                if (targetError != null)
                    return targetError;

                // Make the named path idempotent too, so a retry after a timeout is not an error.
                if (targets.Count == 1 && IsRunning(targets[0]))
                    targets = new List<PlayerHandle>();
            }

            if (targets.Count == 0)
            {
                var note = request.Count != null && runningBefore.Length > request.Count.Value
                    ? $"More players are running ({runningBefore.Length}) than the requested {request.Count.Value}. A surplus is never stopped; deactivate it explicitly if that matters."
                    : "Nothing to activate: the requested player(s) are already running.";

                return new SuccessResponse(
                    $"{runningBefore.Length} additional editor player(s) already running: {string.Join(", ", runningBefore)}.",
                    BuildActivationPayload(playmodeType, action, note, new List<TargetResult>(), 0,
                        new string[0], new List<string>(), new List<string>(), runningBefore, request.Count));
            }

            var results = new List<TargetResult>();
            var failures = new List<string>();
            var inherited = new List<string>();
            var replaced = new List<string>();

            foreach (var target in targets)
            {
                try
                {
                    var existing = target.ReadTags();
                    var desired = request.ClearTags
                        ? new string[0]
                        : string.IsNullOrWhiteSpace(request.Tag) ? null : new[] { request.Tag };

                    if (desired != null)
                    {
                        // A role kept from a previous run is the easiest way to launch a
                        // misconfigured player, so an explicit tag REPLACES the tag set. Skip the
                        // rewrite when it already matches: clearing first would make a live child
                        // observe an empty tag set for a moment.
                        if (!SameTagSet(existing, desired))
                        {
                            if (existing.Length > 0)
                            {
                                replaced.Add($"{target.Name} -> [{string.Join(", ", existing)}]");

                                if (!target.ClearTags(out var clearError))
                                {
                                    results.Add(target.ToResult(false, $"clearing tags failed: {clearError}"));
                                    failures.Add($"{target.Name}: clearing tags failed: {clearError}");
                                    continue;
                                }
                            }

                            if (desired.Length > 0 && !target.AddTag(desired[0], out var tagError))
                            {
                                results.Add(target.ToResult(false, $"adding tag '{desired[0]}' failed: {tagError}"));
                                failures.Add($"{target.Name}: adding tag '{desired[0]}' failed: {tagError}");
                                continue;
                            }
                        }
                    }
                    else if (existing.Length > 0)
                    {
                        inherited.Add($"{target.Name} -> [{string.Join(", ", existing)}]");
                    }

                    var ok = target.Activate(request.ExtraArgs, out var error);
                    results.Add(target.ToResult(ok, error));

                    if (!ok)
                        failures.Add($"{target.Name}: {error}");
                }
                catch (MissingMemberException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    results.Add(target.ToResult(false, inner.Message));
                    failures.Add($"{target.Name}: {inner.Message}");
                }
            }

            return BuildActivationResponse(playmodeType, action, "Activate", "mppm_activation_failed",
                targets, results, failures, inherited, replaced, runningBefore, request.Count);
        }

        private static object Deactivate(Type playmodeType, ToolParams p)
        {
            const string action = "deactivate";

            var validation = ValidateRequest(p, action, out var request);
            if (validation != null)
                return validation;

            var gate = RequireMainEditor("deactivation", action);
            if (gate != null)
                return gate;

            var targetError = ResolveTargets(playmodeType, p, request, action, "deactivation", out var targets);
            if (targetError != null)
                return targetError;

            var results = new List<TargetResult>();
            var failures = new List<string>();

            foreach (var target in targets)
            {
                try
                {
                    var ok = target.Deactivate(out var error);
                    results.Add(target.ToResult(ok, error));

                    if (!ok)
                        failures.Add($"{target.Name}: {error}");
                }
                catch (MissingMemberException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    results.Add(target.ToResult(false, inner.Message));
                    failures.Add($"{target.Name}: {inner.Message}");
                }
            }

            return BuildActivationResponse(playmodeType, action, "Deactivate", "mppm_deactivation_failed",
                targets, results, failures, new List<string>(), new List<string>(), null, null);
        }

        /// <summary>
        /// Builds one payload shape for every activate and deactivate outcome, so a caller can read
        /// the same fields whether the request launched something, was a no-op, or failed part way.
        /// </summary>
        private static object BuildActivationPayload(
            Type playmodeType,
            string action,
            string note,
            List<TargetResult> results,
            int requestedTargets,
            string[] skipped,
            List<string> inherited,
            List<string> replaced,
            string[] runningBefore,
            int? requestedCount,
            string[] failed = null,
            bool? partial = null,
            string code = null)
        {
            var players = BuildSnapshotData(playmodeType);
            var runningNow = players.Count(entry => entry.running && !entry.isMain);

            return new
            {
                code,
                action,
                note,
                partial,
                requested = results,
                attempted = results.Count,
                requestedTargets,
                skipped = skipped ?? new string[0],
                failed = failed ?? new string[0],
                inheritedTags = inherited.ToArray(),
                replacedTags = replaced.ToArray(),
                alreadyRunning = runningBefore,
                runningCount = runningNow,
                requestedCount,
                players,
                projectPath = GetProjectRoot()
            };
        }

        private static object BuildActivationResponse(
            Type playmodeType,
            string action,
            string verb,
            string failureCode,
            List<PlayerHandle> targets,
            List<TargetResult> results,
            List<string> failures,
            List<string> inherited,
            List<string> replaced,
            string[] runningBefore,
            int? requestedCount)
        {
            var skipped = targets.Skip(results.Count).Select(t => t.Name).ToArray();

            if (failures.Count > 0)
            {
                var changed = results.Count(r => r.ok) + replaced.Count;
                var note = changed > 0
                    ? $"{changed} change(s) were already applied before the failure. Read 'players' for the current state."
                    : null;

                return new ErrorResponse(
                    $"{verb} failed: {string.Join("; ", failures)}",
                    BuildActivationPayload(playmodeType, action, note, results, targets.Count, skipped,
                        inherited, replaced, runningBefore, requestedCount,
                        failures.ToArray(), changed > 0, failureCode));
            }

            var notes = new List<string>();
            if (replaced.Count > 0)
                notes.Add("Replaced tags kept from a previous run: " + string.Join("; ", replaced) + ".");

            if (inherited.Count > 0)
            {
                notes.Add("Launched with tags kept from a previous run: " + string.Join("; ", inherited) +
                          ". Pass 'tag' or 'clear_tags' to set the role explicitly.");
            }

            var successNote = notes.Count > 0 ? string.Join(" ", notes) : null;
            var names = string.Join(", ", targets.Select(t => t.Name));

            return new SuccessResponse(
                $"{verb}d {names}." + (successNote == null ? string.Empty : " " + successNote),
                BuildActivationPayload(playmodeType, action, successNote, results, targets.Count, skipped,
                    inherited, replaced, runningBefore, requestedCount));
        }

        // ---------------------------------------------------------------------
        // Tags
        // ---------------------------------------------------------------------

        /// <summary>
        /// Adds, removes, or clears role tags without touching activation state. MPPM keeps tags
        /// in Library/VP/SystemData.json, so they survive deactivation and must be changed
        /// explicitly rather than as a side effect of launching a player.
        /// </summary>
        private static object ChangeTags(Type playmodeType, ToolParams p, TagMode mode, string action)
        {
            var validation = ValidateRequest(p, action, out var request);
            if (validation != null)
                return validation;

            var gate = RequireMainEditor("tag changes", action);
            if (gate != null)
                return gate;

            var targetError = ResolveTargets(playmodeType, p, request, action, "tag changes", out var targets);
            if (targetError != null)
                return targetError;

            if (mode != TagMode.Clear && string.IsNullOrWhiteSpace(request.Tag))
            {
                return new ErrorResponse(
                    $"'tag' is required for the '{action}' action.",
                    new { code = "mppm_tag_required", action });
            }

            var results = new List<TargetResult>();
            var failures = new List<string>();

            foreach (var target in targets)
            {
                try
                {
                    bool ok;
                    string error;

                    switch (mode)
                    {
                        case TagMode.Add:
                            ok = target.AddTag(request.Tag, out error);
                            break;
                        case TagMode.Remove:
                            ok = target.RemoveTag(request.Tag, out error);
                            break;
                        default:
                            ok = target.ClearTags(out error);
                            break;
                    }

                    results.Add(target.ToResult(ok, error));

                    if (!ok)
                        failures.Add($"{target.Name}: {error}");
                }
                catch (MissingMemberException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    results.Add(target.ToResult(false, inner.Message));
                    failures.Add($"{target.Name}: {inner.Message}");
                }
            }

            var running = targets.Where(IsRunning).Select(t => t.Name).ToArray();
            var skipped = targets.Skip(results.Count).Select(t => t.Name).ToArray();

            if (failures.Count > 0)
            {
                var changed = results.Count(r => r.ok);
                return new ErrorResponse(
                    $"Tag change failed: {string.Join("; ", failures)}",
                    BuildTagPayload(playmodeType, action, request.Tag,
                        changed > 0 ? $"{changed} player(s) were already changed before the failure." : null,
                        results, targets.Count, skipped, running, failures.ToArray(), changed > 0, "mppm_tag_failed"));
            }

            var verb = mode switch
            {
                TagMode.Add => $"Added tag '{request.Tag}' to",
                TagMode.Remove => $"Removed tag '{request.Tag}' from",
                _ => "Cleared tags on"
            };

            string note = null;
            if (running.Length > 0)
            {
                // MPPM pushes the tag list to the live player, so CurrentPlayer.ReadOnlyTags()
                // reflects it right away. Game code that already resolved a role from tags does
                // not re-read it on its own.
                note = $"Already running: {string.Join(", ", running)}. MPPM applies the tag immediately, " +
                       "but code that already resolved its role from tags keeps the old value until it reads again.";
            }

            return new SuccessResponse(
                $"{verb} {string.Join(", ", targets.Select(t => t.Name))}.",
                BuildTagPayload(playmodeType, action, request.Tag, note, results, targets.Count, skipped, running));
        }

        private static object BuildTagPayload(
            Type playmodeType,
            string action,
            string tag,
            string note,
            List<TargetResult> results,
            int requestedTargets,
            string[] skipped,
            string[] running,
            string[] failed = null,
            bool? partial = null,
            string code = null)
        {
            return new
            {
                code,
                action,
                tag,
                note,
                partial,
                requested = results,
                attempted = results.Count,
                requestedTargets,
                skipped = skipped ?? new string[0],
                failed = failed ?? new string[0],
                appliedToRunningPlayers = running,
                players = BuildSnapshotData(playmodeType),
                projectPath = GetProjectRoot()
            };
        }

        private static bool SameTagSet(string[] existing, string[] desired)
        {
            if (existing.Length != desired.Length)
                return false;

            return existing
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(desired.OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------
        // Snapshots and helpers
        // ---------------------------------------------------------------------

        private sealed class TargetResult
        {
            public string name;
            public int index;
            public bool ok;
            public string error;
            public string state;
            public string[] tags;
            public string virtualProjectPath;
        }

        private sealed class PlayerSnapshot
        {
            public int index;
            public string name;
            public string type;
            public string state;
            public bool isMain;
            public bool running;
            public string[] tags;
            public string virtualProjectPath;
        }

        private static object BuildSnapshot(Type playmodeType, string action)
        {
            bool? isMain;
            string note = null;

            try
            {
                isMain = IsMainEditor();
            }
            catch (MissingMemberException)
            {
                // A snapshot is informational, so an unresolvable CurrentPlayer must not hide the
                // player table that is still readable.
                isMain = null;
                note = "Could not determine whether this editor is the main editor: the MPPM CurrentPlayer API did not match.";
            }

            var players = BuildSnapshotData(playmodeType);

            if (note == null && isMain == false && players.Length == 0)
            {
                note = "This is a clone editor and it does not hold the MPPM player registry, " +
                       "so the list is empty. Run 'mppm' against the main editor project to see the players.";
            }

            return new SuccessResponse("MPPM player status.", new
            {
                action,
                available = true,
                isMainEditor = isMain,
                note,
                unityVersion = Application.unityVersion,
                projectPath = GetProjectRoot(),
                players
            });
        }

        private static PlayerSnapshot[] BuildSnapshotData(Type playmodeType)
        {
            return GetPlayers(playmodeType)
                .Select(handle => new PlayerSnapshot
                {
                    index = handle.Index,
                    name = handle.Name,
                    type = handle.ReadType(),
                    state = handle.ReadState(),
                    isMain = handle.IsMain,
                    running = IsRunning(handle),
                    tags = handle.ReadTags(),
                    virtualProjectPath = handle.ReadVirtualProjectPath()
                })
                .ToArray();
        }

        private static PlayerSnapshot[] TryBuildSnapshotData(Type playmodeType)
        {
            try
            {
                return BuildSnapshotData(playmodeType);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsRunning(PlayerHandle handle)
        {
            return RunningStates.Contains(handle.ReadState(), StringComparer.OrdinalIgnoreCase);
        }

        private static PlayerHandle SelectPlayer(List<PlayerHandle> players, string selector, out string error)
        {
            error = null;
            var trimmed = selector.Trim();

            if (int.TryParse(trimmed, out var index))
            {
                var byIndex = players.FirstOrDefault(handle => handle.Index == index);
                if (byIndex != null)
                    return byIndex;

                error = $"No MPPM player at index {index}. Valid indices: " +
                        string.Join(", ", players.Select(handle => handle.Index));
                return null;
            }

            var normalized = Normalize(trimmed);
            var matches = players.Where(handle => Normalize(handle.Name) == normalized).ToList();

            if (matches.Count == 1)
                return matches[0];

            if (matches.Count > 1)
            {
                error = $"MPPM player name '{selector}' is ambiguous. Matches: " +
                        string.Join(", ", matches.Select(m => $"{m.Name} (index {m.Index})"));
                return null;
            }

            error = $"No MPPM player named '{selector}'. Available: " +
                    string.Join(", ", players.Select(handle => handle.Name));
            return null;
        }

        private static string Normalize(string value)
        {
            return new string((value ?? string.Empty)
                .Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-')
                .ToArray())
                .ToLowerInvariant();
        }

        private static List<string> SplitArgs(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static List<PlayerHandle> GetPlayers(Type playmodeType)
        {
            var property = playmodeType.GetProperty("Players", StaticMembers);
            if (property == null)
                throw new MissingMemberException(playmodeType.FullName, "Players");

            var array = property.GetValue(null) as Array;
            if (array == null)
                return new List<PlayerHandle>();

            var handles = new List<PlayerHandle>();
            for (var i = 0; i < array.Length; i++)
            {
                var player = array.GetValue(i);
                if (player != null)
                    handles.Add(new PlayerHandle(player, i + 1));
            }
            return handles;
        }

        private static bool IsMainEditor()
        {
            var type = FindType(RuntimeAssemblyName, CurrentPlayerTypeName);
            var property = type?.GetProperty("IsMainEditor", StaticMembers);
            if (property == null)
                throw new MissingMemberException(CurrentPlayerTypeName, "IsMainEditor");

            return property.GetValue(null) is true;
        }

        private static string GetProjectRoot()
        {
            var dataPath = Application.dataPath.Replace('\\', '/');
            return dataPath.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase)
                ? dataPath.Substring(0, dataPath.Length - "/Assets".Length)
                : dataPath;
        }

        private static Type FindType(string assemblyName, string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != assemblyName)
                    continue;

                var type = assembly.GetType(typeName, false);
                if (type != null)
                    return type;
            }
            return null;
        }

        /// <summary>
        /// Reflection wrapper over one MPPM UnityPlayer. The MPPM types are internal, so every
        /// member is resolved by name. Members this tool depends on are resolved strictly: a
        /// missing or renamed member raises an API mismatch instead of degrading to a default
        /// value that would silently misclassify a player.
        /// </summary>
        private sealed class PlayerHandle
        {
            private readonly object _player;
            private readonly Type _type;

            public PlayerHandle(object player, int index)
            {
                _player = player;
                _type = player.GetType();
                Index = index;
            }

            public int Index { get; }

            public string Name => ReadRequired("Name")?.ToString() ?? string.Empty;

            public bool IsMain => string.Equals(ReadType(), "Main", StringComparison.OrdinalIgnoreCase);

            public string ReadType() => ReadRequired("Type")?.ToString();

            public string ReadState() => ReadRequired("PlayerState")?.ToString();

            public TargetResult ToResult(bool ok, string error)
            {
                return new TargetResult
                {
                    name = Name,
                    index = Index,
                    ok = ok,
                    // A success carrying error:"None" reads as a contradiction to an automated caller.
                    error = ok ? null : error,
                    state = ReadState(),
                    tags = ReadTags(),
                    virtualProjectPath = ReadVirtualProjectPath()
                };
            }

            public string[] ReadTags()
            {
                var value = ReadRequired("Tags");
                if (value == null)
                    return new string[0];

                var tags = value as IEnumerable;
                if (tags == null)
                    throw new MissingMemberException(_type.FullName, "Tags");

                return tags.Cast<object>()
                    .Where(tag => tag != null)
                    .Select(tag => tag.ToString())
                    .ToArray();
            }

            /// <summary>
            /// Resolves the clone project path MPPM launches this player with, so the caller can
            /// target the child editor directly with 'unity-cli --project'. Null is correct for the
            /// main editor and for a player that has never been activated.
            /// </summary>
            public string ReadVirtualProjectPath()
            {
                var info = ReadOptional(_player, "TypeDependentPlayerInfo");
                if (info == null)
                    return null;

                var identifier = ReadOptional(info, "VirtualProjectIdentifier");
                if (identifier == null)
                    return null;

                var prefix = ReadOptional(identifier, "m_Prefix")?.ToString();
                var id = ReadOptional(identifier, "m_Id")?.ToString();
                var folder = prefix + id;

                // Never fabricate a path from the identifier's ToString(): the caller feeds this
                // straight into --project, so an unresolved identifier must read as "unknown".
                return string.IsNullOrEmpty(folder)
                    ? null
                    : $"{GetProjectRoot()}/{VirtualProjectsFolder}/{folder}";
            }

            public bool AddTag(string tag, out string error) => InvokeTag("AddTag", tag, out error);

            public bool RemoveTag(string tag, out string error) => InvokeTag("RemoveTag", tag, out error);

            public bool ClearTags(out string error)
            {
                var method = FindExact("ClearTags", parameters => parameters.Length == 1
                                                                  && parameters[0].ParameterType.IsByRef
                                                                  && parameters[0].ParameterType.GetElementType()?.IsEnum == true);

                var args = new[] { DefaultOf(method.GetParameters()[0].ParameterType.GetElementType()) };
                var ok = method.Invoke(_player, args) is true;
                error = args[0]?.ToString();
                return ok;
            }

            public bool Activate(List<string> extraArgs, out string error)
            {
                var method = FindExact("Activate", parameters => parameters.Length == 2
                                                                 && parameters[0].ParameterType.IsByRef
                                                                 && parameters[0].ParameterType.GetElementType()?.IsEnum == true
                                                                 && parameters[1].ParameterType == typeof(List<string>));

                // MPPM hands the second argument straight to the clone editor command line.
                var args = new object[]
                {
                    DefaultOf(method.GetParameters()[0].ParameterType.GetElementType()),
                    extraArgs
                };

                var ok = method.Invoke(_player, args) is true;
                error = args[0]?.ToString();
                return ok;
            }

            public bool Deactivate(out string error)
            {
                var method = FindExact("Deactivate", parameters => parameters.Length == 1
                                                                   && parameters[0].ParameterType.IsByRef
                                                                   && parameters[0].ParameterType.GetElementType()?.IsEnum == true);

                var args = new[] { DefaultOf(method.GetParameters()[0].ParameterType.GetElementType()) };
                var ok = method.Invoke(_player, args) is true;
                error = args[0]?.ToString();
                return ok;
            }

            private bool InvokeTag(string name, string tag, out string error)
            {
                var method = FindExact(name, parameters => parameters.Length == 2
                                                           && parameters[0].ParameterType == typeof(string)
                                                           && parameters[1].ParameterType.IsByRef
                                                           && parameters[1].ParameterType.GetElementType()?.IsEnum == true);

                var args = new object[] { tag, DefaultOf(method.GetParameters()[1].ParameterType.GetElementType()) };
                var ok = method.Invoke(_player, args) is true;
                error = args[1]?.ToString();
                return ok;
            }

            /// <summary>
            /// Binds exactly one overload. A future MPPM overload that merely looks compatible
            /// must not be picked silently, so zero or several matches are an API mismatch.
            /// </summary>
            private MethodInfo FindExact(string name, Func<ParameterInfo[], bool> matches)
            {
                var candidates = _type.GetMethods(InstanceMembers)
                    .Where(m => m.Name == name
                                && m.ReturnType == typeof(bool)
                                && matches(m.GetParameters()))
                    .ToList();

                if (candidates.Count == 1)
                    return candidates[0];

                var overloads = _type.GetMethods(InstanceMembers)
                    .Where(m => m.Name == name)
                    .Select(m => name + "(" + string.Join(", ", m.GetParameters()
                        .Select(x => x.ParameterType.Name)) + ") -> " + m.ReturnType.Name)
                    .ToArray();

                throw new MissingMemberException(_type.FullName,
                    candidates.Count == 0
                        ? $"{name} with the expected signature (found: {(overloads.Length == 0 ? "none" : string.Join(" | ", overloads))})"
                        : $"{name} matched {candidates.Count} overloads ({string.Join(" | ", overloads)})");
            }

            private static object DefaultOf(Type type)
            {
                return type != null && type.IsValueType ? Activator.CreateInstance(type) : null;
            }

            private object ReadRequired(string name)
            {
                var property = _type.GetProperty(name, InstanceMembers);
                if (property != null)
                    return property.GetValue(_player);

                var field = _type.GetField(name, InstanceMembers);
                if (field != null)
                    return field.GetValue(_player);

                throw new MissingMemberException(_type.FullName, name);
            }

            private static object ReadOptional(object target, string name)
            {
                if (target == null)
                    return null;

                var type = target.GetType();

                var property = type.GetProperty(name, InstanceMembers);
                if (property != null)
                    return property.GetValue(target);

                var field = type.GetField(name, InstanceMembers);
                return field?.GetValue(target);
            }
        }
    }
}
