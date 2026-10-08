package cmd

import (
	"encoding/json"
	"github.com/youngwoocho02/unity-cli/internal/client"
	"os"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"
)

func TestHeartbeatReadinessStatesAndFreshness(t *testing.T) {
	now := time.Now()
	for _, state := range []string{"ready", "playing", "paused", "compiling", "reloading", "refreshing", "entering_playmode", "stopped", ""} {
		want := state == "ready" || state == "playing" || state == "paused"
		inst := &client.Instance{State: state, Timestamp: now.Add(-100 * time.Millisecond).UnixMilli()}
		if got := heartbeatAcceptsCommands(inst, now, time.Second); got != want {
			t.Errorf("state %q got %t want %t", state, got, want)
		}
		inst.Timestamp = now.Add(-3 * time.Second).UnixMilli()
		if heartbeatAcceptsCommands(inst, now, time.Second) {
			t.Errorf("stale %q accepted", state)
		}
	}
	if heartbeatAcceptsCommands(nil, now, time.Second) {
		t.Fatal("nil accepted")
	}
}

func TestWaitForAliveDoesNotProbeBusyHeartbeat(t *testing.T) {
	for _, state := range []string{"compiling", "reloading"} {
		t.Run(state, func(t *testing.T) {
			inst := client.Instance{State: state, ProjectPath: filepath.Join(t.TempDir(), "Game"), Port: 8090, PID: os.Getpid(), Timestamp: time.Now().UnixMilli()}
			home := writeInstanceFile(t, inst)
			t.Setenv("HOME", home)
			t.Setenv("USERPROFILE", home)
			var probes atomic.Int32
			_, err := waitForAliveWithProbe(&inst, inst.ProjectPath, 0, 30, func(*client.Instance) bool { probes.Add(1); return true })
			if err == nil || probes.Load() != 0 {
				t.Fatalf("busy accepted: %v probes=%d", err, probes.Load())
			}
		})
	}
}

func TestWaitForAliveWaitsBusyToFreshReady(t *testing.T) {
	inst := client.Instance{State: "reloading", ProjectPath: filepath.Join(t.TempDir(), "Game"), Port: 8090, PID: os.Getpid(), Timestamp: time.Now().UnixMilli()}
	home := writeInstanceFile(t, inst)
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	done := make(chan error, 1)
	go func() {
		time.Sleep(150 * time.Millisecond)
		next := inst
		next.State = "ready"
		next.Timestamp = time.Now().UnixMilli()
		data, err := json.Marshal(next)
		if err == nil {
			err = os.WriteFile(filepath.Join(home, ".unity-cli", "instances", "test.json"), data, 0600)
		}
		done <- err
	}()
	start := time.Now()
	got, err := waitForAliveWithProbe(&inst, inst.ProjectPath, 0, 2000, func(*client.Instance) bool { return true })
	if writeErr := <-done; writeErr != nil {
		t.Fatal(writeErr)
	}
	if err != nil || got.State != "ready" || time.Since(start) < 150*time.Millisecond {
		t.Fatalf("premature/bad readiness %#v %v elapsed=%s", got, err, time.Since(start))
	}
}
