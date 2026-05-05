# Fixed Architecture — Decision Log

This document records every architectural fix applied in v2 and the
trade-offs accepted. Read alongside FIXES.md.

---

## Fix 1 & 2: Policy Engine (Hot-Reload + OPA)

**What changed:**
- `HotReloadPolicyProvider` replaces `YamlGatewayPolicyProvider`
- `AuditingPolicyEvaluator` wraps all evaluations with structured audit entries
- `OpaGatewayPolicyProvider` is available as a one-line swap in `Program.cs`

**Trade-off accepted:**
- `FileSystemWatcher` has edge cases on some container filesystems (inotify limits).
  Mitigated: set `fs.inotify.max_user_watches=524288` in K8s node config.
- OPA adds ~2ms latency per request (HTTP to sidecar). For PCI routes, this is acceptable.
- OPA sidecar is a new operational component. Mitigated: managed via Helm chart.

---

## Fix 3: Saga Reliability

**What changed:**
- `StuckSagaMonitor`: detects and auto-remediates stuck sagas every 60s
- `SagaDeadLetterHandler`: durable dead-letter with Kafka topic + admin API
- `CompensationRetryPolicy`: exponential backoff (1s → 5s → 30s → 2m → 10m)

**Trade-off accepted:**
- Auto-compensation (force-compensate after 10min) assumes compensation is always safe.
  For payment scenarios: voiding a payment after 10min is safe.
  For inventory: releasing a reservation after 10min is safe.
  Exception: if payment was charged but not captured, void must happen within acquirer window (typically 24h).
  Documented in `CompensationRetryPolicy` code.

---

## Fix 4: Eventual Consistency Visibility

**What changed:**
- Every GET response includes `X-Read-Lag-Ms` header
- `X-Consistency: strong` forces wait (up to 3s timeout → 202 with Retry-After)
- `X-Read-Model-Stale: true; lag=XXXms` warns on stale reads > 5s

**Trade-off accepted:**
- Strong consistency adds up to 3s to read requests. Clients should only use it
  when truly necessary (e.g., immediately after a critical write).
- The 5s staleness threshold is configurable — set lower for financial reports.

---

## Fix 5 & 6: Multi-Tenancy at Scale

**What changed:**
- `TenantConnectionPool`: all tenants share PgBouncer pool (transaction mode)
- Per-tenant semaphore limits (10 concurrent connections per tenant, configurable)
- `ParallelTenantMigrationRunner`: DOP=20 parallel migrations (configurable)

**Trade-off accepted:**
- PgBouncer transaction mode requires `SET LOCAL search_path` on every transaction.
  This adds 1 DB round-trip per transaction. At 1ms per query, this is ~0.1% overhead.
- Per-tenant connection semaphore: a slow tenant blocks itself, not others.
  A single very slow tenant query could exhaust its 10-slot semaphore under burst.
  Mitigated: per-tenant query timeout via PgBouncer (30s `query_timeout`).

---

## Fix 7: Offline Conflict Resolution

**What changed:**
- 5 concrete `IConflictResolver` implementations replacing documentation-only policies
- `ConflictResolverRegistry`: plug-in resolvers per conflict type
- Manager notifications implemented as part of resolution, not afterthought

**Trade-off accepted:**
- `InventoryOversoldConflictResolver` allows the sale to stand (store wins).
  This means the cloud inventory can go negative temporarily.
  Mitigated: replenishment triggered immediately, debt recorder tracks deficit.
  Financial exposure: limited to floor limit amount × max concurrent offline stores.

---

## Fix 8: Contract Testing

**What changed:**
- Pact consumer-driven contract tests replace fragile E2E tests for interface contracts
- E2E tests retained for critical happy-path flows only (not as primary contract mechanism)
- Provider verification integrated into Sales service pipeline

**Trade-off accepted:**
- Pact requires a Pact Broker (self-hosted or PactFlow SaaS) for contract sharing.
  Dev environments can run without Broker — contracts written to local files.
- Contract tests don't validate business logic — only interface shape.
  Unit tests + integration tests still required for invariant coverage.

---

## Fix 9: Local Development

**What changed:**
- `podman-compose.dev.yaml`: full infrastructure stack, no Kubernetes
- `run-dev-local.sh`: one-command startup with preflight checks
- Dapr CLI manages service startup with sidecar injection

**Trade-off accepted:**
- Local Podman Compose doesn't replicate K8s NetworkPolicy isolation.
  PCI segment isolation is not enforced locally.
  Documented: dev environment is never PCI-compliant. Use staging for PCI validation.
- Memory requirement: ~4GB RAM for full stack locally.
  Reduced stack available: `podman-compose -f podman-compose.minimal.yaml up -d`
  (PostgreSQL + Redis + Kafka only, no OPA/Jaeger/Grafana).
