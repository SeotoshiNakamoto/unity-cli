# unity-cli

[English](README.md) | [Korean](README.ko.md)

> Control Unity Editor from the command line. Built for AI agents, works with anything.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**No server to run. No config to write. No process to manage. Just type a command.**

## Why this exists

I wanted to control Unity from the terminal. The existing MCP-based integrations required Python runtimes, WebSocket relays, JSON-RPC protocol layers, config files, server processes that need to be started and stopped, tool registration ceremonies, and tens of thousands of lines of over-engineered code. All just to send a simple command to Unity.

On top of that, every AI agent that wanted to use it needed its own MCP config and integration setup. The CLI doesn't care — any agent that can run a shell command can use it immediately.

That felt wrong. If I can `curl` a URL, why do I need all that?

So I built the opposite: a single binary that talks directly to Unity via HTTP. No server to run — the Unity package listens automatically. No config to write — it discovers Unity instances on its own. No tool registration — just call by name. No caching, no protocol layers, no ceremony.

The entire CLI is ~800 lines of Go (plus ~300 lines of help text). The Unity-side connector is ~2,300 lines of C#. It's just a thin layer that lets you control Unity from the shell — nothing more. You install the binary, add the Unity package, and it works.

## Install

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/youngwoocho02/unity-cli/master/install.sh | sh
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/youngwoocho02/unity-cli/master/install.ps1 | iex
```

### Other options

```bash
# Go install (any platform with Go)
go install github.com/youngwoocho02/unity-cli@latest

# Manual download (pick your platform)
# Linux amd64 / Linux arm64 / macOS amd64 / macOS arm64 / Windows amd64
curl -fsSL https://github.com/youngwoocho02/unity-cli/releases/latest/download/unity-cli-linux-amd64 -o unity-cli
chmod +x unity-cli && sudo mv unity-cli /usr/local/bin/
```

Supported platforms: Linux (amd64, arm64), macOS (Intel, Apple Silicon), Windows (amd64).

### Update

This fork has no self-update command. Rebuild the binary from this repository and deploy it to the project that uses it.

## Unity Setup

Add the Unity Connector package via **Package Manager → Add package from git URL**:

```
https://github.com/youngwoocho02/unity-cli.git?path=unity-connector
```

Or add directly to `Packages/manifest.json`:
```json
"com.youngwoocho02.unity-cli-connector": "https://github.com/youngwoocho02/unity-cli.git?path=unity-connector"
```

To pin a specific version, append a tag to the URL (e.g. `#v0.2.21`).

Once added, the Connector starts automatically when Unity opens. No configuration needed.

### Recommended: Disable Editor Throttling

By default, Unity throttles editor updates when the window is unfocused. This means CLI commands may not execute until you click back into Unity.

To fix this, go to **Edit → Preferences → General → Interaction Mode** and set it to **No Throttling**.

This ensures CLI commands are processed immediately, even when Unity is in the background.

## Quick Start

```bash
# Check Unity connection
unity-cli status

# Enter play mode and wait
unity-cli editor play --wait

# Run C# code inside Unity
unity-cli exec "return Application.dataPath;"

# Read console logs
unity-cli console --type error,warning,log
```

## How It Works

```
Terminal                              Unity Editor
────────                              ────────────
$ unity-cli editor play --wait
    │
    ├─ scans ~/.unity-cli/instances/*.json
    │  → finds Unity on port 8090
    │
    ├─ POST http://127.0.0.1:8090/command
    │  { "command": "manage_editor",
    │    "params": { "action": "play",
    │                "wait_for_completion": true }}
    │                                      │
    │                                  HttpServer receives
    │                                      │
    │                                  CommandRouter dispatches
    │                                      │
    │                                  ManageEditor.HandleCommand()
    │                                  → EditorApplication.isPlaying = true
    │                                  → waits for PlayModeStateChange
    │                                      │
    ├─ receives JSON response  ←───────────┘
    │  { "success": true,
    │    "message": "Entered play mode (confirmed)." }
    │
    └─ prints: Entered play mode (confirmed).
```

The Unity Connector:
1. Opens an HTTP server on `localhost:8090` when the Editor starts
2. Writes a per-project instance file to `~/.unity-cli/instances/` so the CLI knows where to connect
3. Updates the instance file every 0.5s with the current state (heartbeat)
4. Finds `[UnityCliTool]` classes through Unity's domain-managed `TypeCache`
5. Routes incoming commands to the matching handler on the main thread
6. Survives domain reloads (script recompilation)
7. Wakes a throttled Editor when commands arrive and restarts a failed listener automatically

