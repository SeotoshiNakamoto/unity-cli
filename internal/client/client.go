package client

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// Instance represents a running Unity Editor discovered from ~/.unity-cli/instances/.
type Instance struct {
	State         string `json:"state"`
	ProjectPath   string `json:"projectPath"`
	Port          int    `json:"port"`
	PID           int    `json:"pid"`
	UnityVersion  string `json:"unityVersion,omitempty"`
	Timestamp     int64  `json:"timestamp,omitempty"`
	CompileErrors bool   `json:"compileErrors,omitempty"`
}

// CommandRequest is the JSON body sent to Unity's HTTP server.
type CommandRequest struct {
	Command string      `json:"command"`
	Params  interface{} `json:"params"`
}

// CommandResponse is the JSON body returned by Unity.
// Data is raw JSON so callers can unmarshal into any shape.
type CommandResponse struct {
	Success bool            `json:"success"`
	Message string          `json:"message"`
	Data    json.RawMessage `json:"data,omitempty"`
}

// isProcessDead returns true only when the process is confirmed to not exist.
// Permission errors or transient failures return false (not confirmed dead),
// so the instance file is preserved.
// Defaults to the OS-specific implementation; overridden in tests.
var isProcessDead = checkProcessDead

// IsProcessDead exposes the process-alive check for use by cmd package.
func IsProcessDead(pid int) bool {
	return isProcessDead(pid)
}

func instancesDir() string {
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".unity-cli", "instances")
}

// ScanInstances reads all instance files from ~/.unity-cli/instances/.
// Stale files whose PID is no longer running are automatically removed.
func ScanInstances() ([]Instance, error) {
	dir := instancesDir()
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}

	var instances []Instance
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		fp := filepath.Join(dir, e.Name())
		data, err := os.ReadFile(fp)
		if err != nil {
			continue
		}
		var inst Instance
		if err := json.Unmarshal(data, &inst); err != nil {
			continue
		}
		if inst.PID > 0 && isProcessDead(inst.PID) {
			_ = os.Remove(fp)
			continue
		}
		instances = append(instances, inst)
	}
	return instances, nil
}

// ActiveInstances returns live instance heartbeats sorted by project path and port.
func ActiveInstances() ([]Instance, error) {
	instances, err := ScanInstances()
	if err != nil {
		return nil, err
	}

	active := instances[:0]
	for _, inst := range instances {
		if inst.State != "stopped" {
			active = append(active, inst)
		}
	}
	sort.Slice(active, func(i, j int) bool {
		left := strings.ToLower(filepath.ToSlash(active[i].ProjectPath))
		right := strings.ToLower(filepath.ToSlash(active[j].ProjectPath))
		if left == right {
			return active[i].Port < active[j].Port
		}
		return left < right
	})
	return active, nil
}

// FindByPort scans instance files and returns the instance matching the given port.
// If multiple instances share the same port, the one with the most recent timestamp wins.
func FindByPort(port int) (*Instance, error) {
	instances, err := ScanInstances()
	if err != nil {
		return nil, err
	}
	var best *Instance
	for i, inst := range instances {
		if inst.Port != port {
			continue
		}
		if best == nil || inst.Timestamp > best.Timestamp {
			best = &instances[i]
		}
	}
	if best == nil {
		return nil, fmt.Errorf("no instance on port %d", port)
	}
	return best, nil
}

// FindActiveByPort is like FindByPort but skips stopped instances.
// Used by polling paths (waitForAlive, waitForReady) that only care about live instances.
func FindActiveByPort(port int) (*Instance, error) {
	instances, err := ScanInstances()
	if err != nil {
		return nil, err
	}
	var best *Instance
	for i, inst := range instances {
		if inst.Port != port || inst.State == "stopped" {
			continue
		}
		if best == nil || inst.Timestamp > best.Timestamp {
			best = &instances[i]
		}
	}
	if best == nil {
		return nil, fmt.Errorf("no active instance on port %d", port)
	}
	return best, nil
}

// FindByProject selects an active instance by project path.
// A canonical absolute path is matched exactly first. For convenience, a unique
// project directory name or path suffix is accepted, but ambiguous matches fail.
func FindByProject(project string) (*Instance, error) {
	alive, err := ActiveInstances()
	if err != nil {
		return nil, err
	}
	if len(alive) == 0 {
		return nil, fmt.Errorf("no Unity instances running")
	}

	query := normalizeProjectPath(project)
	var exact []Instance
	for _, inst := range alive {
		if equalProjectPath(normalizeProjectPath(inst.ProjectPath), query) {
			exact = append(exact, inst)
		}
	}
	if len(exact) > 0 {
		return newestInstance(exact), nil
	}

	querySuffix := strings.Trim(strings.ToLower(filepath.ToSlash(filepath.Clean(project))), "/")
	var suffix []Instance
	for _, inst := range alive {
		pathNorm := strings.Trim(strings.ToLower(filepath.ToSlash(filepath.Clean(inst.ProjectPath))), "/")
		if filepath.Base(pathNorm) == filepath.Base(querySuffix) ||
			strings.HasSuffix(pathNorm, "/"+querySuffix) {
			suffix = append(suffix, inst)
		}
	}
	if len(suffix) == 1 {
		return &suffix[0], nil
	}
	if len(suffix) > 1 {
		return nil, fmt.Errorf("project selector is ambiguous: %s\nMatches:\n%s", project, formatInstancePaths(suffix))
	}

	return nil, fmt.Errorf("no Unity instance found for project: %s\nRunning projects:\n%s", project, formatInstancePaths(alive))
}

