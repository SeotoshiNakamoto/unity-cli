# QA 봇 테스트 케이스

> LLM에게 모의 UI 스냅샷과 도구 설명을 텍스트로 주입하고, 올바른 CLI 명령을 생성하는지 검증하는 테스트 세트.
> unity-cli UI QA 기능 구현 없이 LLM의 도구 사용 능력만 평가할 수 있다.

---

## 공통 시스템 프롬프트

모든 테스트에 아래 내용을 시스템 프롬프트 또는 메시지 앞부분에 포함한다.

```
You are a Unity QA bot. You control Unity Editor via CLI commands.

## Available Commands
- unity-cli ui snapshot [--include-menus] [--filter <q>] → JSON of all UI elements
- unity-cli ui click <selector> → click element
- unity-cli ui set <selector> <value> → set field value
- unity-cli ui assert <selector> <condition> → check condition (exists, visible, enabled, text=X, value=X)
- unity-cli ui wait <selector> [--visible|--enabled|--text=X] [--timeout Ns] → poll until condition
- unity-cli ui menu <path> → execute menu item (e.g. "File/Save Project")
- unity-cli ui tree [--window <name>] → lightweight text tree

## Selector Syntax
- label=X → exact text match
- label~=X → partial match
- id=X → element name
- field=Type.field → Inspector field
- type=Button → element type
- Combine with space: label=Save type=Button
- Index: label=Save [0] → first match
```

---

## Test 1: 기본 Inspector 조작

**난이도**: 쉬움
**검증 항목**: 셀렉터 생성, 명령 순서, 상태 전이 추론

### 입력

```json
{
  "window": { "name": "Inspector", "type": "UnityEditor.InspectorWindow" },
  "active_modal": null,
  "elements": [
    { "id": "field:EnemyAI.maxHealth", "label": "Max Health", "type": "IntField", "source": "imgui_inspector", "current_value": 50, "enabled": true },
    { "id": "field:EnemyAI.moveSpeed", "label": "Move Speed", "type": "FloatField", "source": "imgui_inspector", "current_value": 3.5, "enabled": true },
    { "id": "field:EnemyAI.attackRange", "label": "Attack Range", "type": "FloatField", "source": "imgui_inspector", "current_value": 2.0, "enabled": true },
    { "id": "field:EnemyAI.isAggressive", "label": "Is Aggressive", "type": "Toggle", "source": "imgui_inspector", "current_value": false, "enabled": true },
    { "id": "id:apply-btn", "label": "Apply", "type": "Button", "source": "uitoolkit", "enabled": true },
    { "id": "id:revert-btn", "label": "Revert", "type": "Button", "source": "uitoolkit", "enabled": false }
  ]
}
```

### 태스크

> Set EnemyAI maxHealth to 200, enable isAggressive, then click Apply.
> Verify Apply button exists and Revert becomes enabled after changes.

### 기대 응답 (정답 기준)

```bash
unity-cli ui set "field:EnemyAI.maxHealth" 200
unity-cli ui set "field:EnemyAI.isAggressive" true
unity-cli ui assert "id:apply-btn" exists
unity-cli ui assert "id:revert-btn" enabled
unity-cli ui click "id:apply-btn"
```

### 채점 기준

| 항목 | 배점 | 기준 |
|---|---|---|
| 셀렉터 정확성 | 30 | field:EnemyAI.maxHealth, field:EnemyAI.isAggressive, id:apply-btn, id:revert-btn 사용 |
| 명령어 정확성 | 20 | ui set, ui assert, ui click 올바르게 사용 |
| 순서 논리 | 30 | set → assert → click 순서 (변경 후 검증, 검증 후 적용) |
| 상태 추론 | 20 | Revert가 변경 시 enabled 된다는 추론 |

---

## Test 2: 메뉴 + 플랫폼 전환

**난이도**: 중
**검증 항목**: 비동기 UI 이해, wait 사용, 지시 준수 (빌드하지 마라)

### 입력 (2단계)

**초기 상태:**

```json
{
  "window": { "name": "SceneView", "type": "UnityEditor.SceneView", "focused": true },
  "active_modal": null,
  "elements": [
    { "id": "id:play-btn", "label": "Play", "type": "Button", "enabled": true },
    { "id": "id:pause-btn", "label": "Pause", "type": "Button", "enabled": false },
    { "id": "menu:File/Build Settings...", "label": "Build Settings...", "type": "MenuItem", "shortcut": "Ctrl+Shift+B", "enabled": true },
    { "id": "menu:File/Build And Run", "label": "Build And Run", "type": "MenuItem", "shortcut": "Ctrl+B", "enabled": true },
    { "id": "menu:Edit/Project Settings...", "label": "Project Settings...", "type": "MenuItem", "enabled": true }
  ]
}
```

