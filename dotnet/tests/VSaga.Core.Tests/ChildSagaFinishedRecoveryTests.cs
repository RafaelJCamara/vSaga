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
/// The engine's own ChildSagaFinished row on the outbox's recovery path (docs/design/persistence-contracts.md
/// §4, bugs B1 and B2): what the recovery poller republishes when the inline send never happened, and
/// whether a row survives at all when the failure that staged it was never recorded.
/// </summary>
public sealed class ChildSagaFinishedRecoveryTests
{
    /// <summary>
    /// B1: the row is keyed on the parent's correlation id, because the recovery poller rebuilds the
    /// envelope from the stored id. Simulates a process that committed the child's Failed transition and
    /// its row, then died before the inline send: the transport drops that one publish, the outbox never
    /// sees it marked dispatched, and the row is republished exactly as SagaOutboxDispatcherHostedService
    /// would. Keyed on the child's id, that republish reaches the parent's orchestrator under an id it has
    /// no instance for, and the parent hangs until its own timeout.
    /// </summary>
    [Fact]
    public async Task RecoveredChildSagaFinished_IsRepublishedUnderTheParentsCorrelationId()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .AddSaga<TestChildSafetyNetParentSaga, TestChildSafetyNetParentState>()
            .AddSaga<TestRiskyChildSaga, TestRiskyChildState>());
        // Both registered after the defaults so they win resolution, each wrapping the real one.
        services.AddSingleton<IMessageTransport>(sp => new InlineChildSagaFinishedDroppingTransport(sp.GetRequiredService<InMemoryMessageTransport>()));
        services.AddSingleton<ISagaOutboxStore>(sp => new NeverDispatchedChildSagaFinishedOutbox(sp.GetRequiredService<InMemorySagaStore>()));

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        try
        {
            var transport = provider.GetRequiredService<InMemoryMessageTransport>();
            var reader = provider.GetRequiredService<ISagaSummaryReader>();
            var parents = provider.GetRequiredService<ISagaSnapshotStore<TestChildSafetyNetParentState>>();

            var parentId = Guid.NewGuid();
            await transport.PublishAsync(new BeginSafeguardedJob("JOB-RECOVER"), MessageEnvelope.New(parentId));
            var child = Assert.Single(await reader.FindChildrenAsync(nameof(TestChildSafetyNetParentSaga), parentId));
            await transport.PublishAsync(new TriggerFailure("JOB-RECOVER"), MessageEnvelope.New(child.CorrelationId));

            // The child failed and committed, but the parent never heard: the inline send was dropped.
            Assert.Equal(SagaStatus.Failed, (await reader.GetAsync(nameof(TestRiskyChildSaga), child.CorrelationId))!.Status);
            var parentBefore = (await parents.FindAsync(nameof(TestChildSafetyNetParentSaga), parentId))!;
            Assert.Equal(nameof(TestChildSafetyNetParentSaga.AwaitingResult), parentBefore.CurrentState);

            var outbox = provider.GetRequiredService<ISagaOutboxStore>();
            var row = Assert.Single(await outbox.ClaimPendingAsync(DateTimeOffset.UtcNow.AddYears(1), batchSize: 100),
                m => string.Equals(m.MessageTypeName, nameof(ChildSagaFinished), StringComparison.Ordinal));
            Assert.Equal(parentId, row.CorrelationId);

            // What the recovery poller does with a claimed row -- RedispatchAsync, envelope from the row.
            await transport.PublishRawAsync(row.MessageTypeName, row.Body, new MessageEnvelope(row.CorrelationId, row.MessageId, row.Headers));

            var parentAfter = (await parents.FindAsync(nameof(TestChildSafetyNetParentSaga), parentId))!;
            Assert.Equal(nameof(TestChildSafetyNetParentSaga.Rescued), parentAfter.CurrentState);
            Assert.Equal(SagaStatus.Failed, parentAfter.ChildFinishedStatus);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Drops the engine's inline, typed publish of ChildSagaFinished; everything else, the raw republish included, goes through.</summary>
    private sealed class InlineChildSagaFinishedDroppingTransport(IMessageTransport inner) : IMessageTransport
    {
        public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default) where TMessage : notnull =>
            message is ChildSagaFinished ? Task.CompletedTask : inner.PublishAsync(message, envelope, cancellationToken);

        public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default) where TMessage : notnull =>
            inner.SendAsync(destination, message, envelope, cancellationToken);

        public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
            inner.PublishRawAsync(messageTypeName, body, envelope, cancellationToken);

        public Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
            inner.SendRawAsync(destination, messageTypeName, body, envelope, cancellationToken);

        public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
            inner.SubscribeAsync(subscription, handler, cancellationToken);
    }

    /// <summary>Leaves every ChildSagaFinished row Pending whatever the engine marks, so the recovery claim can see it; everything else goes through.</summary>
    private sealed class NeverDispatchedChildSagaFinishedOutbox(ISagaOutboxStore inner) : ISagaOutboxStore
    {
        private readonly HashSet<string> _childFinishedMessageIds = new(StringComparer.Ordinal);

        public Task EnqueueAsync(string sagaType, Guid correlationId, string messageId, string messageTypeName, ReadOnlyMemory<byte> body, string? destination,
            IReadOnlyDictionary<string, string> headers, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
        {
            if (string.Equals(messageTypeName, nameof(ChildSagaFinished), StringComparison.Ordinal))
                _childFinishedMessageIds.Add(messageId);
            return inner.EnqueueAsync(sagaType, correlationId, messageId, messageTypeName, body, destination, headers, createdAtUtc, cancellationToken);
        }

        public Task MarkDispatchedAsync(string messageId, CancellationToken cancellationToken = default) =>
            _childFinishedMessageIds.Contains(messageId) ? Task.CompletedTask : inner.MarkDispatchedAsync(messageId, cancellationToken);

        public Task DiscardPendingAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default) =>
            inner.DiscardPendingAsync(messageIds, cancellationToken);

        public Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default) =>
            inner.ClaimPendingAsync(olderThan, batchSize, cancellationToken);
    }
}
