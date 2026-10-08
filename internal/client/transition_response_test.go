package client

import (
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
)

func TestStartedResponseRequiresStructuredEvidenceAndTransition(t *testing.T) {
	for _, state := range []string{"started", "not_started", ""} {
		for _, allow := range []bool{false, true} {
			body := fmt.Sprintf(`{"success":false,"message":"Command execution started, but its outcome is unknown","data":{"execution_state":%q}}`, state)
			response, err := decodeResponse(&http.Response{StatusCode: 503, Body: io.NopCloser(strings.NewReader(body))}, "manage_editor", allow)
			if allow && state == "started" {
				if err != nil || response == nil || response.Success || !response.TransitionPending {
					t.Fatalf("missing verification marker: %#v %v", response, err)
				}
			} else if err == nil || response != nil {
				t.Fatalf("unconfirmed response accepted: %s %t %#v", state, allow, response)
			}
		}
	}
}

func TestStartedTransitionSendNeverRetries(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		calls.Add(1)
		w.WriteHeader(503)
		_, _ = io.WriteString(w, `{"success":false,"message":"outcome is unknown","data":{"execution_state":"started"}}`)
	}))
	defer server.Close()
	port, err := strconv.Atoi(strings.TrimPrefix(server.URL, "http://127.0.0.1:"))
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		command string
		params  map[string]interface{}
	}{
		{"manage_editor", map[string]interface{}{"action": "play"}},
		{"manage_editor", map[string]interface{}{"action": "stop"}},
		{"manage_editor", map[string]interface{}{"action": "quit"}},
		{"refresh_unity", map[string]interface{}{"compile": "request"}},
		{"run_tests", map[string]interface{}{"mode": "PlayMode"}},
	} {
		result, err := Send(&Instance{Port: port}, c.command, c.params, 1000)
		if err != nil || result == nil || !result.TransitionPending || result.Success {
			t.Fatalf("%s: %#v %v", c.command, result, err)
		}
	}
	if calls.Load() != 5 {
		t.Fatalf("automatic resend: %d", calls.Load())
	}
}
