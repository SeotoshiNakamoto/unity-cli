package cmd

import (
	"bufio"
	"encoding/json"
	"net"
	"os"
	"testing"
	"time"
)

func TestSendPlayerCommandUsesLoopbackAndMapsConvenienceFields(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = listener.Close() }()

	requestChannel := make(chan map[string]interface{}, 1)
	go func() {
		connection, acceptErr := listener.Accept()
		if acceptErr != nil {
			return
		}
		defer func() { _ = connection.Close() }()

		line, readErr := bufio.NewReader(connection).ReadBytes('\n')
		if readErr != nil {
			return
		}
		var request map[string]interface{}
		if json.Unmarshal(line, &request) != nil {
			return
		}
		requestChannel <- request
		_, _ = connection.Write([]byte(`{"id":"1","ok":true,"error":"","payload":"accepted"}` + "\n"))
	}()

	port := listener.Addr().(*net.TCPAddr).Port
	response, err := sendPlayerCommand(port, "secret", "createRoom", map[string]string{
		"room-name": "smoke-room",
	}, 1000)
	if err != nil {
		t.Fatal(err)
	}
	if !response.OK || response.Payload != "accepted" {
		t.Fatalf("unexpected response: %+v", response)
	}

	select {
	case request := <-requestChannel:
		if request["token"] != "secret" || request["command"] != "createRoom" || request["roomName"] != "smoke-room" {
			t.Fatalf("unexpected request: %#v", request)
		}
	case <-time.After(time.Second):
		t.Fatal("server did not receive request")
	}
}

func TestPlayerKillRequiresForce(t *testing.T) {
	err := killPlayer(map[string]string{"pid": "123"}, 100)
	if err == nil {
		t.Fatal("expected --force safety error")
	}
}

func TestPlayerLaunchRequiresExplicitPortAndToken(t *testing.T) {
	if err := launchPlayer(map[string]string{"exe": "missing.exe", "token": "x"}, 0, 100); err == nil {
		t.Fatal("expected missing port error")
	}
	if err := launchPlayer(map[string]string{"exe": "missing.exe"}, 47100, 100); err == nil {
		t.Fatal("expected missing token error")
	}
}

func TestPlayerLaunchPassesStableIdentity(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}

	original := startPlayerProcess
	t.Cleanup(func() { startPlayerProcess = original })
	var launchedArgs []string
	startPlayerProcess = func(_ string, args []string) (int, error) {
		launchedArgs = append([]string(nil), args...)
		return 4321, nil
	}

	err = launchPlayer(map[string]string{
		"exe":              executable,
		"token":            "control-token",
		"identity":         "player-a",
		"matching-address": "10.220.150.31",
		"matching-port":    "7777",
	}, 47100, 100)
	if err != nil {
		t.Fatal(err)
	}

	if !containsAdjacent(launchedArgs, "-e2eIdentity", "player-a") {
		t.Fatalf("identity argument missing: %#v", launchedArgs)
	}
	if !containsAdjacent(launchedArgs, "-e2eMatchingAddress", "10.220.150.31") ||
		!containsAdjacent(launchedArgs, "-e2eMatchingPort", "7777") {
		t.Fatalf("matching endpoint arguments missing: %#v", launchedArgs)
	}
}

func containsAdjacent(values []string, first string, second string) bool {
	for i := 0; i+1 < len(values); i++ {
		if values[i] == first && values[i+1] == second {
			return true
		}
	}
	return false
}
