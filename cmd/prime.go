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
	"manage_parrel_sync": "parrelsync",
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
		sb.WriteString("\n\n")
	}
	sb.WriteString("Compiled Development/ReleaseE2E Player: use `player launch/call/wait/stop/kill`; final Shipping builds should omit the bridge. Run `player --help` for the contract.\n\n")
	sb.WriteString("Warm validation worktrees: use `slot doctor` and `slot submit --snapshot --suite compile --wait`; never edit a validation worktree directly. Run `slot --help` for the contract.\n\n")

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
	sb.WriteString("파라미터 상세는 `list` 명령으로 확인. 명령 옵션이 확실하지 않으면 `<command> --help`를 먼저 확인.\n")

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
