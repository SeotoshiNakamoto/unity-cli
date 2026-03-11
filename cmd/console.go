package cmd

import (
	"strconv"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func consoleCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	params := map[string]interface{}{}

	flags := parseSubFlags(args)

	if v, ok := flags["lines"]; ok {
		if n, err := strconv.Atoi(v); err == nil {
			params["count"] = n
		}
	}
	if v, ok := flags["filter"]; ok {
		switch v {
		case "all":
			params["types"] = []string{"error", "warning", "log"}
		case "error":
			params["types"] = []string{"error"}
		case "warn", "warning":
			params["types"] = []string{"warning"}
		case "log":
			params["types"] = []string{"log"}
		default:
			params["types"] = []string{"error", "warning", "log"}
			params["filterText"] = v
		}
	}

	return send("read_console", params)
}
