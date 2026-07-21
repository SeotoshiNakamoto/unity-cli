package slot

import (
	"context"
	"path/filepath"
	"strings"
	"testing"
)

func TestNormalizeExactProjectPathDoesNotCollapseSameBasename(t *testing.T) {
	left := normalizeExactProjectPath(filepath.Join(t.TempDir(), "source", "client"))
	right := normalizeExactProjectPath(filepath.Join(t.TempDir(), "verify", "client"))
	if strings.EqualFold(left, right) {
		t.Fatalf("different worktrees normalized to the same path: %s", left)
	}
}

func TestShutdownUnityWhenIdleIgnoresAbsentEditor(t *testing.T) {
	worktree := t.TempDir()
	if err := shutdownUnityWhenIdle(context.Background(), SlotConfig{
		Worktree:      worktree,
		ProjectSubdir: "client",
	}); err != nil {
		t.Fatal(err)
	}
}
