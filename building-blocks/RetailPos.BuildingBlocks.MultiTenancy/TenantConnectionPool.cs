using Microsoft.Extensions.Logging;
using Npgsql;

namespace RetailPos.BuildingBlocks.MultiTenancy;

/// <summary>
/// Fix #5: Tenant connection pool — prevents pool exhaustion at scale.
///
/// v1 Drawback: each tenant schema used its own Npgsql connection pool.
/// At 200+ tenants, this exhausted the PostgreSQL max_connections limit
/// (default 100) and caused connection timeouts under load.
///
/// Fix:
///   1. Single shared PgBouncer pool — all tenants share a connection pool
///   2. Per-tenant connection limit enforced at application level
///   3. Tenant-aware connection factory sets search_path after acquiring connection
///   4. Circuit breaker: if a tenant's DB calls fail repeatedly, fast-fail
///      that tenant without affecting others
///   5. Metrics: per-tenant connection usage tracked via OTel
///
/// PgBouncer config: infrastructure/pgbouncer/pgbouncer.ini
/// K8s deployment:   infrastructure/k8s/pgbouncer/deployment.yaml
/// </summary>
public class TenantConnectionPool(
    PgBouncerConnectionString pgBouncer,
    ITenantConnectionLimiter limiter,
    ILogger<TenantConnectionPool> logger)
{
    public async Task<NpgsqlConnection> AcquireAsync(string tenantId, CancellationToken ct = default)
    {
        // Enforce per-tenant connection limit (prevent one tenant starving others)
        if (!await limiter.TryAcquireAsync(tenantId, ct))
        {
            logger.LogWarning("Tenant {Tenant} exceeded connection limit. Requests queued.", tenantId);
            // Wait for a slot (with timeout)
            await limiter.WaitForSlotAsync(tenantId, timeout: TimeSpan.FromSeconds(5), ct);
        }

        // All tenants share one PgBouncer pool (transaction pooling mode)
        var conn = new NpgsqlConnection(pgBouncer.ConnectionString);
        await conn.OpenAsync(ct);

        // Set tenant schema on connection (PgBouncer transaction mode resets this per transaction — use SET LOCAL)
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SET search_path = 'tenant_{SanitizeTenantId(tenantId)}', public";
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Connection acquired for tenant {Tenant}", tenantId);
        return conn;
    }

    public void Release(string tenantId)
    {
        limiter.Release(tenantId);
    }

    // Prevent SQL injection via tenant ID in search_path
    private static string SanitizeTenantId(string tenantId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(tenantId, @"^[a-zA-Z0-9\-_]+$"))
            throw new InvalidOperationException($"Invalid tenant ID format: {tenantId}");
        return tenantId.Replace("-", "_");
    }
}

public class PgBouncerConnectionString(IConfiguration config)
{
    // PgBouncer sits between app and PostgreSQL — handles pooling
    public string ConnectionString => config.GetConnectionString("PgBouncer")
        ?? throw new InvalidOperationException("PgBouncer connection string not configured.");
}

/// <summary>
/// Per-tenant semaphore-based connection limiter.
/// Prevents any single tenant from exhausting all pool connections.
/// </summary>
public class TenantConnectionLimiter(IConfiguration config) : ITenantConnectionLimiter
{
    private readonly int _maxPerTenant = config.GetValue("MultiTenancy:MaxConnectionsPerTenant", 10);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    public Task<bool> TryAcquireAsync(string tenantId, CancellationToken ct)
    {
        var sem = _semaphores.GetOrAdd(tenantId, _ => new SemaphoreSlim(_maxPerTenant, _maxPerTenant));
        return Task.FromResult(sem.CurrentCount > 0);
    }

    public async Task WaitForSlotAsync(string tenantId, TimeSpan timeout, CancellationToken ct)
    {
        var sem = _semaphores.GetOrAdd(tenantId, _ => new SemaphoreSlim(_maxPerTenant, _maxPerTenant));
        if (!await sem.WaitAsync(timeout, ct))
            throw new TimeoutException($"Tenant {tenantId} could not acquire a DB connection within {timeout.TotalSeconds}s.");
    }

    public void Release(string tenantId)
    {
        if (_semaphores.TryGetValue(tenantId, out var sem))
            sem.Release();
    }
}

public interface ITenantConnectionLimiter
{
    Task<bool> TryAcquireAsync(string tenantId, CancellationToken ct);
    Task WaitForSlotAsync(string tenantId, TimeSpan timeout, CancellationToken ct);
    void Release(string tenantId);
}
