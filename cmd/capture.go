package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

const captureUsage = `usage: unity-cli capture <frames|status|stop|wait> [options]
Use 'unity-cli capture --help' for details`

// captureValueFlags take a value; captureBoolFlags do not.
var captureValueFlags = map[string]string{
	"seconds": "seconds", "frames": "frames", "out": "out", "width": "width",
	"crop": "crop", "start-when": "start_when", "wait-timeout": "wait_timeout",
	"usings": "usings", "format": "format", "quality": "quality",
	"video-fps": "video_fps", "ffmpeg": "ffmpeg",
}

var captureBoolFlags = map[string]string{
	"video": "video", "overwrite": "overwrite",
}

// buildCaptureParams parses 'capture frames' flags. --log may repeat, and
// --sheet takes an optional CxR value (default 6x6).
func buildCaptureParams(args []string) (params map[string]interface{}, noWait bool, err error) {
	params = map[string]interface{}{"action": "start"}
	var logs []string
	for i := 0; i < len(args); i++ {
		a := args[i]
		if !strings.HasPrefix(a, "--") {
			return nil, false, fmt.Errorf("unexpected argument %q\n%s", a, captureUsage)
		}
		name := a[2:]
		hasValue := i+1 < len(args) && !strings.HasPrefix(args[i+1], "--")
		switch {
		case name == "no-wait":
			noWait = true
		case name == "log":
			if !hasValue {
				return nil, false, fmt.Errorf("--log needs a C# expression")
			}
			i++
			logs = append(logs, args[i])
		case name == "sheet":
			params["sheet"] = "6x6"
			if hasValue {
				i++
				params["sheet"] = args[i]
			}
		case captureBoolFlags[name] != "":
			params[captureBoolFlags[name]] = true
		case captureValueFlags[name] != "":
			if !hasValue {
				return nil, false, fmt.Errorf("--%s needs a value", name)
			}
			i++
			params[captureValueFlags[name]] = args[i]
		default:
			return nil, false, fmt.Errorf("unknown capture option --%s\n%s", name, captureUsage)
		}
	}
	if len(logs) > 0 {
		params["log"] = logs
	}
	return params, noWait, nil
}

func captureCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("%s", captureUsage)
	}
	switch args[0] {
	case "frames", "start":
		params, noWait, err := buildCaptureParams(args[1:])
		if err != nil {
			return nil, err
		}
		resp, err := send("capture", params)
		if err != nil || !resp.Success || noWait {
			return resp, err
		}
		return pollCapture(send, captureDeadline(params))
	case "status", "stop":
		return send("capture", map[string]interface{}{"action": args[0]})
	case "wait":
		return pollCapture(send, time.Now().Add(time.Duration(flagTimeout)*time.Millisecond))
	default:
		return nil, fmt.Errorf("unknown capture action: %s\nAvailable: frames, status, stop, wait", args[0])
	}
}

// captureDeadline covers the start-when wait, the capture itself and the
// encoding/video tail, and never undercuts --timeout.
func captureDeadline(params map[string]interface{}) time.Time {
	seconds := floatParam(params, "seconds", 2)
	if _, ok := params["frames"]; ok {
		seconds = floatParam(params, "frames", 0) / 10 // assume at least 10 fps
	}
	total := floatParam(params, "wait_timeout", 60) + seconds + 120
	if params["video"] == true {
		total += 300
	}
	d := time.Duration(total * float64(time.Second))
	if t := time.Duration(flagTimeout) * time.Millisecond; t > d {
		d = t
	}
	return time.Now().Add(d)
}

func floatParam(params map[string]interface{}, key string, fallback float64) float64 {
	if s, ok := params[key].(string); ok {
		if v, err := strconv.ParseFloat(s, 64); err == nil {
			return v
		}
	}
	return fallback
}

// pollCapture waits until the running capture reports done or failed.
func pollCapture(send sendFn, deadline time.Time) (*client.CommandResponse, error) {
	for time.Now().Before(deadline) {
		resp, err := send("capture", map[string]interface{}{"action": "status"})
		if err != nil {
			return nil, err
		}
		state := captureState(resp)
		if !resp.Success || state == "done" {
			fmt.Fprintln(os.Stderr)
			return resp, nil
		}
		if state == "idle" {
			fmt.Fprintln(os.Stderr)
			return nil, fmt.Errorf("no capture is running (it may have been lost to a domain reload; check capture.json in the output directory)")
		}
		fmt.Fprint(os.Stderr, ".")
		time.Sleep(300 * time.Millisecond)
	}
	return nil, fmt.Errorf("timed out waiting for the capture; it keeps running in Unity. Use 'capture wait' or 'capture stop'")
}

func captureState(resp *client.CommandResponse) string {
	var data struct {
		State string `json:"state"`
	}
	if len(resp.Data) > 0 && json.Unmarshal(resp.Data, &data) == nil {
		return data.State
	}
	return ""
}
