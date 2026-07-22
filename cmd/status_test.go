package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func writeInstanceFile(t *testing.T, inst client.Instance) string {
	t.Helper()
	home := t.TempDir()
	dir := filepath.Join(home, ".unity-cli", "instances")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatalf("failed to create instances dir: %v", err)
	}
	data, err := json.Marshal(inst)
	if err != nil {
		t.Fatalf("failed to marshal instance: %v", err)
	}
	// Use a fixed filename for testing
	path := filepath.Join(dir, "test.json")
	if err := os.WriteFile(path, data, 0644); err != nil {
		t.Fatalf("failed to write instance file: %v", err)
	}
	return home
}

func TestReadStatus_ValidFile(t *testing.T) {
	want := client.Instance{
		State:        "ready",
		ProjectPath:  "/home/user/MyProject",
		Port:         8090,
		PID:          os.Getpid(),
		UnityVersion: "6000.3.10f1",
		Timestamp:    1000000,
	}

	home := writeInstanceFile(t, want)
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)

	got, err := readStatus(8090)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.State != want.State {
		t.Errorf("State: got %q, want %q", got.State, want.State)
	}
	if got.Port != want.Port {
		t.Errorf("Port: got %d, want %d", got.Port, want.Port)
	}
	if got.ProjectPath != want.ProjectPath {
		t.Errorf("ProjectPath: got %q, want %q", got.ProjectPath, want.ProjectPath)
	}
}

func TestReadStatus_MissingFile(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	_, err := readStatus(9999)
	if err == nil {
		t.Error("expected error for missing status file")
	}
}

func TestReadStatus_InvalidJSON(t *testing.T) {
	home := t.TempDir()
	dir := filepath.Join(home, ".unity-cli", "instances")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatalf("failed to create dir: %v", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "test.json"), []byte("not json"), 0644); err != nil {
		t.Fatalf("failed to write file: %v", err)
	}
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)

	_, err := readStatus(8090)
	if err == nil {
		t.Error("expected error for invalid JSON")
	}
}

func TestWaitForAlive_FollowsProjectToNewPort(t *testing.T) {
	project := filepath.Join(t.TempDir(), "Game")
	want := client.Instance{
		State:       "ready",
		ProjectPath: project,
		Port:        8096,
		PID:         os.Getpid(),
		Timestamp:   time.Now().UnixMilli(),
	}

	home := writeInstanceFile(t, want)
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)

	staleSelection := &client.Instance{
		State:       "reloading",
		ProjectPath: project,
		Port:        8094,
		PID:         os.Getpid(),
		Timestamp:   time.Now().Add(-time.Minute).UnixMilli(),
	}
	got, err := waitForAliveWithProbe(staleSelection, project, 0, 1000, func(instance *client.Instance) bool {
		return instance.Port == 8096
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Port != 8096 {
		t.Fatalf("Port: got %d, want 8096", got.Port)
	}
}

func TestCompilationBarrierRejectsReadyUntilRequestedCycleAndStableReconnect(t *testing.T) {
	barrier := compilationBarrier{fenceTimestamp: 100}

	assertPending := func(status client.Instance, reachable bool) {
		t.Helper()
		if done, _ := barrier.observe(&status, reachable); done {
			t.Fatalf("unexpected completion for status %+v reachable=%t", status, reachable)
		}
	}

	assertPending(client.Instance{State: "ready", Timestamp: 100}, true) // stale heartbeat
	assertPending(client.Instance{State: "ready", Timestamp: 101}, true) // pre-compile ready race
	assertPending(client.Instance{State: "compiling", Timestamp: 102}, false)
	assertPending(client.Instance{State: "ready", Timestamp: 103}, true)  // first ready heartbeat
	assertPending(client.Instance{State: "ready", Timestamp: 103}, true)  // same heartbeat again
	assertPending(client.Instance{State: "ready", Timestamp: 104}, false) // connector not reachable
	assertPending(client.Instance{State: "ready", Timestamp: 105}, true)  // stable streak restarts

	done, hasErrors := barrier.observe(&client.Instance{State: "ready", Timestamp: 106}, true)
	if !done || hasErrors {
		t.Fatalf("expected stable successful completion, done=%t hasErrors=%t", done, hasErrors)
	}
}

func TestCompilationBarrierPreservesCompileErrorAcrossStableReadyHeartbeats(t *testing.T) {
	barrier := compilationBarrier{fenceTimestamp: 200}
	if done, _ := barrier.observe(&client.Instance{State: "reloading", Timestamp: 201}, false); done {
		t.Fatal("reloading must not complete the barrier")
	}
	if done, _ := barrier.observe(&client.Instance{State: "ready", Timestamp: 202, CompileErrors: true}, true); done {
		t.Fatal("one ready heartbeat must not complete the barrier")
	}
	done, hasErrors := barrier.observe(&client.Instance{State: "ready", Timestamp: 203}, true)
	if !done || !hasErrors {
		t.Fatalf("expected stable completion with compile errors, done=%t hasErrors=%t", done, hasErrors)
	}
}
