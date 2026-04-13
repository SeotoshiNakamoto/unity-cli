# UI QA — LLM이 Unity UI를 이해·조작·검증할 수 있게 만드는 CLI

> 목적은 **QA 가능한 수준**이다. 한 번의 스냅샷이 아니라, LLM이 Unity 에디터/런타임 UI를 지속적으로 관찰하고, 조작하고, 결과를 검증할 수 있는 파이프라인을 만든다.

---

## 문서 개정 이력

- **v3 (현재)** — 목표를 "QA 프레임워크"로 재정의. 단발 스냅샷이 아닌 **관찰/조작/검증** 3축. IMGUI Inspector를 MVP로 승격. 모달/메뉴 전략 추가. 시나리오 러너, assertion, watch, diff 도입.
- v2 — 크리틱 반영 (좌표 변환, 클릭 경로, IMGUI 경계).
- v1 — 초안.

---

## 1. 목적 — "QA 가능한 수준" 정의

LLM 에이전트가 **사람 QA 테스터가 하는 일**을 할 수 있어야 한다:

1. **시나리오 실행** — "Save 버튼을 누른다 → `Saved!` 토스트가 나오는지 확인"
2. **Inspector 조작** — "maxHealth 필드를 100으로 바꾼다 → Play → 플레이어 체력 100 검증"
3. **메뉴 탐색** — "File > Build Settings > Android 선택 → 빌드 대화상자 확인"
4. **회귀 감지** — "이번 커밋 전후로 Package Manager 창의 탭 구조가 바뀌지 않았는지"
5. **자연어 탐색** — "설정 화면 어딘가에 오디오 볼륨 조절이 있을 텐데 찾아봐"

**핵심 요구**:
- CLI는 LLM에게 UI 상태를 **지속적으로** 제공 (스트리밍 / 요청 시 즉시)
- 조작 후 결과를 **LLM이 검증 가능한 형태**로 반환 (diff, assertion 결과)
- **스테이블 ID** — 같은 버튼이 여러 스냅샷에서 같은 ID로 참조돼야 대화 문맥 유지
- Unity UI의 **80%+ 실질 커버리지** (Inspector, 메뉴, 모달 포함)

---

## 2. CLI 인터페이스

### 2.1 관찰 (Observe)

```
unity-cli ui snapshot [--window <name>] [--output <prefix>]
                      [--max-depth <n>] [--filter <q>] [--visible-only]
                      [--include-imgui] [--include-menus]
  → <prefix>.png  + <prefix>.json

unity-cli ui watch [--window <name>] [--interval <ms>]
  → stdout에 JSONL 스트림 (UI 변경이 감지될 때마다 diff 한 줄)
  → LLM은 이걸 파이프로 읽거나, 파일로 tail 가능

unity-cli ui diff <snapshot-old> <snapshot-new>
  → 구조적 diff (추가/삭제/텍스트변경/enabled변경)

unity-cli ui tree [--window <name>] [--depth <n>]
  → 텍스트 트리 출력 (LLM이 빠르게 훑기 좋은 경량 포맷)

unity-cli ui query <selector> [--window <name>]
  → 셀렉터 매칭 요소 하나만 JSON으로
```

### 2.2 조작 (Act)

```
unity-cli ui click <selector>
  → 델리게이트 직접 Invoke (1차) → ClickEvent 합성 (2차) → PointerDown/Up (3차)

unity-cli ui type <selector> <text>
  → TextField.value 세팅 + ChangeEvent 발행 (UIToolkit)
  → SerializedProperty.stringValue + ApplyModifiedProperties (Inspector)

unity-cli ui set <selector> <value>
  → 필드 타입별 폴리모픽 세팅 (int/float/bool/enum/Object reference)

unity-cli ui menu <menu-path>
  → "File/Build Settings..." 같은 경로. EditorApplication.ExecuteMenuItem 사용.

unity-cli ui context-menu <selector> <item-path>
  → 요소 우클릭 → GenericMenu 특정 항목 선택

unity-cli ui focus <selector>
  → 포커스 이동 (TextField 진입 등)

unity-cli ui scroll <selector> [--to <target-selector> | --offset <dx,dy>]
```

