package main

import (
	"context"
	"encoding/json"
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

	projectID, err := resolveProjectID(credPath)
	if err != nil {
		return fmt.Errorf("could not resolve firebase project ID: %w", err)
	}

	client, err := firestore.NewClient(ctx, projectID, option.WithCredentialsFile(credPath))
	if err != nil {
		return fmt.Errorf("failed to create firestore client: %w", err)
	}

	firestoreClient = client
	return nil
}

func resolveProjectID(credPath string) (string, error) {
	if id := os.Getenv("FIREBASE_PROJECT_ID"); id != "" {
		return id, nil
	}

	data, err := os.ReadFile(credPath)
	if err != nil {
		return "", fmt.Errorf("cannot read credentials file: %w", err)
	}

	var cred struct {
		ProjectID string `json:"project_id"`
	}
	if err := json.Unmarshal(data, &cred); err != nil {
		return "", fmt.Errorf("cannot parse credentials file: %w", err)
	}

	if cred.ProjectID == "" {
		return "", fmt.Errorf("project_id not found in credentials file")
	}

	return cred.ProjectID, nil
}
