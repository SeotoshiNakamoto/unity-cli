package cmd

import (
	"encoding/json"
	"os"
	"reflect"
	"strings"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func TestBuildCaptureParams(t *testing.T) {
	params, noWait, err := buildCaptureParams([]string{
		"--seconds", "2", "--out", "d:/tmp/x", "--width", "640", "--crop", "10,20,300,200",
		"--start-when", "Time.frameCount > 10", "--log", "Time.time", "--log", "Camera.main.transform.position",
		"--sheet", "--video", "--format", "png", "--no-wait",
	})
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]interface{}{
		"action": "start", "seconds": "2", "out": "d:/tmp/x", "width": "640", "crop": "10,20,300,200",
		"start_when": "Time.frameCount > 10", "log": []string{"Time.time", "Camera.main.transform.position"},
		"sheet": "6x6", "video": true, "format": "png",
	}
	if !reflect.DeepEqual(params, want) {
		t.Fatalf("params = %#v\nwant %#v", params, want)
	}
	if !noWait {
		t.Fatal("--no-wait not detected")
	}

	params, _, err = buildCaptureParams([]string{"--sheet", "4x3"})
	if err != nil || params["sheet"] != "4x3" {
		t.Fatalf("sheet = %v, err = %v", params["sheet"], err)
	}
}

func TestBuildCaptureParamsErrors(t *testing.T) {
	for _, args := range [][]string{{"--bogus"}, {"--seconds"}, {"--log"}, {"stray"}} {
		if _, _, err := buildCaptureParams(args); err == nil {
			t.Errorf("%v: expected error", args)
		}
	}
}

func TestCaptureFramesPollsUntilDone(t *testing.T) {
	var calls []string
	statuses := []string{"waiting", "capturing", "finishing", "done"}
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		action := params.(map[string]interface{})["action"].(string)
		calls = append(calls, action)
		if action == "start" {
			return &client.CommandResponse{Success: true, Data: json.RawMessage(`{"state":"waiting"}`)}, nil
		}
		state := statuses[0]
		statuses = statuses[1:]
		return &client.CommandResponse{Success: true, Data: json.RawMessage(`{"state":"` + state + `"}`)}, nil
	}
	resp, err := captureCmd([]string{"frames", "--seconds", "1"}, send)
	if err != nil {
		t.Fatal(err)
	}
	if captureState(resp) != "done" {
		t.Fatalf("final state = %s", captureState(resp))
	}
	if strings.Join(calls, ",") != "start,status,status,status,status" {
		t.Fatalf("calls = %v", calls)
	}
}

func TestCaptureFramesStopsOnFailure(t *testing.T) {
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		if params.(map[string]interface{})["action"] == "start" {
			return &client.CommandResponse{Success: true, Data: json.RawMessage(`{"state":"waiting"}`)}, nil
		}
		return &client.CommandResponse{Success: false, Message: "start-when stayed false"}, nil
	}
	resp, err := captureCmd([]string{"frames"}, send)
	if err != nil || resp.Success {
		t.Fatalf("resp = %+v, err = %v", resp, err)
	}
}

func TestCaptureHelp(t *testing.T) {
	overview := captureStdout(t, printHelp)
	topic := captureStdout(t, func() { printTopicHelp("capture") })
	for _, text := range []string{"capture frames", "--start-when", "--log", "--sheet", "--crop", "--video", "--no-wait"} {
		if !strings.Contains(overview, text) {
			t.Errorf("overview help missing %q", text)
		}
		if !strings.Contains(topic, text) {
			t.Errorf("topic help missing %q", text)
		}
	}
	for _, text := range []string{"--wait-timeout", "--frames", "--overwrite", "capture wait", "capture.json"} {
		if !strings.Contains(topic, text) {
			t.Errorf("topic help missing %q", text)
		}
	}
}

func captureStdout(t *testing.T, fn func()) string {
	t.Helper()
	file, err := os.CreateTemp(t.TempDir(), "help")
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = file.Close() }()
	old := os.Stdout
	os.Stdout = file
	fn()
	os.Stdout = old
	data, err := os.ReadFile(file.Name())
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}
