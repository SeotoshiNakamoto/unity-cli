package slot

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

type BootstrapOptions struct {
	ConfigPath       string
	Repository       string
	SlotRoot         string
	SlotCount        int
	UnityExecutable  string
	UnityCLI         string
	DesktopHelper    string
	DesktopStart     int
	ForceConfigWrite bool
}

func Bootstrap(ctx context.Context, options BootstrapOptions) (*AgentConfig, error) {
	if options.ConfigPath == "" {
		options.ConfigPath = DefaultConfigPath()
	}
	if options.Repository == "" || options.SlotRoot == "" || options.UnityExecutable == "" {
		return nil, fmt.Errorf("init requires --repo, --slot-root, and --unity")
	}
	if options.SlotCount <= 0 {
		options.SlotCount = 2
	}
	if _, err := os.Stat(options.ConfigPath); err == nil && !options.ForceConfigWrite {
		return nil, fmt.Errorf("config already exists: %s (use --force only to replace it)", options.ConfigPath)
	} else if err != nil && !os.IsNotExist(err) {
		return nil, err
	}
	repository, err := resolveRepository(ctx, options.Repository)
	if err != nil {
		return nil, err
	}
	projectConfig, err := LoadProjectConfig(filepath.Join(repository, ProjectConfigName))
	if err != nil {
		return nil, err
	}
	slotRoot, err := filepath.Abs(options.SlotRoot)
	if err != nil {
		return nil, err
	}
	if samePath(slotRoot, repository) {
		return nil, fmt.Errorf("slot root cannot be the source repository")
	}
	if err := os.MkdirAll(slotRoot, 0o755); err != nil {
		return nil, fmt.Errorf("create slot root: %w", err)
	}
	sourceCommon, err := gitCommonDir(ctx, repository)
	if err != nil {
		return nil, err
	}

	config := &AgentConfig{
		Version:              ProtocolVersion,
		Endpoint:             DefaultEndpoint(),
		PollIntervalMS:       500,
		StickyFailureSeconds: 600,
		Slots:                make([]SlotConfig, 0, options.SlotCount),
	}
	base := filepath.Dir(options.ConfigPath)
	config.StatePath = filepath.Join(base, "state.db")
	config.LogDir = filepath.Join(base, "logs")
	config.configPath = options.ConfigPath

	for i := 0; i < options.SlotCount; i++ {
		id := fmt.Sprintf("%s-verify-%02d", projectConfig.Project, i+1)
		worktree := filepath.Join(slotRoot, id)
		if _, statErr := os.Stat(worktree); os.IsNotExist(statErr) {
			if _, err := gitOutput(ctx, repository, "worktree", "add", "--detach", worktree, "HEAD"); err != nil {
				return nil, fmt.Errorf("create validation worktree %s: %w", worktree, err)
			}
		} else if statErr != nil {
			return nil, statErr
		}
		common, err := gitCommonDir(ctx, worktree)
		if err != nil {
			return nil, fmt.Errorf("validate existing worktree %s: %w", worktree, err)
		}
		if !samePath(sourceCommon, common) {
			return nil, fmt.Errorf("existing slot %s does not share the source Git object store", worktree)
		}
		slotConfig := SlotConfig{
			ID:              id,
			Project:         projectConfig.Project,
			Worktree:        worktree,
			ProjectSubdir:   projectConfig.UnityProjectSubdir,
			UnityCLI:        options.UnityCLI,
			UnityExecutable: options.UnityExecutable,
			DesktopHelper:   options.DesktopHelper,
		}
		if options.DesktopStart >= 0 {
			index := options.DesktopStart + i
			slotConfig.DesktopIndex = &index
		}
		config.Slots = append(config.Slots, slotConfig)
	}

	if err := os.MkdirAll(filepath.Dir(options.ConfigPath), 0o755); err != nil {
		return nil, err
	}
	data, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return nil, err
	}
	tempPath := options.ConfigPath + ".tmp"
	if err := os.WriteFile(tempPath, append(data, '\n'), 0o600); err != nil {
		return nil, err
	}
	if err := os.Rename(tempPath, options.ConfigPath); err != nil {
		_ = os.Remove(tempPath)
		return nil, err
	}
	return config, nil
}
