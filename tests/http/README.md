# HTTP I/O candidate experiments

**Latest disposition: latest transition-response candidate applied and retained;
all four current runtime C# sources synchronized to ProjectD, uncommitted.**
See [RETEST_REPORT.md](RETEST_REPORT.md) for reload-on/off PlayMode smoke results,
reduced regression, focus measurements and the pre-existing EditorSettings
serialization-diff caveat. Compilation and all five Go Verification commands
passed. The installed ProjectD CLI is unchanged. No runtime rollback was done.
The latest independent Frame failure-path source was included in synchronization
without editing it. [TRANSITION_REPORT.md](TRANSITION_REPORT.md) is the earlier
incomplete run and rollback, not the current deployment disposition.

- `candidate.patch` contains the current HTTP C#/Go candidate, transition handling
  and Go regression tests. It does not contain Frame Debugger runtime changes.
- The independent Frame Debugger candidate is `../framedebug/dump-stability.patch`;
  that source and its tests are owned by the concurrent Frame worker.
- `transition-native.py` / `TransitionProbe.cs` reproduce the transition/idle HTTP
  boundaries without Show/Focus/input/scene saves. Results are retained under
  `D:/tmp/http-transition/`. No execution workers were launched for this run.
- `D:/tmp/http-split/` and [REVIEW_REPORT.md](REVIEW_REPORT.md) preserve prior staged
  validation/review evidence. The follow-up report below is historical; its old
  hashes, patch inventory and conclusions do not describe the current candidate.
- Do not use the old generic `deploy.ps1 -Mode rollback`: it omits newer Go files
  and includes the independently owned Frame source. Restore only scoped HTTP
  backups if current validation fails.

## Previous follow-up disposition

The previous approved follow-up restored its then-final candidate and left it
**uncommitted** in the authoritative connector and ProjectD deployment copy.
The previous attempt was rolled back conservatively after a cross-mode disabled
Frame Debugger limit assertion and an unattributed Package Manager log. This
follow-up separates those causes, fixes the existing limit restoration omission,
and preserves independent patches. No Go, package version, commit/push/tag/update.

- `tests/http/candidate.patch`: ONLY HttpServer, CommandRouter, AsyncJobManager.
- `tests/framedebug/limit-restore.patch`: ManageFrameDebugger plus managed fixture.
- Reverse apply checks pass for both; they do not depend on each other.
- HTTP SHA-256 values match the previous final candidate exactly:
  - HttpServer: `1230f6879ca9713ef0ea6f8f3685230d7132e2eae414124e9d334a23dd84785a`
  - CommandRouter: `f07c8e207277c70ee5f483b7976dc56c212795311cde10841ca4c84e5b1b19e6`
  - AsyncJobManager: `b57c0beab761178ceaa0338afb04df6634dcefc3e255c55cbab0239df12233c0`

## Design / thread boundary

| Operation | Thread / boundary |
| --- | --- |
| Project/version/PID/package snapshot; start/stop/retry listener | Editor main |
| Heartbeat and editor-state queries | Editor main, unchanged |
| GetContextAsync, body read, request JSON parsing | Pool; ConfigureAwait(false) |
| Admission | ConcurrentQueue + captured main SynchronizationContext Post |
| ForceEditorUpdate / QueuePlayerLoopUpdate / existing repaint | Main; obsolete generations ignored |
| Job creation/polling | Main queue, outside handler semaphore so running jobs can be polled |
| Handler entry and async Task continuation | Main; existing SemaphoreSlim held until completion |
| Handler/job result JSON and UTF-8 encoding | Main, before completing TCS<byte[]> |
| Job completion bookkeeping | Explicit main TaskScheduler, cancellation included |
| Health | Immutable bytes captured on main at listener startup |
| Protocol/CORS error | Pool; no Unity/user data in these DTOs |
| Response length/write/close | Pool, bytes only |

