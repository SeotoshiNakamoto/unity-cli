# ToolDiscovery TypeCache regression and ProjectD timing

Production uses `UnityEditor.TypeCache.GetTypesWithAttribute<UnityCliToolAttribute>()`
in FindHandler and GetToolSchemas, with no own cache or invalidation logic. Name,
IsClass, handler-signature, duplicate/first-choice and parameter schema behavior
are unchanged. Unity owns the TypeCache lifetime.

## Hidden execution only

These scripts never open windows, activate/minimize/move windows, send input,
enter Play Mode or save scenes. All child processes use `CreateNoWindow=true`.
Invoke their PowerShell host with `-WindowStyle Hidden`:

```powershell
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/tooldiscovery/run.ps1 -UnityEditorData "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data"
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/exec/preflight.ps1 -UnityEditorData "C:/Program Files/Unity/Hub/Editor/6000.5.5f1/Editor/Data" -ProjectPath D:/Projects/ProjectD/client
powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File tests/tooldiscovery/measure.ps1 -OutputDirectory C:/Temp/typecache-sample
```

`hidden.ps1` captures stdout/stderr and times the installed CLI process. Preflight
uses actual Unity Bee references, including UnityEditor.CoreModule, and replaces
ExecuteCsharp and ToolDiscovery source entries with authoritative repo files.
Compile outputs are isolated and deleted. No binary/package version deployment
is needed. Copy only ToolDiscovery.cs to the matching ProjectD package path,
preserve its `.meta`, compile, and inspect new Editor.log entries for `error CS`.
If errors occur, immediately restore the previous good deployed file and compile.

The managed fixture tests naming, JObject signature, IsClass, duplicate logging
and first selection, schema fields, and absence of an own cache. TypeCache is a
managed double there; native equivalence is checked separately in ProjectD.

The native measurement script is hard-bound to ProjectD main and the installed
`D:/Projects/ProjectD/tools/unity-cli/unity-cli.exe`. Every call includes the
project selector. It reloads the domain first, captures `list`, measures console
before ANY diagnostic exec, and measures warm `return 1;` exec. Both commands
have two warm-ups followed by 20 timed calls. Thus baseline console starts with
zero exec assemblies. It then runs probe.cs for a read-only old-scan/TypeCache
inventory and 40 direct handler lookups, and accumulate.cs for exactly 200
unique snippets. The loop checks a 256 MiB Mono/native growth limit every ten
iterations, separates pre/post-GC memory and verifies +200 loaded assemblies.
Console/exec round trips and handler timings are measured again, then `finally`
requests a domain reload and verifies the tool list still works. No Assets script
or other project content is created. The temporary reload file is deleted.

`OutputDirectory` contains raw results; delete it after recording needed values.
The script's post-accumulation absolute count is 203: three measurement/helper
assemblies plus the 200 deliberately accumulated snippets. Labels 0/+200 mean
no added experiment assemblies versus 200 added; they are not claiming an
absolute post-count of 200. probe.cs does NOT mutate discovery or tool state.

## Results: Unity 6000.5.5f1 / connector 0.3.13 / main PID 12116

Authoritative file: unity-cli/unity-connector/Editor/ToolDiscovery.cs. ProjectD's
matching tools/unity-cli package file is a deployment copy. Both repos remained
on main; no commit, push, tag, release, update, or package-version change.

### Tool-list equivalence

- 15 tools before and after. Names, descriptions, groups and parameter schemas
  compare identically after sorting by tool name. Output order does change.
- Native old-scan and TypeCache type sets are identical, with no scan-only or
  cache-only types. projectd_e2e comes from ProjectD.E2E.Editor; package tools
  come from UnityCliConnector.Editor and UnityCliConnector.TestRunner.
- No duplicate names exist in this ProjectD domain, so changed enumeration order
  does not change a duplicate winner. Synthetic duplicate tests retain first.
- No precompiled plugin DLL contained a tool in the loaded-domain inventory;
  consequently there was no real plugin-defined tool to validate separately.
- After actual reload, the complete schema list remains identical.

### Final whole-installed-CLI timings (20 calls per cell, mean ms)

| Condition | Before full scan | After TypeCache |
| --- | ---: | ---: |
| console baseline, zero exec assemblies | 1345.23 | 563.94 |
| warm exec baseline | 1325.08 | 589.30 |
| console after +200 snippets (203 absolute) | 1331.66 | 597.77 |
| warm exec after +200 snippets | 1332.68 | 628.73 |
| console delta from baseline | -13.58 | +33.84 |

These include instance/readiness checks, HTTP, Editor scheduling and CLI process
startup/output. They are not isolated handler costs and should not be interpreted
as deterministic accumulation slopes. No focus change or comparison was made.

| Direct FindHandler mean (console/exec, 40 lookups) | Before | After |
| --- | ---: | ---: |
| Before the 200-snippet experiment | 819.886 ms | 0.1233 ms |
| After +200 snippets | 836.516 ms | 0.1307 ms |

An initial exploratory run, with one inventory exec already loaded at console
baseline, measured 790.73 -> 888.38 ms console before the fix (+97.65 ms) and
535.11 -> 518.50 ms after it (-16.61 ms). Its direct lookup means were
336.98 -> 435.04 ms before and 0.0892 -> 0.0604 ms after. The final rerun above
used a strictly zero-exec console baseline. These differing results show that
200 snippets did not consistently increase whole-console latency under the
current environment; they do not establish an overall #432 delay cause.

Both versions did retain exactly 200 additional exec assemblies after GC.
Final pre/post-GC Mono deltas were 54,927,360/40,960 bytes before the fix and
53,264,384/53,248 bytes after. Native deltas were 0 before and 405,456 bytes after.
Profiler totals are process-wide, not assembly-only retained-memory measurements.
TypeCache removes full reflection scans; it does not unload exec assemblies,
remove compiler costs for distinct source, or fix HTTP context/update waits.
Remaining whole-CLI latency around 0.5-0.6 seconds is not changed here.

Actual-reference preflight, post-copy compile/error-CS check, managed fixture,
required Go Verification (lint from PowerShell) and git diff --check pass. Existing
obsolete-API warnings and the unrelated missing-HLSL Shader Graph console error
were not changed. Test accumulations were removed by domain reload, and main
Editor remains ready in Edit Mode. Raw logs and temporary deployment/analyzer
files are removed; only these reproducible tests and report remain.
