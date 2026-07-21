//go:build windows

package slot

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

const startupValueName = "UnitySlotAgent"

func InstallStartup(executable, configPath string) error {
	scriptPath := filepath.Join(filepath.Dir(configPath), "start-unity-slot-agent.ps1")
	script := fmt.Sprintf(`$ErrorActionPreference = "Stop"
Start-Process -FilePath '%s' -ArgumentList @('--config', '%s', 'run') -WindowStyle Hidden
`, quotePowerShellSingle(executable), quotePowerShellSingle(configPath))
	if err := os.WriteFile(scriptPath, []byte(script), 0o600); err != nil {
		return fmt.Errorf("write login launcher: %w", err)
	}
	action := fmt.Sprintf(`powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%s"`, scriptPath)
	cmd := exec.Command("reg.exe", "ADD", `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
		"/V", startupValueName, "/T", "REG_SZ", "/D", action, "/F")
	if output, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("register login launcher: %s: %w", strings.TrimSpace(string(output)), err)
	}
	cmd = exec.Command("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath)
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start login launcher: %w", err)
	}
	if err := cmd.Process.Release(); err != nil {
		return fmt.Errorf("release login launcher process: %w", err)
	}
	return nil
}

func UninstallStartup() error {
	cmd := exec.Command("reg.exe", "DELETE", `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
		"/V", startupValueName, "/F")
	if output, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("remove login launcher: %s: %w", strings.TrimSpace(string(output)), err)
	}
	return nil
}

func quotePowerShellSingle(value string) string {
	return strings.ReplaceAll(value, `'`, `''`)
}
