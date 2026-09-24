using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Dsl;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

public sealed record OpensInstance(string OrderId);

public sealed class InitialTimeoutState : SagaState
{
    public string? OrderId { get; set; }
}

/// <summary>
/// A saga whose instance-creating event records the initial state — i.e. a self-transition — with a
/// timeout registered on that state.
/// </summary>
public sealed class SelfTransitionOnStartSaga : ChoreographedSagaDefinition<InitialTimeoutState>
{
    public State<InitialTimeoutState> Waiting { get; }
    public State<InitialTimeoutState> Abandoned { get; }

    public SelfTransitionOnStartSaga()
    {
        Waiting = InitialState(nameof(Waiting));
        Abandoned = State(nameof(Abandoned));

        On<OpensInstance>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .RecordState(Waiting);

        WithTimeout(Waiting, TimeSpan.FromMinutes(5), t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}

/// <summary>The same saga, but the opening event records a distinct state so the transition is real.</summary>
public sealed class DistinctStateOnStartSaga : ChoreographedSagaDefinition<InitialTimeoutState>
{
    public State<InitialTimeoutState> Waiting { get; }
    public State<InitialTimeoutState> Opened { get; }
    public State<InitialTimeoutState> Abandoned { get; }

    public DistinctStateOnStartSaga()
    {
        Waiting = InitialState(nameof(Waiting));
        Opened = State(nameof(Opened));
        Abandoned = State(nameof(Abandoned));

        On<OpensInstance>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .RecordState(Opened);

        WithTimeout(Opened, TimeSpan.FromMinutes(5), t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}

/// <summary>
/// Pins an engine rule that is easy to get wrong and fails silently: <c>SagaOrchestrator</c> schedules a
/// state's timeout only on a real transition (<c>ToState != FromState</c>), so a timeout registered on a
/// saga's <b>initial</b> state is never scheduled when the instance-creating event records that same
/// state. The saga then has no stall protection at all, and nothing anywhere reports a problem.
///
/// <para>
/// This was a live defect in the OrderProcessing sample's <c>PostShipmentChoreography</c>, found by
/// comparing <c>TimeoutScheduled</c> rows per state against the running stack: every other milestone had
/// hundreds, the initial state had none. The fix there was to give the opening event its own distinct
/// milestone, which is the second case below.
/// </para>
/// </summary>
public sealed class InitialStateTimeoutTests
{
    private static async Task<(ServiceProvider Provider, IReadOnlyList<SagaTimeout> Due)> RunAsync<TDefinition>()
        where TDefinition : class, ISagaDefinition<InitialTimeoutState>
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TDefinition, InitialTimeoutState>());

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        await transport.PublishAsync(new OpensInstance("ORD-1"), MessageEnvelope.New(Guid.NewGuid()));

        var due = await provider.GetRequiredService<ISagaTimeoutStore>()
            .ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);

        return (provider, due);
    }

    [Fact]
    public async Task ATimeoutOnTheInitialStateIsNeverScheduledWhenTheOpeningEventRecordsThatSameState()
    {
        var (provider, due) = await RunAsync<SelfTransitionOnStartSaga>();
        await using var _ = provider;

        // Nothing is scheduled: the opening event's FromState and ToState are both the initial state,
        // so the orchestrator sees no transition to hang a timeout off.
        Assert.Empty(due);
    }

    [Fact]
    public async Task ATimeoutOnADistinctOpeningStateIsScheduled()
    {
        var (provider, due) = await RunAsync<DistinctStateOnStartSaga>();
        await using var _ = provider;

        var scheduled = Assert.Single(due);
        Assert.Equal(nameof(DistinctStateOnStartSaga.Opened), scheduled.ForState);
    }
}
