package slot

import (
	"bytes"
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// CreateSnapshot creates an unreferenced validation commit without changing the
// current branch, worktree, or real Git index. Ignored files stay excluded.
func CreateSnapshot(ctx context.Context, path string) (string, error) {
	repo, err := resolveRepository(ctx, path)
	if err != nil {
		return "", err
	}
	status, err := gitOutput(ctx, repo, "status", "--porcelain=v1", "--untracked-files=all")
	if err != nil {
		return "", err
	}
	if strings.TrimSpace(status) == "" {
		return resolveCommit(ctx, repo, "HEAD")
	}
	tempFile, err := os.CreateTemp("", "unity-slot-index-*")
	if err != nil {
		return "", fmt.Errorf("create temporary Git index path: %w", err)
	}
	indexPath := tempFile.Name()
	if err := tempFile.Close(); err != nil {
		return "", err
	}
	if err := os.Remove(indexPath); err != nil {
		return "", err
	}
	defer func() { _ = os.Remove(indexPath) }()

	env := append(os.Environ(), "GIT_INDEX_FILE="+indexPath)
	if _, err := gitOutputEnv(ctx, repo, env, "read-tree", "HEAD"); err != nil {
		return "", err
	}
	if _, err := gitOutputEnv(ctx, repo, env, "add", "-A", "--", "."); err != nil {
		return "", err
	}
	tree, err := gitOutputEnv(ctx, repo, env, "write-tree")
	if err != nil {
		return "", err
	}
	parent, err := resolveCommit(ctx, repo, "HEAD")
	if err != nil {
		return "", err
	}
	commitEnv := append(os.Environ(),
		"GIT_AUTHOR_NAME=unity-slot",
		"GIT_AUTHOR_EMAIL=unity-slot@local",
		"GIT_COMMITTER_NAME=unity-slot",
		"GIT_COMMITTER_EMAIL=unity-slot@local",
	)
	commit, err := gitOutputEnv(ctx, repo, commitEnv, "commit-tree", strings.TrimSpace(tree), "-p", parent, "-m", "unity-slot validation snapshot")
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(commit), nil
}

func resolveRepository(ctx context.Context, path string) (string, error) {
	if path == "" {
		path = "."
	}
	root, err := gitOutput(ctx, path, "rev-parse", "--show-toplevel")
	if err != nil {
		return "", fmt.Errorf("resolve git repository from %s: %w", path, err)
	}
	abs, err := filepath.Abs(strings.TrimSpace(root))
	if err != nil {
		return "", err
	}
	return filepath.Clean(abs), nil
}

func resolveCommit(ctx context.Context, repo, revision string) (string, error) {
	if revision == "" {
		revision = "HEAD"
	}
	value, err := gitOutput(ctx, repo, "rev-parse", "--verify", revision+"^{commit}")
	if err != nil {
		return "", fmt.Errorf("resolve commit %q: %w", revision, err)
	}
	return strings.TrimSpace(value), nil
}

func gitCommonDir(ctx context.Context, repo string) (string, error) {
	value, err := gitOutput(ctx, repo, "rev-parse", "--path-format=absolute", "--git-common-dir")
	if err != nil {
		return "", err
	}
	return filepath.Clean(strings.TrimSpace(value)), nil
}

func projectConfigAtCommit(ctx context.Context, repo, sha string) (*ProjectConfig, error) {
	value, err := gitOutput(ctx, repo, "show", sha+":"+ProjectConfigName)
	if err != nil {
		return nil, fmt.Errorf("%s is missing from commit %s: %w", ProjectConfigName, shortSHA(sha), err)
	}
	return ParseProjectConfig([]byte(value))
}

func worktreeClean(ctx context.Context, repo string) error {
	value, err := gitOutput(ctx, repo, "status", "--porcelain=v1", "--untracked-files=all")
	if err != nil {
		return err
	}
	if strings.TrimSpace(value) != "" {
		return fmt.Errorf("validation worktree is dirty:\n%s", strings.TrimSpace(value))
	}
	return nil
}

func restoreValidationWorktree(ctx context.Context, repo, sourceRepo string) error {
	if samePath(repo, sourceRepo) {
		return fmt.Errorf("refusing to clean the source worktree as a validation lane")
	}
	branch, err := gitOutput(ctx, repo, "rev-parse", "--abbrev-ref", "HEAD")
	if err != nil {
		return err
	}
	if strings.TrimSpace(branch) != "HEAD" {
		return fmt.Errorf("validation worktree must be detached, found branch %q", strings.TrimSpace(branch))
	}
	status, err := gitOutput(ctx, repo, "status", "--porcelain=v1", "--untracked-files=all")
	if err != nil {
		return err
	}
	for _, line := range strings.Split(strings.TrimSpace(status), "\n") {
		if strings.HasPrefix(line, "?? ") {
			return fmt.Errorf("validation worktree has untracked files; refusing automatic cleanup:\n%s", strings.TrimSpace(status))
		}
	}
	if strings.TrimSpace(status) == "" {
		return nil
	}
	if _, err := gitOutput(ctx, repo, "restore", "--source=HEAD", "--staged", "--worktree", "--", "."); err != nil {
		return fmt.Errorf("restore validation worktree churn: %w", err)
	}
	return worktreeClean(ctx, repo)
}

func checkoutDetached(ctx context.Context, repo, sha string) error {
	if err := worktreeClean(ctx, repo); err != nil {
		return err
	}
	_, err := gitOutput(ctx, repo, "checkout", "--detach", sha)
	if err != nil {
		return fmt.Errorf("checkout %s in %s: %w", shortSHA(sha), repo, err)
	}
	actual, err := resolveCommit(ctx, repo, "HEAD")
	if err != nil {
		return err
	}
	if !strings.EqualFold(actual, sha) {
		return fmt.Errorf("validation worktree HEAD mismatch: got %s, want %s", actual, sha)
	}
	return nil
}

func gitOutput(ctx context.Context, repo string, args ...string) (string, error) {
	return gitOutputEnv(ctx, repo, nil, args...)
}

func gitOutputEnv(ctx context.Context, repo string, env []string, args ...string) (string, error) {
	cmd := exec.CommandContext(ctx, "git", append([]string{"-C", repo}, args...)...)
	if env != nil {
		cmd.Env = env
	}
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	if err := cmd.Run(); err != nil {
		detail := strings.TrimSpace(stderr.String())
		if detail == "" {
			detail = strings.TrimSpace(stdout.String())
		}
		if detail != "" {
			return "", fmt.Errorf("git %s: %s: %w", strings.Join(args, " "), detail, err)
		}
		return "", fmt.Errorf("git %s: %w", strings.Join(args, " "), err)
	}
	return stdout.String(), nil
}

func samePath(left, right string) bool {
	leftAbs, leftErr := filepath.Abs(left)
	rightAbs, rightErr := filepath.Abs(right)
	if leftErr != nil || rightErr != nil {
		return false
	}
	return strings.EqualFold(filepath.Clean(leftAbs), filepath.Clean(rightAbs))
}

func shortSHA(sha string) string {
	if len(sha) > 12 {
		return sha[:12]
	}
	return sha
}
