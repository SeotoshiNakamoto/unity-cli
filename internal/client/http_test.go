package client

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
)

func TestHealthUsesDedicatedEndpoint(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			t.Errorf("method = %s, want GET", r.Method)
		}
		if r.URL.Path != "/health" {
			t.Errorf("path = %s, want /health", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprint(w, `{"success":true,"message":"ok","data":{"listening":true}}`)
	}))
	defer server.Close()

	port, err := strconv.Atoi(strings.TrimPrefix(server.URL, "http://127.0.0.1:"))
	if err != nil {
		t.Fatalf("parse test server port: %v", err)
	}

	resp, err := Health(&Instance{Port: port}, 1000)
	if err != nil {
		t.Fatalf("Health returned error: %v", err)
	}
	if !resp.Success || resp.Message != "ok" {
		t.Fatalf("unexpected health response: %#v", resp)
	}
}

func TestHealthRejectsEmptyResponse(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))
	defer server.Close()

	port, err := strconv.Atoi(strings.TrimPrefix(server.URL, "http://127.0.0.1:"))
	if err != nil {
		t.Fatalf("parse test server port: %v", err)
	}

	if _, err := Health(&Instance{Port: port}, 1000); err == nil {
		t.Fatal("expected empty health response to fail")
	}
}
