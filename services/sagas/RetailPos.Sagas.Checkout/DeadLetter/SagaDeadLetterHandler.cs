using Microsoft.Extensions.Logging;

namespace RetailPos.Sagas.Checkout.DeadLetter;

/// <summary>
/// Fix #3b: Saga dead-letter handling.
///
/// v1 Drawback: failed sagas had no dead-letter mechanism — they were either
/// retried infinitely or silently abandoned. No human intervention workflow.
///
/// Fix: Dead-lettered sagas are:
///   1. Written to a durable dead-letter store (PostgreSQL audit table)
///   2. Published to a dedicated Kafka topic (retail-pos.sagas.dead-letter)
///   3. Exposed via admin API for human review and replay
///   4. Associated with compensation state (what was done, what wasn't)
///   5. Emit PagerDuty/OpsGenie alerts via OTel alerting rules
/// </summary>
public class SagaDeadLetterHandler(
    ISagaDeadLetterStore deadLetterStore,
    IDeadLetterEventPublisher eventPublisher,
    ILogger<SagaDeadLetterHandler> logger)
{
    public async Task HandleAsync(DeadLetteredSaga saga, CancellationToken ct = default)
    {
        logger.LogError(
            "Saga {SagaId} dead-lettered. Tenant: {Tenant} Step: {Step} Retries: {Retries} Reason: {Reason}",
            saga.SagaId, saga.TenantId, saga.LastStep, saga.RetryCount, saga.Reason);

        // 1. Persist in durable dead-letter store
        await deadLetterStore.SaveAsync(saga, ct);

        // 2. Publish to dead-letter Kafka topic for downstream consumers
        await eventPublisher.PublishAsync(new SagaDeadLetteredEvent(
            SagaId: saga.SagaId,
            TenantId: saga.TenantId,
            SaleId: saga.SaleId,
            Reason: saga.Reason,
            CompensationState: saga.CompensationState,
            DeadLetteredAt: DateTimeOffset.UtcNow), ct);

        // 3. Record compensation state clearly so ops knows what was/wasn't done
        logger.LogError(
            "Compensation state for {SagaId}: Inventory reserved? {Inv} Payment authorised? {Pay} Sale completed? {Sale}",
            saga.SagaId,
            saga.CompensationState.InventoryReserved,
            saga.CompensationState.PaymentAuthorised,
            saga.CompensationState.SaleCompleted);
    }
}

/// <summary>
/// Fix #3c: Compensation retry policy with exponential backoff.
/// Compensating transactions themselves can fail — handle that gracefully.
/// </summary>
public class CompensationRetryPolicy(ILogger<CompensationRetryPolicy> logger)
{
    private static readonly TimeSpan[] Backoffs =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    public async Task<bool> ExecuteAsync(string operationName, string sagaId, Func<Task> compensatingAction, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < Backoffs.Length; attempt++)
        {
            try
            {
                await compensatingAction();
                logger.LogInformation("Compensation {Op} succeeded for saga {SagaId} on attempt {Attempt}",
                    operationName, sagaId, attempt + 1);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Compensation {Op} failed for saga {SagaId} on attempt {Attempt}/{Max}. Retrying in {Delay}.",
                    operationName, sagaId, attempt + 1, Backoffs.Length, Backoffs[attempt]);

                if (attempt == Backoffs.Length - 1)
                {
                    logger.LogError("Compensation {Op} EXHAUSTED for saga {SagaId}. Manual intervention required.", operationName, sagaId);
                    return false;
                }

                await Task.Delay(Backoffs[attempt], ct);
            }
        }
        return false;
    }
}

// ── Dead-letter data model ────────────────────────────────────────────────────
public record DeadLetteredSaga(
    string SagaId, string TenantId, Guid SaleId,
    string LastStep, string Reason, int RetryCount,
    DateTimeOffset StartedAt, SagaCompensationState CompensationState);

public record SagaCompensationState(
    bool InventoryReserved, bool InventoryReleased,
    bool PaymentAuthorised, bool PaymentVoided,
    bool SaleCompleted, bool SaleVoided,
    List<string> RemainingActions);

public record SagaDeadLetteredEvent(
    string SagaId, string TenantId, Guid SaleId,
    string Reason, SagaCompensationState CompensationState, DateTimeOffset DeadLetteredAt);

public interface ISagaDeadLetterStore { Task SaveAsync(DeadLetteredSaga saga, CancellationToken ct); }
public interface IDeadLetterEventPublisher { Task PublishAsync(SagaDeadLetteredEvent evt, CancellationToken ct); }
