package cmd

import (
	"fmt"
	"strconv"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func parrelSyncCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli parrelsync <list|ensure|open> [options]")
	}

	action := strings.ToLower(args[0])
	flags := parseSubFlags(args[1:])
	params := map[string]interface{}{"action": action}
	if _, async := flags["async"]; async {
		params["async"] = true
	}

	switch action {
	case "list":
		return send("manage_parrel_sync", params)

	case "ensure":
		count, err := parseOptionalInt(flags, "count", 1)
		if err != nil {
			return nil, err
		}
		params["count"] = count
		_, params["open"] = flags["open"]
		return send("manage_parrel_sync", params)

	case "open":
		index, err := parseOptionalInt(flags, "index", 0)
		if err != nil {
			return nil, err
		}
		params["index"] = index
		_, params["all"] = flags["all"]
		return send("manage_parrel_sync", params)

	default:
		return nil, fmt.Errorf("unknown parrelsync action: %s\nAvailable: list, ensure, open", action)
	}
}

func parseOptionalInt(flags map[string]string, key string, fallback int) (int, error) {
	raw, ok := flags[key]
	if !ok {
		return fallback, nil
	}
	value, err := strconv.Atoi(raw)
	if err != nil {
		return 0, fmt.Errorf("--%s requires an integer, got %q", key, raw)
	}
	return value, nil
}
