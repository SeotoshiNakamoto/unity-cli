package cmd

import (
	"fmt"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestInterruptedEditorTransitionVerifiesWithoutResend(t *testing.T) {
	for _, action := range []string{"play", "stop"} {
		t.Run(action, func(t *testing.T) {
			sends, reads := 0, 0
			send := func(string, interface{}) (*client.CommandResponse, error) {
				sends++
				return &client.CommandResponse{TransitionPending: true}, nil
			}
			reader := func() (*client.Instance, error) {
				reads++
				state := "reloading"
				if reads > 1 {
					state = "ready"
					if action == "play" {
						state = "playing"
					}
				}
				return &client.Instance{State: state, Timestamp: time.Now().UnixMilli(), PID: os.Getpid()}, nil
			}
			reach := func(*client.Instance) bool { return true }
			result, err := sendEditorTransitionWith(send, action, true, reader, reach, time.Second, 2*time.Millisecond)
			if err != nil || result == nil || !result.Success || result.TransitionPending || sends != 1 || reads < 2 {
				t.Fatalf("%#v %v sends=%d reads=%d", result, err, sends, reads)
			}
		})
	}
}

func TestNotExecutedEditorTransitionFailsImmediately(t *testing.T) {
	reads := 0
	_, err := sendEditorTransitionWith(func(string, interface{}) (*client.CommandResponse, error) {
		return nil, fmt.Errorf("HTTP 503: Command was not executed")
	}, "play", false, func() (*client.Instance, error) { reads++; return nil, nil }, nil, time.Second, time.Millisecond)
	if err == nil || reads != 0 {
		t.Fatalf("error=%v reads=%d", err, reads)
	}
}

func TestEditorTransitionRejectsStaleAndWrongState(t *testing.T) {
	for _, state := range []string{"playing", "ready", "reloading"} {
		reader := func() (*client.Instance, error) {
			return &client.Instance{State: state, Timestamp: time.Now().Add(-time.Hour).UnixMilli(), PID: os.Getpid()}, nil
		}
		err := waitForEditorTransition("play", 0, 0, 5*time.Millisecond, time.Millisecond, reader, func(*client.Instance) bool { return true })
		if err == nil || !strings.Contains(err.Error(), "outcome remains unknown") {
			t.Fatal(err)
		}
	}
}

func TestQuitCannotSucceedWithLiveOrUnknownProcess(t *testing.T) {
	for _, pid := range []int{0, os.Getpid()} {
		err := waitForEditorTransition("quit", pid, 0, 5*time.Millisecond, time.Millisecond, func() (*client.Instance, error) { return nil, fmt.Errorf("missing heartbeat") }, nil)
		if err == nil {
			t.Fatalf("quit accepted for pid %d", pid)
		}
	}
}
