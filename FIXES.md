# Retail POS Platform v2 — All Drawbacks Fixed

This repo addresses every architectural drawback identified in v1.
Each fix is a production-grade implementation, not a comment or placeholder.

## Fix Index

| # | Drawback | Fix | Files |
|---|---|---|---|
| 1 | Policy engine: no hot-reload, no audit | `HotReloadPolicyProvider` (FileSystemWatcher) + `PolicyAuditLogger` | `services/api-gateway/.../Policies/` |
| 2 | Policy engine: self-built, no OPA | `OpaGatewayPolicyProvider` — drop-in OPA adapter | `services/api-gateway/.../Policies/OpaGatewayPolicyProvider.cs` |
| 3 | Saga: stuck sagas, no dead-letter | `StuckSagaMonitor` + `SagaDeadLetterHandler` + `CompensationRetryPolicy` | `services/sagas/.../Monitoring/` |
| 4 | Eventual consistency: invisible to callers | `ReadConsistencyMiddleware` (X-Read-Lag header) + `ConsistencyGuard` | `building-blocks/Consistency/` |
| 5 | Multi-tenancy: connection pool exhaustion | `TenantConnectionPool` (PgBouncer-aware) + parallel migration runner | `building-blocks/MultiTenancy/`, `infrastructure/pgbouncer/` |
| 6 | Multi-tenancy: slow sequential migrations | `ParallelTenantMigrationRunner` (configurable parallelism) | `building-blocks/MultiTenancy/ParallelMigrationRunner.cs` |
| 7 | Offline: incomplete conflict resolution | Full `ConflictResolver` implementations for all 5 scenarios | `building-blocks/Offline/ConflictResolvers.cs` |
| 8 | E2E tests: slow, fragile, no contracts | Consumer-driven contract tests (Pact) + shared fixture pool | `tests/RetailPos.Tests.Contract/` |
| 9 | Operational complexity: no local dev path | `podman-compose.dev.yaml` — full stack in one command | `podman-compose.dev.yaml` |
| 10 | No runbooks for failure modes | Runbooks: stuck saga, conflict resolution, migration, offline DR | `docs/runbooks/` |

## Running the Fixed Platform Locally

```bash
# One command — no Kubernetes needed for local development
./scripts/run-dev-local.sh

# What starts:
#   PostgreSQL (port 5432)  + PgBouncer (port 6432)
#   Redis (port 6379)
#   Kafka + Zookeeper (port 9092)
#   Dapr Placement (port 50005)
#   OPA server (port 8181)
#   All services + sidecars via Dapr CLI
```

## Policy Hot-Reload (Fix #1 + #2)

Policies now reload automatically when YAML files change — no restart:
```bash
# Change a policy file
echo "  requestsPerMinute: 2000" >> policies/gateway/rate-limiting.yaml
# Gateway picks it up within 2 seconds — no deploy, no downtime
```

OPA swap:
```csharp
// In Program.cs — swap one line to switch to OPA:
// Before: builder.Services.AddSingleton<IGatewayPolicyProvider, HotReloadPolicyProvider>();
builder.Services.AddSingleton<IGatewayPolicyProvider, OpaGatewayPolicyProvider>();
```
