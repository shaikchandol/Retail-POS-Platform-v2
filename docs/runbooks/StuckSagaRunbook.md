# Runbook: Stuck Saga Response

## Alert Conditions

| Alert | Threshold | Severity |
|---|---|---|
| `saga.stuck.detected` | Saga age > 30s | Warning |
| `saga.force_compensated` | Saga age > 10min | High |
| `saga.dead_lettered` | Saga age > 60min | Critical |

---

## Step 1: Triage (< 2 minutes)

```bash
# Check how many sagas are stuck
kubectl exec -n retail-pos deploy/checkout-saga -- \
  curl -s localhost:3500/v1.0/metadata | jq '.activeActorsCount'

# List stuck sagas via admin API
curl -s -H "X-Tenant-Id: all" \
  https://api.retail-pos.example.com/api/v1/admin/sagas?status=stuck | jq '.'

# Check Dapr Workflow state store
kubectl exec -n retail-pos deploy/checkout-saga -- \
  dapr invoke --app-id checkout-saga --method sagas/stuck --verb GET
```

**If < 5 sagas stuck:** likely transient. Wait for `StuckSagaMonitor` to auto-remediate. Monitor for 2 minutes.

**If > 5 sagas stuck:** proceed to Step 2.

---

## Step 2: Identify Root Cause

```bash
# Check Dapr sidecar health
kubectl logs -n retail-pos -l app=checkout-saga -c daprd --since=5m | grep -i error

# Check downstream service health (inventory, payments)
curl -s https://api.retail-pos.example.com/api/v1/inventory/health
curl -s https://api.retail-pos.example.com/api/v1/payments/health

# Check Kafka consumer lag
kubectl exec -n kafka kafka-0 -- \
  kafka-consumer-groups.sh --bootstrap-server localhost:9093 \
  --group checkout-saga --describe
```

**Common causes and fixes:**

| Symptom | Cause | Fix |
|---|---|---|
| All sagas stuck at `RESERVING_STOCK` | Inventory service down | Restart inventory service |
| All sagas stuck at `AUTHORISING_PAYMENT` | Payment gateway timeout | Check payment gateway status |
| Sagas stuck after pod restart | Dapr Workflow state not recovered | Force resume (Step 3) |
| Compensation stuck | Downstream service unreachable | Manual compensation (Step 4) |

---

## Step 3: Force Resume (Auto-recovery)

```bash
# Resume all stuck sagas (StuckSagaMonitor usually handles this automatically)
curl -X POST \
  -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  https://api.retail-pos.example.com/api/v1/admin/sagas/resume-stuck

# Resume a specific saga
curl -X POST \
  -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  https://api.retail-pos.example.com/api/v1/admin/sagas/{sagaId}/resume
```

---

## Step 4: Manual Compensation (if auto-compensation fails)

```bash
# 1. Get saga compensation state
SAGA_STATE=$(curl -s -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  https://api.retail-pos.example.com/api/v1/admin/sagas/{sagaId})

echo "$SAGA_STATE" | jq '.compensationState'

# 2. If inventory was reserved but not released:
curl -X POST \
  -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  https://api.retail-pos.example.com/api/v1/admin/sagas/{sagaId}/compensate \
  -d '{"steps": ["release-inventory"]}'

# 3. If payment was authorised but sale not completed:
curl -X POST \
  https://api.retail-pos.example.com/api/v1/admin/payments/{paymentId}/void \
  -H "Authorization: Bearer $PLATFORM_OPS_TOKEN"

# 4. Mark saga as manually resolved (removes from stuck queue)
curl -X POST \
  https://api.retail-pos.example.com/api/v1/admin/sagas/{sagaId}/resolve \
  -d '{"resolution": "MANUALLY_COMPENSATED", "notes": "Inventory released, payment voided"}'
```

---

## Step 5: Dead-Letter Review

Dead-lettered sagas appear in the admin portal and Kafka topic `retail-pos.sagas.dead-letter`.

```bash
# List dead-lettered sagas
curl -s -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  https://api.retail-pos.example.com/api/v1/admin/sagas?status=dead-lettered

# Replay a dead-lettered saga (after root cause fixed)
curl -X POST \
  https://api.retail-pos.example.com/api/v1/admin/sagas/{sagaId}/replay \
  -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  -d '{"dryRun": true}'   # Always dry-run first
```

---

## Prevention

- Ensure `StuckSagaMonitor` health check is green: `/health/stuck-saga-monitor`
- Set Dapr Workflow timeout appropriate to checkout SLA (default: 30s)
- Review `CompensationRetryPolicy` backoff for downstream SLAs
- Test compensation paths in staging quarterly (chaos engineering)
