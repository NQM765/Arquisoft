FROM golang:1.22-alpine

WORKDIR /app

RUN apk add --no-cache ca-certificates git

COPY go.mod .
RUN go mod download

COPY . .
RUN go build -o statistics .

EXPOSE 8001

CMD ["./statistics"]