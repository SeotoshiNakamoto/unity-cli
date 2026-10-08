# 최신 후보 재검증

## 결론
최신 `candidate.patch`를 정본에 재적용했고, 현재 정본의 런타임 C# **네 파일**을 ProjectD에 동기화했습니다. 독립 framedebug 실패 경로 수정은 덮어쓰거나 편집하지 않았습니다. 이번에는 실패 기록도 보존하고 **런타임을 원복하지 않았습니다.**

리로드 켬/끔의 전환 명령과 독립 PlayMode smoke, 축소 회귀, 상태 복원·I/O 종료 검증을 완료했습니다. 전체 요청 중 남은 조건은 **EditorSettings.asset이 git 변경 목록에 없어야 한다는 조건**입니다. 이 파일은 시작 시점부터 schema 마이그레이션 diff가 있었고, 현재 값과 byte hash는 이번 시작 상태로 복원됐습니다. 기존 diff를 임의로 삭제하지 않았습니다.

## 후보·컴파일
- 최신 HTTP/Go 후보: `tests/http/candidate.patch`, 55,739 bytes, reverse apply check PASS.
- Frame 후보: 현재 정본 `ManageFrameDebugger.cs`의 최신 실패 경로 검사 포함. 이를 포함해 runtime 4개가 ProjectD와 줄바꿈 정규화 후 동일합니다.
- 실제 Bee 참조 사전 컴파일, 임시 fixture의 실제 PlayMode assembly 참조 사전 컴파일, 실제 Editor 컴파일 PASS.
- 신규 `error CS` 0, shutdown 5초 timeout 0, Package Manager cancellation 0.
- Verification 5개 모두 PASS, lint 0 issues. 최종 Go 테스트 cmd/internal/client PASS.
- 임시 CLI: `D:/tmp/http-retest/unity-cli.exe`. ProjectD 추적 exe는 교체하지 않았습니다.

## 씬 상태·근거
이전 framedebug 결과는 dirty=false, 이전 HTTP 관측 파일은 16:10경 dirty=true를 기록합니다. 이전 HTTP의 첫 Play 전이 기록보다 이 dirty 관측이 먼저여서, dirty 발생을 특정 Play 전이 한 번에 귀속할 수는 없습니다.

이번 최초 읽기 전용 조회에서는 `Assets/Scenes/Main.unity`가 이미 **dirty=false**였습니다. 디스크 Main 씬은 16:39:06에 변경돼 카메라 position/FOV/far-clip prefab override 등이 git diff에 나타났습니다. EditorSettings도 16:39:01에 serializedVersion 15→16 등의 diff가 생겨 있었습니다. 누가 저장했는지 단정하지 않았습니다.

이번에는 사용자 씬을 저장하거나 다시 열거나 dirty를 버릴 필요가 없었습니다. 테스트 사이에도 dirty=false가 유지돼 `discarded-test-dirty.jsonl`은 생성되지 않았습니다. Main 씬 디스크 byte hash는 전체 시험 후 시작 값과 같습니다. 씬 저장·dirty 강제 해제·사용자 씬 git restore는 하지 않았습니다.

## 전환 실기
Game view는 시험 중에만 PlayUnfocused로 설정했습니다. 테스트 전 dirty=false를 명시적으로 확인한 뒤 Test Framework에 들어갔으며 저장 확인 창이 뜨지 않았습니다.

| 명령 | 리로드 끔 (enabled=true/options=3) | 리로드 켬 (enabled=false) |
|---|---|---|
| editor play --wait | exit 0, confirmed, playing=true | exit 0, interrupted-response 검증 완료, playing=true |
| editor play | exit 0, playing=true | exit 0, playing=true |
| editor stop | exit 0, ready/Edit | exit 0, ready/Edit |
| editor refresh --compile | exit 0, barrier 완료/Edit | exit 0, barrier 완료/Edit |
| 독립 PlayMode smoke | exit 0, passed=1/failed=0, Edit cleanup 완료 | exit 0, passed=1/failed=0, Edit cleanup 완료 |

처음 선택했던 기존 Sonity 오디오 테스트는 원래 설정에서 assertion 실패로 exit 1을 반환했습니다. `NextFloorPlayer` 대신 `PreviousFloorPlayer`를 캐시에 유지한 실패이며 `audio-test-failure.json`에 보존했습니다. 결과 polling과 실패 exit/cleanup은 정상입니다. 실패한 게임 테스트를 성공으로 바꾸지 않고, Game 로직과 무관한 `PlayModeTransitionSmoke.PureTransition`을 별도로 추가해 전환 자체를 검증했습니다. 임시 .cs/.meta는 최종 제거했습니다.

## 축소 회귀·성능
| 항목 | 결과 |
|---|---|
| 응답 30개 비교 | 정상 wire 응답 29개 동일, open-window 목록 1개는 환경 차이 |
| schema 비교 | framedebug capture_timeout 설명 변경 1개; 독립 Frame 후보의 의도된 변경 |
| 순차 요청 | 100/100 성공 |
| 병렬 | 8×30=240/240 성공, 최대 handler 동시 실행 1, 메인 continuation/getter 확인 |
| 실제 소스 compile/domain reload | 5/5, epoch 변경 확인 |
| reload 종료 세대 | 5개 모두 loopCompleted=true/unfinished=0 |
| 미실행 요청 방어 | not_started 503, queued entry 없음, CLI exit 1 |
| readiness | compiling/reloading 동안 marker 차단 ≥1200ms, ready 후 exit 0 |
| blocked main 중 health | 0.706ms; fresh heartbeat CLI 대기 2469.79ms |
| console 성능 | 25회 평균 115.96ms |
| 캐시 적중 exec 성능 | 25회 평균 114.50ms |

