#!/usr/bin/env bash
# scripts/demo.sh — Automated end-to-end demo of PDF Processing System
#
# Usage:
#   ./scripts/demo.sh [--build] [--samples-dir ../samples]
#
# Options:
#   --build       Rebuild Docker images before starting
#   --samples-dir Path to sample PDF files (default ../samples)
#
# Prerequisites:
#   - Docker + Docker Compose
#   - curl, jq, dotnet 8

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SAMPLES_DIR="${PROJECT_DIR}/samples"
API_URL="http://localhost:5000"
LOG_DIR="/tmp/pdf-demo-logs"
REBUILD=false

# ── Parse arguments ──
while [[ $# -gt 0 ]]; do
    case "$1" in
        --build) REBUILD=true; shift ;;
        --samples-dir) SAMPLES_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

mkdir -p "$LOG_DIR"

# ── Colors ──
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
pass()  { echo -e "  ${GREEN}PASS${NC} $1"; }
fail()  { echo -e "  ${RED}FAIL${NC} $1"; }
info()  { echo -e "${YELLOW}[INFO]${NC} $1"; }

# ── Cleanup on exit ──
cleanup() {
    info "Cleaning up..."
    kill $GATEWAY_PID $WORKER_PID 2>/dev/null || true
    docker compose -f "$PROJECT_DIR/docker-compose.yml" down -v 2>/dev/null || true
    info "Done. Logs: $LOG_DIR"
}
trap cleanup EXIT

# ── Load .env if present ──
if [ -f "$PROJECT_DIR/.env" ]; then
    set -a
    source "$PROJECT_DIR/.env"
    set +a
fi

# ── Step 1: Build and start infrastructure ──
info "Step 1: Building projects..."
cd "$PROJECT_DIR"
dotnet build src/ApiGateway -q 2>&1 | tail -1 || true
dotnet build src/Worker -q 2>&1 | tail -1 || true

info "Starting PostgreSQL (5432) and RabbitMQ (5672, 15672)..."
docker compose up -d postgres rabbitmq 2>/dev/null
echo "  Ports: 5432 ← PostgreSQL | 5672 AMQP | 15672 ← RabbitMQ Management"
echo "  Credentials: ${RABBITMQ_USER:-pdf_user} / ${RABBITMQ_PASSWORD:-pdf_password}"

# ── Step 2: Start API Gateway ──
info "Step 2: Starting API Gateway on :5000..."
cd "$PROJECT_DIR/src/ApiGateway"
ASPNETCORE_URLS="http://0.0.0.0:5000" \
ASPNETCORE_ENVIRONMENT="Development" \
ConnectionStrings__DefaultConnection="Host=localhost;Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB:-pdf_processing};Username=${POSTGRES_USER:-pdf_user};Password=${POSTGRES_PASSWORD:-pdf_password}" \
RabbitMq__Host="localhost" \
RabbitMq__Username="${RABBITMQ_USER:-pdf_user}" \
RabbitMq__Password="${RABBITMQ_PASSWORD:-pdf_password}" \
    dotnet run --no-launch-profile > "$LOG_DIR/gateway.log" 2>&1 &
GATEWAY_PID=$!
cd "$PROJECT_DIR"

# Wait for gateway to be ready
for i in $(seq 1 10); do
    if curl -sf "$API_URL/health/live" >/dev/null 2>&1; then break; fi
    sleep 1
done

# ── Step 3: Start Worker ──
info "Step 3: Starting Worker (connects to localhost:5672)..."
cd "$PROJECT_DIR/src/Worker"
DOTNET_ENVIRONMENT="Development" \
ConnectionStrings__DefaultConnection="Host=localhost;Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB:-pdf_processing};Username=${POSTGRES_USER:-pdf_user};Password=${POSTGRES_PASSWORD:-pdf_password}" \
RabbitMq__Host="localhost" \
RabbitMq__Username="${RABBITMQ_USER:-pdf_user}" \
RabbitMq__Password="${RABBITMQ_PASSWORD:-pdf_password}" \
Storage__LocalPath="/tmp/pdf-storage" \
    dotnet run --no-launch-profile > "$LOG_DIR/worker.log" 2>&1 &
WORKER_PID=$!
cd "$PROJECT_DIR"

sleep 2

# ── Step 4: Health checks ──
info "Step 4: Checking health endpoints..."
HEALTH_OK=true

if curl -sf "$API_URL/health/live" >/dev/null 2>&1; then
    pass "Gateway /health/live"
else
    fail "Gateway /health/live"
    HEALTH_OK=false
fi

if curl -sf "$API_URL/swagger" >/dev/null 2>&1; then
    pass "Swagger UI (http://localhost:5000/swagger)"
else
    echo "  Swagger not available (expected if swagger disabled)"
fi

if [ "$HEALTH_OK" = false ]; then
    echo
    fail "Gateway did not start. Check logs: $LOG_DIR/gateway.log"
    exit 1
fi

