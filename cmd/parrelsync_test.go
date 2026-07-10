package cmd

import "testing"

func TestParrelSyncEnsure(t *testing.T) {
	send, params := mockSend("manage_parrel_sync", t)
	if _, err := parrelSyncCmd([]string{"ensure", "--count", "2", "--open"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "ensure" || (*params)["count"] != 2 || (*params)["open"] != true {
		t.Fatalf("unexpected params: %#v", *params)
	}
}

func TestParrelSyncEnsureAsync(t *testing.T) {
	send, params := mockSend("manage_parrel_sync", t)
	if _, err := parrelSyncCmd([]string{"ensure", "--count", "2", "--async"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["async"] != true {
		t.Fatalf("expected async=true, got %#v", *params)
	}
}

func TestParrelSyncOpenAll(t *testing.T) {
	send, params := mockSend("manage_parrel_sync", t)
	if _, err := parrelSyncCmd([]string{"open", "--all"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "open" || (*params)["all"] != true {
		t.Fatalf("unexpected params: %#v", *params)
	}
}

func TestParrelSyncRejectsInvalidCount(t *testing.T) {
	send, _ := mockSend("manage_parrel_sync", t)
	if _, err := parrelSyncCmd([]string{"ensure", "--count", "many"}, send); err == nil {
		t.Fatal("expected integer parse error")
	}
}