A ListenerSession owns listener, cancellation source, accept task and request
tasks. Reload/quit invalidates the generation, cancels queued semaphore/response
waits, aborts body/output streams, closes listener, drains queue and joins I/O.
It does NOT join main-context handler continuations (which would deadlock main).
There are no untracked I/O completion callbacks. The five-second shutdown watchdog
logs a Unity error on failure; a validation failure requires rollback. Late Posts
ignore obsolete generations. Dead-loop signals are generation-local; recovery
still runs in main update with the existing one-second retry and port fallback.

Async admission returns job_created before handler execution. Job storage remains
domain-local; reload loses old IDs as before. The two-argument Dispatch API is
preserved via the cancellable overload. ExecuteCsharp and TypeCache are unchanged.

## Hidden execution / bounded screen tests

Scripts are hard-bound to main `D:/Projects/ProjectD/client` and the installed
`D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe`. Every CLI call has the project
selector. All child processes use CREATE_NO_WINDOW; invoke the PowerShell host
with `-WindowStyle Hidden`. Loopback HTTP supplements CLI for malformed/concurrent/
disconnected clients using the project-selected port/PID. Never touch agent-01,
save scenes, activate/move windows or inject input.

1. Precompile candidate and temporary fixture with actual Bee references.
2. `deploy.ps1 fixture` temporarily adds HttpThreadProbe.cs to main's connector;
   it asserts handler/continuation/Unity getter thread and contains a pure
   EditMode NUnit smoke test. The list then has 15 real tools + 1 temporary tool.
3. With deployed runtime still committed, run baseline, smoke, baseline_lifetime.
   `log-check.ps1 mark/check` records byte-bounded log results.
4. After other baseline checks, run `tests/framedebug/limit-check.ps1 baseline_limit`
   under explicit screen permission. It uses one screenshot and one Play dump.
5. Deploy implementation after preflight; run regression/compare, smoke, stress,
   lifetime, log check and Verification. After other checks, use candidate_limit
   for the exact-limit native comparison and capture/dump validation.
6. Cleanup removes temporary fixture source/meta. Check normal 15-tool schemas,
   ready/Edit Mode, no debugger window/pause. On unresolved failure rollback
   updates retained patches BEFORE restoring four runtime files in both repos.

```powershell
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/exec/preflight.ps1 -UnityEditorData "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data" -ProjectPath D:/Projects/ProjectD/client -AdditionalSources D:/Projects/unity-cli/tests/http/HttpThreadProbe.cs
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/http/deploy.ps1 -Mode fixture
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/http/run.ps1 -Phase baseline -OutputDirectory C:/Temp/http-test
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/http/verify.ps1
```

Screen counts are shared across baseline/candidate stages in OutputDirectory:
reserve BEFORE each actual screenshot/dump and fail at three. Do not reset this
counter to evade an approval limit. Only limit-check.py's approved screenshot/dump
reach existing window/focus methods. Other phases use screenshot list/missing
window, framedebug status/invalid max, UI reads without captures/input. The old
`check.py screen` one-shot phase is retained for the prior approval, but must NOT
be mixed into the follow-up budget. Use limit-check.ps1 for this follow-up.

The frame comparison records Edit/pre-Play/within-Play states. Native limit is
already 0 after the prior session; the test sets a disabled selector to -1 without
window/enable calls and records that Unity accepts it. That recreates the original
starting value, then checks exact same-mode restoration. It restores the test
setup's original selector afterwards. Both screen stages delete PNG/dump files.
`limit-semantics.cs` only reads method IL; it never invokes window methods.

## Validation evidence (Unity 6000.5.5f1 / connector 0.3.13 / PID 12116)

