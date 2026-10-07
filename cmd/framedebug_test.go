package cmd

import (
	"io"
	"os"
	"reflect"
	"strings"
	"testing"
)

func TestFrameDebugPassthroughParams(t *testing.T) {
	flags, commands := splitArgs([]string{
		"--project", "D:/Projects/ProjectD/client", "framedebug", "dump",
		"--output", "D:/tmp/frame.json", "--max-events", "100",
		"--capture-timeout", "300", "--timeout", "360000", "--async",
	})
	if !reflect.DeepEqual(flags, []string{"--project", "D:/Projects/ProjectD/client", "--timeout", "360000"}) {
		t.Fatalf("global flags = %v", flags)
	}
	params, err := buildParams(commands[1:], nil)
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]interface{}{
		"args": []string{"dump"}, "output": "D:/tmp/frame.json",
		"max-events": 100, "capture-timeout": 300, "async": true,
	}
	if !reflect.DeepEqual(params, want) {
		t.Fatalf("params = %#v, want %#v", params, want)
	}
}

func TestFrameDebugHelp(t *testing.T) {
	for _, topic := range []bool{false, true} {
		t.Run(map[bool]string{false: "overview", true: "topic"}[topic], func(t *testing.T) {
			file, err := os.CreateTemp(t.TempDir(), "help")
			if err != nil {
				t.Fatal(err)
			}
			t.Cleanup(func() { _ = file.Close() })
			old := os.Stdout
			os.Stdout = file
			if topic {
				printTopicHelp("framedebug")
			} else {
				printHelp()
			}
			os.Stdout = old
			if _, err := file.Seek(0, 0); err != nil {
				t.Fatal(err)
			}
			data, err := io.ReadAll(file)
			if err != nil {
				t.Fatal(err)
			}
			for _, text := range []string{"framedebug", "--output", "--max-events", "one-to-one GPU draws", "no GPU timings"} {
				if !strings.Contains(strings.ToLower(string(data)), strings.ToLower(text)) {
					t.Errorf("help missing %q", text)
				}
			}
		})
	}
}
