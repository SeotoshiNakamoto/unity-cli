package slot

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func TestCreateSnapshotPreservesBranchIndexAndWorktree(t *testing.T) {
	repo := t.TempDir()
	runGitTest(t, repo, "init")
	runGitTest(t, repo, "config", "user.name", "Test")
	runGitTest(t, repo, "config", "user.email", "test@example.com")
	tracked := filepath.Join(repo, "tracked.txt")
	if err := os.WriteFile(tracked, []byte("before\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	runGitTest(t, repo, "add", "tracked.txt")
	runGitTest(t, repo, "commit", "-m", "initial")
	branchBefore := strings.TrimSpace(runGitTest(t, repo, "branch", "--show-current"))
	indexBefore := runGitTest(t, repo, "diff", "--cached")

	if err := os.WriteFile(tracked, []byte("after\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(repo, "new.txt"), []byte("new\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	sha, err := CreateSnapshot(context.Background(), repo)
	if err != nil {
		t.Fatal(err)
	}
	if got := strings.TrimSpace(runGitTest(t, repo, "branch", "--show-current")); got != branchBefore {
		t.Fatalf("branch changed: got %q want %q", got, branchBefore)
	}
	if got := runGitTest(t, repo, "diff", "--cached"); got != indexBefore {
		t.Fatalf("real index changed:\n%s", got)
	}
	if got := runGitTest(t, repo, "show", sha+":tracked.txt"); got != "after\n" {
		t.Fatalf("snapshot tracked content = %q", got)
	}
	if got := runGitTest(t, repo, "show", sha+":new.txt"); got != "new\n" {
		t.Fatalf("snapshot new content = %q", got)
	}
	data, err := os.ReadFile(tracked)
	if err != nil || string(data) != "after\n" {
		t.Fatalf("worktree changed: data=%q err=%v", data, err)
	}
}

func TestRestoreValidationWorktreeOnlyRestoresDetachedTrackedFiles(t *testing.T) {
	repo := t.TempDir()
	runGitTest(t, repo, "init")
	runGitTest(t, repo, "config", "user.name", "Test")
	runGitTest(t, repo, "config", "user.email", "test@example.com")
	tracked := filepath.Join(repo, "tracked.txt")
	if err := os.WriteFile(tracked, []byte("before\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	runGitTest(t, repo, "add", "tracked.txt")
	runGitTest(t, repo, "commit", "-m", "initial")
	runGitTest(t, repo, "checkout", "--detach", "HEAD")
	if err := os.WriteFile(tracked, []byte("unity churn\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := restoreValidationWorktree(context.Background(), repo, filepath.Join(t.TempDir(), "source")); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(tracked)
	if err != nil || strings.TrimSpace(string(data)) != "before" {
		t.Fatalf("tracked file was not restored: data=%q err=%v", data, err)
	}
	if err := os.WriteFile(filepath.Join(repo, "unexpected.txt"), []byte("unexpected\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := restoreValidationWorktree(context.Background(), repo, filepath.Join(t.TempDir(), "source")); err == nil {
		t.Fatal("expected untracked file cleanup refusal")
	}
}

func runGitTest(t *testing.T, repo string, args ...string) string {
	t.Helper()
	cmd := exec.Command("git", append([]string{"-C", repo}, args...)...)
	output, err := cmd.CombinedOutput()
	if err != nil {
		t.Fatalf("git %s: %s: %v", strings.Join(args, " "), output, err)
	}
	return string(output)
}