### 2.3 검증 (Assert)

```
unity-cli ui assert <selector> exists
unity-cli ui assert <selector> visible
unity-cli ui assert <selector> enabled
unity-cli ui assert <selector> text=<expected>
unity-cli ui assert <selector> value=<expected>
unity-cli ui assert <selector> count=<n>          # 여러 개 매칭

unity-cli ui wait <selector> [--visible|--enabled|--text=X] [--timeout <s>]
  → 조건 만족할 때까지 폴링. 타임아웃 시 exit code 1.
```

### 2.4 시나리오 러너

```
unity-cli ui run <scenario.yaml>
  → 단계별 실행 + 각 단계 결과 리포트 (pass/fail + 스크린샷)
```

시나리오 예시:
```yaml
name: "저장 플로우 회귀 테스트"
steps:
  - click: "label=저장"
  - wait: "label=Saved!"
    timeout: 5s
  - assert: { selector: "id=save-timestamp", text: "*방금*" }
  - snapshot: "after-save.png"
```

---

## 3. 셀렉터 문법

LLM이 생성하기 쉬운 평문 문법:

```
label=저장              # 표시 텍스트 정확 일치
label~=저장             # 부분 일치
id=save-btn             # VisualElement.name 정확 일치
type=Button             # 타입 정확 일치
method=SaveData         # 연결된 핸들러 메서드명
path=root/toolbar/save  # hierarchy 경로
rect=120,340            # 좌표 (픽셀) — 해당 좌표의 top 요소
field=maxHealth         # Inspector/SerializedProperty 이름
menu=File/Save          # 메뉴 경로

# 복합 (AND):
label=저장 type=Button

# 인덱스:
label=저장 [0]          # 여러 개 매칭 시 첫 번째
```

---

## 4. 스테이블 ID — LLM이 참조를 유지할 수 있게

같은 요소가 스냅샷 1, 2, 3에서 다른 ID면 LLM이 "아까 그 버튼 눌러"를 못함.

### ID 생성 규칙 (우선순위)

```
1. VisualElement.name 있으면 → "id:<name>"
2. 없으면 안정된 해시:
   hash = sha1(window_title + hierarchy_path + type + label + field_name)
   → "auto:<hash[:8]>"
3. IMGUI 요소는 컨트롤 ID 사용:
   → "imgui:<controlID>"
4. Inspector 필드는:
   → "field:<TypeName>.<fieldPath>"
```

같은 세션에서는 스냅샷 간 ID가 유지됨. UI 구조가 실제로 바뀌면 새 ID 발급 + `ui diff`가 그걸 "added"로 감지.

---

## 5. 커버리지 — 80%+ 실사용 커버를 위한 재편성

| 영역 | 전략 | MVP 포함 | 난이도 |
|---|---|---|---|
| **런타임 UIToolkit** | `UIDocument` 트리 + `worldBound` + 델리게이트 리플렉션 | ✅ | 쉬움 |
| **에디터 UIToolkit 창** (Package Manager, Shader Graph 등) | `EditorWindow` 순회 + rootVisualElement | ✅ | 쉬움 |
| **Inspector — UIToolkit 필드** | `InspectorElement` 트리 순회 | ✅ | 쉬움 |
| **Inspector — IMGUI 필드** | Harmony 훅 필수 (상세 §6.1) | ✅ | **중** |
| **Odin [Button]** | `PropertyTree` + IMGUI 훅 공유 | ✅ | 중 |
| **에디터 메뉴** (File/Edit/...) | `Menu.GetMenuItems` + `ExecuteMenuItem` | ✅ | 쉬움 |
| **컨텍스트 메뉴** (우클릭 → `GenericMenu`) | `GenericMenu.menuItems` 리플렉션 | ✅ | 중 |
| **모달 다이얼로그** (`DisplayDialog`) | `ShowModalUtility` 훅 (상세 §6.2) | ✅ | 중 |
| **ObjectPicker / ColorPicker** | 전용 핸들러 | 2차 | 중 |
| **순수 IMGUI 커스텀 에디터** | `GUI.Button` / `GUILayout.*` Harmony 훅 | ✅ | 중~어려움 |
| **월드스페이스 UI (VR/3D)** | `PanelSettings.targetTexture` 별도 변환 | 2차 | 어려움 |
| **Scene 뷰 gizmo / handle** | | ❌ | 매우 어려움 |