| Area | Result |
| --- | --- |
| A | Actual-reference preflight, deployed compilation and appended log checks pass; required five Verification steps pass (lint in hidden PowerShell) |
| B | 30 complete HTTP responses identical, tool schemas identical; inline/file/async exec + job; EditMode smoke on baseline and candidate |
| C | 300 sequential; 4 x 50 batches (200) and 8 x 50 batches (400), zero unexpected failures; enter/exit pairs alternate, peak=1, thread IDs all 1 |
| C extras | 64 concurrent jobs unique/completed; async error paths; 2 MiB request echo/4 MiB response/hash; 2-second work; killed CLI and partial body recovery |
| D | Baseline and candidate each >=5 real source recompilations; candidate accept task complete and unfinished I/O=0 each round; OS threads 299,298,297,292,291; listener.Stop recovery; Play/Edit transitions |
| E | 25 samples per cell: console 568.89 -> 144.38 ms; cached return-1 exec 561.38 -> 153.62 ms |
| Screens | Baseline + candidate: screenshots 2/3, dumps 2/3 total; PNG 148,212 bytes with all CRCs/IEND; each dump 10/23 successful selected events, zero failed; artifacts removed |

Menu uses its safety blacklist; reserialize uses an invalid string array that
fails BEFORE asset mutation; profiler hierarchy without captured data returns an
expected error. MPPM/ParrelSync are queried, never activated. Responses include
compile/runtime/protocol errors; only times/byte counts/domain metadata are
excluded from comparison. Mono Process.Threads.Count is invalid (zero); native
OS Toolhelp32 supplies thread counts. Pending reload requests can close with an
empty response; CLI exits and subsequent commands succeed, with no new Go retries.

### Existing Frame Debugger cause and fix

Committed HTTP + committed frame tool reproduces -1 -> 0 within the SAME Play
mode (native negative setter accepted). Disable/Close resets the selector, but
Finish only restored oldLimit when wasEnabled. The minimal fix always restores
the native oldLimit after close; window ChangeLimit is invoked only if originally
enabled. Candidate native result is -1 -> -1, with disabled/pause/window state
preserved. Managed class/struct fixtures each pass 85 checks, including disabled
-1/0/positive values across success/setup failure/reload/timeout/write failure.

Actual UnityEditor.CoreModule.dll IL for ChangeFrameEventLimit(int):
`ldarg.1; ldc.i4.0; ble.s <ret>` followed by `count` upper-bound check, then setter
and selection updates. Thus -1 and 0 are distinct stored values, but both are
ignored by window event selection; neither means unlimited capture. With debugger
disabled no replay is active. The native setter still retains -1, so preserving it
is a legitimate state invariant rather than a required active-render difference.
Upstream reference (runtime IL is stronger version-specific evidence):
https://github.com/Unity-Technologies/UnityCsReference/blob/master/Editor/Mono/PerformanceTools/FrameDebugger.cs

### Package Manager log comparison

The prior log remains at Editor.log line 40770. This run's byte-bounded baseline
and candidate series both record ZERO '[Package Manager Window] Operation cancelled'
messages. No new lifecycle/thread exception. The expected invalid-reserialize
JsonReaderException matches baseline. Existing obsolete/empty-asmdef/toolbar/audio
warnings recur. The old Package Manager root cause is not established; neither
version reproduced it, so there is no evidence that the HTTP candidate causes it.
This is not a guarantee against all future Package Manager cancellation events.

Paseo interrupted a combined tool call. Actual hashes/result files showed deploy,
30-response comparison and smoke complete; no matching subprocess survived. Only
the unrecorded stress/lifetime phases were restarted; no screen action repeated.

## Remaining limits / final state

Main handlers and serialization still wait for Editor update. Serialization is
not streaming, and large results allocate on main. Running handler side effects
cannot be forcibly undone; cancellation stops queued work and transport. Distinct
exec assemblies still accumulate. Five shutdown cycles do not prove every future
handler/lifecycle safe, but observed old I/O tasks all terminated before unload.

Both repositories stay main, runtime files synchronized, existing .meta preserved.
ProjectD has ONLY four modified runtime files. Authoritative repo retains those,
reusable tests/docs and separate patches uncommitted. Native fixture/meta/raw
outputs and Python caches are removed. Main remains ready/Edit, unpaused, no
Frame Debugger window. No commits/push/tag/release/update/version change.