Before compiling or reloading, the Connector records the state (`compiling`, `reloading`) to the instance file. The heartbeat also records the Connector version and listener state. Readiness requires a non-future heartbeat less than 1.5 seconds old in `ready`, `playing`, or `paused`, plus a reachable listener. `compiling`, `reloading`, refresh/mode transitions and stale heartbeats are not ready even if HTTP answers. Probes use a lightweight `GET /health` endpoint that does not wait for Unity's main-thread command queue. Compilation additionally requires a witnessed busy cycle and two consecutive fresh `ready` heartbeats. Commands are never automatically resent. If the listener exits unexpectedly, the Connector retries it automatically without changing project selection semantics.

## Built-in Commands

| Command | Description |
|---------|-------------|
| `editor` | Play/stop/pause/quit/refresh the Unity Editor |
| `instances` | List, wait for, or force-kill an explicitly selected Editor |
| `parrelsync` | List, create, and open ParrelSync clones |
| `player` | Launch and control opt-in Development/Release Player bridges |
| `console` | Read, filter, and clear console logs |
| `exec` | Run arbitrary C# code inside Unity |
| `test` | Run EditMode/PlayMode tests |
| `menu` | Execute any Unity menu item by path |
| `reserialize` | Re-serialize assets through Unity's serializer |
| `screenshot` | Capture scene/game view as PNG (game view at its render resolution) |
| `ui` | Observe and interact with UIToolkit UI; opt-in screen-change monitoring |
| `profiler` | Read profiler hierarchy, control recording |
| `framedebug` | Dump one Frame Debugger frame to JSON with rendering state and summaries |
| `list` | Show all available tools with parameter schemas |
| `status` | Show Unity Editor connection state |

### Editor Control

```bash
# Enter play mode
unity-cli editor play

# Enter play mode and wait until fully loaded
unity-cli editor play --wait

# Stop play mode
unity-cli editor stop

# Toggle pause (only works during play mode)
unity-cli editor pause

# Gracefully close the selected Editor
unity-cli --project D:/Projects/Game/client_clone_0 editor quit

# Refresh assets
unity-cli editor refresh

# Refresh and recompile scripts (waits for compilation to finish)
unity-cli editor refresh --compile
```

### Console Logs

```bash
# Read error and warning logs (default)
unity-cli console

# Read last 20 log entries of all types
unity-cli console --lines 20 --filter error,warning,log

# Read only errors
unity-cli console --type error

# Include stack traces (user: user code only, full: raw)
unity-cli console --stacktrace user

# Clear console
unity-cli console --clear
```

### Execute C# Code

Run arbitrary C# code inside the Unity Editor at runtime. This is the most powerful command — it gives you full access to UnityEngine, UnityEditor, ECS, and every loaded assembly. No need to write a custom tool for one-off queries or mutations.

Use `return` to get output. Common namespaces are included by default. Add `--usings` only for project-specific types (e.g. `Unity.Entities`). The csc compiler and dotnet runtime are auto-detected; if detection fails, specify manually with `--csc <path>` or `--dotnet <path>`.

```bash
unity-cli exec "return Application.dataPath;"
unity-cli exec "return EditorSceneManager.GetActiveScene().name;"
unity-cli exec "return World.All.Count;" --usings Unity.Entities

# Pipe via stdin to avoid shell escaping issues
echo 'Debug.Log("hello"); return null;' | unity-cli exec
echo 'var go = new GameObject("Marker"); go.tag = "EditorOnly"; return go.name;' | unity-cli exec

# The whole synchronous script can run as a pollable job
unity-cli exec --file long-running.cs --async

# Deferred callbacks require an explicit lifetime override
unity-cli exec "EditorApplication.delayCall += RunLater; return null;" --allow-deferred-code
```

Identical generated source and compilation inputs reuse a loaded execution method within the current Editor domain; the code still runs on every call and results are not cached. Script compilation and domain reload invalidate the cache. Different code or changed compilation inputs still load additional assemblies: clearing the cache does not unload them. Cache keys use generated source, compiler options/paths and ordered loaded-reference metadata, not DLL file contents. Successful automatic compiler/host paths are reused within the Editor domain and rediscovered if their files disappear; overrides do not replace remembered paths. Restart/reload after in-place toolchain changes. See [exec cache regression tests](tests/exec/README.md) for validation scope and limitations.

