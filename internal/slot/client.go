package slot

import (
	"encoding/json"
	"fmt"
	"time"
)

type Client struct {
	Endpoint string
	Timeout  time.Duration
}

func (c Client) Do(request Request) (*Response, error) {
	if request.Version == 0 {
		request.Version = ProtocolVersion
	}
	timeout := c.Timeout
	if timeout <= 0 {
		timeout = 5 * time.Second
	}
	endpoint := c.Endpoint
	if endpoint == "" {
		endpoint = DefaultEndpoint()
	}
	conn, err := dialEndpoint(endpoint, timeout)
	if err != nil {
		return nil, fmt.Errorf("connect to unity-slot-agent at %s: %w", endpoint, err)
	}
	defer func() { _ = conn.Close() }()
	_ = conn.SetDeadline(time.Now().Add(timeout))
	if err := json.NewEncoder(conn).Encode(request); err != nil {
		return nil, fmt.Errorf("send slot request: %w", err)
	}
	var response Response
	if err := json.NewDecoder(conn).Decode(&response); err != nil {
		return nil, fmt.Errorf("read slot response: %w", err)
	}
	if !response.OK {
		return &response, fmt.Errorf("%s", response.Error)
	}
	return &response, nil
}
