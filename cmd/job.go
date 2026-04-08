package cmd

import (
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func jobCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	var jobID string
	for i, a := range args {
		if (a == "--id" || a == "--job_id") && i+1 < len(args) {
			jobID = args[i+1]
			break
		}
		if !strings.HasPrefix(a, "--") && jobID == "" {
			jobID = a
		}
	}
	if jobID == "" {
		return nil, fmt.Errorf("usage: unity-cli job <job_id>")
	}
	return pollJob(send, jobID)
}

func pollJob(send sendFn, jobID string) (*client.CommandResponse, error) {
	deadline := time.Now().Add(time.Duration(flagTimeout) * time.Millisecond)

	for time.Now().Before(deadline) {
		resp, err := send("job_status", map[string]interface{}{"job_id": jobID})
		if err != nil {
			return nil, err
		}

		if resp.Success && resp.Message == "running" {
			fmt.Fprint(os.Stderr, ".")
			time.Sleep(500 * time.Millisecond)
			continue
		}

		// Completed or failed — return actual result
		fmt.Fprintln(os.Stderr)
		return resp, nil
	}

	return nil, fmt.Errorf("timed out waiting for job %s", jobID)
}
