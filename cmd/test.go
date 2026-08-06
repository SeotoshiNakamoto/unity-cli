package cmd

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

var unityTestBootstrapSceneName = regexp.MustCompile(`(?i)^InitTestScene[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.unity(?:\.meta)?$`)

type suppressWriter struct {
	w        io.Writer
	suppress string
}

func (s *suppressWriter) Write(p []byte) (int, error) {
	if bytes.Contains(p, []byte(s.suppress)) {
		return len(p), nil
	}
	return s.w.Write(p)
}

func testCmd(args []string, send sendFn, port int) (*client.CommandResponse, error) {
	flags := parseSubFlags(args)

	mode := "EditMode"
	if m, ok := flags["mode"]; ok {
		mode = m
	}

	if mode != "EditMode" && mode != "PlayMode" {
		return nil, fmt.Errorf("--mode must be EditMode or PlayMode, got: %s", mode)
	}

	params := map[string]interface{}{
		"mode": mode,
	}
	if filter, ok := flags["filter"]; ok {
		params["filter"] = filter
	}

	resp, err := send("run_tests", params)
	if err != nil {
		return nil, err
	}

	if !resp.Success && strings.Contains(resp.Message, "Unknown command") {
		return nil, fmt.Errorf(
			"'run_tests' is not available.\n" +
				"Install the Unity Test Framework package:\n" +
				"  Window > Package Manager > search 'Test Framework' > Install")
	}

	// EditMode: results returned directly in response
	if mode == "EditMode" {
		return resp, nil
	}

	// PlayMode: Unity returns "running", poll results file
	if !playModeRunStarted(resp) {
		return resp, nil
	}

	fmt.Fprintln(os.Stderr, "PlayMode tests running, waiting for results...")

	// Suppress "Unsolicited response received on idle HTTP channel" during domain reload
	original := log.Writer()
	log.SetOutput(&suppressWriter{w: os.Stderr, suppress: "Unsolicited response received on idle HTTP channel"})
	defer log.SetOutput(original)

	return pollTestResults(port)
}

func playModeRunStarted(resp *client.CommandResponse) bool {
	if resp == nil || !resp.Success {
		return false
	}
	return resp.Message == "running" || strings.HasPrefix(resp.Message, "run_tests sent (connection closed before response)")
}

func pollTestResults(port int) (*client.CommandResponse, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return nil, fmt.Errorf("cannot determine home directory: %w", err)
	}

	resultsPath := filepath.Join(home, ".unity-cli", "status", fmt.Sprintf("test-results-%d.json", port))
	deadline := time.Now().Add(10 * time.Minute)

	for time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)

		data, err := os.ReadFile(resultsPath)
		if err == nil {
			_ = os.Remove(resultsPath)
			var resp client.CommandResponse
			if err := json.Unmarshal(data, &resp); err != nil {
				return nil, fmt.Errorf("failed to parse test results: %w", err)
			}
			if err := waitForPlayModeCleanup(port); err != nil {
				return nil, err
			}
			return &resp, nil
		}

		// Check Unity process is still alive
		inst, err := readStatus(port)
		if err == nil && inst.State == "stopped" {
			return nil, fmt.Errorf("unity editor has stopped (port %d)", port)
		}
		if unityGone(inst) {
			return nil, fmt.Errorf("unity process exited (port %d)", port)
		}
	}

	return nil, fmt.Errorf("timed out waiting for test results (10m)")
}

func waitForPlayModeCleanup(port int) error {
	return waitForPlayModeCleanupWith(port, 30*time.Second, 100*time.Millisecond, readStatus)
}

func waitForPlayModeCleanupWith(port int, timeout, pollInterval time.Duration, statusReader func(int) (*client.Instance, error)) error {
	deadline := time.Now().Add(timeout)
	var lastState string
	var lastArtifacts []string
	var lastErr error
	for time.Now().Before(deadline) {
		status, err := statusReader(port)
		if err != nil {
			lastErr = err
		} else {
			if status.State == "stopped" || unityGone(status) {
				return fmt.Errorf("unity editor stopped before PlayMode cleanup finished (port %d)", port)
			}
			lastState = status.State
			lastArtifacts, lastErr = playModeBootstrapArtifacts(status.ProjectPath)
			if lastErr == nil && status.State == "ready" && len(lastArtifacts) == 0 {
				return nil
			}
		}
		time.Sleep(pollInterval)
	}
	if lastErr != nil {
		return fmt.Errorf("timed out waiting for PlayMode cleanup (port %d): %w", port, lastErr)
	}
	return fmt.Errorf("timed out waiting for PlayMode cleanup (port %d, state=%s, artifacts=%s)", port, lastState, strings.Join(lastArtifacts, ", "))
}

func playModeBootstrapArtifacts(projectPath string) ([]string, error) {
	if strings.TrimSpace(projectPath) == "" {
		return nil, fmt.Errorf("unity instance has no project path")
	}
	assets := filepath.Join(projectPath, "Assets")
	entries, err := os.ReadDir(assets)
	if err != nil {
		return nil, fmt.Errorf("read Unity Assets directory %s: %w", assets, err)
	}
	artifacts := make([]string, 0)
	for _, entry := range entries {
		if entry.IsDir() || !unityTestBootstrapSceneName.MatchString(entry.Name()) {
			continue
		}
		artifacts = append(artifacts, entry.Name())
	}
	return artifacts, nil
}
