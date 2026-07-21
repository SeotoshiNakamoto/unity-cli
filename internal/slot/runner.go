package slot

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	unityclient "github.com/youngwoocho02/unity-cli/internal/client"
)

type Runner struct {
	LogDir      string
	ToolVersion string
}

func (r Runner) Run(ctx context.Context, job Job, slot SlotConfig) (*Result, string, error) {
	if err := os.MkdirAll(r.LogDir, 0o755); err != nil {
		return nil, "", fmt.Errorf("create validation log directory: %w", err)
	}
	logPath := filepath.Join(r.LogDir, job.ID+".log")
	logFile, err := os.Create(logPath)
	if err != nil {
		return nil, "", fmt.Errorf("create validation log: %w", err)
	}
	defer logFile.Close()
	logf := func(format string, args ...any) {
		_, _ = fmt.Fprintf(logFile, "%s "+format+"\n", append([]any{time.Now().UTC().Format(time.RFC3339)}, args...)...)
	}

	logf("job=%s slot=%s sha=%s suite=%s", job.ID, slot.ID, job.SHA, job.Suite)
	sourceCommon, err := gitCommonDir(ctx, job.Repository)
	if err != nil {
		return nil, logPath, fmt.Errorf("resolve source git common directory: %w", err)
	}
	slotCommon, err := gitCommonDir(ctx, slot.Worktree)
	if err != nil {
		return nil, logPath, fmt.Errorf("resolve validation git common directory: %w", err)
	}
	if !samePath(sourceCommon, slotCommon) {
		return nil, logPath, fmt.Errorf("validation worktree does not share the source Git object store")
	}

	unityCLI, err := resolveUnityCLI(slot.UnityCLI)
	if err != nil {
		return nil, logPath, err
	}
	currentProjectPath := filepath.Join(slot.Worktree, slot.ProjectSubdir)
	if isUnityRunning(currentProjectPath) {
		logf("stopping play mode before checkout")
		_, _ = runCommand(ctx, logFile, 2*time.Minute, unityCLI,
			"--project", currentProjectPath, "editor", "stop")
	}
	if err := restoreValidationWorktree(ctx, slot.Worktree, job.Repository); err != nil {
		return nil, logPath, err
	}
	logf("checking out detached commit %s", job.SHA)
	if err := checkoutDetached(ctx, slot.Worktree, job.SHA); err != nil {
		return nil, logPath, err
	}

	projectConfig, err := LoadProjectConfig(filepath.Join(slot.Worktree, ProjectConfigName))
	if err != nil {
		return nil, logPath, err
	}
	if projectConfig.Project != job.Project || projectConfig.Project != slot.Project {
		return nil, logPath, fmt.Errorf("project mismatch: job=%s slot=%s config=%s", job.Project, slot.Project, projectConfig.Project)
	}
	suite, ok := projectConfig.Suites[job.Suite]
	if !ok {
		return nil, logPath, fmt.Errorf("suite %q is not defined for %s", job.Suite, projectConfig.Project)
	}
	projectPath := filepath.Join(slot.Worktree, projectConfig.UnityProjectSubdir)
	if _, err := os.Stat(filepath.Join(projectPath, "ProjectSettings", "ProjectVersion.txt")); err != nil {
		return nil, logPath, fmt.Errorf("invalid Unity project path %s: %w", projectPath, err)
	}

	if !isUnityRunning(projectPath) {
		if slot.UnityExecutable == "" {
			return nil, logPath, fmt.Errorf("unity is not running for %s and unityExecutable is not configured", projectPath)
		}
		logf("launching Unity project=%s executable=%s", projectPath, slot.UnityExecutable)
		pid, launchErr := launchUnity(slot.UnityExecutable, projectPath, logFile)
		if launchErr != nil {
			return nil, logPath, launchErr
		}
		if slot.DesktopIndex != nil && slot.DesktopHelper != "" {
			moveCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
			moveErr := moveWindowToDesktopWithRetry(moveCtx, slot.DesktopHelper, pid, *slot.DesktopIndex)
			cancel()
			if moveErr != nil {
				logf("early desktop move failed; will retry after readiness: %v", moveErr)
			} else {
				logf("moved starting Unity pid=%d to desktop=%d", pid, *slot.DesktopIndex)
			}
		}
		if _, waitErr := waitForExactUnity(ctx, projectPath, 15*time.Minute); waitErr != nil {
			return nil, logPath, fmt.Errorf("wait for exact Unity project readiness: %w", waitErr)
		}
	}

	if _, waitErr := waitForExactUnity(ctx, projectPath, 15*time.Minute); waitErr != nil {
		return nil, logPath, fmt.Errorf("wait for exact Unity project readiness before validation: %w", waitErr)
	}

	if slot.DesktopIndex != nil && slot.DesktopHelper != "" {
		if instance, findErr := findExactUnityInstance(projectPath); findErr != nil {
			logf("desktop move skipped: %v", findErr)
		} else {
			moveCtx, cancel := context.WithTimeout(ctx, 15*time.Second)
			moveErr := moveWindowToDesktop(moveCtx, slot.DesktopHelper, instance.PID, *slot.DesktopIndex)
			cancel()
			if moveErr != nil {
				logf("desktop move disabled for this run: %v", moveErr)
			} else {
				logf("moved Unity pid=%d to desktop=%d", instance.PID, *slot.DesktopIndex)
			}
		}
	}

	result := &Result{
		Passed:       false,
		TestedSHA:    job.SHA,
		SlotID:       slot.ID,
		UnityProject: projectPath,
		Steps:        make([]StepResult, 0, len(suite.Steps)),
	}
	for _, step := range suite.Steps {
		timeout := time.Duration(step.TimeoutSeconds) * time.Second
		if timeout <= 0 {
			timeout = 10 * time.Minute
		}
		started := time.Now()
		args := append([]string{"--project", projectPath}, step.Args...)
		logf("step=%s command=%s %s", step.Name, unityCLI, strings.Join(args, " "))
		output, runErr := runCommand(ctx, logFile, timeout, unityCLI, args...)
		stepResult := StepResult{
			Name:       step.Name,
			ExitCode:   exitCode(runErr),
			DurationMS: time.Since(started).Milliseconds(),
			Output:     truncateOutput(output, 16*1024),
		}
		if runErr == nil {
			runErr = assertStep(step.Assertion, output)
		}
		if runErr != nil {
			stepResult.Error = runErr.Error()
			result.Steps = append(result.Steps, stepResult)
			return result, logPath, fmt.Errorf("step %q failed: %w", step.Name, runErr)
		}
		result.Steps = append(result.Steps, stepResult)
	}
	result.Passed = true
	logf("validation passed")
	return result, logPath, nil
}

