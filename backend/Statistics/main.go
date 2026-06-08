package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"os"
	"strconv"
	"time"

	"github.com/joho/godotenv"
)

func main() {
	_ = godotenv.Load()

	ctx := context.Background()
	if err := initFirebase(ctx); err != nil {
		log.Fatalf("firebase init failed: %v", err)
	}

	if err := registerWithConsul(); err != nil {
		log.Printf("warning: failed to register with consul: %v", err)
	}

	http.HandleFunc("/session-summary", createSessionSummary)

	port := os.Getenv("STATISTICS_SERVICE_PORT")
	if port == "" {
		port = "8002"
	}

	log.Printf("Starting Statistics service on port %s", port)
	log.Fatal(http.ListenAndServe(":"+port, nil))
}

func registerWithConsul() error {
	addr := os.Getenv("CONSUL_HTTP_ADDR")
	if addr == "" {
		addr = "http://consul:8500"
	}

	portStr := os.Getenv("STATISTICS_SERVICE_PORT")
	if portStr == "" {
		portStr = "8002"
	}
	port, err := strconv.Atoi(portStr)
	if err != nil {
		return fmt.Errorf("invalid port: %w", err)
	}

	id := fmt.Sprintf("statistics-%d", time.Now().Unix())

	payload := map[string]any{
		"Name": "statistics",
		"ID":   id,
		"Port": port,
		"Check": map[string]string{
			"TCP":     fmt.Sprintf("statistics:%s", portStr),
			"Interval": "10s",
		},
	}

	b, _ := json.Marshal(payload)

	req, err := http.NewRequest(http.MethodPut, addr+"/v1/agent/service/register", bytes.NewReader(b))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		return fmt.Errorf("consul register returned %s", resp.Status)
	}
	log.Printf("registered with consul at %s as %s", addr, id)
	return nil
}
