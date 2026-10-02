using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

public sealed record ProbeStarted(string Reference);

// Empty records are intentional -- see TestOrderSaga.cs's own precedent.
#pragma warning disable S2094
public sealed record ProbeAdvanced;
public sealed record ProbeBroken;
public sealed record ProbeFinished;
public sealed record ProbeFinishedAck;
public sealed record ProbeLooped;
public sealed record ProbeLoopback;
#pragma warning restore S2094

public sealed class SnapshotProbeState : SagaState
{
    public string? Reference { get; set; }

    public int Steps { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// The saga the state snapshot tests drive: a first step that inserts, a self-transition that updates
/// (and keeps the state's length, so a test can tune a cap to it), a step that mutates and then throws, a
/// terminal step with a deferred publish to drain, a step whose deferred publish loops back to this saga
/// (dispatched synchronously from inside the drain by the in-memory transport), and a timeout out of
/// <see cref="Working"/>.
/// </summary>
public sealed class SnapshotProbeSaga : OrchestratedSagaDefinition<SnapshotProbeState>
{
    public static readonly TimeSpan WorkingTimeout = TimeSpan.FromMinutes(5);

    public State<SnapshotProbeState> Open { get; }
    public State<SnapshotProbeState> Working { get; }
    public State<SnapshotProbeState> Done { get; }
    public State<SnapshotProbeState> Expired { get; }
    public State<SnapshotProbeState> Looping { get; }

    public SnapshotProbeSaga()
    {
        Open = InitialState(nameof(Open));
        Working = State(nameof(Working));
        Done = State(nameof(Done));
        Expired = State(nameof(Expired));
        Looping = State(nameof(Looping));

        During(Looping)
            .When<ProbeLoopback>()
                .TransitionTo(Working);

        During(Open)
            .When<ProbeStarted>()
                .Then((ctx, m) => ctx.Saga.Reference = m.Reference)
                .TransitionTo(Working);

        During(Working)
            .When<ProbeAdvanced>()
                .Then((ctx, _) => ctx.Saga.Steps++)
                .TransitionTo(Working)
            .When<ProbeBroken>()
                .Then((ctx, _) => ctx.Saga.Note = "touched before the throw")
                .Then((_, _) => throw new InvalidOperationException("Simulated step failure for the snapshot tests."))
            .When<ProbeFinished>()
                .Then(async (ctx, _) => await ctx.PublishAfterCommitAsync(new ProbeFinishedAck(), ctx.CancellationToken))
                .TransitionTo(Done)
                .Finalize(SagaStatus.Completed)
            .When<ProbeLooped>()
                .Then(async (ctx, _) => await ctx.PublishAfterCommitAsync(new ProbeLoopback(), ctx.CancellationToken))
                .TransitionTo(Looping);

        WithTimeout(Working, WorkingTimeout, t => t.TransitionTo(Expired).Finalize(SagaStatus.TimedOut));
    }
}

/// <summary>
/// An in-memory engine host for the snapshot tests. Only the engine's subscription service is started:
/// the timeout and outbox pollers would act on their own schedule, and a test that fires a timeout or
/// inspects the outbox does so explicitly. <c>configure</c> runs after <c>AddVSagaEngine</c>, so the
/// options, clock, notifier and store decorators it registers win.
/// </summary>
internal sealed class SnapshotTestHost : IAsyncDisposable
{
    private readonly List<IHostedService> _started;

    public SnapshotTestHost(ServiceProvider provider, List<IHostedService> started)
    {
        Provider = provider;
        _started = started;
    }

    public ServiceProvider Provider { get; }

    public InMemoryMessageTransport Transport => Provider.GetRequiredService<InMemoryMessageTransport>();

    public ISagaEventLogStore EventLog => Provider.GetRequiredService<ISagaEventLogStore>();

    public ISagaSummaryReader Reader => Provider.GetRequiredService<ISagaSummaryReader>();

    public SagaOrchestratorOptions Options => Provider.GetRequiredService<SagaOrchestratorOptions>();

    public static async Task<SnapshotTestHost> StartAsync(Action<SagaEngineBuilder> sagas, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(sagas);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        // Matched by name: both pollers are internal to VSaga.Core.
        var started = provider.GetServices<IHostedService>()
            .Where(h => h.GetType().Name is not ("SagaTimeoutDispatcherHostedService" or "SagaOutboxDispatcherHostedService"))
            .ToList();

        foreach (var hosted in started)
            await hosted.StartAsync(CancellationToken.None);

        return new SnapshotTestHost(provider, started);
    }

    public static Task<SnapshotTestHost> StartProbeAsync(Action<IServiceCollection>? configure = null) =>
        StartAsync(o => o.AddSaga<SnapshotProbeSaga, SnapshotProbeState>(), configure);

    public async Task<IReadOnlyList<SagaLogEntry>> SnapshotsAsync(string sagaType, Guid correlationId) =>
        (await EventLog.GetTimelineAsync(sagaType, correlationId)).Where(e => e.EntryType == SagaEntryType.StatePersisted).ToList();

    /// <summary>Claims this instance's due timeout, a year ahead of any clock a test uses, and fires it through its orchestrator.</summary>
    public async Task FireTimeoutAsync<TState>(string sagaType, Guid correlationId)
        where TState : SagaState, new()
    {
        var due = await Provider.GetRequiredService<ISagaTimeoutStore>().ClaimDueAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100, [sagaType]);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);

        await using var scope = Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SagaOrchestrator<TState>>().HandleTimeoutAsync(timeout, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _started)
            await hosted.StopAsync(CancellationToken.None);

        await Provider.DisposeAsync();
    }
}

