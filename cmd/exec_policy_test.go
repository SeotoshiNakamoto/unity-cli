package cmd

import (
	"strings"
	"testing"
)

func TestValidateExecDeferredPolicyBlocksDeferredCode(t *testing.T) {
	tests := []struct {
		name string
		code string
	}{
		{"await", "await LoadAsync();"},
		{"task", "return Task.Run(() => 1);"},
		{"coroutine", "StartCoroutine(Run());"},
		{"unity async", "SceneManager.LoadSceneAsync(\"Game\");"},
		{"delay call", "EditorApplication.delayCall += RunLater;"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			params := map[string]interface{}{"args": []string{tt.code}}
			err := validateExecDeferredPolicy(params)
			if err == nil || !strings.Contains(err.Error(), "--allow-deferred-code") {
				t.Fatalf("expected deferred-code error, got %v", err)
			}
		})
	}
}

func TestValidateExecDeferredPolicyAllowsSynchronousCode(t *testing.T) {
	params := map[string]interface{}{
		"args": []string{`var taskName = "Task.Run and await are documentation"; // StartCoroutine
return GameObject.FindObjectsByType<Camera>(FindObjectsSortMode.None).Length;`},
	}
	if err := validateExecDeferredPolicy(params); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestValidateExecDeferredPolicyOverrideIsRemovedBeforeDispatch(t *testing.T) {
	params := map[string]interface{}{
		"args":                []string{"await LoadAsync();"},
		"allow-deferred-code": true,
		"async":               true,
	}
	if err := validateExecDeferredPolicy(params); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if _, exists := params["allow-deferred-code"]; exists {
		t.Fatal("CLI-only override was not removed")
	}
	if params["async"] != true {
		t.Fatal("transport-level --async flag must be preserved")
	}
}

func TestValidateExecDeferredPolicyRejectsOverrideValue(t *testing.T) {
	params := map[string]interface{}{
		"args":                []string{"return 1;"},
		"allow-deferred-code": "yes",
	}
	if err := validateExecDeferredPolicy(params); err == nil {
		t.Fatal("expected invalid override error")
	}
}
