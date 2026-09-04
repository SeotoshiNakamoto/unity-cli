# unity-cli — Unity Editor control for agents

Bash/CLI로 Unity Editor를 제어한다. MCP가 아니다. 멀티 Unity 인스턴스 환경에서는 항상 대상 프로젝트를 명확히 해야 한다.

## 🚨 Critical Rules

- 일반 Bash 호출은 항상 canonical 전체 경로로 `--project <UnityProjectPath>`를 붙인다. 절대 경로는 완전일치만 허용하며 대상 Editor가 없으면 실패한다. 다른 Editor로 fallback하지 않는다. 멀티 인스턴스는 `instances list --json`으로 경로·포트·PID를 먼저 확인한다. pi `unity_cli` 도구는 자동으로 붙이므로 `--project`/`--port`를 직접 넘기지 않는다.
- SessionStart의 `연결 상태`는 요청한 프로젝트의 진단 결과다. `Target Editor: not running`이면 다른 프로젝트가 실행 중이어도 Unity 명령을 보내지 말고 대상 Editor를 먼저 연다.
- 추측으로 옵션을 만들지 말고, 파라미터가 헷갈리면 먼저 `unity-cli <command> --help` 또는 `unity-cli list`를 확인한다.
- 복잡한 C# `exec`는 인라인 문자열 대신 `--file d:/tmp/query.cs`를 사용한다. 프로젝트 폴더 안에 임시 스크립트를 만들지 않는다.
- 임시 스크립트, 스크린샷, 로그 덤프는 `d:/tmp/` 아래에 둔다. 스크린샷 기본 경로는 `d:/tmp/screenshot.png`로 덮어쓴다.
- 120초 초과 예상 작업은 `--async`로 실행하고 `job <job_id>`로 폴링한다. job 결과는 1회 조회 후 삭제된다.
- `exec --async`는 명령 전체를 job으로 실행할 뿐 C# callback 수명을 연장하지 않는다. `async`/Coroutine/`delayCall` 등 요청 뒤에 남는 코드는 기본 차단되며, 수명과 정리를 직접 책임질 때만 `--allow-deferred-code`를 사용한다.
- 에셋/SO 수정 후에는 `AssetDatabase.SaveAssets()`를 호출한다. 필요하면 `reserialize` 또는 에디터 refresh/console 확인까지 한다.
- 콘솔/컴파일 확인 때문에 `Assets/Reimport All` 또는 `AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate)`를 쓰지 않는다. `editor refresh` 후 `console --type error`만 사용한다.
- `trace` 훅은 도메인 리로드/스크립트 리컴파일 시 사라진다. 리컴파일 후에는 다시 등록한다.
- UI 화면 전환 감시는 상시 실행되지 않는다. Play Mode 진입 후 연속 UI QA 직전에 `ui events start`, 끝나면 `ui events stop`을 실행한다. 5분 TTL, Play Mode 종료, 도메인 리로드로도 자동 해제된다.
- 검증은 대상 프로젝트의 실제 Editor 인스턴스에 `--project <정확한경로>`를 지정해 수행한다. 같은 worktree의 commit하지 않은 변경도 그 Editor가 직접 읽는다.
- 별도 Editor 실행 수 제한이나 작업 컨텍스트는 프로젝트별 운영 스크립트가 담당한다. unity-cli 자체는 다른 Editor를 자동 종료하거나 작업 소유권을 추론하지 않는다.

## Common Workflows

