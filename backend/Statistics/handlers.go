package main

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"
)

type authPrincipal struct {
	UserID   int64
	Username string
}

type sessionSummaryPayload struct {
	TotalWood          int     `json:"totalWood"`
	TotalGold          int     `json:"totalGold"`
	TotalGatherActions int     `json:"totalGatherActions"`
	ElapsedSeconds     float64 `json:"elapsedSeconds"`
	StartedAt          string  `json:"startedAt"`
	FinishedAt         string  `json:"finishedAt"`
}

func validateToken(tokenStr string) (*authPrincipal, error) {
	secret := os.Getenv("AUTH_TOKEN_SECRET")
	if secret == "" {
		secret = "dev-auth-token-secret-change-me"
	}

	parts := strings.SplitN(tokenStr, ".", 2)
	if len(parts) != 2 {
		return nil, errors.New("invalid token format")
	}
	encodedPayload, encodedSig := parts[0], parts[1]

	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(encodedPayload))
	expected := mac.Sum(nil)

	provided, err := base64.RawURLEncoding.DecodeString(encodedSig)
	if err != nil {
		return nil, errors.New("invalid signature encoding")
	}
	if !hmac.Equal(expected, provided) {
		return nil, errors.New("invalid signature")
	}

	payloadBytes, err := base64.RawURLEncoding.DecodeString(encodedPayload)
	if err != nil {
		return nil, errors.New("invalid payload encoding")
	}

	var claims struct {
		Sub      string `json:"sub"`
		Username string `json:"username"`
		Exp      int64  `json:"exp"`
	}
	if err := json.Unmarshal(payloadBytes, &claims); err != nil {
		return nil, fmt.Errorf("invalid payload JSON: %w", err)
	}

	if time.Now().Unix() > claims.Exp {
		return nil, errors.New("token expired")
	}

	userID, err := strconv.ParseInt(claims.Sub, 10, 64)
	if err != nil {
		return nil, errors.New("invalid user_id in token")
	}

	return &authPrincipal{UserID: userID, Username: claims.Username}, nil
}

func parseDateTime(s string) (time.Time, error) {
	for _, layout := range []string{time.RFC3339Nano, time.RFC3339} {
		if t, err := time.Parse(layout, s); err == nil {
			return t, nil
		}
	}
	return time.Time{}, fmt.Errorf("cannot parse %q as datetime", s)
}

func createSessionSummary(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	authHeader := r.Header.Get("Authorization")
	if !strings.HasPrefix(authHeader, "Bearer ") {
		http.Error(w, "missing or invalid Authorization header", http.StatusUnauthorized)
		return
	}
	principal, err := validateToken(strings.TrimPrefix(authHeader, "Bearer "))
	if err != nil {
		http.Error(w, "unauthorized: "+err.Error(), http.StatusUnauthorized)
		return
	}

	var payload sessionSummaryPayload
	if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
		http.Error(w, "invalid request body: "+err.Error(), http.StatusBadRequest)
		return
	}

	startedAt, err := parseDateTime(payload.StartedAt)
	if err != nil {
		http.Error(w, "invalid startedAt: "+err.Error(), http.StatusBadRequest)
		return
	}
	finishedAt, err := parseDateTime(payload.FinishedAt)
	if err != nil {
		http.Error(w, "invalid finishedAt: "+err.Error(), http.StatusBadRequest)
		return
	}

	ctx := context.Background()
	_, _, err = firestoreClient.Collection("match_summaries").Add(ctx, map[string]any{
		"totalWood":          int64(payload.TotalWood),
		"totalGold":          int64(payload.TotalGold),
		"totalGatherActions": int64(payload.TotalGatherActions),
		"elapsedSeconds":     payload.ElapsedSeconds,
		"startedAt":          startedAt,
		"finishedAt":         finishedAt,
		"userId":             principal.UserID,
		"username":           principal.Username,
	})
	if err != nil {
		http.Error(w, "failed to save summary: "+err.Error(), http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	_ = json.NewEncoder(w).Encode(map[string]string{"message": "Session summary saved successfully"})
}
