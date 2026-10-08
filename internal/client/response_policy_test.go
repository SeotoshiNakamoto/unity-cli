package client

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
)

func TestEmptyTransitionAllowlist(t *testing.T) {
	cases := []struct {
		command string
		params  map[string]interface{}
		allowed bool
	}{
		{"manage_editor", map[string]interface{}{"action": "play"}, true},
		{"manage_editor", map[string]interface{}{"action": "stop"}, true},
		{"manage_editor", map[string]interface{}{"action": "quit"}, true},
		{"manage_editor", map[string]interface{}{"action": "pause"}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": true}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": false}, true},
		{"manage_editor", map[string]interface{}{"action": "play", "async": "true"}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": "false"}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": 0}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": 1}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": nil}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": []bool{false}}, false},
		{"manage_editor", map[string]interface{}{"action": "play", "async": map[string]bool{}}, false},
		{"refresh_unity", map[string]interface{}{"compile": "request"}, true},
		{"refresh_unity", map[string]interface{}{}, false},
		{"run_tests", map[string]interface{}{"mode": "PlayMode"}, true},
		{"run_tests", map[string]interface{}{"mode": "EditMode"}, false},
		{"exec", map[string]interface{}{"code": "return 1;"}, false},
		{"console", nil, false}, {"job_status", nil, false}, {"list", nil, false},
	}
	for i, c := range cases {
		t.Run(fmt.Sprintf("%d/%s", i, c.command), func(t *testing.T) {
			body, err := json.Marshal(CommandRequest{Command: c.command, Params: c.params})
			if err != nil {
				t.Fatal(err)
			}
			allowed := allowsEmptyTransition(c.command, body)
			if allowed != c.allowed {
				t.Fatalf("allowEmpty=%t want %t", allowed, c.allowed)
			}
			response, err := decodeResponse(&http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader(""))}, c.command, allowed)
			if c.allowed {
				if err != nil || response.Success || !response.TransitionPending {
					t.Fatalf("transition: %#v %v", response, err)
				}
			} else {
				if err == nil || !strings.Contains(err.Error(), "execution could not be confirmed") {
					t.Fatalf("missing outcome error: %v", err)
				}
			}
		})
	}
}

func TestServiceUnavailableNeverSucceedsOrRetries(t *testing.T) {
	var calls atomic.Int32
	const message = "Command was not executed: Unity is reloading/restarting the listener. Retry after the editor is ready."
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		calls.Add(1)
		w.WriteHeader(503)
		_, _ = fmt.Fprintf(w, `{"success":false,"message":%q}`, message)
	}))
	defer server.Close()
	port, err := strconv.Atoi(strings.TrimPrefix(server.URL, "http://127.0.0.1:"))
	if err != nil {
		t.Fatal(err)
	}
	for _, command := range []string{"exec", "manage_editor"} {
		result, err := Send(&Instance{Port: port}, command, map[string]interface{}{"action": "play"}, 1000)
		if result != nil || err == nil || !strings.Contains(err.Error(), message) {
			t.Fatalf("503 result=%#v error=%v", result, err)
		}
	}
	if calls.Load() != 2 {
		t.Fatalf("automatic resend occurred: %d", calls.Load())
	}
}

type interruptedBody struct{}

func (interruptedBody) Read([]byte) (int, error) { return 0, io.ErrUnexpectedEOF }
func (interruptedBody) Close() error             { return nil }
func TestPartialResponseIsNotEmptyTransition(t *testing.T) {
	if result, err := decodeResponse(&http.Response{StatusCode: 200, Body: interruptedBody{}}, "manage_editor", true); err == nil || result != nil {
		t.Fatalf("partial success: %#v %v", result, err)
	}
}