# ── Step 5: Upload sample documents ──
info "Step 5: Uploading sample documents..."
RESULTS_DIR=$(mktemp -d)
declare -A DOC_IDS
UPLOAD_FAILED=false

for pdf in "$SAMPLES_DIR"/*.pdf; do
    BASENAME=$(basename "$pdf")
    echo -n "  Uploading $BASENAME... "

    RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$API_URL/upload" \
        -F "file=@$pdf" 2>/dev/null || true)

    HTTP_CODE=$(echo "$RESPONSE" | tail -1)
    BODY=$(echo "$RESPONSE" | head -n -1)

    if [ "$HTTP_CODE" = "202" ]; then
        DOC_ID=$(echo "$BODY" | jq -r '.documentId' 2>/dev/null || echo "unknown")
        DOC_IDS["$BASENAME"]="$DOC_ID"
        pass "$BASENAME → 202 Accepted"
    else
        fail "$BASENAME → HTTP $HTTP_CODE (expected 202)"
        UPLOAD_FAILED=true
    fi
done

# ── Step 6: Poll for processing results ──
echo
info "Step 6: Waiting for processing..."
echo "  Worker checks RabbitMQ every 5s (OutboxPublisher)"
echo "  Polling /text/{id} until each document completes..."
echo

MAX_POLLS=24        # 24 × 5s = 2 min timeout (OCR + Tesseract)
POLL_INTERVAL=5

ALL_DONE=false
CLOCK=0
while [ "$ALL_DONE" = false ] && [ $CLOCK -lt $MAX_POLLS ]; do
    ALL_DONE=true

    for pdf_name in "${!DOC_IDS[@]}"; do
        DOC_ID="${DOC_IDS[$pdf_name]}"

        RESPONSE=$(curl -s -w "\n%{http_code}" "$API_URL/text/$DOC_ID" 2>/dev/null || true)
        HTTP_CODE=$(echo "$RESPONSE" | tail -1)
        BODY=$(echo "$RESPONSE" | head -n -1)

        if [ "$HTTP_CODE" = "200" ]; then
            TEXT_LEN=$(echo "$BODY" | jq '.extractedText | length' 2>/dev/null || echo "0")
            echo "  ✓ $pdf_name → COMPLETED (${TEXT_LEN} chars)"
            echo "$BODY" | jq -r '.extractedText' > "$RESULTS_DIR/${pdf_name%.pdf}.txt" 2>/dev/null || true
            unset DOC_IDS["$pdf_name"]   # remove from polling list
        elif [ "$HTTP_CODE" = "202" ]; then
            STATUS=$(echo "$BODY" | jq -r '.status' 2>/dev/null || echo "processing")
            echo "  ⟳ $pdf_name → $STATUS"
            ALL_DONE=false
        elif [ "$HTTP_CODE" = "409" ]; then
            echo "  ✗ $pdf_name → FAILED"
            echo "$BODY" | jq '.error' 2>/dev/null || true
            unset DOC_IDS["$pdf_name"]
        fi
    done

    if [ "$ALL_DONE" = false ] && [ ${#DOC_IDS[@]} -gt 0 ]; then
        sleep "$POLL_INTERVAL"
        CLOCK=$((CLOCK + 1))
    fi
done

if [ ${#DOC_IDS[@]} -gt 0 ]; then
    echo
    for pdf_name in "${!DOC_IDS[@]}"; do
        fail "$pdf_name → timeout (${MAX_POLLS} polls)"
    done
fi

# ── Step 7: Final status ──
echo
info "Step 7: Final document statuses..."
LIST_RESPONSE=$(curl -s "$API_URL/list" 2>/dev/null || echo "[]")
echo "$LIST_RESPONSE" | jq -r '
    .[] | "  [\(.status)] \(.filename) — \(.created_at | .[0:10])"
' 2>/dev/null || echo "  (empty)"
DOC_COUNT=$(echo "$LIST_RESPONSE" | jq length 2>/dev/null || echo "0")
SUCCESS_COUNT=$(echo "$LIST_RESPONSE" | jq '[.[] | select(.status == "Completed")] | length' 2>/dev/null || echo "0")
FAIL_COUNT=$(echo "$LIST_RESPONSE" | jq '[.[] | select(.status == "Failed")] | length' 2>/dev/null || echo "0")

# ── Summary ──
echo
info "=== Demo Summary ==="
echo "  API Gateway:       http://localhost:5000"
echo "  Swagger UI:        http://localhost:5000/swagger"
echo "  RabbitMQ Mgmt:     http://localhost:15672 (${RABBITMQ_USER:-pdf_user} / ${RABBITMQ_PASSWORD:-pdf_password})"
echo "  Documents:         $DOC_COUNT total | ${SUCCESS_COUNT} completed | ${FAIL_COUNT} failed"
echo "  Logs:              $LOG_DIR/"
if [ -d "$RESULTS_DIR" ]; then
    echo "  Extracted text:    $RESULTS_DIR/"
fi
echo
echo "  Press Enter to stop all services and clean up."
read -r
echo

wait
