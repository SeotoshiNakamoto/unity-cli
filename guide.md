# unity-cli — Unity Editor control for agents

Bash/CLI로 Unity Editor를 제어한다. MCP가 아니다. 멀티 Unity 인스턴스 환경에서는 항상 대상 프로젝트를 명확히 해야 한다.

## 🚨 Critical Rules

- 일반 Bash 호출은 항상 canonical 전체 경로로 `--project <UnityProjectPath>`를 붙인다. 멀티 인스턴스는 `instances list --json`으로 경로·포트·PID를 먼저 확인한다. pi `unity_cli` 도구는 자동으로 붙이므로 `--project`/`--port`를 직접 넘기지 않는다.
- 추측으로 옵션을 만들지 말고, 파라미터가 헷갈리면 먼저 `unity-cli <command> --help` 또는 `unity-cli list`를 확인한다.
- 복잡한 C# `exec`는 인라인 문자열 대신 `--file d:/tmp/query.cs`를 사용한다. 프로젝트 폴더 안에 임시 스크립트를 만들지 않는다.
- 임시 스크립트, 스크린샷, 로그 덤프는 `d:/tmp/` 아래에 둔다. 스크린샷 기본 경로는 `d:/tmp/screenshot.png`로 덮어쓴다.
- 120초 초과 예상 작업은 `--async`로 실행하고 `job <job_id>`로 폴링한다. job 결과는 1회 조회 후 삭제된다.
- 에셋/SO 수정 후에는 `AssetDatabase.SaveAssets()`를 호출한다. 필요하면 `reserialize` 또는 에디터 refresh/console 확인까지 한다.
- 콘솔/컴파일 확인 때문에 `Assets/Reimport All` 또는 `AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate)`를 쓰지 않는다. `editor refresh` 후 `console --type error`만 사용한다.
- `trace` 훅은 도메인 리로드/스크립트 리컴파일 시 사라진다. 리컴파일 후에는 다시 등록한다.
- 검증은 대상 프로젝트의 실제 Editor 인스턴스에 `--project <정확한경로>`를 지정해 수행한다. 같은 worktree의 commit하지 않은 변경도 그 Editor가 직접 읽는다.
- 별도 Editor 실행 수 제한이나 작업 컨텍스트는 프로젝트별 운영 스크립트가 담당한다. unity-cli 자체는 다른 Editor를 자동 종료하거나 작업 소유권을 추론하지 않는다.

## Common Workflows

- 연결 확인: `status`
- 멀티 인스턴스 확인/대기: `instances list --json` → `--project <정확한경로> instances wait --state ready`
- ParrelSync 준비: 메인 프로젝트를 대상으로 `parrelsync ensure --count 2 --open` → 각 clone을 `instances wait`로 확인
- 정상 clone 정리: clone을 대상으로 `editor quit`. 크래시 재현만 `instances kill --force`를 사용한다.
- Windows Development/ReleaseE2E Player: 프로세스마다 고유 port/token/identity로 `player launch --exe ... --port ... --token ... --identity ... --matching-address ... --matching-port ... --wait` → `player call ...`; 같은 플레이어 재실행만 identity를 재사용한다. 빌드 기본 endpoint를 믿지 말고 테스트 대상 matching server를 명시한다. 정상 종료는 `player stop`, 호스트 크래시는 정확한 PID에 `player kill --force`.
- 컴파일/콘솔 확인: `editor refresh` → `console --type error`
- 직접 worktree 검증: 정확한 `--project`로 `editor refresh --compile` → `console --type error` → 필요한 좁은 DryRunner/test를 실행한다.
- C# 조회/수정: 간단하면 `exec "return ...;"`, 복잡하면 `exec --file d:/tmp/query.cs --usings ...`
- 시각 확인: 수치/상태는 `exec` 우선, 눈으로 봐야 할 때만 `screenshot --output_path d:/tmp/screenshot.png`
- UI QA: 플레이 모드에서 `ui tree --runtime --interactive` 먼저 사용한다. 클릭/입력은 `ui click --runtime ...`, `ui type --runtime ...`; 화면 전환은 `ui events`로 확인한다.
- 런타임 호출 추적: `trace hook --type T --method M` → `trace read`/`trace list` → 끝나면 `trace clear`
- 커스텀 도구/파라미터 확인: `list`

## Essential Command Notes

- `exec`: Unity 메인 스레드에서 C# 실행. UnityEngine, UnityEditor, 로드된 어셈블리에 접근 가능. `Object`가 모호하면 `UnityEngine.Object`를 명시한다.
- `console`: 기본은 에러/경고 확인. 컴파일 에러는 `editor refresh` 뒤 `console --type error`로 본다.
- `screenshot`: 항상 `d:/tmp/screenshot.png`에 덮어쓰고 이미지 read 도구로 확인한다. 특정 창은 `screenshot --action list_windows` 후 window 캡처를 사용한다.
- `ui`: 게임 UI만 볼 때는 `--runtime`을 붙인다. `--interactive`는 Button/TextField/Label 중심으로 레이아웃 노이즈를 줄인다.
- `trace`: 오버로드는 첫 매칭일 수 있다. `--stack`은 비용이 크므로 필요한 경우만 쓴다. native/extern 메서드는 훅 불가.
- `profiler`: 성능 분석이 필요할 때만 사용하고, 옵션은 먼저 `profiler --help`로 확인한다.
- `reserialize`: YAML 에셋을 텍스트 수정한 뒤 Unity serializer로 다시 저장할 때 사용한다.
- `test`: Unity Test Framework 실행. PlayMode 테스트는 도메인 리로드 뒤 connector port를 다시 찾고 Editor `ready`와 bootstrap scene 삭제까지 기다린 뒤 반환한다.
- `instances`: Unity 연결 없이 heartbeat를 조회한다. `kill`은 정확한 `--project` 또는 `--port`와 `--force`가 모두 있어야 한다.
- `parrelsync`: 메인 에디터에서만 `list/ensure/open`을 호출한다. 첫 clone 생성은 오래 걸릴 수 있어 `--async` 후 `job` 폴링을 권장한다.
- `editor quit`: Unity 종료 훅을 거치는 정상 종료다. 호스트 크래시/비정상 단절 검증에는 쓰지 말고 외부 `instances kill --force`를 사용한다.
- `player`: 에디터 커넥터가 아니라 Development/ReleaseE2E Player에 포함된 opt-in 루프백 브리지(127.0.0.1)를 제어한다. 최종 Shipping 빌드에는 브리지가 없어야 하며 port/token은 프로세스마다 분리한다.

## ProjectD Notes

- SO 수정은 런타임 핫 리로드 가능하다. 플레이 중 즉시 반영될 수 있으나 저장은 `AssetDatabase.SaveAssets()`로 보장한다.
- SO는 바이너리 익스포트 불필요. ActionDataGroup/JSON/CSV 변경은 `@Spiral/Util/ExportBinary` 실행 대상이다.
- 함정 공격 주기는 SkillTable CSV가 아니라 MonsterConstants SO의 AttackCooldown을 본다.
- 플레이어 현재 방 조회는 `exec "QueryPlayerRoom.Run()" --usings DungeonArchitect.GridBuilder.Testing`를 사용한다.

## When to Read More

- 명령 옵션이 확실하지 않을 때: `unity-cli <command> --help`
- 프로젝트 커스텀 도구 이름/파라미터가 필요할 때: `unity-cli list`
- 같은 명령이 한 번 실패했을 때: 옵션을 추측해 재시도하지 말고 help/list/console을 먼저 확인한다.