Because `exec` compiles and runs real C#, it can do anything a custom tool can — inspect ECS entities, modify assets, call internal APIs, run editor utilities. For AI agents, this means **zero-friction access to Unity's entire runtime** without writing a single line of tool code. Piping via stdin avoids shell escaping headaches with complex code.

Code that can outlive the request (`async`/`await`, tasks, coroutines, Unity async operations, or `EditorApplication` deferred callbacks) is blocked by default. `--async` only moves the complete CLI command into a pollable job; it does not make detached C# callbacks safe. Use `--allow-deferred-code` only when that lifetime is intentional and cleanup is handled explicitly.

### Menu Items

```bash
# Execute any Unity menu item by path
unity-cli menu "File/Save Project"
unity-cli menu "Assets/Refresh"
unity-cli menu "Window/General/Console"
```

Note: `File/Quit` is blocked for safety.

### Asset Reserialize

AI agents (and humans) can edit Unity asset files — `.prefab`, `.unity`, `.asset`, `.mat` — as plain text YAML. But Unity's YAML serializer is strict: a missing field, wrong indent, or stale `fileID` will corrupt the asset silently.

`reserialize` fixes this. After a text edit, it tells Unity to load the asset into memory and write it back out through its own serializer. The result is a clean, valid YAML file — as if you had edited it through the Inspector.

```bash
# Reserialize the entire project (no arguments)
unity-cli reserialize

# After editing a prefab's transform values in a text editor
unity-cli reserialize Assets/Prefabs/Player.prefab

# After batch-editing multiple scenes
unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity

# After modifying material properties
unity-cli reserialize Assets/Materials/Character.mat
```

This is what makes text-based asset editing safe. Without it, a single misplaced YAML field can break a prefab with no visible error until runtime. With it, **AI agents can confidently modify any Unity asset through plain text** — add components to prefabs, adjust scene hierarchies, change material properties — and know the result will load correctly.

### Trace (Harmony)

Hook any C# method at runtime using [Harmony](https://github.com/pardeike/Harmony) — no breakpoints, no code changes, no recompilation. Observe calls, parameters, return values, and stack traces in real time.

```bash
# Hook a method
unity-cli trace hook --type PlayerController --method TakeDamage

# Hook with stack traces (expensive)
unity-cli trace hook --type UnityEngine.Transform --method set_position --stack

# List active hooks
unity-cli trace list

# Read trace buffer
unity-cli trace read
unity-cli trace read --count 20

# Read trace for a specific hook
unity-cli trace read --id "PlayerController.TakeDamage_a1b2c3"

# Remove a hook
unity-cli trace unhook --id "PlayerController.TakeDamage_a1b2c3"

# Remove all hooks and clear buffer
unity-cli trace clear
```

Notes:
- Hooks are lost on domain reload (script recompilation) — this is expected
- Native/extern methods (e.g. some Unity internals) cannot be hooked
- For properties, use the compiled method name: `get_position` / `set_position`
- `--stack` adds significant overhead, use sparingly

### Profiler

```bash
# Read profiler hierarchy (last frame, top-level)
unity-cli profiler hierarchy

# Recursive drill-down
unity-cli profiler hierarchy --depth 3

# Set root by name (substring match) — focus on a specific system
unity-cli profiler hierarchy --root SimulationSystem --depth 3

# Drill into a specific item by ID
unity-cli profiler hierarchy --parent 4 --depth 2

# Average over last 30 frames
unity-cli profiler hierarchy --frames 30 --min 0.5

# Average over a specific frame range
unity-cli profiler hierarchy --from 100 --to 200

# Filter and sort
unity-cli profiler hierarchy --min 0.5 --sort self --max 10

# Enable/disable profiler recording
unity-cli profiler enable
unity-cli profiler disable

# Show profiler state
unity-cli profiler status

# Clear captured frames
unity-cli profiler clear
```

### Frame Debugger

```bash
unity-cli --project D:/Projects/ProjectD/client framedebug status
unity-cli --project D:/Projects/ProjectD/client framedebug enable
unity-cli --project D:/Projects/ProjectD/client framedebug disable
# One command: enable, traverse all events, write JSON, restore state
unity-cli --project D:/Projects/ProjectD/client framedebug dump --output D:/tmp/frame.json
# Capture only a prefix, explicitly marked truncated
unity-cli framedebug dump --max-events 100
# Longer captures use the existing async job transport
unity-cli framedebug dump --capture-timeout 300 --async
unity-cli job <job_id> --timeout 360000
```

