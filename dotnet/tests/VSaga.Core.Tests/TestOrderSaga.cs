using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

// Empty records are intentional: correlation is carried by the message envelope in tests, so these
// zero-payload markers just need a distinct CLR type for routing/deserialization.
#pragma warning disable S2094
public sealed record FlakyWithPolicy;
public sealed record AlwaysFailsWithPolicy;
#pragma warning restore S2094

public sealed class TestOrderSagaState : SagaState
{
    public string? OrderId { get; set; }

    public decimal Amount { get; set; }

    public int InventoryAttempts { get; set; }
}

public sealed record OrderSubmitted(string OrderId, decimal Amount);
public sealed record ReserveInventory(Guid CorrelationId, string OrderId);
#pragma warning disable S2094
public sealed record InventoryReserved;
public sealed record InventoryReservationFailed;
#pragma warning restore S2094
public sealed record ChargePayment(Guid CorrelationId, decimal Amount);
#pragma warning disable S2094
public sealed record PaymentCharged;
public sealed record PaymentFailed;
#pragma warning restore S2094
public sealed record ReleaseInventory(Guid CorrelationId);
#pragma warning disable S2094
public sealed record FlakyStep;
public sealed record FlakyWithLoopback;
public sealed record LoopbackAck;
public sealed record AlwaysFailsWithLoopback;
#pragma warning restore S2094

public sealed class TestOrderSaga : OrchestratedSagaDefinition<TestOrderSagaState>
{
    public State<TestOrderSagaState> Submitted { get; }
    public State<TestOrderSagaState> AwaitingInventory { get; }
    public State<TestOrderSagaState> AwaitingPayment { get; }
    public State<TestOrderSagaState> Completed { get; }
    public State<TestOrderSagaState> Failed { get; }

    /// <summary>Shared across replays so a test can make a step fail once then succeed on manual retry.</summary>
    public int FlakyStepAttempts { get; set; }

    /// <summary>Attempt counter for the in-process, step-level RetryPolicy tests (distinct from the manual retry above).</summary>
    public int FlakyWithPolicyAttempts { get; set; }

    public int AlwaysFailsWithPolicyAttempts { get; set; }

    /// <summary>docs/design/mixed-sagas.md §3.2's dedupe fix: counts real attempts of the step below, distinct from how many times its queued loopback actually gets published.</summary>
    public int FlakyWithLoopbackAttempts { get; set; }

    public TestOrderSaga()
    {
        Submitted = InitialState(nameof(Submitted));
        AwaitingInventory = State(nameof(AwaitingInventory));
        AwaitingPayment = State(nameof(AwaitingPayment));
        Completed = State(nameof(Completed));
        Failed = State(nameof(Failed));

        During(Submitted)
            .When<OrderSubmitted>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.Saga.Amount = m.Amount)
                .Publish((ctx, m) => new ReserveInventory(ctx.CorrelationId, m.OrderId))
                .TransitionTo(AwaitingInventory);

        During(AwaitingInventory)
            .When<InventoryReserved>()
                .Publish((ctx, _) => new ChargePayment(ctx.CorrelationId, ctx.Saga.Amount))
                .TransitionTo(AwaitingPayment)
            .When<InventoryReservationFailed>()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        ConfigureRetryFixtures();

        During(AwaitingPayment)
            .When<PaymentCharged>()
                .TransitionTo(Completed)
                .Finalize(SagaStatus.Completed)
            .When<PaymentFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        Compensate(AwaitingInventory, (ctx, ct) => ctx.PublishAsync(new ReleaseInventory(ctx.CorrelationId), ct));
        Compensate(AwaitingPayment, (_, _) => throw new InvalidOperationException("simulated compensation failure"));

        WithTimeout(AwaitingPayment, TimeSpan.FromMinutes(5), t => t.Compensate().TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
    }

    /// <summary>The manual-retry and step-level-RetryPolicy fixture steps, all registered under AwaitingInventory -- split out of the constructor purely for length.</summary>
    private void ConfigureRetryFixtures()
    {
        During(AwaitingInventory)
            .When<FlakyStep>()
                .Then((_, _) =>
                {
                    FlakyStepAttempts++;
                    if (FlakyStepAttempts == 1)
                        throw new InvalidOperationException("simulated transient failure");
                })
                .TransitionTo(AwaitingPayment)
            .When<FlakyWithPolicy>()
                .Then((_, _) =>
                {
                    FlakyWithPolicyAttempts++;
                    if (FlakyWithPolicyAttempts < 3)
                        throw new InvalidOperationException("transient failure, should be retried in-process");
                })
                .Retry(RetryPolicy.Exponential(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1)))
                .TransitionTo(AwaitingPayment)
            .When<AlwaysFailsWithPolicy>()
                .Then((_, _) =>
                {
                    AlwaysFailsWithPolicyAttempts++;
                    throw new InvalidOperationException("always fails");
                })
                .Retry(RetryPolicy.Exponential(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1)))
                .TransitionTo(AwaitingPayment)
            // docs/design/mixed-sagas.md §3.2: a loopback queued via PublishAfterCommitAsync, then a throw --
            // exactly the shape a .CallHttp-then-.Publish step under .Retry would have. Without
            // StepExecutor clearing the queue on retry, the replayed attempt would queue a second
            // LoopbackAck (a fresh MessageId each time) and the drain would publish both.
            .When<FlakyWithLoopback>()
                .Then(async (ctx, _) =>
                {
                    await ctx.PublishAfterCommitAsync(new LoopbackAck(), ctx.CancellationToken);
                    FlakyWithLoopbackAttempts++;
                    if (FlakyWithLoopbackAttempts < 2)
                        throw new InvalidOperationException("transient failure, should be retried in-process");
                })
                .Retry(RetryPolicy.Exponential(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1)))
                .TransitionTo(AwaitingPayment)
            // §4.5: no .Retry() here, so this throws straight out to HandleStepFailureAsync on its one
            // and only attempt, with a publish already queued -- the shape that fix discards.
            .When<AlwaysFailsWithLoopback>()
                .Then(async (ctx, _) =>
                {
                    await ctx.PublishAfterCommitAsync(new LoopbackAck(), ctx.CancellationToken);
                    throw new InvalidOperationException("unrecoverable failure, with a publish already queued");
                })
                .TransitionTo(AwaitingPayment);
    }
}
