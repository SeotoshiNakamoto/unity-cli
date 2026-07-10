package cmd

import (
	"os"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestInstancesKillRequiresForce(t *testing.T) {
	err := instancesCmd([]string{"kill"}, "/project", 0, 100)
	if err == nil {
		t.Fatal("expected --force error")
	}
}

func TestInstancesKillRequiresExplicitSelector(t *testing.T) {
	err := instancesCmd([]string{"kill", "--force"}, "", 0, 100)
	if err == nil {
		t.Fatal("expected selector error")
	}
}

func TestInstancesWaitRequiresExplicitSelector(t *testing.T) {
	err := instancesCmd([]string{"wait"}, "", 0, 100)
	if err == nil {
		t.Fatal("expected selector error")
	}
}

func TestInstancesKillUsesSelectedPID(t *testing.T) {
	stubPID := 0
	originalKiller := killInstanceProcess
	originalDeadCheck := isInstanceProcessDead
	killInstanceProcess = func(pid int) error {
		stubPID = pid
		return nil
	}
	isInstanceProcessDead = func(pid int) bool { return pid == os.Getpid() }
	t.Cleanup(func() {
		killInstanceProcess = originalKiller
		isInstanceProcessDead = originalDeadCheck
	})

	clientHome := writeInstanceFile(t, client.Instance{
		State:       "ready",
		ProjectPath: "/projects/game",
		Port:        8090,
		PID:         os.Getpid(),
		Timestamp:   1000,
	})
	t.Setenv("HOME", clientHome)
	t.Setenv("USERPROFILE", clientHome)

	if err := instancesCmd([]string{"kill", "--force"}, "/projects/game", 0, 100); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if stubPID != os.Getpid() {
		t.Fatalf("killed pid %d, want %d", stubPID, os.Getpid())
	}
}
