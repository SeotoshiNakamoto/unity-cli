package cmd

import (
	"bufio"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

type playerBridgeResponse struct {
	ID      string `json:"id"`
	OK      bool   `json:"ok"`
	Error   string `json:"error"`
	Payload string `json:"payload"`
}

var startPlayerProcess = startPlayer

func playerCmd(args []string, port int, timeoutMs int) error {
	if len(args) == 0 || strings.HasPrefix(args[0], "--") {
		return fmt.Errorf("usage: unity-cli player <launch|call|wait|stop|kill> [options]")
	}

	action := strings.ToLower(args[0])
	args = args[1:]
	flags := parseSubFlags(args)

	switch action {
	case "launch":
		return launchPlayer(flags, port, timeoutMs)
	case "call":
		return callPlayer(args, flags, port, timeoutMs)
	case "wait":
		return waitForPlayer(flags, port, timeoutMs)
	case "stop":
		return stopPlayer(flags, port, timeoutMs)
	case "kill":
		return killPlayer(flags, timeoutMs)
	default:
		return fmt.Errorf("unknown player action: %s\nAvailable: launch, call, wait, stop, kill", action)
	}
}

func launchPlayer(flags map[string]string, port int, timeoutMs int) error {
	exePath := flags["exe"]
	if exePath == "" {
		return fmt.Errorf("player launch requires --exe <path>")
	}
	if port <= 0 {
		return fmt.Errorf("player launch requires --port <N>")
	}
	token := flags["token"]
	if token == "" {
		return fmt.Errorf("player launch requires --token <value>")
	}

	absPath, err := filepath.Abs(exePath)
	if err != nil {
		return fmt.Errorf("resolve player executable: %w", err)
	}
	if _, err = os.Stat(absPath); err != nil {
		return fmt.Errorf("player executable not found: %s", absPath)
	}

	playerArgs := []string{"-projectdE2E", "-e2ePort", strconv.Itoa(port), "-e2eToken", token}
	identity := flags["identity"]
	if identity == "" {
		identity = token
	}
	playerArgs = append(playerArgs, "-e2eIdentity", identity)
	matchingAddress := flags["matching-address"]
	matchingPort := flags["matching-port"]
	if matchingAddress != "" || matchingPort != "" {
		parsedPort, portErr := strconv.Atoi(matchingPort)
		if matchingAddress == "" || portErr != nil || parsedPort <= 0 || parsedPort > 65535 {
			return fmt.Errorf("player launch matching override requires --matching-address <ip> and --matching-port <1-65535>")
		}
		playerArgs = append(playerArgs,
			"-e2eMatchingAddress", matchingAddress,
			"-e2eMatchingPort", strconv.Itoa(parsedPort))
	}
	if logFile := flags["log-file"]; logFile != "" {
		logPath, pathErr := filepath.Abs(logFile)
		if pathErr != nil {
			return fmt.Errorf("resolve log file: %w", pathErr)
		}
		playerArgs = append(playerArgs, "-logFile", logPath)
	}
	if _, enabled := flags["no-graphics"]; enabled {
		playerArgs = append(playerArgs, "-batchmode", "-nographics")
	}

	pid, err := startPlayerProcess(absPath, playerArgs)
	if err != nil {
		return err
	}

	result := map[string]interface{}{"pid": pid, "port": port, "exe": absPath}
	data, _ := json.MarshalIndent(result, "", "  ")
	fmt.Println(string(data))

	if _, wait := flags["wait"]; wait {
		return waitForPlayer(flags, port, timeoutMs)
	}
	return nil
}

func startPlayer(exePath string, args []string) (int, error) {
	command := exec.Command(exePath, args...)
	command.Dir = filepath.Dir(exePath)
	if err := command.Start(); err != nil {
		return 0, fmt.Errorf("launch player: %w", err)
	}
	return command.Process.Pid, nil
}

func callPlayer(args []string, flags map[string]string, port int, timeoutMs int) error {
	command := firstPositional(args)
	if command == "" {
		return fmt.Errorf("player call requires a command")
	}
	response, err := sendPlayerCommand(port, flags["token"], command, flags, timeoutMs)
	if err != nil {
		return err
	}
	printPlayerResponse(response, flags)
	if !response.OK {
		return fmt.Errorf("player command failed: %s", response.Error)
	}
	return nil
}

func waitForPlayer(flags map[string]string, port int, timeoutMs int) error {
	if port <= 0 {
		return fmt.Errorf("player wait requires --port <N>")
	}
	if flags["token"] == "" {
		return fmt.Errorf("player wait requires --token <value>")
	}

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	var lastErr error
	for time.Now().Before(deadline) {
		response, err := sendPlayerCommand(port, flags["token"], "ping", nil, 1000)
		if err == nil && response.OK {
			fmt.Printf("Player bridge ready on 127.0.0.1:%d\n", port)
			return nil
		}
		if err != nil {
			lastErr = err
		} else {
			lastErr = fmt.Errorf("%s", response.Error)
		}
		time.Sleep(250 * time.Millisecond)
	}
	return fmt.Errorf("timed out waiting for player bridge on port %d: %v", port, lastErr)
}

func stopPlayer(flags map[string]string, port int, timeoutMs int) error {
	response, err := sendPlayerCommand(port, flags["token"], "quit", nil, timeoutMs)
	if err != nil {
		return err
	}
	printPlayerResponse(response, flags)
	if !response.OK {
		return fmt.Errorf("player quit failed: %s", response.Error)
	}
	return nil
}

func killPlayer(flags map[string]string, timeoutMs int) error {
	if _, force := flags["force"]; !force {
		return fmt.Errorf("refusing to kill player without --force")
	}
	pid, err := strconv.Atoi(flags["pid"])
	if err != nil || pid <= 0 {
		return fmt.Errorf("player kill requires --pid <N>")
	}
	if err = killInstanceProcess(pid); err != nil {
		return fmt.Errorf("failed to kill player pid %d: %w", pid, err)
	}

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	for time.Now().Before(deadline) {
		if isInstanceProcessDead(pid) {
			fmt.Printf("Killed player pid %d\n", pid)
			return nil
		}
		time.Sleep(100 * time.Millisecond)
	}
	return fmt.Errorf("timed out waiting for player pid %d to exit", pid)
}

func sendPlayerCommand(port int, token string, command string, flags map[string]string, timeoutMs int) (*playerBridgeResponse, error) {
	if port <= 0 {
		return nil, fmt.Errorf("player command requires --port <N>")
	}
	if token == "" {
		return nil, fmt.Errorf("player command requires --token <value>")
	}
	if timeoutMs <= 0 {
		timeoutMs = 120000
	}

	request := map[string]interface{}{
		"id":      strconv.FormatInt(time.Now().UnixNano(), 10),
		"token":   token,
		"command": command,
	}
	if flags != nil {
		if raw := flags["params"]; raw != "" {
			if err := json.Unmarshal([]byte(raw), &request); err != nil {
				return nil, fmt.Errorf("invalid JSON in --params: %w", err)
			}
			request["token"] = token
			request["command"] = command
		}
		copyPlayerFlag(request, flags, "room-name", "roomName")
		copyPlayerFlag(request, flags, "room-id", "roomId")
		copyPlayerFlag(request, flags, "passive-id", "passiveId")
	}

	data, err := json.Marshal(request)
	if err != nil {
		return nil, fmt.Errorf("encode player request: %w", err)
	}

	timeout := time.Duration(timeoutMs) * time.Millisecond
	connection, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), timeout)
	if err != nil {
		return nil, fmt.Errorf("connect player bridge on port %d: %w", port, err)
	}
	defer func() { _ = connection.Close() }()
	_ = connection.SetDeadline(time.Now().Add(timeout))

	if _, err = connection.Write(append(data, '\n')); err != nil {
		return nil, fmt.Errorf("write player request: %w", err)
	}
	line, err := bufio.NewReader(connection).ReadBytes('\n')
	if err != nil {
		return nil, fmt.Errorf("read player response: %w", err)
	}

	var response playerBridgeResponse
	if err = json.Unmarshal(line, &response); err != nil {
		return nil, fmt.Errorf("decode player response: %w", err)
	}
	return &response, nil
}

func copyPlayerFlag(request map[string]interface{}, flags map[string]string, flagName string, fieldName string) {
	if value := flags[flagName]; value != "" {
		request[fieldName] = value
	}
}

func firstPositional(args []string) string {
	for _, arg := range args {
		if !strings.HasPrefix(arg, "--") {
			return arg
		}
	}
	return ""
}

func printPlayerResponse(response *playerBridgeResponse, flags map[string]string) {
	if _, asJSON := flags["json"]; asJSON {
		data, _ := json.MarshalIndent(response, "", "  ")
		fmt.Println(string(data))
		return
	}
	if response.Payload == "" {
		if response.Error != "" {
			fmt.Println(response.Error)
		}
		return
	}

	var nested interface{}
	if json.Unmarshal([]byte(response.Payload), &nested) == nil {
		data, _ := json.MarshalIndent(nested, "", "  ")
		fmt.Println(string(data))
		return
	}
	fmt.Println(response.Payload)
}
