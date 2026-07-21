package slot

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"time"
)

func newJobID() (string, error) {
	random := make([]byte, 4)
	if _, err := rand.Read(random); err != nil {
		return "", fmt.Errorf("generate validation job id: %w", err)
	}
	return fmt.Sprintf("val-%s-%s", time.Now().UTC().Format("20060102-150405"), hex.EncodeToString(random)), nil
}
