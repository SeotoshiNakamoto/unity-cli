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
	DesktopCount   int `json:"desktopCount"`
	CurrentDesktop int `json:"currentDesktop"`
}

func moveWindowToDesktop(ctx context.Context, helper string, pid, desktopIndex int) error {
	if helper == "" || desktopIndex < 0 || pid <= 0 {
		return nil
	}
	cmd := exec.CommandContext(ctx, helper,
		"move", "--pid", strconv.Itoa(pid), "--desktop", strconv.Itoa(desktopIndex))
	output, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("virtual desktop helper failed: %s: %w", strings.TrimSpace(string(output)), err)
	}
	return nil
}

func moveWindowToDesktopWithRetry(ctx context.Context, helper string, pid, desktopIndex int) error {
	var lastErr error
	for {
		if err := moveWindowToDesktop(ctx, helper, pid, desktopIndex); err == nil {
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

func doctorDesktopHelper(ctx context.Context, helper string, desktopIndex int) error {
	if helper == "" {
		return fmt.Errorf("virtual desktop helper is not configured")
	}
	cmd := exec.CommandContext(ctx, helper, "doctor")
	output, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("virtual desktop helper doctor failed: %s: %w", strings.TrimSpace(string(output)), err)
	}
	var result desktopDoctorResult
	if err := json.Unmarshal(output, &result); err != nil {
		return fmt.Errorf("decode virtual desktop helper doctor output: %w", err)
	}
	if result.DesktopCount <= desktopIndex {
		return fmt.Errorf("desktop index %d is unavailable; desktop count is %d", desktopIndex, result.DesktopCount)
	}
	return nil
}
