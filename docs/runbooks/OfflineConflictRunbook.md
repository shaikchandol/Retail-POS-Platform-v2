# Runbook: Offline Sync Conflict Resolution

## Overview

After a store syncs from offline, the `ConflictResolverRegistry` automatically
resolves all 5 conflict types. This runbook covers cases where AUTO-RESOLUTION
fails or requires human review.

---

## Conflict Types and Auto-Resolution Status

| Conflict | Auto-Resolved? | Human Review Trigger |
|---|---|---|
| PRICE_CHANGED_DURING_OFFLINE | ✅ Yes (server wins) | Price diff > 10% → customer notified |
| INVENTORY_OVERSOLD | ✅ Yes (store wins + replenishment) | Always alerts store manager |
| DUPLICATE_EVENT | ✅ Yes (silent discard) | Never — fully automatic |
| CLOCK_SKEW | ✅ Yes (dual timestamp) | Skew > 5 minutes → audit flag |
| PAYMENT_TOKEN_EXPIRED | ⚠️ Partial (re-auth attempt) | Re-auth fails → manager review |

---

## Monitoring Conflicts

```bash
# Count conflicts by type in the last 24h
curl -s -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  "https://api.retail-pos.example.com/api/v1/admin/conflicts?hours=24" | \
  jq '.conflicts | group_by(.type) | map({type: .[0].type, count: length})'

# Get unresolved conflicts (PENDING_MANAGER_REVIEW)
curl -s -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  "https://api.retail-pos.example.com/api/v1/admin/conflicts?status=pending-review"
```

---

## Handling PAYMENT_TOKEN_EXPIRED (Manual)

When re-authorisation fails, store manager receives an alert.

```
Action required:
1. Contact customer (phone/email) within 24 hours
2. Options:
   a. Customer presents card again (re-authorise in-store)
   b. Customer accepts refund (void the offline transaction)
   c. Write off if amount < floor limit (risk management decision)

Resolve in admin portal:
  Admin → Conflicts → PAYMENT_TOKEN_EXPIRED → [Mark Resolved] + notes
```

---

## Adjusting Floor Limits

Floor limit controls the maximum offline card approval amount.
Higher limit = more risk; lower limit = more declined transactions when offline.

```bash
# View current floor limit for a tenant
curl -s "https://api.retail-pos.example.com/api/v1/admin/tenants/{tenantId}/settings" | jq '.offlineFloorLimitAmount'

# Update floor limit (requires platform-ops role)
curl -X PUT \
  -H "Authorization: Bearer $PLATFORM_OPS_TOKEN" \
  "https://api.retail-pos.example.com/api/v1/admin/tenants/{tenantId}/settings" \
  -d '{"offlineFloorLimitAmount": 50.00}'
```

---

## Clock Skew Investigation

Clock skew > 5 minutes indicates the store server clock is misconfigured.

```bash
# Check NTP sync on store server (run on-site or via remote management)
timedatectl status
chronyc tracking

# Fix: ensure NTP is configured and syncing
timedatectl set-ntp true
systemctl restart chronyd
```

If clock skew is persistent, flag for hardware replacement.