**v2 대비 변경**:
- v2는 IMGUI를 "수요 생기면 4순위", 모달/메뉴를 "미지원"으로 처리했지만 **Unity QA의 실질 60%는 Inspector + 메뉴 + 모달**. 이걸 빼면 "QA 가능" 수준 못 됨.
- IMGUI 훅은 MVP로 승격. 공식 docs edge case 주의하며 설계.

---

## 6. 영역별 구현 메커니즘

### 6.1 IMGUI Inspector rect 획득 — Harmony 훅

**문제**: Inspector 본체는 `IMGUIContainer` 안 immediate-mode 렌더. VisualElement로 rect 못 얻음.

**해법**: `IMGUIContainer.DoOnGUI` 스코프 안에서 `GUILayoutUtility.GetLastRect` 결과를 수집.

```csharp
[HarmonyPatch(typeof(EditorGUILayout), nameof(EditorGUILayout.PropertyField),
    new[] { typeof(SerializedProperty), typeof(GUIContent), typeof(bool), typeof(GUILayoutOption[]) })]
static class PropertyFieldCapture {
    [HarmonyPostfix]
    static void Postfix(SerializedProperty property, bool __result) {
        if (!IMGUICapture.Recording) return;
        var rect = GUILayoutUtility.GetLastRect();
        IMGUICapture.Record(new ImguiEntry {
            rect = rect,
            propertyPath = property.propertyPath,
            targetType = property.serializedObject.targetObject.GetType().FullName,
            label = property.displayName,
        });
    }
}
```

Odin `[Button]` 훅:
```csharp
[HarmonyPatch("Sirenix.OdinInspector.Editor.Drawers.ButtonAttributeDrawer", "DrawPropertyLayout")]
static class OdinButtonCapture { ... }
```

**Harmony Unity edge case 준수** (공식 docs 확인):
- 너무 이른 패칭 → `MissingMethodException`. `[InitializeOnLoadMethod]` + `EditorApplication.delayCall`로 지연.
- 도메인 리로드 후 훅 소멸 → `[InitializeOnLoad]`로 자동 재패치.
- extern / `InternalCall` 메서드는 훅 불가 → 우회 경로 필요.

**캡처 트리거**: `ui snapshot --include-imgui` 시에만 훅 활성화 (평상시 오버헤드 0).
- `IMGUICapture.Recording = true` 세팅 → `EditorApplication.QueuePlayerLoopUpdate()` → Inspector repaint → `Recording = false`.

### 6.2 모달 다이얼로그 커버

Unity 모달은 몇 종류:

| 타입 | 접근 경로 |
|---|---|
| `EditorUtility.DisplayDialog` | 시스템 MessageBox — 훅으로만 가능. Harmony `DisplayDialog` 프리픽스로 intercept. |
| `EditorWindow.ShowModalUtility` | 일반 EditorWindow — 기존 경로로 커버 |
| Build Settings, Project Settings 등 | 일반 EditorWindow |
| `GenericMenu` (드롭다운/컨텍스트 메뉴) | `GenericMenu.menuItems` 리플렉션으로 항목 덤프. `AddItem`의 `GenericMenu.MenuFunction` 델리게이트 직접 Invoke로 선택. |
| `ObjectPicker` / `ColorPicker` | 2차 — 각자 고유 API |

