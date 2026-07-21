package slot

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"time"
)

const ProjectConfigName = ".unity-slot.json"

type AgentConfig struct {
	Version              int          `json:"version"`
	Endpoint             string       `json:"endpoint,omitempty"`
	StatePath            string       `json:"statePath,omitempty"`
	LogDir               string       `json:"logDir,omitempty"`
	PollIntervalMS       int          `json:"pollIntervalMs,omitempty"`
	StickyFailureSeconds int          `json:"stickyFailureSeconds,omitempty"`
	Slots                []SlotConfig `json:"slots"`
	configPath           string
}

type SlotConfig struct {
	ID                     string `json:"id"`
	Project                string `json:"project"`
	Worktree               string `json:"worktree"`
	ProjectSubdir          string `json:"projectSubdir,omitempty"`
	UnityCLI               string `json:"unityCli,omitempty"`
	UnityExecutable        string `json:"unityExecutable,omitempty"`
	DesktopHelper          string `json:"desktopHelper,omitempty"`
	DesktopIndex           *int   `json:"desktopIndex,omitempty"`
	DesktopName            string `json:"desktopName,omitempty"`
	DesktopFallbackFromEnd *int   `json:"desktopFallbackFromEnd,omitempty"`
	ShutdownWhenIdle       bool   `json:"shutdownWhenIdle,omitempty"`
}

type ProjectConfig struct {
	Version            int                    `json:"version"`
	Project            string                 `json:"project"`
	UnityProjectSubdir string                 `json:"unityProjectSubdir"`
	Suites             map[string]SuiteConfig `json:"suites"`
}

type SuiteConfig struct {
	Steps []StepConfig `json:"steps"`
}

type StepConfig struct {
	Name           string   `json:"name"`
	Args           []string `json:"args"`
	TimeoutSeconds int      `json:"timeoutSeconds,omitempty"`
	Assertion      string   `json:"assertion,omitempty"`
}

func DefaultConfigPath() string {
	base, err := os.UserCacheDir()
	if err != nil || base == "" {
		base = os.TempDir()
	}
	return filepath.Join(base, "unity-slot", "config.json")
}

func DefaultEndpoint() string {
	if runtime.GOOS == "windows" {
		return `\\.\pipe\unity-slot`
	}
	return filepath.Join(os.TempDir(), "unity-slot.sock")
}

func LoadAgentConfig(path string) (*AgentConfig, error) {
	if path == "" {
		path = DefaultConfigPath()
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read slot config %s: %w", path, err)
	}
	var config AgentConfig
	if err := json.Unmarshal(data, &config); err != nil {
		return nil, fmt.Errorf("parse slot config %s: %w", path, err)
	}
	if config.Version != ProtocolVersion {
		return nil, fmt.Errorf("unsupported slot config version %d", config.Version)
	}
	if len(config.Slots) == 0 {
		return nil, fmt.Errorf("slot config has no slots")
	}
	config.configPath = path
	base := filepath.Dir(path)
	if config.Endpoint == "" {
		config.Endpoint = DefaultEndpoint()
	}
	if config.StatePath == "" {
		config.StatePath = filepath.Join(base, "state.db")
	}
	if config.LogDir == "" {
		config.LogDir = filepath.Join(base, "logs")
	}
	if config.PollIntervalMS <= 0 {
		config.PollIntervalMS = 500
	}
	if config.StickyFailureSeconds <= 0 {
		config.StickyFailureSeconds = 600
	}
	seen := map[string]bool{}
	for i := range config.Slots {
		s := &config.Slots[i]
		if s.ID == "" || s.Project == "" || s.Worktree == "" {
			return nil, fmt.Errorf("slot %d requires id, project, and worktree", i)
		}
		if seen[s.ID] {
			return nil, fmt.Errorf("duplicate slot id %q", s.ID)
		}
		seen[s.ID] = true
		if s.ProjectSubdir == "" {
			s.ProjectSubdir = "client"
		}
		if s.DesktopIndex != nil && (s.DesktopName != "" || s.DesktopFallbackFromEnd != nil) {
			return nil, fmt.Errorf("slot %q desktopIndex cannot be combined with desktopName/desktopFallbackFromEnd", s.ID)
		}
		if s.DesktopFallbackFromEnd != nil && *s.DesktopFallbackFromEnd <= 0 {
			return nil, fmt.Errorf("slot %q desktopFallbackFromEnd must be positive", s.ID)
		}
	}
	return &config, nil
}

func (c *AgentConfig) ConfigPath() string { return c.configPath }

func (c *AgentConfig) PollInterval() time.Duration {
	return time.Duration(c.PollIntervalMS) * time.Millisecond
}

func (c *AgentConfig) StickyFailureDuration() time.Duration {
	return time.Duration(c.StickyFailureSeconds) * time.Second
}

func LoadProjectConfig(path string) (*ProjectConfig, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read project slot config %s: %w", path, err)
	}
	return ParseProjectConfig(data)
}

func ParseProjectConfig(data []byte) (*ProjectConfig, error) {
	var config ProjectConfig
	if err := json.Unmarshal(data, &config); err != nil {
		return nil, fmt.Errorf("parse project slot config: %w", err)
	}
	if config.Version != ProtocolVersion {
		return nil, fmt.Errorf("unsupported project slot config version %d", config.Version)
	}
	if config.Project == "" || config.UnityProjectSubdir == "" {
		return nil, fmt.Errorf("project slot config requires project and unityProjectSubdir")
	}
	if len(config.Suites) == 0 {
		return nil, fmt.Errorf("project slot config has no suites")
	}
	for name, suite := range config.Suites {
		if len(suite.Steps) == 0 {
			return nil, fmt.Errorf("suite %q has no steps", name)
		}
		for i, step := range suite.Steps {
			if step.Name == "" || len(step.Args) == 0 {
				return nil, fmt.Errorf("suite %q step %d requires name and args", name, i)
			}
		}
	}
	return &config, nil
}
