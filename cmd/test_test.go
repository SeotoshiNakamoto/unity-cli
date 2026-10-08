package cmd

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestWaitForPlayModeCleanupWaitsForReadyEditorAndBootstrapDeletion(t *testing.T) {
	project := t.TempDir()
	assets := filepath.Join(project, "Assets")
	if err := os.MkdirAll(assets, 0o755); err != nil {
		t.Fatal(err)
	}
	artifact := filepath.Join(assets, "InitTestScene592d22c6-9145-49d7-a6bc-694f1b6686b2.unity")
	if err := os.WriteFile(artifact, []byte("generated\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	calls := 0
	readStatus := func(int) (*client.Instance, error) {
		calls++
		if calls == 2 {
			if err := os.Remove(artifact); err != nil {
				t.Fatal(err)
			}
		}
		state := "playing"
		if calls >= 3 {
			state = "ready"
		}
		return &client.Instance{State: state, ProjectPath: project, PID: os.Getpid()}, nil
	}

	if err := waitForPlayModeCleanupWith(8091, time.Second, time.Millisecond, readStatus); err != nil {
		t.Fatal(err)
	}
	if calls < 3 {
		t.Fatalf("cleanup returned before editor became ready: calls=%d", calls)
	}
}

func TestPlayModeRunStartedAcceptsDomainReloadConnectionClose(t *testing.T) {
	for _, response := range []*client.CommandResponse{
		{Success: true, Message: "running"},
		{TransitionPending: true, Message: "outcome is unknown"},
	} {
		if !playModeRunStarted(response) {
			t.Fatalf("expected PlayMode run to start for %#v", response)
		}
	}
	for _, response := range []*client.CommandResponse{
		nil,
		{Success: false, Message: "running"},
		{Success: true, Message: "another response"},
	} {
		if playModeRunStarted(response) {
			t.Fatalf("unexpected PlayMode run start for %#v", response)
		}
	}
}

func TestWaitForPlayModeCleanupReportsLeftoverBootstrapScene(t *testing.T) {
	project := t.TempDir()
	assets := filepath.Join(project, "Assets")
	if err := os.MkdirAll(assets, 0o755); err != nil {
		t.Fatal(err)
	}
	name := "InitTestScene592d22c6-9145-49d7-a6bc-694f1b6686b2.unity.meta"
	if err := os.WriteFile(filepath.Join(assets, name), []byte("generated\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	readStatus := func(int) (*client.Instance, error) {
		return &client.Instance{State: "ready", ProjectPath: project, PID: os.Getpid()}, nil
	}

	err := waitForPlayModeCleanupWith(8091, 5*time.Millisecond, time.Millisecond, readStatus)
	if err == nil || !strings.Contains(err.Error(), name) {
		t.Fatalf("expected leftover artifact error, got %v", err)
	}
}

func TestPlayModeBootstrapArtifactsIgnoresSimilarUserSceneNames(t *testing.T) {
	project := t.TempDir()
	assets := filepath.Join(project, "Assets")
	if err := os.MkdirAll(assets, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{
		"InitTestScene.unity",
		"InitTestScene-not-a-guid.unity",
		"MyInitTestScene592d22c6-9145-49d7-a6bc-694f1b6686b2.unity",
	} {
		if err := os.WriteFile(filepath.Join(assets, name), []byte("user\n"), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	artifacts, err := playModeBootstrapArtifacts(project)
	if err != nil {
		t.Fatal(err)
	}
	if len(artifacts) != 0 {
		t.Fatalf("similar user scenes were classified as generated: %v", artifacts)
	}
}
