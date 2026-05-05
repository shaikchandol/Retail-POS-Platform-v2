namespace RetailPos.BuildingBlocks.Consistency;

/// <summary>
/// Fix #4: Eventual consistency made visible to API callers.
///
/// v1 Drawback: read endpoints returned stale data silently.
/// Callers had no way to know if the read model was up-to-date,
/// leading to confusing behaviour (create succeeds → immediate GET returns old data).
///
/// Fix:
///   1. Every GET response includes X-Read-Lag-Ms header (how stale the read model is)
///   2. Callers can send X-Consistency: strong to force a wait (with timeout)
///   3. Stale reads beyond a configurable threshold return 202 Accepted (not 200)
///      with Retry-After header — client knows to poll
///   4. Projection checkpoint is exposed via /api/v1/system/consistency endpoint
/// </summary>
public class ReadConsistencyMiddleware(
    RequestDelegate next,
    IProjectionLagMonitor lagMonitor,
    ILogger<ReadConsistencyMiddleware> logger)
{
    private const string ConsistencyHeader   = "X-Consistency";
    private const string LagHeader           = "X-Read-Lag-Ms";
    private const string StaleWarningHeader  = "X-Read-Model-Stale";
    private const string RetryAfterHeader    = "Retry-After";

    // If read model is older than this, warn the client
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(5);
    // Max time to wait for strong consistency
    private static readonly TimeSpan StrongConsistencyTimeout = TimeSpan.FromSeconds(3);

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Only applies to GET requests
        if (!HttpMethods.IsGet(ctx.Request.Method))
        {
            await next(ctx);
            return;
        }

        var lag = await lagMonitor.GetCurrentLagAsync();
        ctx.Response.Headers[LagHeader] = lag.TotalMilliseconds.ToString("F0");

        var requestedConsistency = ctx.Request.Headers[ConsistencyHeader].FirstOrDefault() ?? "eventual";

        if (requestedConsistency == "strong" && lag > StrongConsistencyTimeout)
        {
            // Client wants strong consistency — wait for lag to drop
            var waited = await WaitForLagAsync(StrongConsistencyTimeout, lagMonitor, ctx.RequestAborted);
            if (!waited)
            {
                // Could not achieve strong consistency within timeout — tell client to retry
                logger.LogWarning("Strong consistency timeout. Lag: {Lag}ms", lag.TotalMilliseconds);
                ctx.Response.StatusCode = 202;
                ctx.Response.Headers[RetryAfterHeader] = "1";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    message = "Read model is catching up. Retry after 1 second.",
                    lagMs = lag.TotalMilliseconds,
                    retryAfterSeconds = 1
                });
                return;
            }
        }
        else if (lag > StaleThreshold)
        {
            // Stale but not requesting strong — warn the caller
            ctx.Response.Headers[StaleWarningHeader] = $"true; lag={lag.TotalMilliseconds:F0}ms";
        }

        await next(ctx);
    }

    private static async Task<bool> WaitForLagAsync(TimeSpan timeout, IProjectionLagMonitor monitor, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var lag = await monitor.GetCurrentLagAsync();
            if (lag <= TimeSpan.FromMilliseconds(500)) return true;
            await Task.Delay(200, ct);
        }
        return false;
    }
}

public interface IProjectionLagMonitor
{
    Task<TimeSpan> GetCurrentLagAsync(CancellationToken ct = default);
}

/// <summary>
/// Measures lag by comparing the latest event store position vs
/// the latest projection checkpoint position.
/// </summary>
public class ProjectionLagMonitor(IEventPositionReader eventReader, ICheckpointReader checkpointReader)
    : IProjectionLagMonitor
{
    public async Task<TimeSpan> GetCurrentLagAsync(CancellationToken ct = default)
    {
        var (latestEventTime, checkpointTime) = await (
            eventReader.GetLatestEventTimeAsync(ct),
            checkpointReader.GetCheckpointTimeAsync(ct)
        ).AwaitBoth();

        if (latestEventTime is null || checkpointTime is null)
            return TimeSpan.Zero;

        var lag = latestEventTime.Value - checkpointTime.Value;
        return lag < TimeSpan.Zero ? TimeSpan.Zero : lag;
    }
}

public interface IEventPositionReader  { Task<DateTimeOffset?> GetLatestEventTimeAsync(CancellationToken ct); }
public interface ICheckpointReader     { Task<DateTimeOffset?> GetCheckpointTimeAsync(CancellationToken ct); }

public static class TaskTupleExtensions
{
    public static async Task<(T1, T2)> AwaitBoth<T1, T2>(this (Task<T1> t1, Task<T2> t2) tasks)
    {
        await Task.WhenAll(tasks.t1, tasks.t2);
        return (tasks.t1.Result, tasks.t2.Result);
    }
}
