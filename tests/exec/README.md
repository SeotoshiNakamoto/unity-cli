# exec method-cache regression tests

## Cache policy

The authoritative source is this fork's
`unity-connector/Editor/Tools/ExecuteCsharp.cs`. ProjectD's
`tools/unity-cli/unity-connector/Editor/Tools/ExecuteCsharp.cs` is a deployment
copy, referenced through `file:../../tools/unity-cli/unity-connector`.

The existing static class caches compilation-condition hashes to loaded
`MethodInfo`, never return values. The key includes only:

- Generated source, including the wrapper and using directives.
- The fixed compiler option string.
- Full csc and execution-host paths.
- Ordered compiler references: `FullName`, manifest-module MVID, and `Location`.

Loaded assembly identities are stable within an AppDomain. Script compilation
and domain reload invalidate the dictionary; compilation-start, compilation-end
and before-reload events clear it, and compiling requests bypass it. Keys are
calculated once per request, not recalculated when storing a successful compile.
A runtime exception does not invalidate successful compilation.

There are no DLL-content reads, compiler-directory scans, environment-variable
reads or working-directory values in the key. PATH-only compiler/host paths,
unavailable reference metadata and ambient `csc.rsp` bypass caching. The latter
uses two file-existence checks, not a folder walk. The current directory is used
only to check for `csc.rsp`; it is not hashed. With absolute compiler/reference
paths and no ambient response file, changing it does not change the key.

This policy assumes an installed compiler/runtime and reference files are not
replaced in place during the same domain. After toolchain maintenance, restart
or reload the Editor. Deliberate environment-only compiler customization is not
fingerprinted. New/changed loaded reference identities still invalidate keys.
Different code still loads new assemblies; dictionary clearing and GC do not
unload them. No separate AppDomain/process/collectible execution area was added.
This is not a claim that ProjectD #432's overall memory/delay problem is solved.

## Compiler/host path discovery

Successful automatic paths are stored in `AutoCscPath` / `AutoDotnetPath` for
this Editor AppDomain. Subsequent lookups check `File.Exists` before reuse.
Missing files trigger the original search again. Overrides return unchanged
and do not overwrite the automatic fields. Failed searches are not memoized;
null csc and the original dotnet PATH fallback are retried on the next call.
The csc.dll-before-Windows-csc.exe priority and error messages are unchanged.
Domain reload naturally resets these fields; no new lifetime or event was added.

`tests/exec/discovery.cs` times actual private discovery methods (5 lookups),
ordered reference selection/key construction (20 samples), and internal hits
(20 calls), without editing the production handler. Run it with both explicit
compiler/host flags to observe initially empty discovery fields after reload.
`tests/exec/regression.cs` is a short main-Editor counter/+1 assembly check.

```powershell
unity-cli --project D:/Projects/ProjectD/client exec --file tests/exec/discovery.cs --csc "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll" --dotnet "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data/DotNetSdk/dotnet.exe" --timeout 120000
unity-cli --project D:/Projects/ProjectD/client exec --file tests/exec/regression.cs --timeout 120000
```

