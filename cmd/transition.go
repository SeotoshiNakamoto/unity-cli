package cmd

import (
	"fmt"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func sendEditorTransition(send sendFn, action string, wait bool, port int, project string, explicitPort int) (*client.CommandResponse, error) {
	return sendEditorTransitionWith(send, action, wait, func() (*client.Instance, error) {
		return resolveCompilationStatus(port, project, explicitPort)
	}, unityHTTPReachable, time.Duration(flagTimeout)*time.Millisecond, 100*time.Millisecond)
}

// Only the initial send dispatches work. All later probes read heartbeat/health.
func sendEditorTransitionWith(send sendFn, action string, wait bool, statusReader func() (*client.Instance, error), reachable func(*client.Instance) bool, timeout, interval time.Duration) (*client.CommandResponse, error) {
	fence := time.Now().UnixMilli()
	var originalPID int
	if action == "quit" {
		if status, err := statusReader(); err == nil && status != nil {
			originalPID = status.PID
		}
	}
	params := map[string]interface{}{"action": action}
	if action == "play" {
		params["wait_for_completion"] = wait
	}
	resp, err := send("manage_editor", params)
	if err != nil || resp == nil || !resp.TransitionPending {
		return resp, err
	}
	if err := waitForEditorTransition(action, originalPID, fence, timeout, interval, statusReader, reachable); err != nil {
		return nil, fmt.Errorf("%s response was interrupted; command was not resent: %w", action, err)
	}
	return &client.CommandResponse{Success: true, Message: fmt.Sprintf("Editor %s completed (verified after interrupted response).", action)}, nil
}

// Raw tool names remain valid passthroughs, but an interrupted acknowledgment
// must use the same verification rules as the dedicated editor/test commands.
func sendPassthroughTransition(command string, params map[string]interface{}, send sendFn, port int, project string, explicitPort int) (*client.CommandResponse, error) {
	fence := time.Now().UnixMilli()
	var pid int
	if command == "manage_editor" {
		if status, err := resolveCompilationStatus(port, project, explicitPort); err == nil {
			pid = status.PID
		}
	}
	mode, _ := params["mode"].(string)
	if command == "run_tests" && strings.EqualFold(mode, "PlayMode") {
		if err := removePreviousTestResults(port); err != nil {
			return nil, err
		}
	}
	resp, err := send(command, params)
	if err != nil || resp == nil || !resp.TransitionPending {
		return resp, err
	}
	switch command {
	case "manage_editor":
		action, _ := params["action"].(string)
		err = waitForEditorTransition(strings.ToLower(action), pid, fence, time.Duration(flagTimeout)*time.Millisecond, 100*time.Millisecond,
			func() (*client.Instance, error) { return resolveCompilationStatus(port, project, explicitPort) }, unityHTTPReachable)
	case "refresh_unity":
		var hasErrors bool
		hasErrors, err = waitForReady(port, project, explicitPort, fence)
		if err == nil && hasErrors {
			err = fmt.Errorf("compilation finished with errors (check unity-cli console)")
		}
	case "run_tests":
		return pollTestResults(port)
	default:
		return nil, fmt.Errorf("%s outcome cannot be verified; command was not resent", command)
	}
	if err != nil {
		return nil, fmt.Errorf("%s interrupted; command was not resent: %w", command, err)
	}
	return &client.CommandResponse{Success: true, Message: command + " completed (verified after interrupted response)."}, nil
}

func waitForEditorTransition(action string, originalPID int, fence int64, timeout, interval time.Duration, statusReader func() (*client.Instance, error), reachable func(*client.Instance) bool) error {
	deadline := time.Now().Add(timeout)
	lastState := "unavailable"
	for time.Now().Before(deadline) {
		if action == "quit" && originalPID > 0 && client.IsProcessDead(originalPID) {
			return nil
		}
		status, err := statusReader()
		if err == nil && status != nil {
			lastState = status.State
			if action != "quit" && unityGone(status) {
				return fmt.Errorf("unity process exited before editor %s completed", action)
			}
			wanted := (action == "play" && status.State == "playing") || (action == "stop" && status.State == "ready")
			if wanted && status.Timestamp > fence && heartbeatAcceptsCommands(status, time.Now(), 1500*time.Millisecond) && reachable(status) {
				return nil
			}
		}
		time.Sleep(interval)
	}
	return fmt.Errorf("timed out verifying editor %s (state=%s); execution outcome remains unknown", action, lastState)
}
