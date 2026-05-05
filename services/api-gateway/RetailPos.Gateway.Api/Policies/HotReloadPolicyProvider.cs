using YamlDotNet.Serialization;

namespace RetailPos.Gateway.Api.Policies;

/// <summary>
/// Fix #1: Policy hot-reload without service restart.
///
/// v1 Drawback: policies were cached at startup; a policy change required
/// a full pod restart (breaking in-flight requests).
///
/// Fix: FileSystemWatcher monitors the policies directory.
/// On any YAML change, policies are atomically reloaded (Interlocked swap).
/// In-flight requests complete with the old policy; new requests get new policy.
/// All reloads are audit-logged with diff summary.
/// </summary>
public class HotReloadPolicyProvider : IGatewayPolicyProvider, IDisposable
{
    private volatile GatewayPolicySet _current = new();
    private readonly FileSystemWatcher _watcher;
    private readonly IDeserializer _yaml = new DeserializerBuilder().Build();
    private readonly IPolicyAuditLogger _auditLogger;
    private readonly ILogger<HotReloadPolicyProvider> _logger;
    private readonly string _policyDir;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    public HotReloadPolicyProvider(
        IWebHostEnvironment env,
        IPolicyAuditLogger auditLogger,
        ILogger<HotReloadPolicyProvider> logger)
    {
        _policyDir   = Path.Combine(env.ContentRootPath, "Policies");
        _auditLogger = auditLogger;
        _logger      = logger;

        // Initial load
        _current = LoadFromDisk();

        // Watch for changes
        _watcher = new FileSystemWatcher(_policyDir, "*.yaml")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };
        _watcher.Changed += OnPolicyFileChanged;
        _watcher.Created += OnPolicyFileChanged;
        _watcher.Deleted += OnPolicyFileChanged;

        _logger.LogInformation("HotReloadPolicyProvider watching {Dir}", _policyDir);
    }

    public GatewayPolicySet GetPolicies() => _current;

    private async void OnPolicyFileChanged(object _, FileSystemEventArgs e)
    {
        // Debounce: editors write multiple events per save
        if (!await _reloadLock.WaitAsync(200)) return;
        try
        {
            await Task.Delay(300);   // Let the file write complete
            var previous = _current;
            var next     = LoadFromDisk();

            // Atomic swap — readers always get a consistent snapshot
            Interlocked.Exchange(ref _current, next);

            var added   = next.Policies.Select(p => p.Name).Except(previous.Policies.Select(p => p.Name)).ToList();
            var removed = previous.Policies.Select(p => p.Name).Except(next.Policies.Select(p => p.Name)).ToList();

            _logger.LogInformation("Policies hot-reloaded. Added: [{Added}] Removed: [{Removed}] Trigger: {File}",
                string.Join(", ", added), string.Join(", ", removed), e.Name);

            await _auditLogger.LogReloadAsync(new PolicyReloadAuditEntry(
                Timestamp: DateTimeOffset.UtcNow,
                TriggerFile: e.Name ?? "unknown",
                PoliciesAdded: added,
                PoliciesRemoved: removed,
                TotalPolicies: next.Policies.Count));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Policy hot-reload failed for {File}. Keeping previous policies.", e.Name);
            // Previous policies remain active — safe fallback
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private GatewayPolicySet LoadFromDisk()
    {
        var set = new GatewayPolicySet();
        if (!Directory.Exists(_policyDir)) return set;

        foreach (var file in Directory.EnumerateFiles(_policyDir, "*.yaml"))
        {
            try
            {
                var yaml   = File.ReadAllText(file);
                var policy = _yaml.Deserialize<GatewayPolicy>(yaml);
                if (policy is not null)
                    set.Policies.Add(policy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse policy file {File}. Skipping.", file);
            }
        }
        return set;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _reloadLock.Dispose();
    }
}

// ── Policy Audit Log ──────────────────────────────────────────────────────────
public interface IPolicyAuditLogger
{
    Task LogReloadAsync(PolicyReloadAuditEntry entry, CancellationToken ct = default);
    Task LogEvaluationAsync(PolicyEvaluationAuditEntry entry, CancellationToken ct = default);
}

public record PolicyReloadAuditEntry(
    DateTimeOffset Timestamp, string TriggerFile,
    List<string> PoliciesAdded, List<string> PoliciesRemoved, int TotalPolicies);

public record PolicyEvaluationAuditEntry(
    DateTimeOffset Timestamp, string PolicyName, string TenantId,
    string RoutePath, bool Allowed, string? DenyReason, long EvaluationMs);

/// <summary>
/// Fix #1b: Every policy evaluation is audit-logged (required for PCI and debugging).
/// Previously, policy denials were only logged at Warning level — no structured audit trail.
/// </summary>
public class AuditingPolicyEvaluator(IGatewayPolicyProvider provider, IPolicyAuditLogger auditLogger)
    : IPolicyEvaluator
{
    public PolicyResult Evaluate(PolicyEvaluationContext ctx)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var policies = provider.GetPolicies().Policies
            .Where(p => p.AppliesTo(ctx.RoutePath))
            .OrderBy(p => p.Priority);

        foreach (var policy in policies)
        {
            var result = policy.Evaluate(ctx);
            sw.Stop();

            // Fire-and-forget audit log (non-blocking)
            _ = auditLogger.LogEvaluationAsync(new PolicyEvaluationAuditEntry(
                Timestamp: DateTimeOffset.UtcNow,
                PolicyName: policy.Name,
                TenantId: ctx.TenantId ?? "anonymous",
                RoutePath: ctx.RoutePath,
                Allowed: result.Allowed,
                DenyReason: result.DenyReason,
                EvaluationMs: sw.ElapsedMilliseconds));

            if (!result.Allowed) return result;
        }

        return PolicyResult.Allow();
    }
}