**`unity-cli ui menu "File/Build Settings..."` 실행 후:**

```json
{
  "window": { "name": "BuildSettings", "type": "UnityEditor.BuildPlayerWindow", "focused": true },
  "active_modal": "BuildPlayerWindow",
  "elements": [
    { "id": "id:platform-list", "label": "Platform", "type": "ListView", "enabled": true },
    { "id": "id:platform-android", "label": "Android", "type": "ListItem", "source": "uitoolkit", "enabled": true, "visible": true },
    { "id": "id:platform-ios", "label": "iOS", "type": "ListItem", "source": "uitoolkit", "enabled": true, "visible": true },
    { "id": "id:platform-windows", "label": "Windows, Mac, Linux", "type": "ListItem", "source": "uitoolkit", "enabled": true, "visible": true, "selected": true },
    { "id": "id:switch-platform-btn", "label": "Switch Platform", "type": "Button", "enabled": false },
    { "id": "id:build-btn", "label": "Build", "type": "Button", "enabled": true },
    { "id": "id:build-and-run-btn", "label": "Build And Run", "type": "Button", "enabled": true },
    { "id": "id:player-settings-btn", "label": "Player Settings...", "type": "Button", "enabled": true }
  ]
}
```

### 태스크

> Switch the build target to Android, wait for the platform switch to complete, then verify Android is selected and Build button is enabled. Do NOT actually build.

### 기대 응답 (정답 기준)

```bash
unity-cli ui menu "File/Build Settings..."
unity-cli ui click "id:platform-android"
unity-cli ui wait "id:switch-platform-btn" --enabled --timeout 5s
unity-cli ui click "id:switch-platform-btn"
unity-cli ui wait "id:switch-platform-btn" --enabled=false --timeout 30s
unity-cli ui assert "id:build-btn" enabled
```

### 채점 기준

| 항목 | 배점 | 기준 |
|---|---|---|
| 메뉴 열기 | 10 | Build Settings 메뉴 실행 |
| Android 선택 | 15 | platform-android 클릭 |
| Switch Platform 대기+클릭 | 25 | 버튼 활성화 대기 후 클릭 |
| 완료 대기 | 25 | 플랫폼 전환 완료 대기 (wait 사용) |
| 검증 | 15 | Build 버튼 enabled 확인 |
| 지시 준수 | 10 | Build/Build And Run 버튼 클릭 안 함 |

---

## Test 3: 탐색형 QA

**난이도**: 중~어려움
**검증 항목**: 정보 없는 상태에서 탐색 전략, 단계별 추론

### 입력

```
No window is focused. You have not taken any snapshots yet.
```

### 태스크

> A user says: "I heard there's an audio volume setting somewhere in Unity's project settings. Can you find it and tell me what the current value is?"
>
> You must EXPLORE the UI to find it. Write the exact CLI commands you would execute step by step. You don't know exactly where it is — you need to navigate menus, open windows, and search through the UI. After each command, explain your reasoning and what you'd look for in the result before deciding the next step.

### 기대 응답 (정답 기준)

```bash
unity-cli ui menu "Edit/Project Settings..."
# Project Settings 창 열기

unity-cli ui snapshot
# 탭 목록에서 Audio 관련 항목 찾기

unity-cli ui click "label=Audio"
# Audio 탭 진입

unity-cli ui snapshot
# Volume 관련 필드 찾아서 값 확인
```

### 채점 기준

| 항목 | 배점 | 기준 |
|---|---|---|
| 올바른 진입점 | 25 | Edit > Project Settings 경로 사용 |
| 탐색 루프 | 25 | snapshot → 판단 → 행동 → snapshot 패턴 |
| Audio 탭 식별 | 25 | label=Audio 또는 유사 셀렉터로 탭 클릭 |
| 값 확인 | 15 | 최종 snapshot에서 Volume 필드 값 읽기 시도 |
| 추론 설명 | 10 | 각 단계마다 "왜 이걸 하는지" 설명 |

---

## Test 4: 회귀 분석

**난이도**: 어려움
**검증 항목**: diff 해석, 의도성 판단, 심각도 분류, 후속 조치 제안

### 입력

**Baseline (before.json):**