func resolveUnityCLI(configured string) (string, error) {
	if configured != "" {
		if _, err := os.Stat(configured); err != nil {
			return "", fmt.Errorf("unity-cli executable not found: %s", configured)
		}
		return configured, nil
	}
	executable, err := os.Executable()
	if err != nil {
		return "", err
	}
	name := "unity-cli"
	if runtime.GOOS == "windows" {
		name += ".exe"
	}
	candidate := filepath.Join(filepath.Dir(executable), name)
	if _, err := os.Stat(candidate); err != nil {
		return "", fmt.Errorf("unity-cli executable not found beside slot agent: %s", candidate)
	}
	return candidate, nil
}

func isUnityRunning(projectPath string) bool {
	_, err := findExactUnityInstance(projectPath)
	return err == nil
}

func findExactUnityInstance(projectPath string) (*unityclient.Instance, error) {
	instances, err := unityclient.ActiveInstances()
	if err != nil {
		return nil, err
	}
	wanted := normalizeExactProjectPath(projectPath)
	for i := range instances {
		if strings.EqualFold(normalizeExactProjectPath(instances[i].ProjectPath), wanted) {
			return &instances[i], nil
		}
	}
	return nil, fmt.Errorf("no exact Unity instance for project %s", projectPath)
}

func waitForExactUnity(ctx context.Context, projectPath string, timeout time.Duration) (*unityclient.Instance, error) {
	deadline := time.Now().Add(timeout)
	for {
		instance, err := findExactUnityInstance(projectPath)
		if err == nil && strings.EqualFold(instance.State, "ready") {
			return instance, nil
		}
		if time.Now().After(deadline) {
			if err != nil {
				return nil, err
			}
			return nil, fmt.Errorf("unity state is %q, expected ready", instance.State)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
	}
}

func normalizeExactProjectPath(path string) string {
	cleaned := filepath.Clean(path)
	if absolute, err := filepath.Abs(cleaned); err == nil {
		cleaned = absolute
	}
	if resolved, err := filepath.EvalSymlinks(cleaned); err == nil {
		cleaned = resolved
	}
	return strings.TrimRight(filepath.ToSlash(cleaned), "/")
}

func launchUnity(executable, projectPath string, output io.Writer) (int, error) {
	if _, err := os.Stat(executable); err != nil {
		return 0, fmt.Errorf("unity executable not found: %s", executable)
	}
	cmd := exec.Command(executable, "-projectPath", projectPath)
	cmd.Stdout = output
	cmd.Stderr = output
	if err := cmd.Start(); err != nil {
		return 0, fmt.Errorf("launch Unity: %w", err)
	}
	pid := cmd.Process.Pid
	if err := cmd.Process.Release(); err != nil {
		return 0, fmt.Errorf("release Unity process handle: %w", err)
	}
	return pid, nil
}

func runCommand(parent context.Context, log io.Writer, timeout time.Duration, executable string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(parent, timeout)
	defer cancel()
	var output bytes.Buffer
	writer := io.MultiWriter(log, &output)
	cmd := exec.CommandContext(ctx, executable, args...)
	cmd.Stdout = writer
	cmd.Stderr = writer
	err := cmd.Run()
	if ctx.Err() == context.DeadlineExceeded {
		return output.String(), fmt.Errorf("command timed out after %s", timeout)
	}
	return output.String(), err
}

func assertStep(assertion, output string) error {
	switch assertion {
	case "", "exit-zero":
		return nil
	case "empty-json-array":
		var values []json.RawMessage
		decoder := json.NewDecoder(strings.NewReader(strings.TrimSpace(output)))
		if err := decoder.Decode(&values); err != nil {
			return fmt.Errorf("expected a JSON array: %w", err)
		}
		if len(values) > 0 {
			return fmt.Errorf("expected no entries, found %d", len(values))
		}
		return nil
	default:
		return fmt.Errorf("unknown step assertion %q", assertion)
	}
}

func exitCode(err error) int {
	if err == nil {
		return 0
	}
	var exitErr *exec.ExitError
	if errors.As(err, &exitErr) {
		return exitErr.ExitCode()
	}
	return -1
}

func truncateOutput(output string, limit int) string {
	if len(output) <= limit {
		return strings.TrimSpace(output)
	}
	return strings.TrimSpace(output[:limit]) + "\n... output truncated; see full log"
}
