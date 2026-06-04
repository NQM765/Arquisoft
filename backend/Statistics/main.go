package main

import (
	"context"
	"log"
	"net/http"
	"os"

	"github.com/joho/godotenv"
)

func main() {
	_ = godotenv.Load()

	ctx := context.Background()
	if err := initFirebase(ctx); err != nil {
		log.Fatalf("firebase init failed: %v", err)
	}

	http.HandleFunc("/session-summary", createSessionSummary)

	port := os.Getenv("STATISTICS_SERVICE_PORT")
	if port == "" {
		port = "8002"
	}

	log.Printf("Starting Statistics service on port %s", port)
	log.Fatal(http.ListenAndServe(":"+port, nil))
}
