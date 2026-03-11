package cmd

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

var (
	flagPort    int
	flagProject string
	flagTimeout int
	flagJSON    bool
)

func Execute() error {
	flag.IntVar(&flagPort, "port", 0, "Override Unity instance port")
	flag.StringVar(&flagProject, "project", "", "Select Unity instance by project path")
	flag.IntVar(&flagTimeout, "timeout", 120000, "Request timeout in milliseconds")
	flag.BoolVar(&flagJSON, "json", false, "Output raw JSON")

	flag.Usage = func() { printHelp() }

	// Find first non-flag arg position
	args := os.Args[1:]
	cmdArgs := extractCommandArgs(args)

	if len(cmdArgs) == 0 {
		printHelp()
		return nil
	}

	category := cmdArgs[0]
	subArgs := cmdArgs[1:]

	// Handle help/version before instance discovery
	switch category {
	case "help", "--help", "-h":
		printHelp()
		return nil
	case "version", "--version", "-v":
		fmt.Println("unity-cli v0.1.0")
		return nil
	}

	// Parse remaining flags
	flag.CommandLine.Parse(extractFlags(args))

	inst, err := client.DiscoverInstance(flagProject, flagPort)
	if err != nil {
		return err
	}

	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		return client.Send(inst, command, params, flagTimeout)
	}

	var resp *client.CommandResponse

	switch category {
	case "editor":
		resp, err = editorCmd(subArgs, send)
	case "console":
		resp, err = consoleCmd(send)
	case "exec":
		resp, err = execCmd(subArgs, send)
	case "query":
		resp, err = queryCmd(subArgs, send)
	case "game":
		resp, err = gameCmd(subArgs, send)
	case "tool":
		resp, err = toolCmd(subArgs, send)
	case "diag":
		resp, err = diagCmd(subArgs, send)
	case "profiler":
		resp, err = profilerCmd(subArgs, send)
	case "menu":
		resp, err = menuCmd(subArgs, send)
	case "reserialize":
		resp, err = reserializeCmd(subArgs, send)
	default:
		// Try as direct custom tool call
		resp, err = send(category, map[string]interface{}{})
	}

	if err != nil {
		return err
	}

	printResponse(resp)

	if !resp.Success {
		os.Exit(1)
	}

	return nil
}

type sendFn func(command string, params interface{}) (*client.CommandResponse, error)

func printResponse(resp *client.CommandResponse) {
	if flagJSON {
		b, _ := json.MarshalIndent(resp, "", "  ")
		fmt.Println(string(b))
		return
	}

	if resp.Data != nil && len(resp.Data) > 0 && string(resp.Data) != "null" {
		var pretty interface{}
		if json.Unmarshal(resp.Data, &pretty) == nil {
			b, _ := json.MarshalIndent(pretty, "", "  ")
			fmt.Println(string(b))
		} else {
			fmt.Println(string(resp.Data))
		}
	} else {
		fmt.Println(resp.Message)
	}
}

func extractCommandArgs(args []string) []string {
	var result []string
	skip := false
	for _, a := range args {
		if skip {
			skip = false
			continue
		}
		if a == "--port" || a == "--project" || a == "--timeout" {
			skip = true
			continue
		}
		if a == "--json" {
			continue
		}
		if len(a) > 2 && a[:2] == "--" {
			continue
		}
		result = append(result, a)
	}
	return result
}

func extractFlags(args []string) []string {
	var result []string
	for i, a := range args {
		if a == "--port" || a == "--project" || a == "--timeout" {
			result = append(result, a)
			if i+1 < len(args) {
				result = append(result, args[i+1])
			}
		}
		if a == "--json" {
			result = append(result, a)
		}
	}
	return result
}

func printHelp() {
	fmt.Print(`unity-cli v0.1.0 — Control Unity Editor from the command line

Usage: unity-cli <command> [subcommand] [options]

Editor Control:
  editor play [--wait]          Enter play mode (--wait blocks until fully entered)
  editor stop                   Exit play mode
  editor pause                  Toggle pause/resume (play mode only)
  editor refresh                Refresh asset database
  editor refresh --compile      Request script compilation and wait

Console:
  console                       Read error & warning logs (default)
  console --lines 20            Limit to N entries
  console --filter all          Filter: error, warn, log, all

Execute C#:
  exec "<code>"                 Run C# code in Unity (single expression auto-returns)
  exec "<code>" --usings x,y    Add extra using directives

  Examples:
    exec "Time.time"
    exec "GameObject.FindObjectsOfType<Camera>().Length"
    exec "var go = new GameObject(\"Test\"); return go.name;"

Menu:
  menu "<path>"                 Execute Unity menu item by path

  Examples:
    menu "File/Save Project"
    menu "Assets/Refresh"

Reserialize:
  reserialize <path> [paths...] Force YAML reserialize after text edits

  Examples:
    reserialize Assets/Scenes/Main.unity
    reserialize Assets/Prefabs/A.prefab Assets/Prefabs/B.prefab

Profiler:
  profiler hierarchy             Read profiler data (last frame)
  profiler hierarchy --depth 3   Limit sample depth

Custom Tools:
  tool list                     List all registered tools (built-in + custom)
  tool call <name>              Call a tool with no parameters
  tool call <name> --params '{"key":"val"}'
                                Call a tool with JSON parameters
  tool help <name>              Show tool description

Global Options:
  --port <N>          Connect to specific Unity port (skip auto-discovery)
  --project <path>    Select Unity instance by project path
  --json              Output full JSON response (default: data only)
  --timeout <ms>      Request timeout in ms (default: 120000)

Notes:
  - Unity must be open with the Connector package installed
  - Multiple Unity instances: use --port or --project to select
  - Custom tools: any [UnityCliTool] class is auto-discovered
  - Run 'tool list' to see all available tools
`)
}