/// <summary>A clock that never moves, so a serialised state (its CreatedAtUtc/UpdatedAtUtc) is the same text on every run.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Wraps the in-memory snapshot store so a test can act at a precise point: right after the next update
/// commits, right after the next business-key lookup reads its (soon stale) snapshot, or by failing the
/// next update as infrastructure. Each hook fires once and clears itself.
/// </summary>
internal sealed class HookedSnapshotStore<TState>(ISagaSnapshotStore<TState> inner) : ISagaSnapshotStore<TState>
    where TState : SagaState
{
    public Func<Task>? OnNextUpdate { get; set; }

    public Func<Task>? OnNextFindByBusinessKey { get; set; }

    public bool ThrowOnNextUpdate { get; set; }

    public static void Register(IServiceCollection services) =>
        services.AddSingleton<ISagaSnapshotStore<TState>>(sp =>
            new HookedSnapshotStore<TState>(new InMemorySagaSnapshotStore<TState>(sp.GetRequiredService<InMemorySagaStore>())));

    public static HookedSnapshotStore<TState> From(IServiceProvider provider) =>
        (HookedSnapshotStore<TState>)provider.GetRequiredService<ISagaSnapshotStore<TState>>();

    public Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
        inner.FindAsync(sagaType, correlationId, cancellationToken);

    public Task InsertAsync(TState state, CancellationToken cancellationToken = default) =>
        inner.InsertAsync(state, cancellationToken);

    public async Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
    {
        if (ThrowOnNextUpdate)
        {
            ThrowOnNextUpdate = false;
            throw new InvalidOperationException("Simulated infrastructure failure on update.");
        }

        await inner.UpdateAsync(state, expectedVersion, cancellationToken);

        var trigger = OnNextUpdate;
        if (trigger is not null)
        {
            OnNextUpdate = null;
            await trigger();
        }
    }

    public async Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default)
    {
        var state = await inner.FindByBusinessKeyAsync(sagaType, businessKey, cancellationToken);

        var trigger = OnNextFindByBusinessKey;
        if (trigger is not null)
        {
            OnNextFindByBusinessKey = null;
            await trigger();
        }

        return state;
    }
}

internal enum SnapshotAppendFault
{
    /// <summary>Every StatePersisted append throws.</summary>
    Throw,

    /// <summary>Every StatePersisted append blocks until its token is cancelled.</summary>
    Hang,
}

/// <summary>An event log whose StatePersisted appends fail in the configured way; every other entry is stored normally.</summary>
internal sealed class SnapshotFaultingEventLog(ISagaEventLogStore inner, SnapshotAppendFault fault) : ISagaEventLogStore
{
    private int _abandoned;

    /// <summary>How many hanging appends were cancelled by the caller's token.</summary>
    public int AbandonedAppends => Volatile.Read(ref _abandoned);

    public static void Register(IServiceCollection services, SnapshotAppendFault fault) =>
        services.AddSingleton<ISagaEventLogStore>(sp => new SnapshotFaultingEventLog(sp.GetRequiredService<InMemorySagaStore>(), fault));

    public async Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.EntryType != SagaEntryType.StatePersisted)
            return await inner.AppendAsync(entry, cancellationToken);

        if (fault == SnapshotAppendFault.Throw)
            throw new InvalidOperationException("Simulated StatePersisted append failure.");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _abandoned);
            throw;
        }

        return 0;
    }

    public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
        inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);

    public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
        inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);
}

/// <summary>Records every notification the engine sends, in order.</summary>
internal sealed class RecordingChangeNotifier : ISagaChangeNotifier
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<SagaLogEntry> _entries = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<SagaSummary> _summaries = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _calls = new();

    public IReadOnlyList<SagaLogEntry> Entries => [.. _entries];

    public IReadOnlyList<SagaSummary> Summaries => [.. _summaries];

    /// <summary>Every call in arrival order: the entry type for a timeline entry, <c>SagaUpdated</c> for a summary.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    public Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default)
    {
        _summaries.Enqueue(summary);
        _calls.Enqueue("SagaUpdated");
        return Task.CompletedTask;
    }

    public Task TimelineEntryAddedAsync(string sagaType, Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        _entries.Enqueue(entry);
        _calls.Enqueue(entry.EntryType.ToString());
        return Task.CompletedTask;
    }
}
