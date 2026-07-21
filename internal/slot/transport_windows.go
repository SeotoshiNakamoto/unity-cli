//go:build windows

package slot

import (
	"net"
	"time"

	"github.com/Microsoft/go-winio"
)

func listenEndpoint(endpoint string) (net.Listener, error) {
	return winio.ListenPipe(endpoint, nil)
}

func dialEndpoint(endpoint string, timeout time.Duration) (net.Conn, error) {
	return winio.DialPipe(endpoint, &timeout)
}