```json
{
  "window": { "name": "Inspector", "type": "UnityEditor.InspectorWindow" },
  "elements": [
    { "id": "field:WeaponConfig.damage", "label": "Damage", "type": "FloatField", "current_value": 25.0, "enabled": true },
    { "id": "field:WeaponConfig.fireRate", "label": "Fire Rate", "type": "FloatField", "current_value": 0.5, "enabled": true },
    { "id": "field:WeaponConfig.maxAmmo", "label": "Max Ammo", "type": "IntField", "current_value": 30, "enabled": true },
    { "id": "field:WeaponConfig.reloadTime", "label": "Reload Time", "type": "FloatField", "current_value": 2.0, "enabled": true },
    { "id": "id:fire-btn", "label": "Test Fire", "type": "Button", "enabled": true },
    { "id": "id:reload-btn", "label": "Reload", "type": "Button", "enabled": true }
  ]
}
```

**Diff 결과:**

```json
{
  "added": [
    { "id": "field:WeaponConfig.burstCount", "label": "Burst Count", "type": "IntField", "current_value": 3 }
  ],
  "removed": [
    { "id": "id:reload-btn", "label": "Reload", "type": "Button" }
  ],
  "changed": [
    { "id": "field:WeaponConfig.fireRate", "field": "current_value", "from": 0.5, "to": 0.1 },
    { "id": "field:WeaponConfig.damage", "field": "enabled", "from": true, "to": false }
  ],
  "unchanged_count": 3
}
```

### 태스크

> Analyze this diff and write a regression report. For each change, assess:
> 1. Is this likely intentional or a bug?
> 2. What is the severity (info/warning/critical)?
> 3. What follow-up verification would you do?
>
> Then write any CLI commands you'd run to investigate further.

### 기대 응답 (정답 기준)

각 변경에 대한 분석:

| 변경 | 판단 | 심각도 | 근거 |
|---|---|---|---|
| burstCount 추가 | 의도적일 수 있음 (신규 기능) | info~warning | 새 필드 추가 자체는 정상이나 검증 필요 |
| Reload 버튼 삭제 | 버그 가능성 높음 | critical | 기존 기능 제거, reloadTime 필드는 남아있음 |
| fireRate 0.5→0.1 | 버그 가능성 | critical | 5배 변경은 밸런스에 큰 영향 |
| damage disabled | 버그 가능성 | critical | 핵심 필드 편집 불가 |

후속 CLI 명령:

```bash
unity-cli ui assert "id:reload-btn" exists          # Reload 버튼 존재 재확인
unity-cli ui assert "field:WeaponConfig.damage" enabled  # damage 필드 상태 확인
unity-cli ui snapshot --output after-verify.json     # 전체 상태 재캡처
```

### 채점 기준

| 항목 | 배점 | 기준 |
|---|---|---|
| 4개 변경 전부 분석 | 20 | 누락 없이 모두 언급 |
| 의도성 판단 | 20 | 각 변경의 의도성을 근거와 함께 판단 |
| 심각도 분류 | 20 | 합리적인 심각도 부여 (Reload 삭제 = critical 등) |
| 연관성 추론 | 20 | burstCount↔fireRate, damage disabled 연관 가능성 등 |
| 후속 CLI 명령 | 10 | 검증용 명령 제시 |
| 리포트 구조 | 10 | 읽기 쉬운 형태로 정리 |

---

## 테스트 실행 방법

### Ollama (로컬)

```bash
curl -s http://<host>:11434/api/chat -d '{
  "model": "qwen3:30b-a3b",
  "messages": [
    { "role": "user", "content": "<공통 시스템 프롬프트>\n\n<테스트 입력 + 태스크>" }
  ],
  "stream": false,
  "options": { "num_predict": 16384, "num_ctx": 32768 }
}'
```

### 주의사항

- **thinking 예산 충분히 할당** — `num_predict: 16384` 이상 권장. 1024 이하에서는 thinking에 토큰을 다 써서 빈 응답이 나올 수 있음.
- 한글 프롬프트는 Windows 셸에서 인코딩 깨질 수 있음 — 영어로 작성하거나 파일로 전달.
- 채점은 사람이 하거나, 별도 LLM에게 정답 기준과 함께 넘겨서 자동 채점 가능.

---

## 검증 완료 모델

| 모델 | 파라미터 | Test 1 | Test 2 | Test 3 | Test 4 |
|---|---|---|---|---|---|
| qwen3:30b-a3b (Q4_K_M) | 30.5B MoE | ✅ 합격 | ✅ 합격 | ✅ 합격 | ✅ 최우수 |

테스트 일자: 2026-04-14
