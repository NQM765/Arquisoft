package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

type SessionSummary struct {
	UserID      int               `json:"user_id"`
	MatchID     string            `json:"match_id"`
	SummaryData map[string]any    `json:"summary_data"`
}

func createSessionSummary(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	var payload SessionSummary
	if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
		http.Error(w, "invalid request body: "+err.Error(), http.StatusBadRequest)
		return
	}

	if payload.UserID == 0 || payload.MatchID == "" {
		http.Error(w, "user_id and match_id are required", http.StatusBadRequest)
		return
	}

	docID := fmt.Sprintf("%d_%s", payload.UserID, payload.MatchID)
	ctx := context.Background()

	_, err := firestoreClient.Collection("session_summaries").Doc(docID).Set(ctx, map[string]any{
		"user_id":      payload.UserID,
		"match_id":     payload.MatchID,
		"summary_data": payload.SummaryData,
		"timestamp":    time.Now(),
	})
	if err != nil {
		http.Error(w, "failed to save summary: "+err.Error(), http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	_ = json.NewEncoder(w).Encode(map[string]any{"message": "Session summary saved successfully", "id": docID})
}

func getSessionSummary(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	path := strings.TrimPrefix(r.URL.Path, "/session-summary/")
	parts := strings.SplitN(path, "/", 2)
	if len(parts) != 2 {
		http.Error(w, "invalid path, use /session-summary/{user_id}/{match_id}", http.StatusBadRequest)
		return
	}

	userID, err := strconv.Atoi(parts[0])
	if err != nil {
		http.Error(w, "invalid user_id", http.StatusBadRequest)
		return
	}

	docID := fmt.Sprintf("%d_%s", userID, parts[1])
	ctx := context.Background()
	doc, err := firestoreClient.Collection("session_summaries").Doc(docID).Get(ctx)
	if err != nil {
		http.Error(w, "failed to read summary: "+err.Error(), http.StatusInternalServerError)
		return
	}

	data := doc.Data()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(data)
}
