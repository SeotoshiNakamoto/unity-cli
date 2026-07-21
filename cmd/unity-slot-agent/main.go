package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"syscall"

	"github.com/youngwoocho02/unity-cli/internal/slot"
)

var Version = "dev"

func main() {
	configPath := flag.String("config", slot.DefaultConfigPath(), "Path to unity-slot agent config")
	repository := flag.String("repo", "", "Source Git repository for init")
	slotRoot := flag.String("slot-root", "", "Directory that owns validation worktrees")
	slotCount := flag.Int("slots", 2, "Number of validation worktrees to create")
	unityExecutable := flag.String("unity", "", "Unity Editor executable for init")
	unityCLI := flag.String("unity-cli", "", "unity-cli executable used by workers")
	desktopHelper := flag.String("desktop-helper", "", "unity-slot-desktop executable")
	desktopStart := flag.Int("desktop-start", -1, "Zero-based desktop index for the first slot")
	force := flag.Bool("force", false, "Replace an existing agent config during init")
	flag.Parse()
	action := "run"
	if flag.NArg() > 0 {
		action = flag.Arg(0)
	}
	if action == "version" {
		fmt.Println("unity-slot-agent " + Version)
		return
	}
	if action == "init" {
		config, err := slot.Bootstrap(context.Background(), slot.BootstrapOptions{
			ConfigPath: *configPath, Repository: *repository, SlotRoot: *slotRoot,
			SlotCount: *slotCount, UnityExecutable: *unityExecutable, UnityCLI: *unityCLI,
			DesktopHelper: *desktopHelper, DesktopStart: *desktopStart, ForceConfigWrite: *force,
		})
		if err != nil {
			fatal(err)
		}
		data, _ := json.MarshalIndent(config, "", "  ")
		fmt.Println(string(data))
		return
	}
	if action == "install" {
		if _, err := slot.LoadAgentConfig(*configPath); err != nil {
			fatal(err)
		}
		executable, err := os.Executable()
		if err != nil {
			fatal(err)
		}
		if err := slot.InstallStartup(executable, *configPath); err != nil {
			fatal(err)
		}
		fmt.Println("Installed and started unity-slot-agent login launcher")
		return
	}
	if action == "uninstall" {
		if err := slot.UninstallStartup(); err != nil {
			fatal(err)
		}
		fmt.Println("Removed unity-slot-agent login task")
		return
	}
	config, err := slot.LoadAgentConfig(*configPath)
	if err != nil {
		fatal(err)
	}
	agent, err := slot.NewAgent(config, Version)
	if err != nil {
		fatal(err)
	}
	defer func() { _ = agent.Close() }()

	switch action {
	case "run":
		ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
		defer cancel()
		fmt.Printf("unity-slot-agent %s listening on %s with %d slot(s)\n", Version, config.Endpoint, len(config.Slots))
		if err := agent.Serve(ctx); err != nil {
			fatal(err)
		}
	case "doctor":
		report := agent.Doctor(context.Background())
		data, _ := json.MarshalIndent(report, "", "  ")
		fmt.Println(string(data))
		if !report.Healthy {
			os.Exit(1)
		}
	default:
		fatal(fmt.Errorf("unknown action %q; available: init, install, uninstall, run, doctor, version", action))
	}
}

func fatal(err error) {
	fmt.Fprintln(os.Stderr, "Error:", err)
	os.Exit(1)
}