**모달 감지**: 스냅샷 시 현재 활성 모달 탐지
```csharp
var focused = EditorWindow.focusedWindow;
bool isModal = focused != null && focused.GetType().Name.Contains("Modal");
```

JSON 메타에 `"active_modal": "BuildSettingsWindow"` 표기 → LLM이 "지금 모달이 떠 있다" 인지.

`EditorUtility.DisplayDialog`는 호출 시점에 차단형이라 이벤트 루프 멈춤. **자동 응답 모드**:
```csharp
// 훅으로 DisplayDialog를 intercept, 자동으로 지정한 버튼 응답
UIQA.AutoDismiss(dialog => dialog.title.Contains("Save") ? "OK" : "Cancel");
```

시나리오에서:
```yaml
- prepare_modal: { match: "Save before build?", response: "Save" }
- menu: "File/Build And Run"
```

### 6.3 메뉴 탐색

```csharp
// 전체 메뉴 목록 덤프
var menus = (string[])typeof(Menu)
    .GetMethod("GetMenuItems", BindingFlags.NonPublic | BindingFlags.Static)
    ?.Invoke(null, new object[] { "", false, false });

// 실행
EditorApplication.ExecuteMenuItem("File/Save Project");
```

컨텍스트 메뉴(`GenericMenu`):
```csharp
// GenericMenu.menuItems는 ArrayList<MenuItem> (internal class)
var itemsField = typeof(GenericMenu).GetField("menuItems", BindingFlags.NonPublic | BindingFlags.Instance);
var items = itemsField.GetValue(menu) as IList;
// 각 item의 content.text, func, func2 리플렉션
```

### 6.4 관찰 메커니즘 — `ui watch`

연속 QA를 위한 변경 스트리밍.

**변경 감지 방식**:
1. `PanelSettings.SetPanelChangeReceiver(IDebugPanelChangeReceiver)` — 런타임 UIToolkit 변경 이벤트 (공식 API)
2. 에디터: `EditorApplication.update`에서 주기적(예: 500ms) 트리 해시 비교
3. Inspector: `Undo.postprocessModifications` + `ActiveEditorTracker.RebuildAllIfNecessary` 훅

**출력 포맷** (JSONL, stdout):
```jsonl
{"ts":"2026-04-13T14:55:01","type":"added","id":"auto:abc12345","label":"Saved!","rect":[...]}
{"ts":"2026-04-13T14:55:02","type":"removed","id":"auto:abc12345"}
{"ts":"2026-04-13T14:55:03","type":"text_changed","id":"id:status","from":"Idle","to":"Running"}
{"ts":"2026-04-13T14:55:04","type":"enabled_changed","id":"id:stop-btn","to":true}
```

LLM이 `ui watch` 프로세스를 백그라운드로 돌리면서 `ui click` 후 반응을 실시간으로 받을 수 있음.

---

## 7. 스냅샷 JSON 스키마 (v3)

