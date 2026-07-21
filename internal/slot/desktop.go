package slot

import (
	"context"
	"encoding/json"
	"fmt"
	"os/exec"
	"strconv"
	"strings"
	"time"
)

type desktopDoctorResult struct {
	DesktopCount    int    `json:"desktopCount"`
	CurrentDesktop  int    `json:"currentDesktop"`
	SelectedDesktop *int   `json:"selectedDesktop,omitempty"`
	SelectedName    string `json:"selectedName,omitempty"`
	SelectionSource string `json:"selectionSource,omitempty"`
}

type desktopTarget struct {
	Index           *int
	Name            string
	FallbackFromEnd *int
}

func desktopTargetFor(slot SlotConfig) desktopTarget {
	return desktopTarget{
		Index:           slot.DesktopIndex,
		Name:            strings.TrimSpace(slot.DesktopName),
		FallbackFromEnd: slot.DesktopFallbackFromEnd,
	}
}

func (target desktopTarget) configured() bool {
	return target.Index != nil || target.Name != "" || target.FallbackFromEnd != nil
}

func (target desktopTarget) args() []string {
	if target.Index != nil {
		return []string{"--desktop", strconv.Itoa(*target.Index)}
	}
	args := make([]string, 0, 4)
	if target.Name != "" {
		args = append(args, "--desktop-name", target.Name)
	}
	if target.FallbackFromEnd != nil {
		args = append(args, "--fallback-from-end", strconv.Itoa(*target.FallbackFromEnd))
	}
	return args
}

func (target desktopTarget) description() string {
	if target.Index != nil {
		return fmt.Sprintf("index=%d", *target.Index)
	}
	if target.Name != "" && target.FallbackFromEnd != nil {
		return fmt.Sprintf("name=%q fallback-from-end=%d", target.Name, *target.FallbackFromEnd)
	}
	if target.Name != "" {
		return fmt.Sprintf("name=%q", target.Name)
	}
	if target.FallbackFromEnd != nil {
		return fmt.Sprintf("fallback-from-end=%d", *target.FallbackFromEnd)
	}
	return "unconfigured"
}

func moveWindowToDesktop(ctx context.Context, helper string, pid int, target desktopTarget) error {
	if helper == "" || pid <= 0 || !target.configured() {
		return nil
	}
	args := []string{"move", "--pid", strconv.Itoa(pid)}
	args = append(args, target.args()...)
	cmd := exec.CommandContext(ctx, helper, args...)
	output, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("virtual desktop helper failed: %s: %w", strings.TrimSpace(string(output)), err)
	}
	return nil
}

func moveWindowToDesktopWithRetry(ctx context.Context, helper string, pid int, target desktopTarget) error {
	var lastErr error
	for {
		if err := moveWindowToDesktop(ctx, helper, pid, target); err == nil {
			return nil
		} else {
			lastErr = err
		}
		select {
		case <-ctx.Done():
			return fmt.Errorf("move window before readiness: %w", lastErr)
		case <-time.After(500 * time.Millisecond):
		}
	}
}

func doctorDesktopHelper(ctx context.Context, helper string, target desktopTarget) (*desktopDoctorResult, error) {
	if helper == "" {
		return nil, fmt.Errorf("virtual desktop helper is not configured")
	}
	args := []string{"doctor"}
	args = append(args, target.args()...)
	cmd := exec.CommandContext(ctx, helper, args...)
	output, err := cmd.CombinedOutput()
	if err != nil {
		return nil, fmt.Errorf("virtual desktop helper doctor failed: %s: %w", strings.TrimSpace(string(output)), err)
	}
	result, err := decodeDesktopDoctorOutput(output)
	if err != nil {
		return nil, err
	}
	if target.configured() && result.SelectedDesktop == nil {
		return nil, fmt.Errorf("virtual desktop helper did not resolve %s", target.description())
	}
	return result, nil
}

func decodeDesktopDoctorOutput(output []byte) (*desktopDoctorResult, error) {
	lines := strings.Split(strings.TrimSpace(string(output)), "\n")
	for i := len(lines) - 1; i >= 0; i-- {
		line := strings.TrimSpace(lines[i])
		if line == "" {
			continue
		}
		var result desktopDoctorResult
		if err := json.Unmarshal([]byte(line), &result); err == nil {
			return &result, nil
		}
	}
	return nil, fmt.Errorf("decode virtual desktop helper doctor output: no JSON object in %q", strings.TrimSpace(string(output)))
}
