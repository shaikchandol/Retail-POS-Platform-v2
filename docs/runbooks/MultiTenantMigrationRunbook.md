# Runbook: Multi-Tenant Migration

## Running a Migration

```bash
# 1. DRY RUN first — always
kubectl create job --from=cronjob/tenant-migration migration-dryrun-$(date +%s) \
  --namespace retail-pos \
  -o yaml | \
  sed 's/dry-run.*false/dry-run\n          - "true"/' | \
  kubectl apply -f -

# Check dry run output
kubectl logs -n retail-pos job/migration-dryrun-* --follow

# 2. Apply to staging first
MIGRATION_NAME=AddSaleIndexes BUILD_ID=123 \
  envsubst < infrastructure/k8s/migrations/tenant-migration-job.yaml | \
  kubectl apply -n retail-pos-staging -f -

# 3. Verify staging (spot-check 3 tenants)
for TENANT in tenant_acme tenant_beta tenant_gamma; do
  kubectl exec -n retail-pos-staging deploy/postgres -- \
    psql -U retail_pos -c "SET search_path=$TENANT; \d sale_read_models;" retail_pos
done

# 4. Apply to production
MIGRATION_NAME=AddSaleIndexes BUILD_ID=123 \
  envsubst < infrastructure/k8s/migrations/tenant-migration-job.yaml | \
  kubectl apply -n retail-pos -f -

# 5. Monitor progress
kubectl logs -n retail-pos job/tenant-migration-123 --follow
```

---

## If a Tenant Migration Fails

Failed tenants are logged and do NOT block other tenants (parallel isolation).

```bash
# Get failed tenants from job logs
kubectl logs -n retail-pos job/tenant-migration-123 | grep "✗"

# Re-run for failed tenants only
curl -X POST \
  -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  https://api.retail-pos.example.com/api/v1/admin/migrations/retry \
  -d '{"migrationName": "AddSaleIndexes", "tenantIds": ["tenant-xyz", "tenant-abc"]}'
```

---

## Time Estimates

| Tenant Count | Sequential (old) | Parallel DOP=20 (new) |
|---|---|---|
| 50 | ~4 min | ~20 sec |
| 200 | ~17 min | ~50 sec |
| 500 | ~41 min | ~2 min |
| 1000 | ~83 min | ~4 min |

---

## Rollback

EF Core migrations are tracked per schema. To rollback:

```bash
# Rollback to specific migration for all tenants
dotnet run --project services/sales/RetailPos.Sales.Infrastructure -- \
  --rollback-to AddSaleIndexes_Previous \
  --all-tenants \
  --degree-of-parallelism 20
```