## Managed fixture

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/exec/run.ps1 -UnityEditorData "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data"
```

Uses Unity's actual csc/dotnet/Mono with managed Unity doubles. It checks 600
identical calls (+1 assembly), 20 counter increments, source/usings/positional
inputs, ordered reference identities and changed MVID/location, compiler/host
paths, exclusion of file contents/compiler configuration/environment/cwd,
response-file bypass, failures, serialization and simulated invalidation events.
The doubles include `UnityEditor.Compilation.Assembly` to catch namespace clashes.
Discovery tests use isolated fake files and verify reuse without traversal,
dll/exe priority, unchanged overrides, deleted-file rediscovery, failure retry,
and the uncached dotnet PATH fallback. Installed compiler files are never deleted.
The retained native diagnostic is also syntax-compiled and stopped by its Unity
version guard. `-Repetitions 3` is a smoke test. A 256 MiB managed/private-byte
growth limit is checked every 25 calls; temporary compilation outputs are deleted.

A measured lightweight-key fixture run: 600 calls, +1 assembly; first compile
270.82 ms, early/late hits 0.10/0.10 ms. This is not native Editor performance.

## Required actual-Unity preflight before deployment

```powershell
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/exec/preflight.ps1 -UnityEditorData "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data" -ProjectPath D:/Projects/ProjectD/client
```

Compiles the whole connector using the actual project's Bee response files,
including `UnityEditor.CoreModule`, replacing the ExecuteCsharp and ToolDiscovery
source entries with authoritative files and redirecting outputs to a disposable
temp directory. Compiler processes run hidden. It keeps actual references, defines, compiler options and analyzers.
Do not deploy if it fails. After copying the one `.cs` (preserve existing `.meta`),
run `unity-cli --project D:/Projects/ProjectD/client editor refresh --compile`
and check new `Logs/Editor.log` entries for `error CS`. On errors, restore the
committed ProjectD copy immediately and recompile. Do not commit ProjectD changes.

The original unqualified `using UnityEditor.Compilation` caused CS0104 in the
real Editor but was missed by the initial doubles. Production now aliases only
`CompilationPipeline`. Actual-response-file preflight and Editor compilation
both pass. Unrelated obsolete-API warnings remain unchanged.

## Native Editor and CLI diagnostics

Only use ProjectD main, Unity 6000.5.5f1. Keep `--project` explicit on every call.
Do not use agent-01, save scenes, or leave Play Mode enabled.

```powershell
# Run once per fresh domain: repeated source + counter + 100 distinct sources.
unity-cli --project D:/Projects/ProjectD/client exec --file tests/exec/native.cs --timeout 180000
# Whole installed-CLI process/transport timing, with automatic compiler discovery.
powershell -NoProfile -ExecutionPolicy Bypass -File tests/exec/transport.ps1 -Repetitions 20
# Same transport with explicit compiler paths.
powershell -NoProfile -ExecutionPolicy Bypass -File tests/exec/transport.ps1 -Repetitions 20 -ExplicitCompiler
# Only --csc specified: dotnet remains automatic.
powershell -NoProfile -ExecutionPolicy Bypass -File tests/exec/transport.ps1 -Repetitions 20 -CscOnly
# Compare execution MVID across inline/file/async transports.
powershell -NoProfile -ExecutionPolicy Bypass -File tests/exec/roundtrips.ps1
# Actual domain reload and add/change/remove a temporary project script.
powershell -NoProfile -ExecutionPolicy Bypass -File tests/exec/lifecycle.ps1
```

`native.cs` runs synchronously on the Editor thread. It caps identical-source
calls at 600 and distinct-source calls at 100, checks 256 MiB Mono/native-allocated
growth every 25/10 calls, and executes no scene mutations or deferred callbacks.
Initialization/JIT is separated from the repetition baseline; pre-GC allocations
and post-GC deltas are sampled separately. Running it again without reload may
fail the cold-source assertion because its methods are already cached.

`transport.ps1` caps calls at 600 (default 20) and checks the same memory limit
every 25 calls. It measures actual installed `unity-cli exec` inline round trips,
including process startup/instance discovery/HTTP/output parsing and compiler
auto-discovery or explicit paths. Snapshot helpers use `--file` to avoid Windows
PowerShell quote loss; its temporary file is removed. Helpers now always use
explicit paths so they do not warm automatic discovery before the measured
inline calls. `allHitsRoundTripMeanMs` excludes the first of the 20 calls.
A historical 600-round-trip attempt
exceeded a 10-minute tool limit; the completed 20/40-call samples below are not
600-round-trip measurements.

`lifecycle.ps1` checks a changed execution-assembly MVID and cache reset after real
domain reload, then same-MVID reuse on the next call. It adds a uniquely named
script under `Assets/Editor`, changes a constant from 1 to 2 and verifies the same
exec source observes 2, then removes that script and its generated `.meta` in
`finally`, recompiles, and checks the deleted type fails compilation. Refuse to
run if the temporary script already exists. It preserves all pre-existing metas.

## Recorded ProjectD main measurements before path memoization

Unity 6000.5.5f1, connector 0.3.13, Edit Mode, main Editor PID 12116.

| Internal HandleCommand measurement (explicit compiler paths) | Final run |
| --- | ---: |
| Identical calls | 600 |
| Exec assemblies before / after repetition | 4 / 5 |
| First compilation + execution | 518.76 ms |
| First 99 hits / last 100 hits, mean | 6.31 / 5.82 ms |
| Mono-used baseline | 1,629,581,312 bytes |
| Native-allocated baseline | 1,736,002,491 bytes |
| Mono-used pre-GC delta | +141,381,632 bytes |
| Mono-used post-GC delta | -352,256 bytes |
| Native-allocated pre/post-GC delta | -1,104 / -1,104 bytes |
| Counter executions | 20 |
| Distinct-source assemblies before / after 100 calls | 6 / 106 |
| Distinct-source compilation/execution, mean | 456.65 ms |
| Distinct-source Mono pre/post-GC delta from repetition baseline | +177,123,328 / -335,872 bytes |
| Distinct-source native pre/post-GC delta from repetition baseline | -848 / -848 bytes |
| Exec assemblies after all native checks | 108 |

An earlier cold-domain run also passed: identical calls +1 assembly, first compile
383.95 ms, early/late hits 3.31/3.02 ms. Both runs held assembly counts constant
on hits. These internal timings exclude outer CLI transport and compiler search.

| Actual installed CLI round trips | Auto-discovery, 20 calls | Explicit paths, 40 calls |
| --- | ---: | ---: |
| First cold-source call | 28,787.46 ms | 1,543.11 ms |
| Early hit mean | 2,560.59 ms | 1,243.31 ms |
| Late hit mean | 2,562.97 ms | 1,279.18 ms |
| Assembly delta | 1 | 1 |
| Mono-used pre-GC delta | +31,690,752 bytes | +57,683,968 bytes |
| Mono-used post-GC delta | +16,384 bytes | +32,768 bytes |
| Native-allocated post-GC delta | 0 bytes | 0 bytes |

Whole CLI latency remains significant and is not attributed solely to compiler
search; instance readiness/transport/process overhead is also included. The
28.8-second historical cold auto-discovery sample was not isolated at that stage;
follow-up discovery timing and the current 20-call comparison are reported below. No claim of
multi-minute latency or 23.6 GB memory growth being explained by exec is made.

The large pre-GC Mono deltas are temporary allocations. Negative post-GC deltas
reflect collection of other heap garbage/measurement variability, not unloading.
Profiler totals are process-wide, not exec-only retained-memory measurements.
GC left the 100 distinct-source assemblies loaded; actual domain reload removed
them later. Assembly accumulation for unique source remains demonstrably present.

## Lifecycle, transport and cleanup results

- Real reload produced a new MVID, cache count 1 and one exec assembly on the
  first probe; next probe reused that MVID. First/hit round trips 4362.82/1331.84 ms
  include readiness waiting, not isolated csc durations.
- Script addition, modification and removal each produced another new MVID, then
  reused it. Identical constant-source output changed 1 -> 2; deleted-type exec
  failed after cleanup. Each fresh first probe reported cache=1, assemblies=1.
- Inline, `--file`, async inline and async file returned the same execution MVID:
  `d5637896-dfdd-494e-ac2e-08ace9c0776d`.
- Different usings produced one new assembly then reused it; compile failures
  loaded none; three runtime exceptions reused one successfully compiled method.
- Editor.log has no `error CS` after corrected deployment. Console retains an
  unrelated missing-HLSL Shader Graph error at
  `Assets/@RND/Test_UiToolkit/ImageFillShader.shadergraph`; it was not changed.
- Previous PID 119632's log records CoreShutdown, Cleanup mono and Package Manager
  shutdown, rather than a new crash dump. This does not establish why it exited.

Final cleanup reloaded the Editor domain to remove the distinct-source experiment
assemblies. A final state query reported execAssemblies=1, cacheEntries=1 (the
query itself), compiling=false, playing=false and temporaryScriptExists=false.
Main Editor PID 12116 remains ready/open. Temporary raw logs, interrupted-run
files and UI-recovery helpers were removed; reproducible tests remain here.

Only ExecuteCsharp.cs was synchronized to ProjectD; existing `.meta` was preserved.
No Go binary/package-version change is needed. Version 0.3.13 is intentional in
this fork. Commit, push, tag, release and global update remain forbidden.

## Follow-up: automatic compiler-path memoization

Same main Editor PID 12116, installed CLI v0.3.27, Unity 6000.5.5f1. Production
changes are limited to two static path fields and reuse/rediscovery in FindCsc /
FindDotnet. Actual-reference preflight and subsequent Editor compilation pass;
no new `error CS` entries were found. No Go/transport/reference-key/temp-directory
behavior was modified.

All rows below are actual installed-CLI samples of 20 calls (19 hits), not 600
round trips. First means the first execution of fresh source. The post-change
default run began with empty automatic path fields after deployment/reload;
later explicit runs share the warmed domain. Snapshot helper calls now use both
explicit paths, so they do not warm automatic path fields before the default run.

| Mode | Before first | After first | Before hit mean | After hit mean |
| --- | ---: | ---: | ---: | ---: |
| Automatic csc + dotnet | 3046.40 ms | 3582.92 ms | 2704.14 ms | 1272.59 ms |
| Only --csc explicit (dotnet automatic) | 3221.70 ms | 1721.00 ms | 1950.91 ms | 1304.94 ms |
| Both --csc and --dotnet explicit | 1797.29 ms | 1746.64 ms | 1347.41 ms | 1301.29 ms |

The earlier 1.24-second explicit sample specified BOTH paths, not just --csc.
The approximately 1.43-second improvement in default hits closely matches the
removed repeated searches. Initial discovery is intentionally not eliminated;
first-call samples fluctuate and are not evidence of a cold-start speedup.

| Direct private-method timing | Before memoization | Warm after memoization |
| --- | ---: | ---: |
| FindCsc subsequent mean (4 calls) | 689.21 ms | 0.034 ms |
| FindDotnet subsequent mean (4 calls) | 682.04 ms | 0.031 ms |

After an actual RequestScriptReload, a probe invoked with explicit outer paths
observed AutoCscPath=null and AutoDotnetPath=null. First automatic lookup took
3001.23 ms for csc and 688.50 ms for dotnet; both fields then contained the
original discovered paths. Subsequent lookups averaged 0.041/0.032 ms. Thus
reload resets discovery and first lookup searches again, then reuses it.
The pre-change first direct lookup was 2430.78/693.13 ms. Search can contribute
several seconds to cold calls, but the historical 28.79-second first CLI sample
was not reproduced or explained solely by discovery.

### Remaining fixed costs (measurements, not additional fixes)

| Component / diagnostic | Measured time |
| --- | ---: |
| Ordered selection of 555 loaded references | 2.26 ms mean |
| Metadata-key generation (reflection probe) | 4.28 ms mean |
| Internal explicit-path method-cache hit | 6.27 ms mean |
| Installed CLI version, 10 calls (process/output, no Unity request) | 35.99 ms mean |
| Installed CLI status, 10 calls (discovery/heartbeat/output) | 74.86 ms mean |
| Main listener GET /health, reused diagnostic HttpClient, 9 warm calls | 215.53 ms mean |
| CLI async submission (does not await code result), 19 warm calls | 1187.62 ms mean |

These are separate probes and cannot be added/subtracted as an exact breakdown
of one CLI request. The remaining ~1.3-second round trip is outside the ~6 ms
exec hit path for the most part. Async submission remaining near 1.19 seconds
also points to readiness/HTTP/Editor scheduling overhead rather than csc or
reference metadata alone. It was not isolated further and no code there changed.
Temporary source/output directories are processed only on compilation misses,
not hits. References/key construction remain unchanged. The health diagnostic
resolved/verified the main project/port before GET and never contacted agent-01.

### Follow-up regression and cleanup

- Managed isolated-file tests passed for cache reuse, missing-file rediscovery,
  failure retry, dll-before-exe priority, original dotnet PATH fallback, and
  explicit paths not overwriting the automatic fields.
- The real Editor's 20-call counter regression returned 1..20 with one additional
  execution assembly; the same-code cache still executes on every hit.
- Inline/file/async-inline/async-file returned the same method MVID:
  `130684c8-93fb-4643-9f4a-57d575bd74fa`.
- Actual-reference preflight and required Go Verification were rerun; lint runs
  from PowerShell. The existing missing-HLSL Shader Graph error remains unrelated.
- Final follow-up state query: main project path verified, playing=false,
  compiling=false, both remembered tool files exist, execAssemblies=6 (diagnostic
  methods retained normally). No cleanup reload was needed for this small sample.
- Detailed raw timing files and helper scripts were removed; only reproducible
  fixtures/probes and this report are retained. Editor PID 12116 remains ready
  and open in Edit Mode; scenes were not saved.

