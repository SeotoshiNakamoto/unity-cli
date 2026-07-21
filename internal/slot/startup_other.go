//go:build !windows

package slot

import "fmt"

func InstallStartup(executable, configPath string) error {
	return fmt.Errorf("automatic login installation is currently supported only on Windows")
}

func UninstallStartup() error {
	return fmt.Errorf("automatic login installation is currently supported only on Windows")
}
