using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// Pins Slice 2b's opt-in mechanism: a parent "opts in" to ChildSagaFinished simply by declaring a
/// handler for it somewhere in its own DSL — that declaration is what
/// <c>SagaRuntime.Subscription</c> is built from (<c>ISagaDefinition.MessageTypes</c>), so a parent that
/// never declares one is never even subscribed. There is no separate opt-in switch to test.
///
/// <para>
/// Kept in its own DI container, deliberately separate from <see cref="ChildSagaFinishedTests"/>: that
/// class registers a saga (<c>TestChildSafetyNetParentSaga</c>) that <i>does</i> declare a
/// <c>ChildSagaFinished</c> handler, which would make the engine subscribe that saga type to it — proving
/// nothing about whether <em>this</em> saga type, which declares no handler, receives it. Isolating the
/// container is what makes "never even subscribed" an observable, not just an inferred, property.
/// </para>
/// </summary>
public sealed class ChildSagaFinishedOptInTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly ISagaSummaryReader _reader;
    private readonly ISagaEventLogStore _log;

    public ChildSagaFinishedOptInTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .AddSaga<TestNaiveParentSaga, TestNaiveParentState>()
            .AddSaga<TestRiskyChildSaga, TestRiskyChildState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _reader = _provider.GetRequiredService<ISagaSummaryReader>();
        _log = _provider.GetRequiredService<ISagaEventLogStore>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task AParentThatNeverDeclaresAHandler_NeverReceivesChildSagaFinished()
    {
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginUnsafeguardedJob("JOB-1"), MessageEnvelope.New(parentId));
        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestNaiveParentSaga), parentId));
        await _transport.PublishAsync(new TriggerFailure("JOB-1"), MessageEnvelope.New(child.CorrelationId));

        // The engine still published it — the child's own timeline proves that unconditionally, exactly
        // as it would in ChildSagaFinishedTests.
        var childTimeline = await _log.GetTimelineAsync(nameof(TestRiskyChildSaga), child.CorrelationId);
        Assert.Single(childTimeline, e => e.EntryType == SagaEntryType.ChildSagaFinished);

        // But the parent never subscribed to the message type at all, so it never even reaches
        // HandleCoreAsync — not even as an UnexpectedEvent, which is what would show up if the parent
        // *were* subscribed but simply had no handler for its current state. This is the difference
        // between "opted in but the message arrived in the wrong state" and "never opted in".
        var parentTimeline = await _log.GetTimelineAsync(nameof(TestNaiveParentSaga), parentId);
        Assert.DoesNotContain(parentTimeline, e => string.Equals(e.MessageType, nameof(ChildSagaFinished), StringComparison.Ordinal));
        Assert.DoesNotContain(parentTimeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);

        var parent = await _provider.GetRequiredService<ISagaSnapshotStore<TestNaiveParentState>>()
            .FindAsync(nameof(TestNaiveParentSaga), parentId);
        Assert.NotNull(parent);
        Assert.Equal(nameof(TestNaiveParentSaga.AwaitingResult), parent.CurrentState);
        Assert.Equal(SagaStatus.Running, parent.Status);
    }
}
