FROM golang:1.22-alpine AS gobuilder

WORKDIR /src
RUN apk add --no-cache ca-certificates git

COPY Statistics/go.mod Statistics/go.sum ./Statistics/
RUN go mod download || true
COPY Statistics /src/Statistics

WORKDIR /src/Statistics
RUN go mod download || true
RUN go build -o /app_dir/statistics .

FROM python:3.13-slim

WORKDIR /app_dir

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

# Copy the prebuilt statistics binary from the Go builder stage
COPY --from=gobuilder /app_dir/statistics /app_dir/statistics

ENV PYTHONPATH=/app_dir:/app_dir/support-service

WORKDIR /app_dir/support-service

CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
