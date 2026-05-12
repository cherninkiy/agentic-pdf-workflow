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
#   - curl, jq

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SAMPLES_DIR="${PROJECT_DIR}/samples"
API_URL="http://localhost:5000"
REBUILD=false

# ── Parse arguments ──
while [[ $# -gt 0 ]]; do
    case "$1" in
        --build) REBUILD=true; shift ;;
        --samples-dir) SAMPLES_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# ── Colors ──
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
pass()  { echo -e "  ${GREEN}PASS${NC} $1"; }
fail()  { echo -e "  ${RED}FAIL${NC} $1"; }
info()  { echo -e "${YELLOW}[INFO]${NC} $1"; }

# ── Step 1: Build and start services ──
info "Step 1: Starting infrastructure..."
cd "$PROJECT_DIR"

if [ "$REBUILD" = true ]; then
    info "Rebuilding Docker images..."
    docker compose build --quiet 2>/dev/null
fi

docker compose up -d postgres rabbitmq 2>/dev/null
info "Waiting for Postgres and RabbitMQ to be healthy..."
sleep 5

# ── Step 2: Start API Gateway ──
info "Step 2: Starting API Gateway..."
cd "$PROJECT_DIR/src/ApiGateway"
ASPNETCORE_URLS="http://0.0.0.0:5000" \
ASPNETCORE_ENVIRONMENT="Development" \
ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=pdf_processing;Username=pdf_user;Password=pdf_password" \
    dotnet run --no-build &
GATEWAY_PID=$!
cd "$PROJECT_DIR"

# Wait for gateway to start
sleep 3

# ── Step 3: Start Worker ──
info "Step 3: Starting Worker..."
cd "$PROJECT_DIR/src/Worker"
DOTNET_ENVIRONMENT="Development" \
ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=pdf_processing;Username=pdf_user;Password=pdf_password" \
RabbitMq__Host="localhost" \
RabbitMq__Username="guest" \
RabbitMq__Password="guest" \
Storage__LocalPath="/tmp/pdf-storage" \
    dotnet run --no-build &
WORKER_PID=$!
cd "$PROJECT_DIR"

# Wait for worker to connect
sleep 3

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
    pass "Swagger UI"
else
    fail "Swagger UI (expected if running in Development)"
fi

if [ "$HEALTH_OK" = false ]; then
    echo
    fail "Gateway did not start properly. Check logs."
    kill $GATEWAY_PID $WORKER_PID 2>/dev/null || true
    exit 1
fi

# ── Step 5: Upload sample documents ──
info "Step 5: Uploading sample documents..."
RESULTS_DIR=$(mktemp -d)
declare -A DOC_IDS
UPLOAD_FAILED=false

for pdf in "$SAMPLES_DIR"/*.pdf; do
    BASENAME=$(basename "$pdf")
    info "Uploading $BASENAME..."

    RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$API_URL/upload" \
        -F "file=@$pdf" 2>/dev/null || true)

    HTTP_CODE=$(echo "$RESPONSE" | tail -1)
    BODY=$(echo "$RESPONSE" | head -n -1)

    if [ "$HTTP_CODE" = "202" ]; then
        DOC_ID=$(echo "$BODY" | jq -r '.documentId' 2>/dev/null || echo "unknown")
        DOC_IDS["$BASENAME"]="$DOC_ID"
        pass "$BASENAME → 202 Accepted, id=$DOC_ID"
    else
        fail "$BASENAME → HTTP $HTTP_CODE (expected 202)"
        UPLOAD_FAILED=true
    fi
done

# ── Step 6: Poll for processing results ──
info "Step 6: Polling for processing results..."
MAX_POLLS=12
POLL_INTERVAL=5

for pdf_name in "${!DOC_IDS[@]}"; do
    DOC_ID="${DOC_IDS[$pdf_name]}"
    SUCCESS=false

    for ((i=1; i<=MAX_POLLS; i++)); do
        RESPONSE=$(curl -s -w "\n%{http_code}" "$API_URL/text/$DOC_ID" 2>/dev/null || true)
        HTTP_CODE=$(echo "$RESPONSE" | tail -1)
        BODY=$(echo "$RESPONSE" | head -n -1)

        if [ "$HTTP_CODE" = "200" ]; then
            TEXT_LEN=$(echo "$BODY" | jq '.extractedText | length' 2>/dev/null || echo "0")
            pass "$pdf_name → completed, text length=$TEXT_LEN chars"
            echo "$BODY" | jq -r '.extractedText' > "$RESULTS_DIR/${pdf_name%.pdf}.txt" 2>/dev/null || true
            SUCCESS=true
            break
        elif [ "$HTTP_CODE" = "202" ]; then
            echo "  Processing $pdf_name... (${i}/${MAX_POLLS})"
            sleep "$POLL_INTERVAL"
        elif [ "$HTTP_CODE" = "409" ]; then
            fail "$pdf_name → failed"
            echo "$BODY" | jq '.error' 2>/dev/null || true
            break
        else
            echo "  Unexpected HTTP $HTTP_CODE for $pdf_name"
            sleep "$POLL_INTERVAL"
        fi
    done

    if [ "$SUCCESS" = false ]; then
        fail "$pdf_name → timeout after ${MAX_POLLS} polls"
    fi
done

# ── Step 7: List documents ──
info "Step 7: Listing all documents..."
LIST_RESPONSE=$(curl -s "$API_URL/list" 2>/dev/null || echo "[]")
DOC_COUNT=$(echo "$LIST_RESPONSE" | jq length 2>/dev/null || echo "0")
echo "  Total documents: $DOC_COUNT"
echo "$LIST_RESPONSE" | jq -r '.[] | "  - \(.id | .[0:8])... \(.filename) [\(.status | .[0:1])]"' 2>/dev/null || true

# ── Summary ──
echo
info "=== Demo Summary ==="
echo "  Sample PDFs processed: ${#DOC_IDS[@]}"
echo "  Results saved to: $RESULTS_DIR"
echo "  Upload failures: $([ "$UPLOAD_FAILED" = true ] && echo 'YES' || echo 'NONE')"
echo
echo "To view extracted text: cat $RESULTS_DIR/*.txt"
echo
echo "To stop services: kill $GATEWAY_PID $WORKER_PID"
echo