Output paths are on the **Editor host**, relative to the Unity project root. The default is `Temp/FrameDebugger/<timestamp>.json`. Capture runs across Editor updates (2-second detail timeout per event, 90-second overall default); the CLI normally waits for the final summary. Waiting for a ready frame is separately limited to 10 seconds per stabilization attempt, including the first frame; increasing `--capture-timeout` does not extend that wait. For longer synchronous captures, set global `--timeout` (milliseconds) longer than `--capture-timeout` (seconds), or use `--async`/`job`.

The versioned JSON records event index/type/name, object identity and hierarchy/asset path, original/real shaders, pass/LightMode/index, keywords and shader properties (float/int/vector/matrix/texture/buffer), geometry/draw counts, batching causes, render targets including load/store/clear, blend/raster/depth/stencil state, and available compute/ray-tracing dispatch fields. `status` and the dump include reflected runtime API members; missing optional fields are `null` and listed in `missingFields`. Fields not applicable to an event type are null (or empty keywords): Unity can retain the previous draw's graphics data on clear/dispatch events. Shader/pass/batching and reported draw-call summaries include draw events only; compute/ray dispatches have separate histograms. Buffer properties expose names/flags, not contents. The implementation uses actual Unity runtime types via reflection, including class/ref-struct and InstanceID/EntityId variants; Unity **6000.5.5f1** is the primary API baseline.

