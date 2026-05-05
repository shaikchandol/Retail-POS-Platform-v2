using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RetailPos.Sagas.Checkout.Monitoring;

/// <summary>
/// Fix #3a: Stuck saga detection and alerting.
///
/// v1 Drawback: sagas could get stuck (compensation failure, Dapr crash mid-workflow)
/// with no detection, no alerting, and no remediation path.
///
/// Fix: StuckSagaMonitor runs every 60 seconds, finds sagas that have been
/// running longer than their expected SLA, and:
///   1. Emits a structured alert (Slack/PagerDuty via OTel alerting)
///   2. Attempts automatic remediation (resume if transient, compensate if terminal)
///   3. Publishes to dead-letter topic if not recoverable
///   4. Records in audit log for manual review
/// </summary>
public class StuckSagaMonitor(
    ISagaStateRepository sagaRepo,
    ISagaRemediator remediator,
    ISagaAlertPublisher alertPublisher,
    ILogger<StuckSagaMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval       = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckoutSagaSla     = TimeSpan.FromSeconds(30);   // Checkout must finish in 30s
    private static readonly TimeSpan MaxRetryWindow      = TimeSpan.FromMinutes(10);   // Auto-retry for 10 min
    private static readonly TimeSpan TerminalStaleness   = TimeSpan.FromHours(1);      // Dead-letter after 1hr

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("StuckSagaMonitor started.");
        while (!ct.IsCancellationRequested)
        {
            await DetectAndRemediateAsync(ct);
            await Task.Delay(CheckInterval, ct);
        }
    }

    private async Task DetectAndRemediateAsync(CancellationToken ct)
    {
        var stuck = await sagaRepo.GetStuckSagasAsync(olderThan: CheckoutSagaSla, ct);
        if (!stuck.Any()) return;

        logger.LogWarning("Found {Count} stuck sagas.", stuck.Count);

        foreach (var saga in stuck)
        {
            var age = DateTimeOffset.UtcNow - saga.StartedAt;

            if (age < MaxRetryWindow)
            {
                // Transient: attempt to resume
                await TryResumeAsync(saga, ct);
            }
            else if (age < TerminalStaleness)
            {
                // Degraded: force compensation
                await ForceCompensateAsync(saga, ct);
            }
            else
            {
                // Terminal: dead-letter
                await DeadLetterAsync(saga, ct);
            }
        }
    }

    private async Task TryResumeAsync(SagaState saga, CancellationToken ct)
    {
        try
        {
            await remediator.ResumeAsync(saga.SagaId, ct);
            logger.LogInformation("Resumed stuck saga {SagaId} (age: {Age}s)", saga.SagaId, (DateTimeOffset.UtcNow - saga.StartedAt).TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resume saga {SagaId}.", saga.SagaId);
        }
    }

    private async Task ForceCompensateAsync(SagaState saga, CancellationToken ct)
    {
        logger.LogWarning("Force-compensating saga {SagaId} after {Age}min.", saga.SagaId, (DateTimeOffset.UtcNow - saga.StartedAt).TotalMinutes);
        await alertPublisher.PublishAsync(new SagaAlert(saga.SagaId, "FORCE_COMPENSATION", saga.CurrentStep, saga.TenantId), ct);
        await remediator.ForceCompensateAsync(saga.SagaId, reason: "stuck-saga-monitor", ct);
    }

    private async Task DeadLetterAsync(SagaState saga, CancellationToken ct)
    {
        logger.LogError("Saga {SagaId} is terminal (stuck > {Hours}hr). Moving to dead-letter.", saga.SagaId, TerminalStaleness.TotalHours);
        await alertPublisher.PublishAsync(new SagaAlert(saga.SagaId, "DEAD_LETTERED", saga.CurrentStep, saga.TenantId), ct);
        await remediator.DeadLetterAsync(saga.SagaId, reason: "stuck-saga-exceeded-terminal-threshold", ct);
    }
}

public record SagaState(string SagaId, string TenantId, string CurrentStep, DateTimeOffset StartedAt, int RetryCount);
public record SagaAlert(string SagaId, string AlertType, string CurrentStep, string TenantId);

public interface ISagaStateRepository
{
    Task<List<SagaState>> GetStuckSagasAsync(TimeSpan olderThan, CancellationToken ct);
}
public interface ISagaRemediator
{
    Task ResumeAsync(string sagaId, CancellationToken ct);
    Task ForceCompensateAsync(string sagaId, string reason, CancellationToken ct);
    Task DeadLetterAsync(string sagaId, string reason, CancellationToken ct);
}
public interface ISagaAlertPublisher
{
    Task PublishAsync(SagaAlert alert, CancellationToken ct);
}
