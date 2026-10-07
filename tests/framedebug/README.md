# Frame Debugger regression fixture

Run on Windows with an installed Unity Editor (no Editor process/GPU required):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/framedebug/run.ps1 -UnityEditorData "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data"
```

Compiles the production tool against managed test doubles twice: class EventData
and ref-struct EventData. Checks update yielding, exact index traversal, stale
index rejection, summaries, shader/pass/state/property serialization, lossless
EntityId, missing fields, partial failures, truncation, overall/no-frame timeouts,
changed-frame rejection, enabled/limit/window/pause restoration, setup/write
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

Implementation references (no source copied):
- UnityCsReference `Editor/Mono/PerformanceTools/FrameDebugger*.cs`
  (Unity Reference Only License), checked against runtime 6000.5.5f1 members.
- wotakuro/FrameDebuggerSave `Editor/Core/FrameInfoCrawler.cs`
  (MIT, Copyright 2019 Yusuke Kurokawa), for deferred limit/repaint traversal only.