Keep a Game view rendering (Play Mode may be required). `enable` and `dump` enable capture through the native utility without opening/focusing windows or calling Game view `ShowTab`. An existing user Frame Debugger window remains open; its managed data-view fields are initialized if needed, and disabling uses its managed cleanup method when available (otherwise the native utility). Capture explicitly pauses the playing Editor, settles the full frame for three matching hash/count samples, then traverses it. Hash/count checks bracket successful and failed event reads, including failed name/object lookup. A detected frame change discards the pending event and causes up to three complete restarts within the original timeout; events from different attempts are not combined. Responses/dumps add `retryCount`, `maxRetries`, `stableSamplesRequired`, `pausedDuringCapture`, and `frameChanges`. Repaint/player-loop requests also drive background capture; increase the overall timeout for large or heavily throttled traversal, but ensure a ready frame is available within the separate fixed 10-second wait. Capture restores original enabled/limit/pause state even on error (known limitation: an open debugger window's tree selection highlight is not restored), never enters/exits Play Mode, and never saves scenes. Avoid editing scenes, recompiling or changing debugger controls during capture. Failed events are marked individually; exhausted retries or timeout return an error with a partial JSON if a frame was acquired.

The response and JSON include totals/failures, event-type and batching-reason histograms, shader and shader/pass counts, summed reported draw calls, and transitions between reported render-target descriptors. Transitions do not establish native attachment identity and can miss same-name targets. Summaries only cover the selected successful events. Compare identical scene/camera/resolution conditions; check `complete`, `truncated` and failure counts before diffing. **Frame Debugger events are not one-to-one GPU draws** (clear, dispatch and scopes are included), and **no GPU timings are available**. Reduced counts do not by themselves prove a GPU-time improvement.

### UIToolkit UI

Inspect and interact with runtime or Editor UIToolkit elements. A click or type command returns an immediate before/after diff. For asynchronous screen transitions, monitoring is opt-in so unused UI tooling adds no per-frame scan.

```bash
# Inspect runtime UI in Play Mode
unity-cli ui tree --runtime --interactive

# Monitor a sequence that may add or remove UIDocuments
unity-cli ui events start
unity-cli ui click --runtime "id=start-button"
unity-cli ui events                 # read and clear pending events
unity-cli ui events status
unity-cli ui events stop
```

`ui events` is an alias for `ui events read`; reading does not start monitoring. `start` clears stale events and captures the current UI as its baseline. Monitoring automatically stops after five minutes, on Play Mode exit, or on domain reload.


### Run Tests

Run EditMode and PlayMode tests via the Unity Test Framework.

```bash
# Run EditMode tests (default)
unity-cli test

# Run PlayMode tests
unity-cli test --mode PlayMode

# Filter by test name (substring match)
unity-cli test --filter MyTestClass
```

Requires the Unity Test Framework package. PlayMode tests trigger a domain reload. The CLI follows connector port changes, polls the result, and returns only after the Editor is ready and the framework's `InitTestScene<GUID>` bootstrap assets are gone.

### List Tools

```bash
# Show all available tools (built-in + project custom) with parameter schemas
unity-cli list
```

### Custom Tools

```bash
# Call a custom tool directly by name
unity-cli my_custom_tool

# Call with parameters
unity-cli my_custom_tool --params '{"key": "value"}'
```

### Status

```bash
# Show Unity Editor state
unity-cli status
# Output: Unity (port 8090): ready
#   Project: /path/to/project
#   Version: 6000.1.0f1
#   PID:     12345
```

The CLI also checks Unity's state automatically before sending any command. If Unity is busy (compiling, reloading), it waits for Unity to become responsive.

## Global Options

| Flag | Description | Default |
|------|-------------|---------|
| `--port <N>` | Override Unity instance port (skip auto-discovery) | auto |
| `--project <path>` | Absolute paths require an exact match; relative names/suffixes must be unambiguous | latest |
| `--timeout <ms>` | HTTP request timeout | 120000 |

```bash
# Connect to a specific Unity instance
unity-cli --port 8091 editor play

# Select by canonical project path when multiple Unity instances are open
unity-cli --project D:/Projects/MyGame editor stop
```

Use `--help` on any command for detailed usage:

```bash
unity-cli editor --help
unity-cli exec --help
unity-cli profiler --help
```

## Writing Custom Tools

Create a static class with `[UnityCliTool]` attribute in any Editor assembly. The Connector discovers it automatically on domain reload.

```csharp
using UnityCliConnector;
using Newtonsoft.Json.Linq;
using UnityEngine;

[UnityCliTool(Name = "spawn", Description = "Spawn an enemy at a position", Group = "gameplay")]
public static class SpawnEnemy
{
    public class Parameters
    {
        [ToolParameter("X world position", Required = true)]
        public float X { get; set; }

        [ToolParameter("Y world position", Required = true)]
        public float Y { get; set; }

        [ToolParameter("Z world position", Required = true)]
        public float Z { get; set; }

        [ToolParameter("Prefab name in Resources folder", DefaultValue = "Enemy")]
        public string Prefab { get; set; }
    }

    public static object HandleCommand(JObject parameters)
    {
        var p = new ToolParams(parameters);
        float x = p.GetFloat("x", 0);
        float y = p.GetFloat("y", 0);
        float z = p.GetFloat("z", 0);
        string prefabName = p.Get("prefab", "Enemy");

        var prefab = Resources.Load<GameObject>(prefabName);
        var instance = Object.Instantiate(prefab, new Vector3(x, y, z), Quaternion.identity);

        return new SuccessResponse("Enemy spawned", new
        {
            name = instance.name,
            position = new { x, y, z }
        });
    }
}
```

Call it directly with flags or JSON:

```bash
unity-cli spawn --x 1 --y 0 --z 5 --prefab Goblin
unity-cli spawn --params '{"x":1,"y":0,"z":5,"prefab":"Goblin"}'
```

**Key points:**

- **Name**: without `Name`, auto-derived from class name (`SpawnEnemy` → `spawn_enemy`, `UITree` → `ui_tree`). With `Name = "spawn"`, the command becomes `unity-cli spawn`.
- **Parameters class**: optional but recommended. `unity-cli list` uses it to expose parameter names, types, descriptions, and required flags — so AI assistants can discover your tool without reading the source.
- **ToolParams**: use `p.Get()`, `p.GetInt()`, `p.GetFloat()`, `p.GetBool()`, `p.GetRaw()` for consistent param reading.
- **Discovery**: `unity-cli list` shows built-in tools first (`group: "built-in"`), then custom tools (`group: "custom"`) detected from the connected Unity project.

**Attribute reference:**

| Attribute | Property | Description |
|---|---|---|
| `[UnityCliTool]` | `Name` | Command name override (default: class name → snake_case) |
| | `Description` | Tool description shown in `list` |
| | `Group` | Group name for categorization |
| `[ToolParameter]` | `Description` | Parameter description (constructor arg) |
| | `Required` | Whether the parameter is required (default: `false`) |
| | `Name` | Parameter name override |
| | `DefaultValue` | Default value hint |

### Rules

- Class must be `static`
- Must have `public static object HandleCommand(JObject parameters)` or `async Task<object>` variant
- Return `SuccessResponse(message, data)` or `ErrorResponse(message)`
- Add a `Parameters` nested class with `[ToolParameter]` attributes for discoverability
- Class name is auto-converted to snake_case for the command name
- Override with `[UnityCliTool(Name = "my_name")]` if needed
- Runs on Unity main thread, so all Unity APIs are safe to call
- Discovered automatically on Editor start and after every script recompilation
- Duplicate tool names are detected and logged as errors — only the first discovered handler is used

## Multiple Unity Instances

When multiple Unity Editors are open, each registers on a different port (8090, 8091, ...):

```bash
# See all running instances with machine-readable identity
unity-cli instances list --json

# Select by exact project path
unity-cli --project D:/Projects/MyGame editor play

# Select by port
unity-cli --port 8091 editor play

# Default: uses the most recently registered instance
unity-cli editor play

# Wait until a clone is ready
unity-cli --project D:/Projects/MyGame_clone_0 instances wait --state ready

# Force-kill one explicit process for crash testing
unity-cli --project D:/Projects/MyGame instances kill --force
```

Full project paths are matched exactly before any convenience fallback, so a
project such as `MyGame` is never confused with `MyGame_clone_0`. Destructive
`instances kill` calls require both an explicit selector and `--force`.

### ParrelSync Lifecycle

ParrelSync is optional. The connector discovers it through reflection, so it
does not add a compile-time package dependency.

```bash
# Run against the original project
unity-cli --project D:/Projects/MyGame parrelsync list
unity-cli --project D:/Projects/MyGame parrelsync ensure --count 2 --open

# Normal cleanup
unity-cli --project D:/Projects/MyGame_clone_0 editor quit

# Crash simulation (bypasses Unity shutdown hooks)
unity-cli --project D:/Projects/MyGame instances kill --force
```

### Compiled Player E2E

A compatible game Development or Release Build can expose an opt-in loopback bridge. Each
process uses a distinct port and token; Distribution builds should omit the bridge.
Set `MATCHING_HOST` and `MATCHING_PORT` from the target project's own instructions; unity-cli does not own a game's matching endpoint.

```bash
unity-cli player launch --exe Builds/StandaloneWindows64/DoomBreaker.exe --port 47101 --token host-control --identity host-player --matching-address "$MATCHING_HOST" --matching-port "$MATCHING_PORT" --wait
unity-cli player call createRoom --port 47101 --token host --room-name e2e-smoke
unity-cli player call snapshot --port 47101 --token host --json
unity-cli player stop --port 47101 --token host

# Crash simulation for host-migration tests
unity-cli player kill --pid 12345 --force
```

## Worktree Validation

Run validation against the Unity Editor that opened the target worktree. Because
the Editor reads that worktree directly, uncommitted source and asset changes are
included without a snapshot commit or detached checkout.

```bash
unity-cli --project D:/Projects/MyGame-worktree status
unity-cli --project D:/Projects/MyGame-worktree editor refresh --compile
unity-cli --project D:/Projects/MyGame-worktree console --type error
```

Editor launch limits, work ownership, and branch-switch safety are project policy;
unity-cli does not infer them or terminate another Editor automatically.

## AI Agent Integration

The `prime` command outputs Unity connection status and all available tools in a format designed for LLM context injection:

```bash
# Inject Unity context into an AI agent's prompt
unity-cli prime
```

This prints the connection state, port, project path, and a compact list of all registered tools (built-in + custom). AI agents that can run shell commands get immediate access to Unity's full runtime — no MCP config, no tool registration ceremony.

You can also place a `guide.md` file next to the `unity-cli` binary to inject project-specific instructions into the prime output.

## Compared to MCP

| | MCP | unity-cli |
|---|-----|-----------|
| **Install** | Python + uv + FastMCP + config JSON | Single binary |
| **Dependencies** | Python runtime, WebSocket relay | None |
| **Protocol** | JSON-RPC 2.0 over stdio + WebSocket | Direct HTTP POST |
| **Setup** | Generate MCP config, restart AI tool | Add Unity package, done |
| **Reconnection** | Complex reconnect logic for domain reloads | Stateless per request |
| **Compatibility** | MCP-compatible clients only | Anything with a shell |
| **Custom tools** | Same `[Attribute]` + `HandleCommand` pattern | Same |

## Author

Created by **DevBookOfArray**

[![YouTube](https://img.shields.io/badge/YouTube-DevBookOfArray-red?logo=youtube&logoColor=white)](https://www.youtube.com/@DevBookOfArray)
[![GitHub](https://img.shields.io/badge/GitHub-youngwoocho02-181717?logo=github)](https://github.com/youngwoocho02)

## License

MIT
