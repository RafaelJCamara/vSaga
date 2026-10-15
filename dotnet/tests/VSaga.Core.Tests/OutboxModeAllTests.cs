using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Dsl;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

public sealed record BeginModeAllJob(string JobId);
public sealed record ModeAllBroadcast(string JobId);
public sealed record ModeAllAddressed(string JobId);
public sealed record BeginModeAllChildWork(string JobId);

public sealed class TestModeAllState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>
/// Exercises all three publish shapes Mode=All has to route differently: a broadcast publish (the
/// saga's own correlation id), an addressed send (carries a destination), and a StartChildAsync (a
/// *fresh* correlation id). The last two are what production-readiness.md §8 item 11 turned from
/// redundant into load-bearing on the outbox row.
/// </summary>
public sealed class TestModeAllSaga : OrchestratedSagaDefinition<TestModeAllState>
{
    public State<TestModeAllState> Requested { get; }
    public State<TestModeAllState> Working { get; }

    public TestModeAllSaga()
    {
        Requested = InitialState(nameof(Requested));
        Working = State(nameof(Working));

        During(Requested)
            .When<BeginModeAllJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.PublishAsync(new ModeAllBroadcast(m.JobId), ctx.CancellationToken))
                .Then((ctx, m) => ctx.SendAsync("inventory", new ModeAllAddressed(m.JobId), ctx.CancellationToken))
                .Then((ctx, m) => ctx.StartChildAsync(new BeginModeAllChildWork(m.JobId), ctx.CancellationToken))
                .TransitionTo(Working);
    }
}

public sealed record BeginFailingModeAllJob(string JobId);
public sealed record ShouldNeverBeSeen(string JobId);

public sealed class TestFailingModeAllState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>Publishes, then throws — under Mode=All the publish is queued, so the failure path discards it.</summary>
public sealed class TestFailingModeAllSaga : OrchestratedSagaDefinition<TestFailingModeAllState>
{
    public State<TestFailingModeAllState> Requested { get; }
    public State<TestFailingModeAllState> Working { get; }

    public TestFailingModeAllSaga()
    {
        Requested = InitialState(nameof(Requested));
        Working = State(nameof(Working));

        During(Requested)
            .When<BeginFailingModeAllJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.PublishAsync(new ShouldNeverBeSeen(m.JobId), ctx.CancellationToken))
                .Then((_, _) => throw new InvalidOperationException("step blew up after publishing"))
                .TransitionTo(Working);
    }
}

/// <summary>
/// Wraps the real in-memory transport and refuses to send the named message types, so the inline drain
/// fails and leaves its outbox rows <c>Pending</c> — the only way to read back what the engine actually
/// wrote on a row, since a healthy drain marks every row Dispatched and <c>ClaimPendingAsync</c> is the
/// store's only read path. Doubles as coverage of the drain-failed → poller-recovers path itself.
/// </summary>
internal sealed class DrainFailingTransport(InMemoryMessageTransport inner, params string[] failFor) : IMessageTransport
{
    private bool ShouldFail(string messageTypeName) => failFor.Contains(messageTypeName, StringComparer.Ordinal);

    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull =>
        ShouldFail(typeof(TMessage).Name)
            ? throw new InvalidOperationException($"transport refused {typeof(TMessage).Name}")
            : inner.PublishAsync(message, envelope, cancellationToken);

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull =>
        ShouldFail(typeof(TMessage).Name)
            ? throw new InvalidOperationException($"transport refused {typeof(TMessage).Name}")
            : inner.SendAsync(destination, message, envelope, cancellationToken);

    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        inner.PublishRawAsync(messageTypeName, body, envelope, cancellationToken);

    public Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        inner.SendRawAsync(destination, messageTypeName, body, envelope, cancellationToken);

    public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        inner.SubscribeAsync(subscription, handler, cancellationToken);
}

