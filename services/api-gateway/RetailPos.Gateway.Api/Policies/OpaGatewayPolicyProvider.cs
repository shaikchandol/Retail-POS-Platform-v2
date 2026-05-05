using System.Net.Http.Json;
using System.Text.Json;

namespace RetailPos.Gateway.Api.Policies;

/// <summary>
/// Fix #2: OPA (Open Policy Agent) adapter — drop-in replacement for the YAML engine.
///
/// v1 Drawback: self-built YAML policy engine lacked a query language,
/// UI, hot-reload, and evaluation audit trails. Teams with complex policy
/// needs had to extend custom C# code.
///
/// Fix: OpaGatewayPolicyProvider delegates evaluation to an OPA sidecar.
/// - Policy logic moves to Rego files (version-controlled alongside YAML)
/// - OPA provides a REST API, UI (OPA Playground), and built-in audit
/// - Zero application code changes to add new policy logic
/// - Hot-reload: OPA bundles update policies without service restart
///
/// Swap: change ONE line in Program.cs:
///   builder.Services.AddSingleton&lt;IGatewayPolicyProvider, OpaGatewayPolicyProvider&gt;();
///
/// OPA sidecar runs as a Kubernetes sidecar container (see infrastructure/opa/).
/// </summary>
public class OpaGatewayPolicyProvider(IHttpClientFactory httpFactory, ILogger<OpaGatewayPolicyProvider> logger)
    : IGatewayPolicyProvider, IPolicyEvaluator
{
    // OPA sidecar runs on localhost:8181 (K8s sidecar pattern)
    private const string OpaUrl = "http://localhost:8181/v1/data/gateway/allow";

    // IGatewayPolicyProvider: OPA manages policies internally via bundles
    public GatewayPolicySet GetPolicies() => new();   // OPA owns policies; not fetched locally

    // IPolicyEvaluator: delegate to OPA REST API
    public PolicyResult Evaluate(PolicyEvaluationContext ctx)
    {
        // Run synchronously for middleware pipeline compatibility
        return EvaluateAsync(ctx).GetAwaiter().GetResult();
    }

    private async Task<PolicyResult> EvaluateAsync(PolicyEvaluationContext ctx)
    {
        var client = httpFactory.CreateClient("opa");

        var opaInput = new
        {
            input = new
            {
                path       = ctx.RoutePath,
                tenant_id  = ctx.TenantId,
                roles      = ctx.UserRoles,
                claims     = ctx.Claims,
                client_ip  = ctx.ClientIp,
                method     = "POST",
                timestamp  = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        try
        {
            var sw       = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync(OpaUrl, opaInput);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("OPA returned {Status} — failing open (allow). Latency: {Ms}ms",
                    response.StatusCode, sw.ElapsedMilliseconds);
                // Fail-open: if OPA is unreachable, allow through (configure fail-closed per risk appetite)
                return PolicyResult.Allow();
            }

            var result = await response.Content.ReadFromJsonAsync<OpaResponse>();

            if (result?.Result?.Allow == true)
                return PolicyResult.Allow();

            var reason = result?.Result?.DenyReason ?? "OPA_DENY";
            var status = result?.Result?.StatusCode ?? 403;
            return PolicyResult.Deny(reason, status);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "OPA sidecar unreachable — using fail-open. Configure fail-closed for PCI routes.");
            return PolicyResult.Allow();   // Change to Deny for fail-closed
        }
    }

    private record OpaResponse(OpaResult? Result);
    private record OpaResult(bool Allow, string? DenyReason, int StatusCode = 403);
}

/// <summary>
/// OPA policy written in Rego — lives in infrastructure/opa/gateway.rego
/// This is the policy that replaces all the YAML files:
///
/// package gateway
///
/// default allow = false
///
/// allow {
///     input.path startswith "/health"
/// }
///
/// allow {
///     input.tenant_id != ""
///     has_required_role
///     not blocked_tenant
/// }
///
/// has_required_role {
///     route_roles := route_policy[input.path].roles
///     route_roles[_] == input.roles[_]
/// }
///
/// blocked_tenant {
///     data.blocked_tenants[input.tenant_id]
/// }
/// </summary>
internal static class OpaRegoReference { }
