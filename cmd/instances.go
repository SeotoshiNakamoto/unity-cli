package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

var (
	killInstanceProcess   = client.KillProcess
	isInstanceProcessDead = client.IsProcessDead
)

func instancesCmd(args []string, project string, port int, timeoutMs int) error {
	action := "list"
	if len(args) > 0 && !strings.HasPrefix(args[0], "--") {
		action = strings.ToLower(args[0])
		args = args[1:]
	}
	flags := parseSubFlags(args)

	switch action {
	case "list":
		return listInstances(flags)
	case "wait":
		return waitForInstance(project, port, flags, timeoutMs)
	case "kill":
		return killInstance(project, port, flags, timeoutMs)
	default:
		return fmt.Errorf("unknown instances action: %s\nAvailable: list, wait, kill", action)
	}
}

func listInstances(flags map[string]string) error {
	var (
		instances []client.Instance
		err       error
	)
	if _, includeAll := flags["all"]; includeAll {
		instances, err = client.ScanInstances()
	} else {
		instances, err = client.ActiveInstances()
	}
	if err != nil {
		return err
	}

	if _, asJSON := flags["json"]; asJSON {
		data, marshalErr := json.MarshalIndent(instances, "", "  ")
		if marshalErr != nil {
			return marshalErr
		}
		fmt.Println(string(data))
		return nil
	}

	if len(instances) == 0 {
		fmt.Println("No Unity instances found.")
		return nil
	}

	w := tabwriter.NewWriter(os.Stdout, 0, 4, 2, ' ', 0)
	_, _ = fmt.Fprintln(w, "STATE\tPORT\tPID\tPROJECT")
	for _, inst := range instances {
		_, _ = fmt.Fprintf(w, "%s\t%d\t%d\t%s\n", inst.State, inst.Port, inst.PID, inst.ProjectPath)
	}
	return w.Flush()
}

func waitForInstance(project string, port int, flags map[string]string, timeoutMs int) error {
	if project == "" && port == 0 {
		return fmt.Errorf("instances wait requires --project <path> or --port <N>")
	}
	desired := strings.ToLower(flags["state"])
	if desired == "" {
		desired = "ready"
	}

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	for {
		var (
			inst *client.Instance
			err  error
		)
		if port > 0 {
			inst, err = client.FindActiveByPort(port)
		} else {
			inst, err = client.FindByProject(project)
		}
		if err == nil && (desired == "any" || strings.EqualFold(inst.State, desired)) {
			fmt.Printf("Unity instance reached %s: %s (port %d, pid %d)\n", inst.State, inst.ProjectPath, inst.Port, inst.PID)
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("timed out waiting for Unity instance state %q", desired)
		}
		time.Sleep(250 * time.Millisecond)
	}
}

func killInstance(project string, port int, flags map[string]string, timeoutMs int) error {
	if _, force := flags["force"]; !force {
		return fmt.Errorf("refusing to kill Unity without --force")
	}
	if project == "" && port == 0 {
		return fmt.Errorf("instances kill requires --project <path> or --port <N>")
	}

	inst, err := client.DiscoverInstance(project, port)
	if err != nil {
		return err
	}
	if inst.PID <= 0 && port > 0 {
		inst, err = client.FindActiveByPort(port)
		if err != nil {
			return err
		}
	}
	if err := killInstanceProcess(inst.PID); err != nil {
		return fmt.Errorf("failed to kill Unity pid %d: %w", inst.PID, err)
	}

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	for time.Now().Before(deadline) {
		if isInstanceProcessDead(inst.PID) {
			fmt.Printf("Killed Unity instance: %s (pid %d)\n", inst.ProjectPath, inst.PID)
			return nil
		}
		time.Sleep(100 * time.Millisecond)
	}
	return fmt.Errorf("timed out waiting for Unity pid %d to exit", inst.PID)
}
