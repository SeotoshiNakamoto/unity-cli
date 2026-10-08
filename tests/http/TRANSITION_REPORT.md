# 전환 응답 리뷰 수정·실기 결과

## 최종 판정과 현재 배포 상태
**전체 검증 미완료입니다. 최신 전환 수정은 `tests/http/candidate.patch`에 보존하고, 정본·ProjectD의 HTTP/Go 런타임은 작업 시작 직전 후보로 복원했습니다.** PlayMode 테스트는 기존 씬이 dirty여서 실행하지 못했고, 승인된 리로드 실기 중 게임/Unity native 콘솔 오류가 누적됐습니다. 이 오류의 HTTP 인과관계는 확인하지 못했습니다. 독립 framedebug 작업은 원복·수정·동기화하지 않았습니다.

최신 후보는 actual Bee 참조 사전 컴파일, 실제 메인 에디터 컴파일, Go Verification 5개와 추가 단위 테스트를 통과했습니다. 원복된 런타임도 에디터 컴파일과 Verification 5개를 통과했습니다. 이 둘을 전체 실기 PASS로 해석하면 안 됩니다.

## 최신 후보 수정 내용 (현재 patch에만 있음)
- `unity-connector/Editor/HttpServer.cs`: 취소 응답에 `data.execution_state=started/not_started`를 추가했습니다.
- `internal/client/client.go`: 허용된 동기 전환에서 시작이 확인된 503 또는 빈 200을 `TransitionPending`으로 구분합니다. 이는 성공 응답이 아니며 명령별 완료 검증이 필요합니다. 일반 명령·미실행 503·partial read·구조화된 시작 증거 없는 503은 실패합니다.
- `cmd/editor.go`, `cmd/transition.go`, `cmd/root.go`: 전환 결과를 heartbeat/health 또는 기존 compilation barrier로 검증합니다. raw tool passthrough도 같은 정책을 사용합니다. 최초 명령은 한 번만 보내며 복구 경로가 재전송하지 않습니다.
- `cmd/test.go`: PlayMode 전송 전에 이전 결과 파일을 삭제하고, 시작이 확인된 503/빈 응답 뒤에도 기존 결과 polling·Edit cleanup 경로에 들어갑니다.
- 단위 테스트: `cmd/transition_test.go`, `cmd/transition_poll_test.go`, `cmd/test_test.go`, `internal/client/transition_response_test.go`, `internal/client/response_policy_test.go`입니다. 기존 readiness 테스트도 patch에 포함했습니다.
- 문서는 정본 README.md/README.ko.md/guide.md의 readiness 문장만 수정했습니다. framedebug 문장·도움말·소스·테스트는 수정하지 않았습니다. HTTP README의 최신 상태와 patch 경로를 정리했습니다.

`candidate.patch`는 55,739 bytes이며 `git apply --cached --check`가 통과했습니다. HTTP C# 3개와 Go 코드/테스트를 포함하며 ManageFrameDebugger.cs나 framedebug 도움말을 포함하지 않습니다. 최신 적용본과 직전 후보 백업은 `D:/tmp/http-transition/latest/`, `before/`, `project-before/`에 보존했습니다. 보존한 최신 Go/C# 테스트가 현재 작업 트리에 실행 가능한 최신 런타임으로 남아 있다는 뜻은 아닙니다.

## 전환 명령별 정책
| 요청 | 시작 후 503/허용된 빈 200의 확인 경로 | 실패 조건 |
|---|---|---|
| editor play / play --wait | 전송 이후 최신 heartbeat `playing` + listener 연결 | 프로세스 종료·timeout·목표 상태 미확인 |
| editor stop | 전송 이후 최신 `ready` + listener 연결 | 프로세스 종료·timeout |
| editor quit | 전송 전 PID의 실제 종료 확인 | PID 불명·살아 있는 프로세스·timeout |
| editor refresh --compile | 기존 busy witness + 두 개의 최신 ready heartbeat barrier | 시작 미관측·compile error·timeout |
| PlayMode test | 새 결과 파일 polling + 기존 Editor ready/bootstrap cleanup | 결과 parse 실패·프로세스 종료·10분 timeout·cleanup 실패 |

미실행 503은 그대로 실패합니다. 일반 명령과 async job acknowledgment를 잃으면 전환 성공으로 처리하지 않습니다. 확인에 실패하면 outcome unknown/no resend를 명시합니다. quit 실기는 메인 종료 금지 때문에 실행하지 않았으며 alive/unknown PID의 거짓 성공 방지는 단위 테스트로 확인했습니다.