- 연결 확인: `status`
- 멀티 인스턴스 확인/대기: `instances list --json` → `--project <정확한경로> instances wait --state ready`
- MPPM 준비(LLM 네트워크 E2E 기본): 검증할 checkout의 MPPM 원본(source) Editor에 `mppm --action activate --player "Player 2"` → 응답의 `virtualProjectPath`로 `instances wait --state ready --timeout 600000`(첫 활성화는 수 분) → 정상 종료는 같은 원본 Editor에서 `mppm --action deactivate --player "Player 2"` 또는 `--all`. 여기서 원본 Editor는 canonical main worktree를 뜻하지 않는다. agent worktree Editor도 원본이 될 수 있으며, 그 자식은 해당 checkout의 미커밋 `ScriptAssemblies`를 공유한다. 자식 하나가 커밋 메모리 약 6 GB이므로 여유를 확인한 뒤에만 `--count N`(1..3)으로 늘린다.
- ParrelSync 준비(사람이 직접 보는 검증·폴백): 메인 프로젝트를 대상으로 `parrelsync ensure --count 2 --open` → 각 clone을 `instances wait`로 확인
- 정상 정리: ParrelSync clone은 clone을 대상으로 `editor quit`, MPPM 자식은 그 자식을 활성화한 원본 Editor에서 `mppm --action deactivate`. 크래시 재현만 정확한 대상에 `instances kill --force`를 사용한다.
- Windows Development/Release Player: 프로세스마다 고유 port/token/identity로 `player launch --exe ... --port ... --token ... --identity ... --matching-address ... --matching-port ... --wait` → `player call ...`; 같은 플레이어 재실행만 identity를 재사용한다. 빌드 기본 endpoint를 믿지 말고 테스트 대상 matching server를 명시한다. 정상 종료는 `player stop`, 호스트 크래시는 정확한 PID에 `player kill --force`. Distribution은 브리지를 제거하므로 제어 대상이 아니다.
- 컴파일/콘솔 확인: `editor refresh` → `console --type error`
- 직접 worktree 검증: 정확한 `--project`로 `editor refresh --compile` → `console --type error` → 필요한 좁은 DryRunner/test를 실행한다.
- C# 조회/수정: 간단하면 `exec "return ...;"`, 복잡하면 `exec --file d:/tmp/query.cs --usings ...`
- 시각 확인: 수치/상태는 `exec` 우선, 눈으로 봐야 할 때만 `screenshot --output_path d:/tmp/screenshot.png`
- UI QA: 플레이 모드에서 `ui tree --runtime --interactive` 먼저 사용한다. 단일 클릭/입력은 `ui click --runtime ...`, `ui type --runtime ...`의 즉시 diff를 본다. 비동기 화면 전환을 연속 감시할 때만 `ui events start` → 작업 중 `ui events` → 반드시 `ui events stop` 순서로 사용한다.
- 런타임 호출 추적: `trace hook --type T --method M` → `trace read`/`trace list` → 끝나면 `trace clear`
- 커스텀 도구/파라미터 확인: `list`

## Essential Command Notes