30응답 중 nonexistent-window 요청의 output_path만 임시 폴더가 달랐고 실제 error JSON은 동일했습니다. open-window 목록은 이전 11개→현재 10개(HotReload 창 없음), view 크기가 달라 동일성 판정 대상의 환경 차이로 분리했습니다. screenshot 실제 캡처는 하지 않았습니다.

일반 30응답에 신규 503을 임의로 허용하지 않았습니다. 별도 native reload/cancellation에서 **started 503**과 **not_started 503**, `execution_state`를 의도된 차이로 확인했습니다. 성능은 요청한 최소 20회보다 많은 25회이며 historical baseline 평균(console 266.23ms/exec 278.19ms)은 동시 대조군이 아니므로 참고 수치입니다.

### 리로드 자원 추세
매 리로드 후 GC/finalizer, 0.5초 간격 OS 측정 3회 중앙값입니다.

- handles: **4447, 4423, 4443, 4461, 4477**. 최종-초기 +30, 회귀 기울기 +9.8/회.
- threads: **290, 284, 284, 285, 284**. 최종-초기 −6, 기울기 −1.1/회.
- 이전 기준선: handles 2757,2773,2791,2807,2820 (+63/+16.0), threads 289,289,289,289,288 (−1/−0.2).

이번 추세는 이전 기준선보다 악화되지 않았으며 threads 누적 증가도 관측하지 못했습니다. 기준선은 historical 자료이므로 **누수 가능성 전체를 부정하는 동시 통제 실험은 아닙니다.** 원시 5회 값과 종료 보고를 보존했습니다.

## 덤프 중 리로드
- 캡처 진행 중임을 확인한 marker: captureRunning=true/enabled=true/paused=true/reloadRequested=true.
- async 제출: HTTP 200/job_created.
- 리로드 후: paused=false/debugger=false/Frame Debugger window=0.
- 종료 세대: loopCompleted=true, unfinished=0.
- stalled partial POST: HTTP 503/empty body 종료. 정상 성공으로 처리하지 않았습니다.
- 중단 시험에서는 dump JSON이 생성되지 않았습니다. dump 성공·결과 영속화를 주장하지 않습니다. 상태 복원과 I/O 정리만 통과했습니다.

## 콘솔 오류 판단
- 원래 설정에서 **기존 Sonity 오디오 PlayMode 테스트**를 실행한 첫 구간: SceneID 오류 신규 1건, Transform 관심 등록 해제 오류 0건. 요청대로 리로드를 끈 상태에서도 발생한 것을 보고합니다.
- 독립 smoke로 다시 검증한 리로드 끔/켬 구간: 두 오류 모두 신규 0건.
- 후속 축소 회귀/덤프 구간: 두 오류 모두 신규 0건.

오디오 테스트 failure와 SceneID 오류를 HTTP 변경 인과관계로 단정하지 않았습니다. 기존 로그를 지우거나 게임·URP 코드를 고치지 않았습니다.

## 포커스·복원
읽기 전용 GetForegroundWindow/GetWindowThreadProcessId를 약 20ms 간격으로 숨김 실행하여 파일에 지속 기록했습니다. 모든 재개 구간 합계 **25,545 samples**, Unity foreground sample=0, foreground episode=0입니다. Show/Focus/입력 전송을 호출하지 않았습니다.

최종 Edit Mode, paused=false, debugger=false, Frame Debugger window=0, dirty=false입니다. Game view의 EntityId/count/behavior=원래 0이 유지됐습니다. EditorSettings API 값은 enabled=true/options=3입니다.

Test Framework 실행 뒤 RAM options=3과 별개로 디스크 options=0이 남는 것을 발견했습니다. API 복원 순서를 enabled→options로 수정하고, 마지막 재컴파일 뒤 API 값을 확인한 다음 **이번 시작 시 백업한 EditorSettings 파일만** 복원하여 원래 byte hash를 확인했습니다. 해당 파일에는 시작부터 schema 마이그레이션 diff가 있었으므로 git 변경 목록에는 여전히 있습니다. 이 기존 diff까지 지우려면 별도 판단이 필요합니다.

#418 세 파일, UI_Floating.spriteatlas, Main 씬, 설치 CLI의 byte hash는 시작 값과 모두 같습니다. buildops는 수정하지 않았습니다. 모든 임시 fixture와 생성 .meta를 제거했습니다.

## 작업 트리·자료
양쪽 main이며 커밋/push/tag/release/update/ProjectD exe 교체를 하지 않았습니다. 정본에는 최신 HTTP/Go 후보·Go 테스트, 독립 Frame 변경, 기존 테스트 지원 변경, 문서·이번 검증 스크립트가 미커밋 상태로 남았습니다.

ProjectD git status: 런타임 C# 네 파일 및 **시작부터 있던** Main.unity, EditorSettings.asset, #418 renderer, UI_Floating.spriteatlas 변경입니다. 보호한 ThemeProp/EditorBuildSettings는 status에 보이지 않지만 byte hash는 동일합니다.

결과 폴더: `D:/tmp/http-retest/`. 주요 파일은 transitions-off/on-result.json, responses-result.json, reloads-result.json, cancelled-result.json, readiness-result.json, dump-reload-result.json, result.json, foreground-stream.jsonl입니다.

실행 스크립트의 argv import, schema-comparison 중단 처리, list 결과 집계 오류는 native 작업 완료 여부를 확인한 뒤 수정했습니다. 완료한 전환·응답·readiness는 저장 자료로 이어갔고 다시 실행하지 않았습니다. 초기 오디오 assertion 실패와 중간 script 실패 기록도 보존했습니다.
