package cmd

import (
	"fmt"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func uiCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli ui <snapshot|tree|query|click> [options]\nUse 'unity-cli ui --help' for details")
	}

	action := args[0]
	rest := args[1:]
	flags := parseSubFlags(rest)

	params := map[string]interface{}{"action": action}

	switch action {
	case "snapshot":
		setStr(flags, params, "window", "window")
		setStr(flags, params, "output", "output")
		setInt(flags, params, "depth", "max_depth")
		setStr(flags, params, "filter", "filter")
		if _, ok := flags["visible-only"]; ok {
			params["visible_only"] = true
		}
		if _, ok := flags["no-screenshot"]; ok {
			params["screenshot"] = false
		}

	case "tree":
		setStr(flags, params, "window", "window")
		setInt(flags, params, "depth", "max_depth")

	case "query":
		selector := extractPositional(rest)
		if selector == "" {
			return nil, fmt.Errorf("usage: unity-cli ui query <selector> [--window <name>]")
		}
		params["selector"] = selector
		setStr(flags, params, "window", "window")

	case "click":
		selector := extractPositional(rest)
		if selector == "" {
			return nil, fmt.Errorf("usage: unity-cli ui click <selector> [--window <name>]")
		}
		params["selector"] = selector
		setStr(flags, params, "window", "window")

	case "events":
		// no extra params

	default:
		return nil, fmt.Errorf("unknown ui action: %s\nAvailable: snapshot, tree, query, click, events", action)
	}

	return send("ui_snapshot", params)
}

// extractPositional collects non-flag tokens and joins them with spaces.
// Selectors like "label=Save type=Button" are multi-word.
func extractPositional(args []string) string {
	var parts []string
	for i := 0; i < len(args); i++ {
		if strings.HasPrefix(args[i], "--") {
			// skip flag name + its value (if next arg is not another flag)
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				i++
			}
			continue
		}
		parts = append(parts, args[i])
	}
	return strings.Join(parts, " ")
}