- `exec`: Unity 메인 스레드에서 C# 실행. UnityEngine, UnityEditor, 로드된 어셈블리에 접근 가능. `Object`가 모호하면 `UnityEngine.Object`를 명시한다. 지연 callback/API는 기본 차단되며 `--async`와 `--allow-deferred-code`는 서로 다른 옵션이다.
- `console`: 기본은 에러/경고 확인. 컴파일 에러는 `editor refresh` 뒤 `console --type error`로 본다.
- `screenshot`: 항상 `d:/tmp/screenshot.png`에 덮어쓰고 이미지 read 도구로 확인한다. 특정 창은 `screenshot --action list_windows` 후 window 캡처를 사용한다.
- `ui`: 게임 UI만 볼 때는 `--runtime`을 붙인다. `--interactive`는 Button/TextField/Label 중심으로 레이아웃 노이즈를 줄인다. `ui events`는 감시를 켜지 않고 대기 이벤트만 읽으므로, 화면 전환 감시는 먼저 `ui events start`가 필요하다.
- `trace`: 오버로드는 첫 매칭일 수 있다. `--stack`은 비용이 크므로 필요한 경우만 쓴다. native/extern 메서드는 훅 불가.
- `profiler`: 성능 분석이 필요할 때만 사용하고, 옵션은 먼저 `profiler --help`로 확인한다.
- `reserialize`: YAML 에셋을 텍스트 수정한 뒤 Unity serializer로 다시 저장할 때 사용한다.
- `test`: Unity Test Framework 실행. PlayMode 테스트는 도메인 리로드 뒤 connector port를 다시 찾고 Editor `ready`와 bootstrap scene 삭제까지 기다린 뒤 반환한다.
- `instances`: Unity 연결 없이 heartbeat를 조회한다. heartbeat에는 Connector 버전/listener 상태가 포함되고, CLI readiness 확인은 메인 스레드와 독립적인 `/health`를 쓴다. `kill`은 정확한 `--project` 또는 `--port`와 `--force`가 모두 있어야 한다.
- `mppm`: 플레이어를 바꾸는 action은 모두 그 MPPM 세션의 원본(source) Editor에서 호출한다(`list/status`만 어디서든). 원본 Editor는 `D:\Projects\ProjectD` 같은 canonical main worktree가 아니라 명령의 `--project`로 지정한 checkout의 Editor다. `--player`/`--all`/`--count`는 정확히 하나만 주고, `activate --all`은 거부된다. `--count`는 activate 전용이며 `--tag`와 함께 못 쓴다(역할은 `--player`로). `--count N`은 최소 N명 보장이라 잉여를 끄지 않는다. 자식은 응답의 `virtualProjectPath`로 지목하고 경로를 조립하지 않는다(한 번 활성화된 플레이어에만 채워지며 port는 재기동마다 바뀐다). 응답은 `data`만 출력되므로 확인할 값은 `note`/`players` 같은 data 필드에서 읽는다.
- ProjectD 세션 조작·상태 대기는 `projectd_e2e`(`snapshot`/`wait_for`/`create_room`/`join_room`/`leave_session`/`mark_local_player`)를 행동할 인스턴스에 보낸다. `wait_for`는 구조화 predicate로 Unity 안에서 프레임마다 평가하므로 CLI 반복 조회를 대신한다. dispatched는 완료가 아니다.
- `mppm` 자식 특성: 태그는 식별 메타데이터일 뿐 역할을 부여하지 않는다(자식을 host로 만들려면 자동 진입을 끄고 그 자식에 `CreateRoom`을 보낸다). `activate --tag`는 기존 태그를 교체하고, 태그는 `SystemData.json`에 남아 비활성화 후에도 유지되며 떠 있는 자식은 변경을 즉시 본다. `ScriptAssemblies`·빌드 타겟·`ProjectSettings`를 원본 Editor와 공유하므로 컴파일은 원본에서 한 번이고 자식 전용 초기화가 없다. ProjectD의 main/agent 원본 Editor 실행 상한은 별도 원본 Editor를 여는 제한이며, 원본이 관리하는 MPPM virtual Player를 canonical main worktree로 우회하라는 뜻이 아니다. SceneView가 없고 `-noUpm` UPM 에러는 상시 남으므로 에러 판정에서 제외한다.
- MPPM E2E 역할 배치: 원본 Editor에는 끝까지 프로세스가 종료·deactivate·재시작되지 않는 참가자를 두고 crash·재시작·former-host는 자식에 배정한다. 원본이 최종 생존자일 필요는 없다. 활성 시나리오 중 원본 종료·PlayMode stop·compile/도메인 리로드가 일어나면 제품 결함으로 세지 말고 시도를 무효 처리한 뒤 처음부터 다시 실행한다.
- `parrelsync`: 메인 에디터에서만 `list/ensure/open`을 호출한다. 첫 clone 생성은 오래 걸릴 수 있어 `--async` 후 `job` 폴링을 권장한다.
- `editor quit`: Unity 종료 훅을 거치는 정상 종료다. 호스트 크래시/비정상 단절 검증에는 쓰지 말고 외부 `instances kill --force`를 사용한다.
- `player`: 에디터 커넥터가 아니라 Development/Release Player에 포함된 opt-in 루프백 브리지(127.0.0.1)를 제어한다. Distribution 빌드에는 브리지가 없어야 하며 port/token은 프로세스마다 분리한다.

## ProjectD Notes

- ProjectD Unity는 user-local `projectd-work-slot/scripts/open-projectd-unity.ps1`로만 연다. main+agent 합계 3개 상한이며 agent 창은 `LLM 유니티 슬롯 1/2` 중 덜 찬 가상 데스크톱으로 이동한다.
- agent Editor는 connector ready 뒤 Run In Background, GameDevTool, Player ID, 원콤 프로필이 자동 적용된다. 이 초기화가 실패하면 compile/runtime 검증을 계속하지 않는다.
- SO 수정은 런타임 핫 리로드 가능하다. 플레이 중 즉시 반영될 수 있으나 저장은 `AssetDatabase.SaveAssets()`로 보장한다.
- SO는 바이너리 익스포트 불필요. ActionDataGroup/JSON/CSV 변경은 `@Spiral/Util/ExportBinary` 실행 대상이다.
- 함정 공격 주기는 SkillTable CSV가 아니라 MonsterConstants SO의 AttackCooldown을 본다.
- 플레이어 현재 방 조회는 `exec "QueryPlayerRoom.Run()" --usings DungeonArchitect.GridBuilder.Testing`를 사용한다.

## When to Read More

- 명령 옵션이 확실하지 않을 때: `unity-cli <command> --help`
- 프로젝트 커스텀 도구 이름/파라미터가 필요할 때: `unity-cli list`
- 같은 명령이 한 번 실패했을 때: 옵션을 추측해 재시도하지 말고 help/list/console을 먼저 확인한다.
