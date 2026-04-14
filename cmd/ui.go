package cmd

import (
	"fmt"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func uiCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli ui <snapshot|tree|query|click|type|events> [options]\nUse 'unity-cli ui --help' for details")
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

	case "type":
		// ui type <selector> <text> [--window <name>]
		// selector and text are positional: everything before the last token is selector, last is text
		positional := extractPositional(rest)
		selector, text := splitSelectorText(positional)
		if selector == "" || text == "" {
			return nil, fmt.Errorf("usage: unity-cli ui type <selector> <text> [--window <name>]\nExample: unity-cli ui type \"id=input-name\" \"PlayerOne\"")
		}
		params["selector"] = selector
		params["text"] = text
		setStr(flags, params, "window", "window")

	case "events":
		// no extra params

	default:
		return nil, fmt.Errorf("unknown ui action: %s\nAvailable: snapshot, tree, query, click, type, events", action)
	}

	return send("ui_snapshot", params)
}

// splitSelectorText splits "id=input-name PlayerOne" into selector "id=input-name" and text "PlayerOne".
// Tokens containing '=' or starting with '[' are selector parts; the rest is text.
func splitSelectorText(positional string) (selector, text string) {
	parts := strings.Split(positional, " ")
	var selParts, textParts []string
	for _, p := range parts {
		if strings.Contains(p, "=") || strings.HasPrefix(p, "[") {
			selParts = append(selParts, p)
		} else {
			textParts = append(textParts, p)
		}
	}
	return strings.Join(selParts, " "), strings.Join(textParts, " ")
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
