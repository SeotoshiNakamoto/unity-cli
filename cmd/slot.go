package cmd

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/slot"
)

func slotCmd(args []string, timeoutMS int) error {
	if len(args) == 0 || strings.HasPrefix(args[0], "--") {
		return fmt.Errorf("usage: unity-cli slot <submit|status|result|doctor> [options]")
	}
	action := strings.ToLower(args[0])
	args = args[1:]
	flags := parseSubFlags(args)
	endpoint := flags["endpoint"]
	if endpoint == "" {
		endpoint = os.Getenv("UNITY_SLOT_ENDPOINT")
	}
	client := slot.Client{Endpoint: endpoint, Timeout: 5 * time.Second}

	switch action {
	case "submit":
		return submitSlot(client, flags, timeoutMS)
	case "status":
		jobID := slotFirstPositional(args)
		response, err := client.Do(slot.Request{Action: "status", JobID: jobID})
		if err != nil {
			return err
		}
		return printSlotResponse(response, hasFlag(flags, "json"), false)
	case "result":
		jobID := slotFirstPositional(args)
		if jobID == "" {
			return fmt.Errorf("slot result requires <job-id>")
		}
		return waitForSlotResult(client, jobID, hasFlag(flags, "wait"), hasFlag(flags, "json"), timeoutMS)
	case "doctor":
		response, err := client.Do(slot.Request{Action: "doctor"})
		if err != nil {
			return err
		}
		if hasFlag(flags, "json") {
			return printSlotJSON(response.Doctor)
		}
		printSlotDoctor(response.Doctor)
		if response.Doctor != nil && !response.Doctor.Healthy {
			return fmt.Errorf("one or more validation slots are unhealthy")
		}
		return nil
	default:
		return fmt.Errorf("unknown slot action: %s\nAvailable: submit, status, result, doctor", action)
	}
}

func submitSlot(client slot.Client, flags map[string]string, timeoutMS int) error {
	repository := flags["repo"]
	if repository == "" {
		cwd, err := os.Getwd()
		if err != nil {
			return err
		}
		repository = cwd
	}
	absRepository, err := filepath.Abs(repository)
	if err != nil {
		return err
	}
	revision := flags["sha"]
	if hasFlag(flags, "snapshot") {
		if revision != "" {
			return fmt.Errorf("slot submit accepts either --sha or --snapshot, not both")
		}
		snapshotCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		revision, err = slot.CreateSnapshot(snapshotCtx, absRepository)
		cancel()
		if err != nil {
			return err
		}
	}
	request := slot.SubmitRequest{
		Repository: absRepository,
		SHA:        revision,
		Suite:      flags["suite"],
		Affinity:   flags["affinity"],
	}
	if request.SHA == "" {
		request.SHA = "HEAD"
	}
	if request.Suite == "" {
		request.Suite = "compile"
	}
	response, err := client.Do(slot.Request{Action: "submit", Submit: &request})
	if err != nil {
		return err
	}
	if hasFlag(flags, "json") && !hasFlag(flags, "wait") {
		return printSlotJSON(response.Job)
	}
	if response.Job == nil {
		return fmt.Errorf("slot agent returned no job")
	}
	fmt.Printf("Queued %s: %s suite=%s sha=%s\n", response.Job.ID, response.Job.Project, response.Job.Suite, shortSlotSHA(response.Job.SHA))
	if !hasFlag(flags, "wait") {
		return nil
	}
	return waitForSlotResult(client, response.Job.ID, true, hasFlag(flags, "json"), timeoutMS)
}

func waitForSlotResult(client slot.Client, jobID string, wait, asJSON bool, timeoutMS int) error {
	deadline := time.Now().Add(time.Duration(timeoutMS) * time.Millisecond)
	for {
		response, err := client.Do(slot.Request{Action: "result", JobID: jobID})
		if err != nil {
			return err
		}
		if response.Job == nil {
			return fmt.Errorf("slot agent returned no job")
		}
		job := response.Job
		finished := job.Status == slot.JobPassed || job.Status == slot.JobFailed || job.Status == slot.JobCanceled
		if finished || !wait {
			if asJSON {
				if err := printSlotJSON(job); err != nil {
					return err
				}
			} else {
				printSlotJob(job)
			}
			if job.Status == slot.JobFailed || job.Status == slot.JobCanceled {
				return fmt.Errorf("validation %s: %s", job.Status, job.Error)
			}
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("timed out waiting for validation job %s", jobID)
		}
		time.Sleep(time.Second)
	}
}

func printSlotResponse(response *slot.Response, asJSON, failOnFailed bool) error {
	if asJSON {
		if response.Job != nil {
			return printSlotJSON(response.Job)
		}
		return printSlotJSON(response.Jobs)
	}
	if response.Job != nil {
		printSlotJob(response.Job)
		if failOnFailed && response.Job.Status == slot.JobFailed {
			return fmt.Errorf("validation failed: %s", response.Job.Error)
		}
		return nil
	}
	w := tabwriter.NewWriter(os.Stdout, 0, 4, 2, ' ', 0)
	_, _ = fmt.Fprintln(w, "STATUS\tJOB\tSLOT\tSUITE\tSHA\tCREATED")
	for _, job := range response.Jobs {
		_, _ = fmt.Fprintf(w, "%s\t%s\t%s\t%s\t%s\t%s\n",
			job.Status, job.ID, job.SlotID, job.Suite, shortSlotSHA(job.SHA), job.CreatedAt.Local().Format("01-02 15:04"))
	}
	return w.Flush()
}

func printSlotJob(job *slot.Job) {
	fmt.Printf("Job: %s\nStatus: %s\nProject: %s\nSHA: %s\nSuite: %s\n", job.ID, job.Status, job.Project, job.SHA, job.Suite)
	if job.SlotID != "" {
		fmt.Printf("Slot: %s\n", job.SlotID)
	}
	if job.LogPath != "" {
		fmt.Printf("Log: %s\n", job.LogPath)
	}
	if job.Error != "" {
		fmt.Printf("Error: %s\n", job.Error)
	}
}

func printSlotDoctor(report *slot.DoctorReport) {
	if report == nil {
		fmt.Println("No doctor report returned.")
		return
	}
	fmt.Printf("Agent: %s\nState: %s\n", report.Endpoint, report.StatePath)
	for _, item := range report.Slots {
		state := "healthy"
		if !item.Healthy {
			state = "unhealthy: " + item.Error
		}
		fmt.Printf("- %s [%s] %s (%s)\n", item.ID, item.Project, item.Worktree, state)
	}
}

func printSlotJSON(value any) error {
	data, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	fmt.Println(string(data))
	return nil
}

func slotFirstPositional(args []string) string {
	for i := 0; i < len(args); i++ {
		if strings.HasPrefix(args[i], "--") {
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				i++
			}
			continue
		}
		return args[i]
	}
	return ""
}

func hasFlag(flags map[string]string, name string) bool {
	_, ok := flags[name]
	return ok
}

func shortSlotSHA(sha string) string {
	if len(sha) > 12 {
		return sha[:12]
	}
	return sha
}
