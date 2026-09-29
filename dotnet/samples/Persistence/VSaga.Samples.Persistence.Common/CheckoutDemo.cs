using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;

namespace VSaga.Samples.Persistence.Common;

/// <summary>
/// The scenario every persistence sample runs, and the report it prints. Everything is read back
/// through the provider-neutral store contracts (<see cref="ISagaSnapshotStore{TState}"/>,
/// <see cref="ISagaEventLogStore"/>, <see cref="ISagaSummaryReader"/>), so the output is the same shape
/// whichever provider wrote it. What differs is whether the "from earlier runs" count ever grows.
/// </summary>
public static class CheckoutDemo
{
    private const string SagaType = nameof(CheckoutSaga);

    /// <summary>Longer than <see cref="CheckoutSaga.PaymentTimeout"/> plus the timeout dispatcher's 5-second poll.</summary>
    private static readonly TimeSpan FinishDeadline = TimeSpan.FromSeconds(30);

    private sealed record Scenario(string Label, string OrderNumber, decimal Amount, string PaymentToken)
    {
        public Guid CorrelationId { get; } = Guid.NewGuid();
    }

    /// <summary>
    /// Submits three orders (approved, declined, no response), waits for all three sagas to finish,
    /// resubmits the first order number to show the business key at work, then prints each saga's
    /// snapshot and timeline. Call after the host has started and the store is ready.
    /// </summary>
    public static async Task RunAsync(IServiceProvider services, string providerName, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"=== vSaga persistence sample: {providerName} ===");
        var before = await CountSagasAsync(services, cancellationToken);
        Console.WriteLine($"The store holds {before} {SagaType} instance(s) from earlier runs.");

        // A per-run prefix keeps order numbers (the business key) unique across runs of a durable store.
        var run = DateTimeOffset.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        Scenario[] scenarios =
        [
            new("card approved", $"ORD-{run}-1", 49.90m, PaymentTokens.Approved),
            new("card declined", $"ORD-{run}-2", 1_250.00m, PaymentTokens.Declined),
            new("gateway never answers", $"ORD-{run}-3", 15.00m, PaymentTokens.NoResponse),
        ];

        var transport = services.GetRequiredService<IMessageTransport>();
        foreach (var scenario in scenarios)
        {
            Console.WriteLine($"Submitting {scenario.OrderNumber} ({scenario.Label})");
            await transport.PublishAsync(new SubmitOrder(scenario.OrderNumber, scenario.Amount, scenario.PaymentToken),
                MessageEnvelope.New(scenario.CorrelationId), cancellationToken);
        }

        Console.WriteLine($"Waiting for all three to finish (the timeout takes {CheckoutSaga.PaymentTimeout.TotalSeconds:0}-{CheckoutSaga.PaymentTimeout.TotalSeconds + 5:0} s)...");
        await WaitUntilFinishedAsync(services, scenarios, cancellationToken);

        await ResubmitAsync(services, transport, scenarios[0], cancellationToken);

        foreach (var scenario in scenarios)
            await PrintSagaAsync(services, scenario, cancellationToken);

        var after = await CountSagasAsync(services, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"The store now holds {after} {SagaType} instance(s).");
    }

    private static async Task<int> CountSagasAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISagaSummaryReader>();
        var page = await reader.ListAsync(new SagaListFilter { SagaType = SagaType, PageSize = 1 }, cancellationToken);
        return page.TotalCount;
    }

    private static async Task WaitUntilFinishedAsync(IServiceProvider services, IReadOnlyList<Scenario> scenarios, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FinishDeadline);

        while (true)
        {
            // A fresh scope per poll: the EF Core, MongoDB and Redis stores are Scoped, like the engine's
            // own per-message unit of work.
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<CheckoutState>>();

            var running = 0;
            foreach (var scenario in scenarios)
            {
                var state = await store.FindAsync(SagaType, scenario.CorrelationId, cancellationToken);
                if (state is null || state.Status == SagaStatus.Running)
                    running++;
            }

            if (running == 0)
                return;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{running} saga(s) were still running after {FinishDeadline.TotalSeconds:0} s.");
            }
        }
    }

    /// <summary>
    /// Publishes the same order number again under a brand-new correlation id. The transport id misses,
    /// the business-key lookup hits, and the message lands on the existing, already-finished instance
    /// (recorded as UnexpectedEvent) instead of opening a second saga.
    /// </summary>
    private static async Task ResubmitAsync(IServiceProvider services, IMessageTransport transport, Scenario original, CancellationToken cancellationToken)
    {
        var duplicateId = Guid.NewGuid();
        await transport.PublishAsync(new SubmitOrder(original.OrderNumber, original.Amount, original.PaymentToken),
            MessageEnvelope.New(duplicateId), cancellationToken);

        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<CheckoutState>>();
        var underNewId = await store.FindAsync(SagaType, duplicateId, cancellationToken);
        var owner = await store.FindByBusinessKeyAsync(SagaType, original.OrderNumber, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Resubmitted {original.OrderNumber} under a new correlation id {duplicateId}:");
        Console.WriteLine($"  instance under the new id: {(underNewId is null ? "none" : "created (unexpected)")}");
        Console.WriteLine($"  business key {original.OrderNumber} belongs to: {owner?.CorrelationId.ToString() ?? "nobody (unexpected)"}");
    }

    private static async Task PrintSagaAsync(IServiceProvider services, Scenario scenario, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<CheckoutState>>();
        var eventLog = scope.ServiceProvider.GetRequiredService<ISagaEventLogStore>();

        var state = await store.FindAsync(SagaType, scenario.CorrelationId, cancellationToken);
        var timeline = await eventLog.GetTimelineAsync(SagaType, scenario.CorrelationId, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"{scenario.OrderNumber} ({scenario.Label})");
        if (state is null)
        {
            Console.WriteLine("  no snapshot found");
            return;
        }

        Console.WriteLine($"  correlation id {state.CorrelationId}, business key {state.BusinessKey}");
        Console.WriteLine($"  snapshot: state {state.CurrentState}, status {state.Status}, version {state.Version}{Outcome(state)}");
        Console.WriteLine($"  timeline ({timeline.Count} entries):");
        foreach (var entry in timeline)
            Console.WriteLine($"    {entry.SequenceNumber,3}  {entry.EntryType,-18} {Describe(entry)}");
    }

    private static string Outcome(CheckoutState state)
    {
        if (state.AuthorizationCode is not null)
            return $", authorization {state.AuthorizationCode}";

        return state.DeclineReason is not null ? $", declined: {state.DeclineReason}" : string.Empty;
    }

    private static string Describe(SagaLogEntry entry) => entry switch
    {
        { FromState: not null, ToState: not null } => $"{entry.FromState} -> {entry.ToState}",
        { ToState: not null } => entry.ToState,
        { FromState: not null, MessageType: not null } => $"{entry.MessageType} in {entry.FromState}",
        { MessageType: not null } => entry.MessageType,
        { FromState: not null } => entry.FromState,
        _ => string.Empty,
    };
}