## 리로드 활성화 실기
EditorSettings API로 `enterPlayModeOptionsEnabled=false`를 임시 적용했습니다. Unity가 options 저장값도 3→0으로 바꾸므로 두 값을 모두 기록하고 복원했습니다. Game view는 reflection property `enterPlayModeBehavior`를 임시 `PlayUnfocused`(2)로 바꿨습니다. Show/Focus/입력 전송은 하지 않았습니다.

| 항목 | CLI exit / 최종 상태 | 판정 |
|---|---|---|
| editor play --wait | 0, interrupted-response verification 메시지, playing=true (37.840초) | PASS; Paseo 중단 전 저장된 완료 기록 |
| 후속 dump 준비의 play --wait | 0, 같은 verification 메시지, playing=true (23.662초) | PASS; 포커스 기록 보존된 구간 |
| editor play | 0, Entered play mode, 실제 playing=true (1.634초) | PASS |
| editor stop | 0, 실제 playing=false (0.595초 / 후속 0.706초) | PASS |
| editor refresh --compile | 0, compilation complete, Edit/compiling=false (14.646초) | PASS |
| 가벼운 PlayMode test | 실행하지 않음 | **BLOCKED** |

기존 씬은 처음부터 dirty였습니다. Test Framework의 `SaveModifiedSceneTask`가 `SaveCurrentModifiedScenesIfUserWantsTo`를 호출하므로 저장 대화상자·씬 저장 금지 조건 아래에서는 실행할 수 없었습니다. 씬을 저장하거나 dirty를 강제로 지우지 않았습니다. 최신 Go 단위 테스트에서는 pending acknowledgment 뒤 새 결과 polling, 이전 결과 제거, send 횟수=1을 확인했습니다. 이는 native PlayMode 테스트를 대체하지 않습니다.

## async 해석
빈 응답 예외는 `async`가 없거나 **Boolean false**인 경우에만 허용합니다. Boolean true, 문자열 `true/false`, 숫자 0/1, null, 배열·객체는 예외에서 제외합니다. 그 값에 대해 C#이 async job으로 변환하거나 오류를 내더라도 Go가 잃어버린 acknowledgment를 성공으로 바꾸지 않습니다.

Go 테스트는 위 타입 전체와 전환 allowlist, 구조화된 started/not_started 503, partial read, 자동 재전송 없음, stale/wrong heartbeat, 전환 timeout, quit alive/unknown PID, PlayMode 새 결과 polling을 통과했습니다.

native C# 정상 응답 관측: true/"true"/1은 job_created, false/"false"/0은 동기 stop, null/배열/객체는 Request error HTTP 500이었습니다. C# coercion 자체를 수정한 것은 아닙니다. 선택한 해법은 Go의 빈 응답 예외를 보수적으로 제한하는 것입니다.

## 덤프 중 리로드와 I/O
첫 async dump는 캡처 진행 중이라는 증거가 없어 판정 자료에서 제외했습니다. 관측 fixture를 수정한 후 추가 시험에서 **captureRunning=true, enabled=true, paused=true 시점에 실제 reloadRequested=true**를 기록했습니다.

- HTTP 제출 응답: 200, job_created, job ID입니다. async acknowledgment를 잃지 않았으므로 이를 503으로 바꾸지 않습니다.
- 리로드 후: enabled=false, paused=false, Frame Debugger window=0으로 복원됐습니다.
- reload 후 기존 job ID 조회: CLI exit 1, `Unknown job`입니다. job 저장소는 domain-local이며 제출 성공이 최종 dump 성공을 보장하지 않습니다.
- 이 중단 시험에서는 dump JSON이 만들어지지 않았습니다. 성공한 dump 또는 중단 결과 영속화를 주장하지 않습니다. framedebug 실패 경로는 독립 담당자의 수정 범위입니다.
- 같은 리로드에 stalled partial POST를 남겼고 HTTP 503/empty body로 종료됐습니다. CLI 예외에 넣지 않았습니다.
- 관측 순서를 고친 4개 종료 세대는 모두 loopCompleted=true, unfinished=0이었습니다. 초기 5개 관측은 HttpServer Stop보다 먼저 실행돼 loop=false/unfinished=1이었으므로 종료 완료 증거로 사용하지 않았습니다.

