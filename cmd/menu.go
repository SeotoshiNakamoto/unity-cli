package cmd

import (
	"fmt"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func menuCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli menu <menu/path>")
	}
	return send("execute_menu_item", map[string]interface{}{"menu_path": args[0]})
}
