package cmd

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func traceCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli trace <hook|unhook|list|read|clear> [options]\nUse 'unity-cli trace --help' for details")
	}

	action := args[0]
	flags := parseSubFlags(args[1:])

	params := map[string]interface{}{"action": action}

	switch action {
	case "hook":
		setStr(flags, params, "type", "type_name")
		setStr(flags, params, "method", "method_name")
		setStr(flags, params, "assembly", "assembly")
		// Booleans: log_params and log_return default to true
		if _, ok := flags["no-params"]; ok {
			params["log_params"] = false
		}
		if _, ok := flags["no-return"]; ok {
			params["log_return"] = false
		}
		if _, ok := flags["stack"]; ok {
			params["log_stack"] = true
		}
		// Auto-attach 0Harmony.dll path (next to this exe)
		if exe, err := os.Executable(); err == nil {
			harmonyPath := filepath.Join(filepath.Dir(exe), "0Harmony.dll")
			if _, err := os.Stat(harmonyPath); err == nil {
				params["harmony_path"] = filepath.ToSlash(harmonyPath)
			}
		}
	case "unhook":
		setStr(flags, params, "id", "hook_id")
	case "list":
		// no extra params
	case "read":
		setStr(flags, params, "id", "hook_id")
		setInt(flags, params, "count", "count")
	case "clear":
		setStr(flags, params, "id", "hook_id")
	default:
		return nil, fmt.Errorf("unknown trace action: %s\nAvailable: hook, unhook, list, read, clear", action)
	}

	return send("trace_method", params)
}
