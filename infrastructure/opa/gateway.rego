package gateway

import future.keywords.in
import future.keywords.if
import future.keywords.contains

# Default: deny everything
default allow := false

# ── Public routes (no auth) ───────────────────────────────────────────────────
allow if {
    input.path == "/health"
}

allow if {
    startswith(input.path, "/metrics")
}

# ── Authenticated routes ───────────────────────────────────────────────────────
allow if {
    is_valid_tenant
    not is_blocked_tenant
    has_required_role
    not is_rate_limited
}

# ── Rules ─────────────────────────────────────────────────────────────────────
is_valid_tenant if {
    input.tenant_id != ""
    input.tenant_id != null
}

is_blocked_tenant if {
    data.blocked_tenants[input.tenant_id]
}

has_required_role if {
    route := matching_route
    role  := route.allowed_roles[_]
    role  in input.roles
}

# No role restrictions configured → allow (open route)
has_required_role if {
    not matching_route
}

is_rate_limited if {
    data.rate_limits[input.tenant_id].exceeded == true
}

matching_route := route if {
    some path_prefix, route in data.route_policies
    startswith(input.path, path_prefix)
}

# ── Deny reason (used by OpaGatewayPolicyProvider) ────────────────────────────
deny_reason := "MISSING_TENANT"    if { not is_valid_tenant }
deny_reason := "TENANT_BLOCKED"    if { is_blocked_tenant }
deny_reason := "INSUFFICIENT_ROLE" if { not has_required_role; is_valid_tenant; not is_blocked_tenant }
deny_reason := "RATE_LIMITED"      if { is_rate_limited }

status_code := 400 if { deny_reason == "MISSING_TENANT" }
status_code := 403 if { deny_reason != "MISSING_TENANT" }
status_code := 429 if { deny_reason == "RATE_LIMITED" }
