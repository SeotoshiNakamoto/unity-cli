package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

type toolSchema struct {
	Name        string `json:"name"`
	Description string `json:"description"`
}

var cliCommandMap = map[string]string{
	"execute_csharp":     "exec",
	"execute_menu_item":  "menu",
	"manage_editor":      "editor",
	"read_console":       "console",
	"refresh_unity":      "editor refresh",
	"reserialize_assets": "reserialize",
	"manage_profiler":    "profiler",
	"screenshot":         "screenshot",
	"trace_method":       "trace",
	"ui_snapshot":        "ui",
}

func primeCmd(project string, port int) error {
	var sb strings.Builder

	// 1. Guide file (next to binary)
	guideContent := loadGuideFile()
	if guideContent != "" {
		sb.WriteString(guideContent)
		sb.WriteString("\n")
	}

	// 2. Connection status + tool list
	inst, err := client.DiscoverInstance(project, port)
	if err != nil {
		sb.WriteString("## 연결 상태\n")
		sb.WriteString("Unity not available (에디터가 이 프로젝트를 열고 있지 않음)\n")
		fmt.Print(sb.String())
		return nil
	}

	// Quick connectivity check (2 second timeout)
	statusResp, err := client.Send(inst, "list_tools", map[string]interface{}{}, 2000)
	if err != nil {
		sb.WriteString("## 연결 상태\n")
		sb.WriteString("Unity not available (연결 실패)\n")
		fmt.Print(sb.String())
		return nil
	}

	// Status line
	sb.WriteString("## 연결 상태\n")
	fmt.Fprintf(&sb, "Port: %d | Project: %s | State: ready\n\n", inst.Port, inst.ProjectPath)

	// 3. Compact tool list
	sb.WriteString("## 사용 가능한 도구\n")
	if statusResp != nil && statusResp.Data != nil {
		var tools []toolSchema
		if json.Unmarshal(statusResp.Data, &tools) == nil {
			for _, t := range tools {
				cliCmd := cliCommandMap[t.Name]
				if cliCmd != "" {
					fmt.Fprintf(&sb, "- %s (%s): %s\n", t.Name, cliCmd, t.Description)
				} else {
					fmt.Fprintf(&sb, "- %s: %s\n", t.Name, t.Description)
				}
			}
		}
	}
	sb.WriteString("파라미터 상세는 `list` 명령으로 확인.\n\n")
	sb.WriteString("## 팁\n")
	sb.WriteString("- 시각적 확인이 필요하면 `screenshot --output_path d:/tmp/screenshot.png` 사용. 항상 같은 경로에 덮어쓰고 Read 도구로 읽을 것.\n")
	sb.WriteString("- 특정 에디터 창만 캡처: `screenshot --view window --window_type InspectorWindow --output_path d:/tmp/screenshot.png`. 열린 창 목록은 `screenshot --action list_windows`로 확인.\n")
	sb.WriteString("- 수치 확인은 `exec`가 더 빠르고 정확함. screenshot은 눈으로 봐야 할 때만.\n")
	sb.WriteString("- UI 탐색 (play mode): `ui tree --runtime --interactive`가 게임 QA 기본. `--runtime`은 에디터 창(Inspector/Toolbar 등) 제외하고 게임 UI(UIDocument)만, `--interactive`는 Button/TextField/Label만 (레이아웃 노이즈 제거). `--runtime` 빠지면 에디터 창까지 다 나와서 LLM 헷갈림. `ui snapshot`은 JSON+스크린샷.\n")
	sb.WriteString("- UI 클릭: `ui click --runtime \"id=save-btn\"` — 클릭 후 diff 자동 반환. diff에 added/removed/changed 포함, 별도 snapshot 불필요. 게임 UI 클릭 시 `--runtime` 권장.\n")
	sb.WriteString("- UI 텍스트 입력: `ui type --runtime \"id=input-name\" \"PlayerOne\"` — TextField 입력 + diff 반환. ChangeEvent 자동 발행.\n")
	sb.WriteString("- 화면 전환 감지 (play mode): `ui events`로 UIDocument 추가/제거 이벤트 확인. 로딩 완료, 새 UI 등장 등 비동기 전환 감지. 호출 시 이벤트 큐를 읽고 비움.\n")
	sb.WriteString("\n## async 실행\n")
	sb.WriteString("- 120초 초과 예상 시 반드시 --async 사용 (예: 빌드, 대량 에셋 순회, 번들 빌드)\n")
	sb.WriteString("- `exec \"code\" --async` → job_id 즉시 반환 → `job <job_id>`로 결과 폴링\n")
	sb.WriteString("- job 결과는 1회 폴링 후 삭제됨 — 필요하면 저장해둘 것\n")
	sb.WriteString("- job 폴링 기본 타임아웃 120초, 더 걸리면 `--timeout <ms>` 지정\n")
	sb.WriteString("- 30분 미폴링 job은 자동 정리됨\n")

	fmt.Print(sb.String())
	return nil
}

func loadGuideFile() string {
	// Look for guide.md next to the binary
	execPath, err := os.Executable()
	if err != nil {
		return ""
	}
	guidePath := filepath.Join(filepath.Dir(execPath), "guide.md")

	data, err := os.ReadFile(guidePath)
	if err != nil {
		return ""
	}

	// Trim trailing whitespace but keep structure
	content := strings.TrimRight(string(data), " \t\r\n")
	return content
}