```json
{
  "snapshot_version": "3",
  "session_id": "s-20260413-1455",
  "captured_at": "2026-04-13T14:55:00+09:00",
  "window": {
    "name": "GameView",
    "type": "UnityEditor.GameView",
    "rect": [100, 100, 1280, 720],
    "focused": true
  },
  "active_modal": null,
  "coverage": {
    "uitoolkit": true,
    "imgui_inspector": true,
    "menus": true,
    "modals": true,
    "imgui_custom_editors": false
  },
  "elements": [
    {
      "id": "id:save-btn",
      "rect": [120, 340, 80, 24],
      "rect_space": "screen_px",
      "label": "저장",
      "type": "Button",
      "source": "uitoolkit",
      "path": "root/toolbar/save-btn",
      "enabled": true,
      "visible": true,
      "picking_mode": "Position",
      "occluded": false,
      "method": "MyProject.Tools.DataExporter.SaveData",
      "method_source": "Assets/Scripts/Tools/DataExporter.cs:47",
      "method_source_confidence": "pdb",
      "signature": "void SaveData(string path = null)"
    },
    {
      "id": "field:PlayerController.maxHealth",
      "rect": [300, 120, 200, 18],
      "rect_space": "screen_px",
      "label": "Max Health",
      "type": "IntField",
      "source": "imgui_inspector",
      "property_path": "maxHealth",
      "target_type": "PlayerController",
      "target_instance": "Player (PlayerController)",
      "current_value": 100,
      "value_type": "int",
      "enabled": true,
      "visible": true
    },
    {
      "id": "menu:File/Build Settings...",
      "label": "Build Settings...",
      "type": "MenuItem",
      "source": "editor_menu",
      "menu_path": "File/Build Settings...",
      "shortcut": "Ctrl+Shift+B",
      "enabled": true,
      "rect": null,
      "rect_space": "none"
    }
  ],
  "truncated": false,
  "element_count_total": 87,
  "element_count_returned": 87
}
```

핵심 신규 필드:
- `source` — 해당 요소를 어느 경로로 수집했는지 (`uitoolkit` / `imgui_inspector` / `odin_button` / `editor_menu` / `context_menu` / `modal_dialog`)
- `property_path`, `target_type`, `current_value` — Inspector 필드용
- `menu_path`, `shortcut` — 메뉴용
- `active_modal`, `coverage` — 메타 수준 상태

---

## 8. QA 루프 시나리오

### 8.1 단순 클릭 검증
```
LLM: "저장 버튼 눌러보고 제대로 되는지 확인해줘"
→ unity-cli ui snapshot          # 현재 상태
→ LLM이 id:save-btn 식별
→ unity-cli ui click id:save-btn
→ unity-cli ui wait label~=Saved --timeout 5s
→ exit 0 → pass
```

### 8.2 Inspector 조작 + Play 검증
```
LLM: "Player의 maxHealth 100으로 바꾸고 Play에서 실제 그 값인지 확인"
→ ui snapshot --include-imgui
→ ui set field:PlayerController.maxHealth 100
→ ui menu "Edit/Play"
→ ui wait window=GameView visible --timeout 3s
→ (런타임 씬 쿼리 — 기존 exec 기능 활용)
→ exec "UnityEngine.Object.FindObjectOfType<PlayerController>().maxHealth"
→ assert 결과 == 100
```

### 8.3 회귀 감지
```
# before 커밋
unity-cli ui snapshot --output before.json
# 커밋 후 재실행
unity-cli ui snapshot --output after.json
unity-cli ui diff before.json after.json
→ { "removed": ["id:legacy-export-btn"], "text_changed": [...] }
```

### 8.4 탐색형 QA
```
LLM: "오디오 볼륨 조절 어딘가에 있을 텐데 찾아봐"
→ ui menu list                    # 모든 메뉴 덤프
→ LLM이 "Edit/Project Settings..." 선택
→ ui menu "Edit/Project Settings..."
→ ui snapshot --include-imgui
→ LLM이 JSON에서 "Audio" 탭 발견
→ ui click label=Audio
→ ui snapshot --include-imgui
→ LLM이 "Volume" 필드 찾음
→ 결과 보고
```

---

## 9. 핵심 기술 결정 (v2에서 유지)

이 항목들은 v2 크리틱에서 확정 — v3에서도 그대로 적용:

### 9.1 클릭 실행 전략 (우선순위)
1. 델리게이트 직접 Invoke — `Button.clickable.clicked?.Invoke()`
2. ClickEvent 합성 — `ClickEvent.GetPooled()` + `SendEvent`
3. PointerDown/Up 합성 — 최후 수단

### 9.2 Button 핸들러 리플렉션 다중 폴백
```
Button.clicked → Button.clickable.clicked → Clickable.clicked → m_Clicked
```

