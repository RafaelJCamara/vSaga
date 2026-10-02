using System.Collections.Concurrent;
using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Transport;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// The dashboard appends pushed timeline entries and joins the timeline to the map on sequence
/// numbers, so every entry the orchestrator notifies with must carry the number the event log
/// assigned when it stored that entry, not the 0 an entry is created with.
/// </summary>
public sealed class SagaChangeNotifierSequenceNumberTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly TestOrderSaga _saga;
    private readonly RecordingNotifier _notifier = new();

    public SagaChangeNotifierSequenceNumberTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ISagaChangeNotifier>(_notifier);
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());

        _provider = services.BuildServiceProvider();
        _transport = _provider.GetRequiredService<InMemoryMessageTransport>();
        _saga = _provider.GetRequiredService<TestOrderSaga>();

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
    public async Task EveryNotifiedEntry_CarriesTheSequenceNumberTheStoreAssigned()
    {
        var correlationId = Guid.NewGuid();

        // A compensated failure: orchestrator entries (SagaStarted, MessageReceived, StepSucceeded,
        // SagaCompleted) and the ones the step logs through SagaContext's sink (MessagePublished,
        // CompensationStarted, CompensationStepFailed) all reach the notifier.
        await _transport.PublishAsync(new OrderSubmitted("ORD-SEQ", 7m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentFailed(), MessageEnvelope.New(correlationId));

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(_saga.SagaType, correlationId);
        var notified = _notifier.Entries
            .Where(n => n.CorrelationId == correlationId && string.Equals(n.SagaType, _saga.SagaType, StringComparison.Ordinal))
            .Select(n => n.Entry)
            .ToList();

        Assert.Contains(notified, e => e.EntryType == SagaEntryType.MessagePublished);
        Assert.Contains(notified, e => e.EntryType == SagaEntryType.CompensationStarted);
        Assert.All(notified, e => Assert.True(e.SequenceNumber > 0, $"{e.EntryType} was notified with sequence number {e.SequenceNumber}"));
        Assert.Equal(notified.Count, notified.Select(e => e.SequenceNumber).Distinct().Count());
        Assert.Equal(timeline, notified.OrderBy(e => e.SequenceNumber));
    }

    private sealed class RecordingNotifier : ISagaChangeNotifier
    {
        private readonly ConcurrentQueue<(string SagaType, Guid CorrelationId, SagaLogEntry Entry)> _entries = new();

        public IReadOnlyList<(string SagaType, Guid CorrelationId, SagaLogEntry Entry)> Entries => [.. _entries];

        public Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TimelineEntryAddedAsync(string sagaType, Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Enqueue((sagaType, correlationId, entry));
            return Task.CompletedTask;
        }
    }
}
