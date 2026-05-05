#!/usr/bin/env bash
set -euo pipefail

# Fix #9: One-command local development startup script.
# Starts the full platform stack locally using Podman Compose.
# No Kubernetes, no Azure account, no secrets required for development.

COMPOSE_FILE="$(dirname "$0")/../podman-compose.dev.yaml"
DAPR_COMPONENTS="$(dirname "$0")/../infrastructure/dapr/components/dev"

echo "╔══════════════════════════════════════════════════════╗"
echo "║   Retail POS Platform — Local Dev Start             ║"
echo "╚══════════════════════════════════════════════════════╝"

# ── Preflight checks ──────────────────────────────────────────────────────────
command -v podman          >/dev/null 2>&1 || { echo "Error: podman not installed. https://podman.io"; exit 1; }
command -v podman-compose  >/dev/null 2>&1 || { echo "Error: podman-compose not installed. pip install podman-compose"; exit 1; }
command -v dapr            >/dev/null 2>&1 || { echo "Error: dapr CLI not installed. https://docs.dapr.io/getting-started/install-dapr-cli/"; exit 1; }
command -v dotnet          >/dev/null 2>&1 || { echo "Error: .NET 10 SDK not installed."; exit 1; }

# ── Start infrastructure ──────────────────────────────────────────────────────
echo ""
echo "▶  Starting infrastructure (PostgreSQL, PgBouncer, Redis, Kafka, OPA, Jaeger, Grafana)..."
podman-compose -f "$COMPOSE_FILE" up -d

echo ""
echo "⏳  Waiting for services to be healthy..."
sleep 10

# ── Apply dev database migrations ─────────────────────────────────────────────
echo ""
echo "▶  Running dev database migrations..."
dotnet run --project services/sales/RetailPos.Sales.Infrastructure -- --migrate --tenant dev-tenant-001 || true

# ── Start services with Dapr sidecar ─────────────────────────────────────────
echo ""
echo "▶  Starting services with Dapr sidecars..."

dapr run \
  --app-id api-gateway \
  --app-port 5000 \
  --dapr-http-port 3500 \
  --components-path "$DAPR_COMPONENTS" \
  -- dotnet run --project services/api-gateway/RetailPos.Gateway.Api &

dapr run \
  --app-id sales-service \
  --app-port 5001 \
  --dapr-http-port 3501 \
  --components-path "$DAPR_COMPONENTS" \
  -- dotnet run --project services/sales/RetailPos.Sales.Api &

dapr run \
  --app-id bff-pos \
  --app-port 5010 \
  --dapr-http-port 3510 \
  --components-path "$DAPR_COMPONENTS" \
  -- dotnet run --project services/bff/RetailPos.Bff.Pos &

dapr run \
  --app-id checkout-saga \
  --app-port 5020 \
  --dapr-http-port 3520 \
  --components-path "$DAPR_COMPONENTS" \
  -- dotnet run --project services/sagas/RetailPos.Sagas.Checkout &

echo ""
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║   Platform ready!                                           ║"
echo "║                                                              ║"
echo "║   API Gateway:    http://localhost:5000                     ║"
echo "║   POS BFF:        http://localhost:5010                     ║"
echo "║   Jaeger (Traces):http://localhost:16686                    ║"
echo "║   Grafana:        http://localhost:3000  (no login needed)  ║"
echo "║   OPA Playground: http://localhost:8181                     ║"
echo "║   PostgreSQL:     localhost:6432 (via PgBouncer)            ║"
echo "║                                                              ║"
echo "║   Stop:  CTRL+C then: podman-compose down                   ║"
echo "╚══════════════════════════════════════════════════════════════╝"

wait
