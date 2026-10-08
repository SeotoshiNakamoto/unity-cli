# Independent-review fixes: retained candidate, NOT approved for deployment

## Final disposition

All runtime files in unity-cli and ProjectD are restored to committed HEAD.
Latest C# + Go implementation AND its new Go tests are preserved in
`tests/http/candidate.patch`; Frame Debugger fix/fixture remain separate in
`tests/framedebug/limit-restore.patch`. The former applies cleanly to current
runtime HEAD; the latter checks against the index (its fixture is already changed
in the worktree). No commits, push, tags, release, update or installed binary copy.

The main Editor changed to Play twice outside the test phases that initiate Play,
then disappeared before final rollback/recompile. ProjectD developed unrelated
changes in ThemeProp_MineF3.asset, PC_DeferredPlus_Renderer.asset and
EditorBuildSettings.asset. These were preserved. No claim is made about who caused
those external changes. Do not use agent-01 as fallback or open a new Editor.
A coordinated, exclusive main-Editor validation window is needed to complete tests.

## Changes in the retained patch

1. HttpServer creates each response with default HTTP 503, so transport teardown
   cannot silently finish with its own default empty 200. RequestState atomically
   distinguishes pending/started/cancelled-before-start. CommandRouter marks start
   only after acquiring the semaphore and checking cancellation. On cancellation,
   background response completion receives explicit JSON 503: not executed versus
   outcome unknown after execution start. Shutdown gives response workers 250 ms
   to send that small error before closing listener; incomplete writes Abort,
   not success Close. No automatic command resend.
2. Retiring generations remain in s_Retiring until their tracked Retirement task
   ends. The bounded join is 250 + 4750 ms, not infinite. CTS is disposed by the
   tracked WhenAll finalizer only after all task completion. Requests remove
   themselves at end; a separately tracked Tasks list covers the small return
   tail and is pruned by main update. No untracked request-cleanup task created.
3. waitForAlive checks state AND freshness AND listener liveness. ready/playing/
   paused accept commands; compiling/reloading/refreshing/mode transition/stopped/
   unknown do not. Future/stale heartbeats are rejected. The compile barrier also
   checks freshness and keeps its busy-cycle + consecutive-ready rules. Waiting
   messages say command-accepting heartbeat/listener, not readiness from health
   alone. Existing status and instances wait semantics are not redefined.

### Empty-200 compatibility allowlist

- manage_editor action play, stop, quit
- refresh_unity compile=request
- run_tests mode=PlayMode
- Never for async job acknowledgment, exec, console, list, job, normal refresh,
  editor pause or EditMode test. Exceptions are request-parameter-specific.
- A 503 is always failure, even on a transition; the JSON message is shown.
- Read error/partial body stays failure. Other empty 200 responses report execution
  could not be confirmed, nonzero exit, no automatic retry.
- Evidence: git log -S allowEmpty found 5627b9d (health extraction preserved a broad
  old exception); git log -S 'connection closed before response' found 2c44578,
  originally for compile/domain reload. ManageEditor crosses mode/quit transitions;
  RunTests.StartPlayModeRun crosses reload and cmd/test.go explicitly recognizes
  the empty run_tests kickoff message. Normal exec has no such transition contract.

## Native before/after false-success proof

Temporary CLI binaries were built with `go build` (old and new) under Temp.
ProjectD's tracked unity-cli.exe was never overwritten.

A 4-second async probe held CommandRouter's semaphore. At 1300 ms it restarted
only the main listener through an update callback. An identifiable exec and probe
queued behind it. Marker files contained enter/exit ONLY for the blocker.

| Version | Queued exec handler entered | HTTP wire | CLI exit / output |
| --- | --- | --- | --- |
| Before fixes | No | 200, zero-byte body | 0; exec sent (connection closed before response) |
| After fixes | No | 503, success=false, Command was not executed... | 1; same 503 message on stderr |

This was an actual native race reproduction, not a mock. Started-work retirement
and reload returned a different 503 message: Command execution started, but its
outcome is unknown... . Side-effect retries are explicitly discouraged.

## Test status

| Requested test | Evidence / status |
| --- | --- |
| 1 false success | PASS, actual empty-200/exit-0 before and JSON503/exit-1 after, no queued marker entry |
| 2 Play transitions | Final new-CLI editor stop passed; complete controlled play/stop pair not finished |
| 3 readiness | Go state/freshness/busy-probe tests pass. Isolated synthetic heartbeat with real main endpoint: compiling/reloading held entry until >=1200 ms; CLI 4936/2375 ms including cold compile. During long main work, HTTP health 1.54 ms but stale-heartbeat CLI waited 2552 ms. Timing assertions retained |
| 4 cleanup | Five actual recompilation cycles with unread 16 MiB response + stalled valid partial POST. Started-work 503 each time; shutdown log generated but final zero-task assertion was not reached after the footprint assertion failed. Threads 291,290,290,288,288. Handle counts 5415,5348,5427,5429,5446 were noisy and failed the +30 bound (+31); not declared leak-free. Stabilized follow-up could not start because main changed to Play |
| 4 forced timeout | PASS synthetic 6800 ms unfinished tracked task, safe listener restart (not reload). Stop 5028 ms; old generation still tracked/CTS NOT disposed then, after task completion CTS disposed and retirement completed |
| 5 active Frame Debugger | NOT RUN, script prepared; no actual screenshot/dump used this task |
| 6 reduced regression | Candidate preflight and five Go Verification steps pass including new tests before rollback. Baseline 30-case record saved, but after-comparison/100 sequential/8x30/full controlled transitions/final performance not completed |
| 7 temporary CLI | PASS old/new binaries built and used; installed binary untouched |

The synthetic retirement timeout intentionally produces one warning. It does not
prove the timing of every real native socket failure. Initial raw-socket tests
used Host: localhost and received Mono 400 before reaching the connector; the
host was corrected to 127.0.0.1:<port> and five real boundary cycles then ran.
Earlier partial-body claims using the wrong Host are not evidence of that path.

An initial temporary fixture callback used Action rather than Unity's
CallbackFunction, failed preflight, and an incorrectly chained shell batch still
copied it. It was immediately removed, good compilation restored, callback type
corrected and real-reference preflight passed before recopy. This is a test-harness
error, not hidden as a clean compiler history. Subsequent batch stages use &&.

Before revalidation, console/cached exec means over 25 samples were 126.79/126.22
ms on the previous HTTP candidate. No valid final after-performance result exists;
do not reuse earlier-task timings as this task's after result.

## Limits and next steps

No overall PASS/deployment claim. Complete remaining regression, active-state dump,
controlled transition tests and stabilized handle/control measurements while main
Editor is reserved. Screenshot/dump usage for THIS task: 0/3 each. Keep patches on
any failure; apply both to a clean runtime for continuation, and build a new temp
CLI. The latest client flags/policy do not justify retrying an unknown command.

Runtime diff is empty in both repositories. Reusable test/report/preflight/pipe
changes remain uncommitted. Runtime compile after final rollback could not be
verified because main Editor exited; source restore was verified by git diff.
The final main Editor cannot be reported as Edit/ready because it is not running.
Temporary fixture/meta, binaries, synthetic homes and marker/raw output files are
removed after preserving this evidence. No unrelated asset changes were reverted.