### 9.3 좌표 변환 — `worldBound` → 스크린 픽셀
- 런타임: `PanelSettings.scaleMode` 기반 스케일 적용
- 에디터 창: `EditorWindow.position` 오프셋 추가
- HiDPI: `EditorGUIUtility.pixelsPerPoint` 보정
- 실패 시 `rect_space: "unknown"` 명시

### 9.4 레이아웃 완료 보장
첫 `GeometryChangedEvent` 이전이면 `worldBound = Rect.zero`.
- `resolvedStyle.width > 0` 체크 후 미완이면 `yield return null`

### 9.5 스냅샷 트랜잭션성
traversal + `ScreenCapture`를 단일 coroutine 내 `WaitForEndOfFrame` 직전/직후로 묶음.

### 9.6 `method_source` 3단계 폴백
1. PDB 심볼 → `file:line` [confidence=pdb]
2. `AssetDatabase` 스크립트 매칭 → 파일 경로 [confidence=asset_match]
3. 실패 → 타입 이름만 [confidence=type_only]

### 9.7 Lambda 이름 클린업
`<OnEnable>b__3_0` → `OnEnable (lambda)` 정규식 처리.

### 9.8 z-order 교차검증
각 rect 중심점에서 `panel.Pick` → 자기 자신 아니면 `occluded: true`.

---

## 10. 구현 순서 (QA MVP까지)

### Phase 1 — 관찰 코어 (2주)
- [ ] `UISnapshotTool` 기본 구조 (`UnityCliTool` 등록)
- [ ] 런타임 UIToolkit 트리 순회 + 어노테이션
- [ ] 좌표 변환 (런타임 + 에디터 창)
- [ ] 스크린샷 트랜잭션성 coroutine
- [ ] 스테이블 ID 생성기
- [ ] JSON 스키마 v3 직렬화
- [ ] `ui snapshot`, `ui tree`, `ui query` CLI

### Phase 2 — IMGUI Inspector (2주)
- [ ] Harmony 동적 로드 인프라 확인 (기존 `trace`와 공유)
- [ ] `EditorGUILayout.PropertyField` / `EditorGUI.*Field` 훅
- [ ] Odin `ButtonAttributeDrawer.DrawPropertyLayout` 훅
- [ ] `IMGUICapture` 세션 시스템 (기본 비활성, `--include-imgui` 시만)
- [ ] `field:*` 셀렉터 + `ui set` 구현 (`SerializedProperty` 경유)

### Phase 3 — 조작 + 검증 (1주)
- [ ] `ui click` 3단계 폴백 구현
- [ ] `ui type`, `ui set`, `ui focus`
- [ ] `ui wait`, `ui assert`
- [ ] 셀렉터 파서 (`label=`, `id=`, `type=`, `method=`, `field=`, 복합)

### Phase 4 — 메뉴 + 모달 (1주)
- [ ] `ui menu <path>` (`ExecuteMenuItem`)
- [ ] `ui menu list` (`GetMenuItems` 리플렉션)
- [ ] `GenericMenu` 항목 덤프 + 직접 호출
- [ ] `DisplayDialog` 훅 + 자동 응답 모드
- [ ] `ui context-menu` 구현

### Phase 5 — 스트리밍 + 시나리오 (1주)
- [ ] `ui watch` JSONL 스트림
- [ ] `ui diff` 구조적 diff
- [ ] `ui run <scenario.yaml>` 러너
- [ ] 시나리오 리포트 (HTML 또는 마크다운, 단계별 스크린샷 포함)

### Phase 6 — 정리 (1주)
- [ ] `prime` 가이드 업데이트 (새 스키마/셀렉터/커버리지 문서화)
- [ ] 통합 테스트 시나리오 10개 (대표 QA 케이스)
- [ ] 성능 측정 — 스냅샷 1회당 목표 < 300ms

---

## 11. 열려 있는 이슈

