# Frame Debugger regression fixture

The working tree contains the reviewed Frame Debugger candidate; this offline
review follow-up has not been synchronized to ProjectD or tested in its Editor.
`dump-stability.patch` retains the capture changes, including disabled limit
restoration, independently of `../http/candidate.patch`. Native evidence below
is historical and does not validate this follow-up.

Run on Windows with an installed Unity Editor (no Editor process/GPU required):

```powershell
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/framedebug/run.ps1 -UnityEditorData "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data"
```

Compiles the production tool against managed test doubles twice: class EventData
and ref-struct EventData. Checks update yielding, exact index traversal, stale
index rejection, summaries, shader/pass/state/property serialization, lossless
EntityId, missing fields, partial failures, truncation, overall/no-frame timeouts,
startup stabilization, bounded full restarts, post-read hash fencing for both
successful and failed events (last-event throw/false/detail-timeout crossed with
hash/count changes, plus changes during failed identity lookup), fixed 10-second
no-ready-frame waits with overall timeouts of 90 and 300 seconds,
already-enabled unpaused capture, enabled/limit/window/pause restoration, setup/write
failure, and reload/quit cleanup. Temporary executables and JSON are removed.
Also simulates stale native graphics fields on clear/compute events and checks
that shader/pass/batch/draw summaries exclude them while retaining dispatch data.
These are not native/GPU integration tests; real capture still requires the
requested project's Editor and a rendering Game view.

Native validation on ProjectD main, Unity 6000.5.5f1: full Play Mode capture
completed with 164/164 events, 0 failures, missingFields=[], 8 compute dispatches,
and 264 reported draw calls after excluding clears/dispatches. Enabled-state
prefix capture restored limit=5 and pause state. Initial scene-loading captures
correctly rejected a changed hash; this scene produced no Edit Mode Game-view
frame, returning an error without leaving the debugger enabled.

## Disabled-limit restoration follow-up

Committed HTTP + frame code reproduced native limit -1 -> 0 within the same
Play state. The native setter accepts -1; Disable/Close initializes limit to 0,
but the previous Finish restored it only when the debugger had been enabled.
The minimal fix moves native oldLimit assignment after closure for both enabled
and disabled states, preserving the native selector after disabling. The current windowless path no longer calls window ChangeLimit.
`dump-stability.patch` includes this fix, stability changes, and managed regression
coverage, independent of `../http/candidate.patch`.

Before the stability work, class/struct fixtures passed 85 checks each. They simulate the native reset and
cover disabled -1/0/positive values after success, setup failure, timeout, reload,
and file-write failure. Two native Play dumps total (baseline/fixed) captured
10 successful events from 23 with no failures/missing fields. Fixed native result
was -1 -> -1, with disabled/pause/window states restored. Screenshot/dump budget
was 2/3 each; no additional capture was used for semantics inspection.

`limit-semantics.cs` reads actual Unity 6000.5.5f1 method IL without invoking GUI:
ChangeFrameEventLimit(int) returns for newLimit <= 0 or > count. Thus -1/0 are
stored differently but neither is a valid active event selection or means all
events. The disabled debugger has no active replay; preserving oldLimit is still
a useful state invariant. Before/after values are compared within Play, not
across Edit/Play transitions. The test's temporary negative selector is restored
to its initial value afterwards.

Use `limit-check.ps1 -Label baseline_limit|candidate_limit -OutputDirectory ...`
only with explicit user screen permission. Its shared `screen-counts.json`
reserves before calls and enforces maximum three each for the entire operation.
Only approved screenshot/dump tool calls may manipulate windows. It deletes
artifacts and exits Play in finally. Full HTTP validation/report: [tests/http](../http/README.md).

## Stability candidate

Capture explicitly pauses a playing Editor even if the debugger was already
enabled. It replays the full limit and waits for three unchanged hash/count
samples, separated by repaint/update waits, before starting from event zero.
A changed frame discards the current traversal and restarts after settling,
up to three times within the original overall timeout. Enable and selection use
native SetEnabled and limit without OpenWindow/GetWindow/ShowTab, with Game view
repaint, scene repaint and player-loop requests. This is not exclusively native:
a user's existing window is retained, its managed data-view fields are initialized
if needed, and disable uses its managed cleanup method when available. No
focus-taking window enable method is called. Hash/count fences reject changes
during successful detail serialization and failed name/object lookup alike; the
pending event is discarded before restarting. False reads still within their
2-second detail deadline also check for changes before waiting.
The no-ready-frame wait is fixed at 10 seconds per stabilization attempt, including
the first frame; increasing --capture-timeout only extends the overall deadline. `retryCount`, `maxRetries`,
`stableSamplesRequired`, `pausedDuringCapture`, and `frameChanges` are additive
response/dump fields. Error paths preserve the previous partial frame, never a
mixture of attempts, and restore enabled/limit/pause/window state.

