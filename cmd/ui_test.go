package cmd

import "testing"

func TestUICmdEventsActions(t *testing.T) {
	tests := []struct {
		name       string
		args       []string
		wantAction string
	}{
		{name: "default is read", args: []string{"events"}, wantAction: "read"},
		{name: "explicit read", args: []string{"events", "read"}, wantAction: "read"},
		{name: "start", args: []string{"events", "start"}, wantAction: "start"},
		{name: "stop", args: []string{"events", "stop"}, wantAction: "stop"},
		{name: "status", args: []string{"events", "status"}, wantAction: "status"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			send, captured := mockSend("ui_snapshot", t)
			if _, err := uiCmd(tt.args, send); err != nil {
				t.Fatalf("uiCmd(%v) returned error: %v", tt.args, err)
			}
			if got := (*captured)["event_action"]; got != tt.wantAction {
				t.Fatalf("event_action = %v, want %q", got, tt.wantAction)
			}
		})
	}
}

func TestUICmdEventsRejectsUnknownAction(t *testing.T) {
	send, _ := mockSend("ui_snapshot", t)
	if _, err := uiCmd([]string{"events", "watch"}, send); err == nil {
		t.Fatal("uiCmd accepted an unknown events action")
	}
}

func TestUICmdBooleanFlagBeforeSelector(t *testing.T) {
	send, captured := mockSend("ui_snapshot", t)
	if _, err := uiCmd([]string{"click", "--runtime", "id=start-button"}, send); err != nil {
		t.Fatalf("uiCmd returned error: %v", err)
	}
	if got := (*captured)["selector"]; got != "id=start-button" {
		t.Fatalf("selector = %v, want id=start-button", got)
	}
	if got := (*captured)["source"]; got != "runtime" {
		t.Fatalf("source = %v, want runtime", got)
	}
}

func TestUICmdRejectsUnknownOption(t *testing.T) {
	send, _ := mockSend("ui_snapshot", t)
	if _, err := uiCmd([]string{"events", "--unknown", "start"}, send); err == nil {
		t.Fatal("uiCmd accepted an unknown option")
	}
}