- **Play 모드 진입/탈출 시 도메인 리로드** — Harmony 훅이 살아남게 `[InitializeOnLoad]` + Play 모드 상태 체크
- **Odin 내부 private API 버전 취약성** — `ButtonAttributeDrawer` 클래스명/시그니처 변경 시 회귀. Odin 버전별 훅 맵 필요.
- **커스텀 IMGUI 에디터 커버리지 한계** — `OnInspectorGUI` 내부에서 `GUILayout.Button` 직접 호출하는 경우는 Harmony로 잡히지만, 라벨-액션 매핑이 약함 (버튼 이전 마지막 `GUILayout.Label`을 힌트로 쓰는 휴리스틱 필요)
- **`EditorUtility.DisplayDialog`는 동기 차단** — 훅 안에서 응답 결정 못 하면 프리즈. 시나리오 러너가 prepare_modal을 미리 받아두는 설계 필수.
- **JSON 크기 폭발** — Package Manager 전체 덤프 시 수천 요소. `--max-depth`, `--visible-only`, `--filter`, 500 초과 시 truncated 표기가 강제 디폴트.
- **HiDPI 실측** — 150% / 200% 스케일 환경에서 좌표 정확도 검증 미완.
- **월드스페이스/VR UI** — 2차.
- **Scene 뷰 gizmo** — 현재 설계 범위 밖.

---

## 12. 기존 기능과의 관계

- **`trace`**: Harmony 인프라 공유. IMGUI 훅을 새로 구현하지 말고 `trace`의 훅 로드/언훅 메커니즘 재사용.
- **`exec --file`**: QA 시나리오에서 "런타임 씬 상태 쿼리" 스텝으로 필수 (예: `FindObjectOfType<X>().field`). `ui` 명령과 `exec`가 한 시나리오 안에서 자연스럽게 섞이는 형태.
- **`prime`**: LLM 에이전트 초기화 시 `ui` 명령군과 셀렉터 문법, 셔플 커버리지 상태를 설명해야 함. 별도 가이드 섹션 추가.
- **`test`**: PlayMode 테스트 러너. `ui run` 시나리오 러너와 다른 축 (한쪽은 실제 테스트 코드, 한쪽은 UI 시나리오). 리포트 포맷 통일 검토.

---

## 13. 부록 — 설계 결정 근거

### Unity UIToolkit 공식 docs에서 확인
- 합성 이벤트는 `GetPooled()` + `SendEvent()` 패턴 (KeyDownEvent 레퍼런스)
- `Button`은 `ClickEvent` 콜백 권장 — PointerDown/Up 직접 다루는 건 fragile
- `PanelSettings.SetPanelChangeReceiver` — 변경 감지 공식 API 존재 (`ui watch`에 활용)
- `picking_mode: Ignore` 요소는 포인터 이벤트 수신 안 함

### Harmony docs에서 확인 (Unity edge cases)
- 너무 이른 패칭 → `MissingMethodException` (`SceneManager.sceneLoaded` / `EditorApplication.delayCall` 이후)
- RET 없는 throw-only 메서드는 `InvalidProgramException` — Transpiler 필요
- `[MethodImpl(MethodImplOptions.InternalCall)]` extern 메서드는 안정 패치 어려움
- `PatchAll(Assembly.GetExecutingAssembly())` attribute 기반 일괄 패치 표준

### v2 → v3 방향 전환 근거
v2는 "스냅샷 + 개별 조작"에 초점. 하지만 QA는 **관찰-조작-검증의 연속**:
- 단발 스냅샷만으로는 "눌렀더니 뭐 바뀌었는지"를 LLM이 판단 불가 → `diff`, `watch`, `assert` 필수
- Inspector/모달/메뉴 빠지면 실제 Unity QA 시나리오 대부분 커버 불가 → MVP 포함 불가피
- 시나리오 파일로 반복 실행 + 회귀 감지가 "QA 가능한 수준"의 최소 요건
