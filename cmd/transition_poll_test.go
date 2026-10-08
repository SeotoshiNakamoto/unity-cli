package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestPlayModePendingPollsFreshResultsWithoutResend(t *testing.T) {
	home := t.TempDir()
	t.Setenv("USERPROFILE", home)
	t.Setenv("HOME", home)
	project := t.TempDir()
	if err := os.MkdirAll(filepath.Join(project, "Assets"), 0o755); err != nil {
		t.Fatal(err)
	}
	instances := filepath.Join(home, ".unity-cli", "instances")
	status := filepath.Join(home, ".unity-cli", "status")
	for _, dir := range []string{instances, status} {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			t.Fatal(err)
		}
	}
	const port = 34567
	inst, err := json.Marshal(client.Instance{Port: port, PID: os.Getpid(), ProjectPath: project, State: "ready", Timestamp: time.Now().UnixMilli()})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(instances, "test.json"), inst, 0o644); err != nil {
		t.Fatal(err)
	}
	resultPath := filepath.Join(status, "test-results-34567.json")
	if err := os.WriteFile(resultPath, []byte(`{"success":true,"message":"STALE"}`), 0o644); err != nil {
		t.Fatal(err)
	}
	calls := 0
	send := func(string, interface{}) (*client.CommandResponse, error) {
		calls++
		if _, err := os.Stat(resultPath); !os.IsNotExist(err) {
			t.Fatal("old result was not removed before dispatch")
		}
		if err := os.WriteFile(resultPath, []byte(`{"success":true,"message":"fresh results","data":{"passed":1}}`), 0o644); err != nil {
			t.Fatal(err)
		}
		return &client.CommandResponse{TransitionPending: true, Message: "outcome is unknown"}, nil
	}
	response, err := testCmd([]string{"--mode", "PlayMode"}, send, port)
	if err != nil || response == nil || !response.Success || response.Message != "fresh results" || calls != 1 {
		t.Fatalf("%#v %v sends=%d", response, err, calls)
	}
}
