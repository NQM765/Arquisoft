package main

import (
	"context"
	"fmt"
	"os"

	"cloud.google.com/go/firestore"
	"google.golang.org/api/option"
)

var firestoreClient *firestore.Client

func initFirebase(ctx context.Context) error {
	credPath := os.Getenv("FIREBASE_CREDENTIALS")
	if credPath == "" {
		return fmt.Errorf("FIREBASE_CREDENTIALS is not set")
	}

	if _, err := os.Stat(credPath); err != nil {
		return fmt.Errorf("firebase credentials file not found: %w", err)
	}

	projectID := os.Getenv("FIREBASE_PROJECT_ID")
	if projectID == "" {
		return fmt.Errorf("FIREBASE_PROJECT_ID is not set")
	}

	client, err := firestore.NewClient(ctx, projectID, option.WithCredentialsFile(credPath))
	if err != nil {
		return fmt.Errorf("failed to create firestore client: %w", err)
	}

	firestoreClient = client
	return nil
}