Run `stability-check.py diagnostics` against the installed baseline candidate
before deploying, and `stability-check.py repetitions` after the actual-response-
file preflight. The latter can run each group separately (`repetitions a|b|c 5`) and waits for window/native initialization before setting and verifying limit=3. It snapshots restored states, checks Edit Mode, and cleans up. All OS foreground HWND/PIDs are sampled read-only at 20 ms intervals, including before/during/after dumps. It temporarily selects PlayUnfocused on existing Game views for Play entry and restores their original settings. All commands target only ProjectD
main, all subprocesses are hidden, and no windows are opened or focused.
No asset, renderer setting, camera, time scale, input, or focus changes are made.
Evidence lives in `D:/tmp/framedebug-stability/`.

Previously validated windowless candidate: class/ref-struct fixtures passed 111
checks each. Native
(a) disabled/full and (b) enabled/paused/limit3 captured 5/5 each, restored all
states, and recorded zero Unity foreground samples/episodes. Both groups had
retryCount distribution {0:4, 1:1}. The initial full frame needed one restart;
subsequent settled captures needed none. Edit Mode returned the existing no-frame
error and restored all states. No screenshots/input/focus-setting APIs were used.

(c) is not a maintainable native state in this Editor: five same-request probes
confirmed pause=true allows SetEnabled, but unpausing turns it off and SetEnabled
while unpaused is rejected immediately (enabled=false, limit=0). These are blocked
setup probes, not five successful dumps. The managed fixture still covers that
initial state for runtimes that permit it; pause is restored before enabled/limit
because the pause transition itself can reset the native selector.

IL evidence: FrameDebuggerWindow.OpenWindow calls GetWindow(Type), and its enable
method calls OpenPlayModeView -> ShowTab. HandleEnablingFrameDebugger calls
ChangeFrameEventLimit(count) after four repaint events, explaining the old test's
limit3 -> count race. These paths are absent from the product capture. A creation
API's focus=false flag alone would not remove the separate ShowTab path.

Pause freezes game simulation, not GPU render history, resident-instance uploads,
or Editor/UI/realtime callbacks. Permanently changing event topology can exhaust
the retries or timeout; capture returns an explicit error instead of combining
frames. Native traces include InstanceDataSystemBuffer/UpdateOccluders and STP
passes while Time.time is fixed, so GPU Resident Drawer/temporal rendering are
plausible contributors; this is not an isolated component-level attribution.

## Offline review follow-up and deferred native validation

The new last-event throw/hash fixture fails against the pre-fix code with
`Last-event failure fence missing: throw/hash`. The corrected class/ref-struct
fixtures pass 191 checks each. Actual Unity 6000.5.5f1 response-file preflight
passes with no errors and six existing CS0618 warnings; outputs are isolated and
removed. All five Go Verification commands pass, with golangci-lint invoked from
PowerShell. Logs: `D:/tmp/framedebug-review/`. No Editor calls or ProjectD writes
were made. This phase must not invoke Unity, synchronize ProjectD, or use the
repetitions script while the HTTP session owns the main Editor.

After the HTTP session releases the Editor, the orchestrator can validate:

1. Deploy only approved C# changes, refresh/compile, and explicitly target
   `--project D:/Projects/ProjectD/client` (main port 8090). Record pause, enabled,
   limit, scene dirty state, existing Frame Debugger window instance IDs, and
   foreground HWND/PID. Use hidden subprocesses and read-only foreground sampling.
2. With the user's existing Frame Debugger window left open, test initially
   enabled/paused/positive-limit prefix dumps, then initially disabled full dumps.
   Repeat each five times; check restored selectors/pause/enabled, unchanged
   window IDs/count, no new windows, and zero Unity foreground entries. Do not
   reuse the current repetitions cleanup for this test: it closes debugger
   windows and would destroy the existing-window invariant.
3. Exercise last-event native detail failures during frame changes. Check
   frameChanges/index and retryCount, traversal restarted at zero, and no failed
   name/object from the changed frame published as complete. If native throw,
   false or detail-timeout combinations cannot be observed reliably, mark them
   unverified; an explicitly approved temporary managed fault-injection harness
   may be used separately, without modifying assets or patching native extern
   methods. Managed fixture results are not native reproduction evidence.
4. Remove any temporary hooks/callbacks, restore the user's original window and
   state, and verify scene dirty flags, protected asset hashes and exe hash.
   Never save scenes or change OS focus. Report (c) as blocked if native enable
   still rejects unpaused state; do not count blocked setup as a successful dump.

Implementation references (no source copied):
- UnityCsReference `Editor/Mono/PerformanceTools/FrameDebugger*.cs`
  (Unity Reference Only License), checked against runtime 6000.5.5f1 members.
- wotakuro/FrameDebuggerSave `Editor/Core/FrameInfoCrawler.cs`
  (MIT, Copyright 2019 Yusuke Kurokawa), for deferred limit/repaint traversal only.
