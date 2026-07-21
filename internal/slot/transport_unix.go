//go:build !windows

package slot

import (
	"net"
	"os"
	"time"
)

func listenEndpoint(endpoint string) (net.Listener, error) {
	_ = os.Remove(endpoint)
	return net.Listen("unix", endpoint)
}

func dialEndpoint(endpoint string, timeout time.Duration) (net.Conn, error) {
	return net.DialTimeout("unix", endpoint, timeout)
}
