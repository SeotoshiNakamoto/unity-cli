package slot

import (
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
