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
	if status.ConnectorVersion != "" {
		fmt.Printf("  Connector: %s\n", status.ConnectorVersion)
		if !status.ConnectorListening {
			fmt.Printf("  Connector listener: unavailable")
			if status.ConnectorError != "" {
				fmt.Printf(" (%s)", status.ConnectorError)
			}
			fmt.Println()
		}
	}
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
	return waitForAliveWithProbe(inst, project, explicitPort, timeoutMs, unityHTTPReachable)
}

func waitForAliveWithProbe(inst *client.Instance, project string, explicitPort int, timeoutMs int, reachable func(*client.Instance) bool) (*client.Instance, error) {
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

	if current, err := resolve(); err == nil && time.Now().UnixMilli()-current.Timestamp < 1000 && reachable(current) {
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
		if time.Now().UnixMilli()-status.Timestamp < 1500 && reachable(status) {
			fmt.Fprintf(os.Stderr, "Unity is ready.\n")
			return status, nil
		}
	}

	return nil, fmt.Errorf("timed out waiting for Unity project %q", selector)
}

func unityHTTPReachable(instance *client.Instance) bool {
	resp, err := client.Health(instance, 1000)
	if err == nil {
		return resp.Success
	}

	// Connector versions before /health existed still need to remain usable
	// during a CLI-first upgrade. Fall back to a lightweight tool command.
	resp, err = client.Send(instance, "list", nil, 1000)
	return err == nil && resp.Success
}

const (
	compilationPollInterval       = 250 * time.Millisecond
	compilationWaitTimeout        = 5 * time.Minute
	stableReadyHeartbeatsRequired = 2
)

type compilationBarrier struct {
	fenceTimestamp     int64
	observedBusy       bool
	readyHeartbeats    int
	lastReadyTimestamp int64
	compileErrors      bool
}

func (b *compilationBarrier) observe(status *client.Instance, reachable bool) (done bool, hasErrors bool) {
	if status == nil || status.Timestamp <= b.fenceTimestamp {
		return false, false
	}

	switch status.State {
	case "compiling", "reloading":
		b.observedBusy = true
		b.resetReadyStreak()
		return false, false
	case "ready":
		if !b.observedBusy || !reachable {
			b.resetReadyStreak()
			return false, false
		}
		if status.Timestamp <= b.lastReadyTimestamp {
			return false, false
		}
		b.lastReadyTimestamp = status.Timestamp
		b.readyHeartbeats++
		b.compileErrors = b.compileErrors || status.CompileErrors
		return b.readyHeartbeats >= stableReadyHeartbeatsRequired, b.compileErrors
	default:
		b.resetReadyStreak()
		return false, false
	}
}

func (b *compilationBarrier) resetReadyStreak() {
	b.readyHeartbeats = 0
	b.lastReadyTimestamp = 0
	b.compileErrors = false
}

func resolveCompilationStatus(port int, project string, explicitPort int) (*client.Instance, error) {
	if explicitPort > 0 {
		return readActiveStatus(port)
	}
	return client.FindByProject(project)
}

// compilationFenceTimestamp captures the last heartbeat that existed before
// RequestScriptCompilation is sent. A later ready heartbeat is not sufficient
// by itself because Unity may publish it before the requested compile starts.
func compilationFenceTimestamp(port int, project string, explicitPort int) int64 {
	fence := time.Now().UnixMilli()
	if status, err := resolveCompilationStatus(port, project, explicitPort); err == nil && status.Timestamp > fence {
		fence = status.Timestamp
	}
	return fence
}

// waitForReady accepts completion only after the requested compilation cycle
// was observed and the reconnected Editor published consecutive fresh ready
// heartbeats that also answer over HTTP.
func waitForReady(port int, project string, explicitPort int, fenceTimestamp int64) (bool, error) {
	fmt.Fprintf(os.Stderr, "Waiting for compilation...\n")

	barrier := compilationBarrier{fenceTimestamp: fenceTimestamp}
	deadline := time.Now().Add(compilationWaitTimeout)
	for time.Now().Before(deadline) {
		time.Sleep(compilationPollInterval)
		status, err := resolveCompilationStatus(port, project, explicitPort)
		if err != nil {
			continue
		}
		if unityGone(status) {
			return false, fmt.Errorf("unity process exited during compilation (pid %d)", status.PID)
		}

		reachable := status.State == "ready" && unityHTTPReachable(status)
		if done, hasErrors := barrier.observe(status, reachable); done {
			if hasErrors {
				fmt.Fprintf(os.Stderr, "Compilation finished with errors.\n")
			} else {
				fmt.Fprintf(os.Stderr, "Compilation complete.\n")
			}
			return hasErrors, nil
		}
	}

	if !barrier.observedBusy {
		return false, fmt.Errorf("timed out waiting for requested compilation to start (%s)", compilationWaitTimeout)
	}
	return false, fmt.Errorf("timed out waiting for Unity to become stable after compilation (%s)", compilationWaitTimeout)
}
