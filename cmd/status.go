package cmd

import (
	"fmt"
	"os"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func statusCmd(inst *client.Instance) error {
	status, err := readStatus(inst.Port)
	if err != nil {
		return fmt.Errorf("no status for port %d — Unity may not be running", inst.Port)
	}

	age := time.Since(time.UnixMilli(status.Timestamp))
	if age > 3*time.Second {
		fmt.Fprintf(os.Stderr, "Unity (port %d): not responding (last heartbeat %s ago)\n", status.Port, age.Truncate(time.Second))
		return nil
	}

	fmt.Printf("Unity (port %d): %s\n", status.Port, status.State)
	fmt.Printf("  Project: %s\n", status.ProjectPath)
	fmt.Printf("  Version: %s\n", status.UnityVersion)
	fmt.Printf("  PID:     %d\n", status.PID)
	return nil
}

// readStatus finds the instance file matching the given port (any state).
func readStatus(port int) (*client.Instance, error) {
	return client.FindByPort(port)
}

// readActiveStatus finds the active (non-stopped) instance on the given port.
func readActiveStatus(port int) (*client.Instance, error) {
	return client.FindActiveByPort(port)
}

// unityGone checks if the Unity process is confirmed dead. A stale heartbeat
// alone is not enough because domain reloads can stall for 30+ seconds.
func unityGone(status *client.Instance) bool {
	if status == nil {
		return false
	}
	age := time.Since(time.UnixMilli(status.Timestamp))
	if age < 30*time.Second {
		return false // heartbeat still fresh enough
	}
	return status.PID > 0 && client.IsProcessDead(status.PID)
}

// waitForAlive follows the selected project across connector port changes.
// Domain reload can temporarily bind the same Editor to a new port, so a fixed
// port is used only when the caller explicitly supplied --port.
func waitForAlive(inst *client.Instance, project string, explicitPort int, timeoutMs int) (*client.Instance, error) {
	selector := project
	if selector == "" && inst != nil {
		selector = inst.ProjectPath
	}

	resolve := func() (*client.Instance, error) {
		if explicitPort > 0 {
			return readActiveStatus(explicitPort)
		}
		return client.FindByProject(selector)
	}

	if current, err := resolve(); err == nil && time.Now().UnixMilli()-current.Timestamp < 1000 {
		return current, nil
	}

	fmt.Fprintf(os.Stderr, "Waiting for Unity...\n")

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	for time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)
		status, err := resolve()
		if err != nil {
			continue
		}
		if unityGone(status) {
			return nil, fmt.Errorf("unity process exited (pid %d)", status.PID)
		}
		if time.Now().UnixMilli()-status.Timestamp < 1500 {
			fmt.Fprintf(os.Stderr, "Unity is ready.\n")
			return status, nil
		}
	}

	return nil, fmt.Errorf("timed out waiting for Unity project %q", selector)
}

// waitForReady polls until the heartbeat state becomes "ready".
// Returns true if compilation had errors.
func waitForReady(port int, project string, explicitPort int) bool {
	fmt.Fprintf(os.Stderr, "Waiting for compilation...\n")

	deadline := time.Now().Add(5 * time.Minute)
	for time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)
		var (
			status *client.Instance
			err    error
		)
		if explicitPort > 0 {
			status, err = readActiveStatus(port)
		} else {
			status, err = client.FindByProject(project)
		}
		if err != nil {
			continue
		}
		if unityGone(status) {
			fmt.Fprintf(os.Stderr, "Unity process exited during compilation.\n")
			return true
		}
		if status.State == "ready" {
			if status.CompileErrors {
				fmt.Fprintf(os.Stderr, "Compilation finished with errors.\n")
			} else {
				fmt.Fprintf(os.Stderr, "Compilation complete.\n")
			}
			return status.CompileErrors
		}
	}

	fmt.Fprintf(os.Stderr, "Timed out waiting for compilation (5m).\n")
	return true
}