## keep-alive 빈 200
기존 연결을 /health로 연 뒤 idle 상태에서 리로드를 요청하고 exec를 전송한 5회 모두 **HTTP 503/not_started**, entry marker 없음이었습니다. ready 뒤 별도로 보낸 fresh CLI exec는 5회 모두 exit 0과 entry marker가 일치했습니다. 추가 3회도 HTTP 503/not_started였습니다. 이 요청들에 대해 empty200/미실행 exit0은 재현하지 못했습니다.

반면 compile의 health polling 과정에서 Go Transport의 `Unsolicited response received on idle HTTP channel ... HTTP/1.1 200 OK ... Content-Length: 0` 로그는 여러 번 실제 관측했습니다. 이는 특정 command의 응답으로 수락된 empty200과 구분해야 합니다. 해당 compile CLI는 기존 barrier를 확인한 뒤 exit 0으로 끝났습니다. idle 응답을 숨기거나 성공 증거로 사용하지 않았습니다.

## 포커스·복원
- 재개 실행: 6,857 samples, Unity foreground samples=0, episodes=0.
- 추가 덤프/리로드 실행: 5,306 samples, Unity foreground samples=0, episodes=0.
- 합계 기록된 실기 구간: **12,163 samples, Unity가 전면으로 올라온 횟수 0회**입니다. 20ms 간격의 읽기 전용 GetForegroundWindow/GetWindowThreadProcessId를 사용했습니다.
- Paseo 중단 전 첫 play --wait의 메모리 중간 포커스 기록은 유실됐습니다. 그 구간까지 0회였다고 단정하지 않습니다. 이후 기록 보존된 play --wait를 포함한 후속 구간은 0회입니다.
- 최종: Edit Mode, paused=false, debugger=false, Frame Debugger window=0.
- EditorSettings: enabled=true/options=3, 원본 byte hash와 일치하고 git status에 EditorSettings.asset 없음.
- Game view: 같은 EntityId, behavior=원래 0, view count 동일.
- 원래 dirty 씬 상태는 그대로 유지했습니다. #418 세 파일과 설치된 CLI의 byte hash도 시작 시점과 같습니다.
- 임시 TransitionProbe.cs/.meta는 제거했습니다. 기존 runtime meta는 수정하지 않았습니다.

## 로그·작업 트리
최신 후보 및 원복 후 컴파일에서 error CS와 검사한 HTTP lifecycle exception은 없습니다. native 결과의 새 package cancellation/5초 shutdown timeout도 0입니다.

다만 최종 콘솔에는 `[DungeonMaster] Failed to resolve PurrNet SceneID for 'KoboldCave1'` 및 `Transform has '(old ObjectDispatcher_TransformSystem)' change interests present when destroying the hierarchy...` 오류가 누적됐습니다. 승인된 Play/domain reload 중 나타났으며 HTTP 인과관계는 확인하지 못했습니다. 정본 외 영역을 고치거나 콘솔을 지우지 않았습니다.

양쪽 main을 유지하고 커밋/push/tag/release/update/ProjectD exe 교체는 하지 않았습니다. 정본에는 직전 HTTP 후보·독립 framedebug 변경·기존 테스트 지원 변경과 readiness 문서/최신 patch/이번 관측 스크립트가 미커밋 상태로 남았습니다. ProjectD에는 런타임 4개(HTTP 3개는 직전 후보, framedebug는 독립 담당 배포본), #418 renderer와 새로 나타난 범위 밖 `UI_Floating.spriteatlas` 변경이 있습니다. spriteatlas는 수정 주체를 단정하거나 원복하지 않고 보존했습니다. ThemeProp/EditorBuildSettings는 status에 나타나지 않지만 시작 byte hash가 동일합니다.

## 결과 파일
- `D:/tmp/http-transition/native-result.json`, `followup-result.json`, `transitions-progress.json`
- `foreground.json`, `followup-foreground.json`, `foreground-live.jsonl`
- `final-check.json`, `final-editor-tail.log`, `native.log`, `deploy.log`
- `attempt-setup-failure.json`, `attempt-dirty-scene.json`, `interrupted-transitions.json`, `interrupted-restored.json`
- `tests/http/transition-native.py`, `TransitionProbe.cs` (보관 fixture; ProjectD 배포본에는 없음)

초기 관측 코드의 obsolete GetInstanceID/출력 encoding 오류와 과도한 dirty-scene gate를 수정했고 실패 기록을 유지했습니다. Paseo 중단 후 실제 프로세스와 저장 결과를 확인하고 설정부터 복원했으며 완료된 첫 play --wait를 무조건 반복하지 않았습니다.