func normalizeProjectPath(path string) string {
	cleaned := filepath.Clean(path)
	if abs, err := filepath.Abs(cleaned); err == nil {
		cleaned = abs
	}
	if resolved, err := filepath.EvalSymlinks(cleaned); err == nil {
		cleaned = resolved
	}
	return filepath.ToSlash(cleaned)
}

func equalProjectPath(left, right string) bool {
	return strings.EqualFold(strings.TrimRight(left, "/"), strings.TrimRight(right, "/"))
}

func newestInstance(instances []Instance) *Instance {
	best := instances[0]
	for _, inst := range instances[1:] {
		if inst.Timestamp > best.Timestamp {
			best = inst
		}
	}
	return &best
}

func formatInstancePaths(instances []Instance) string {
	paths := make([]string, 0, len(instances))
	for _, inst := range instances {
		paths = append(paths, fmt.Sprintf("  %s (port %d, pid %d)", inst.ProjectPath, inst.Port, inst.PID))
	}
	sort.Strings(paths)
	return strings.Join(paths, "\n")
}

// KillProcess forcibly terminates a Unity process. Callers should require an
// explicit confirmation flag because this bypasses Unity's normal shutdown.
func KillProcess(pid int) error {
	if pid <= 0 {
		return fmt.Errorf("invalid process id: %d", pid)
	}
	process, err := os.FindProcess(pid)
	if err != nil {
		return err
	}
	return process.Kill()
}

// DiscoverInstance finds a running Unity instance from ~/.unity-cli/instances/.
// If port > 0, skips discovery and connects directly.
// If project is set, uses exact canonical matching with an unambiguous suffix fallback.
// Otherwise returns the most recently active instance.
func DiscoverInstance(project string, port int) (*Instance, error) {
	if port > 0 {
		return &Instance{ProjectPath: "override", Port: port}, nil
	}

	instances, err := ActiveInstances()
	if err != nil {
		return nil, fmt.Errorf("no Unity instances found.\nIs Unity running with the Connector package?\nExpected: %s", instancesDir())
	}

	if len(instances) == 0 {
		return nil, fmt.Errorf("no Unity instances running")
	}

	if project != "" {
		return FindByProject(project)
	}

	// Try to match by current working directory before falling back to timestamp
	if cwd, err := os.Getwd(); err == nil {
		cwdNorm := filepath.ToSlash(cwd)
		for _, inst := range instances {
			projNorm := filepath.ToSlash(inst.ProjectPath)
			if cwdNorm == projNorm || strings.HasPrefix(cwdNorm, projNorm+"/") {
				return &inst, nil
			}
		}
	}

	// Return the most recently updated
	best := instances[0]
	for _, inst := range instances[1:] {
		if inst.Timestamp > best.Timestamp {
			best = inst
		}
	}
	return &best, nil
}

func Send(inst *Instance, command string, params interface{}, timeoutMs int) (*CommandResponse, error) {
	if params == nil {
		params = map[string]interface{}{}
	}

	body, err := json.Marshal(CommandRequest{Command: command, Params: params})
	if err != nil {
		return nil, err
	}

	url := fmt.Sprintf("http://127.0.0.1:%d/command", inst.Port)
	httpClient := &http.Client{Timeout: time.Duration(timeoutMs) * time.Millisecond}

	resp, err := httpClient.Post(url, "application/json", bytes.NewReader(body))
	if err != nil {
		return nil, fmt.Errorf("cannot connect to Unity at port %d: %v", inst.Port, err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		var body []byte
		body, _ = io.ReadAll(resp.Body)
		if len(body) > 0 {
			return nil, fmt.Errorf("HTTP %d from Unity: %s", resp.StatusCode, string(body))
		}
		return nil, fmt.Errorf("HTTP %d from Unity (command: %s)", resp.StatusCode, command)
	}

	respBody, err := io.ReadAll(resp.Body)
	if err != nil || len(respBody) == 0 {
		// Some commands (e.g. play mode entry) close the connection before responding.
		return &CommandResponse{
			Success: true,
			Message: fmt.Sprintf("%s sent (connection closed before response)", command),
		}, nil
	}

	var result CommandResponse
	if err := json.Unmarshal(respBody, &result); err != nil {
		// Unity sent a non-JSON body — treat as plain message.
		return &CommandResponse{
			Success: true,
			Message: string(respBody),
		}, nil
	}

	return &result, nil
}