/// <summary>
/// production-readiness.md §8 item 11: <c>Outbox:Mode=All</c> routes every ctx publish through the
/// outbox, not just <c>PublishAfterCommitAsync</c>. Since a mid-step publish is already gone by the time
/// any persist commits, "routed through the outbox" can only mean deferred to after that persist — a row
/// written alongside a message that already left guarantees nothing.
/// <para>
/// Default (Deferred) behaviour is covered by every other test in this suite, all of which stay green
/// untouched; nothing here changes what happens without the switch.
/// </para>
/// </summary>
public sealed class OutboxModeAllTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly ISagaOutboxStore _outbox;

    public OutboxModeAllTests() : this([])
    {
    }

    private OutboxModeAllTests(string[] refuseToSend)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // Registered before AddVSagaEngine, whose TryAddSingleton then leaves it alone.
        services.AddSingleton(new SagaOutboxOptions { Mode = SagaOutboxMode.All });
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();

        if (refuseToSend.Length > 0)
        {
            services.AddSingleton<IMessageTransport>(sp =>
                new DrainFailingTransport(sp.GetRequiredService<InMemoryMessageTransport>(), refuseToSend));
        }

        services.AddVSagaEngine(o => o
            .AddSaga<TestModeAllSaga, TestModeAllState>()
            .AddSaga<TestFailingModeAllSaga, TestFailingModeAllState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _outbox = _provider.GetRequiredService<ISagaOutboxStore>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    /// <summary>Everything queued still goes out, in order, and nothing is left for the recovery poller.</summary>
    [Fact]
    public async Task ModeAll_StillDeliversEveryPublish_AndLeavesNothingPending()
    {
        await _transport.PublishAsync(new BeginModeAllJob("JOB-1"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Contains(_transport.GetPublished(), p => p.Message is ModeAllBroadcast);
        Assert.Contains(_transport.GetPublished(), p => p.Message is ModeAllAddressed);
        Assert.Contains(_transport.GetPublished(), p => p.Message is BeginModeAllChildWork);

        // Claimed with a cutoff far in the future: any row the inline drain failed to mark Dispatched
        // would surface here and be republished for real by the poller.
        Assert.Empty(await _outbox.ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100));
    }

    /// <summary>
    /// The regression this commit exists to prevent, read off the row itself. StartChildAsync publishes
    /// under a *fresh* correlation id (the child's), not the publishing saga's — a row keyed on the latter
    /// would have the recovery poller recreate the child under the parent's own id, where the parent
    /// already lives. The addressed send is checked in the same run because it fails the same way: a row
    /// that dropped its destination would be rebroadcast instead of sent, which is what SendRawAsync
    /// (§8 item 7) exists to prevent.
    /// </summary>
    [Fact]
    public async Task ModeAll_RowsCarryTheEnvelopesOwnIdentityAndDestination_NotThePublishingSagas()
    {
        await using var fixture = new OutboxModeAllTests(
            [nameof(ModeAllBroadcast), nameof(ModeAllAddressed), nameof(BeginModeAllChildWork)]);

        var parentId = Guid.NewGuid();
        await fixture._transport.PublishAsync(new BeginModeAllJob("JOB-2"), MessageEnvelope.New(parentId));

        // Every drain threw, so every row is still Pending and therefore readable.
        var pending = await fixture._outbox.ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100);

        var broadcast = Assert.Single(pending, m => string.Equals(m.MessageTypeName, nameof(ModeAllBroadcast), StringComparison.Ordinal));
        Assert.Equal(parentId, broadcast.CorrelationId); // an ordinary publish does ride the saga's own id
        Assert.Null(broadcast.Destination);

        var addressed = Assert.Single(pending, m => string.Equals(m.MessageTypeName, nameof(ModeAllAddressed), StringComparison.Ordinal));
        Assert.Equal("inventory", addressed.Destination);
        Assert.Equal(parentId, addressed.CorrelationId);

        var child = Assert.Single(pending, m => string.Equals(m.MessageTypeName, nameof(BeginModeAllChildWork), StringComparison.Ordinal));
        Assert.NotEqual(parentId, child.CorrelationId);
        Assert.Null(child.Destination);

        // And the id on the row is the one the child saga would actually be created under -- the same id
        // the linkage headers point back to the parent from.
        Assert.Equal(parentId.ToString(), child.Headers[MessageEnvelope.ParentCorrelationIdHeader]);
    }

    /// <summary>
    /// The behaviour change Mode=All buys, and the reason it is opt-in: a step that publishes and then
    /// throws no longer leaks the publish. Under the default Deferred mode that message is already on the
    /// wire before the throw and cannot be recalled.
    /// </summary>
    [Fact]
    public async Task ModeAll_AStepThatThrowsAfterPublishing_NeverSendsIt()
    {
        await _transport.PublishAsync(new BeginFailingModeAllJob("JOB-4"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.DoesNotContain(_transport.GetPublished(), p => p.Message is ShouldNeverBeSeen);
        Assert.Empty(await _outbox.ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100));
    }
}
