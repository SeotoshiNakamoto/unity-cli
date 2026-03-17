package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

type toolSchema struct {
	Name        string `json:"name"`
	Description string `json:"description"`
}

var cliCommandMap = map[string]string{
	"execute_csharp":    "exec",
	"execute_menu_item": "menu",
	"manage_editor":     "editor",
	"read_console":      "console",
	"refresh_unity":     "editor refresh",
	"reserialize_assets": "reserialize",
	"manage_profiler":   "profiler",
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
	sb.WriteString(fmt.Sprintf("Port: %d | Project: %s | State: ready\n\n", inst.Port, inst.ProjectPath))

	// 3. Compact tool list
	sb.WriteString("## 사용 가능한 도구\n")
	if statusResp != nil && statusResp.Data != nil {
		var tools []toolSchema
		if json.Unmarshal(statusResp.Data, &tools) == nil {
			for _, t := range tools {
				cliCmd := cliCommandMap[t.Name]
				if cliCmd != "" {
					sb.WriteString(fmt.Sprintf("- %s (%s): %s\n", t.Name, cliCmd, t.Description))
				} else {
					sb.WriteString(fmt.Sprintf("- %s: %s\n", t.Name, t.Description))
				}
			}
		}
	}
	sb.WriteString("파라미터 상세는 `list` 명령으로 확인.\n")

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

// waitForAliveQuick is a short timeout version for prime
func waitForAliveQuick(port int) bool {
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		_, err := client.Send(
			&client.Instance{Port: port},
			"list_tools",
			map[string]interface{}{},
			1000,
		)
		if err == nil {
			return true
		}
		time.Sleep(200 * time.Millisecond)
	}
	return false
}
