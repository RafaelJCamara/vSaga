using VSaga.Abstractions.Persistence;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// Fix F9 (docs/design/persistence-contracts.md §3): the timeout dispatcher claims only the saga types
/// this process has runtimes for. Several services can share one timeout store, each hosting its own
/// sagas; a claim fires the row for good, so a dispatcher claiming everything and dropping what it cannot
/// dispatch as "unknown saga type" would lose every other service's timeouts. The dispatcher's poll runs
/// on its own schedule, so the case waits on the claim itself — recorded by a store wrapping the real one
/// — rather than on timing, and then checks the other service's row survived that poll still Pending.
/// </summary>
public sealed class TimeoutDispatcherScopingTests
{
    [Fact]
    public async Task Dispatcher_ClaimsOnlyItsOwnSagaTypes_AndLeavesAnotherServicesTimeoutPending()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());
        // Registered after AddVSagaInMemoryPersistence so it wins resolution, wrapping the same
        // InMemorySagaStore -- the override-order trick SagaOrchestratorTimeoutRaceTests uses.
        services.AddSingleton<RecordingTimeoutStore>(sp => new RecordingTimeoutStore(sp.GetRequiredService<InMemorySagaStore>()));
        services.AddSingleton<ISagaTimeoutStore>(sp => sp.GetRequiredService<RecordingTimeoutStore>());
        await using var provider = services.BuildServiceProvider();

        var recording = provider.GetRequiredService<RecordingTimeoutStore>();
        var theirs = Guid.NewGuid();
        await recording.ScheduleAsync("AnotherServicesSaga", theirs, "Waiting", DateTimeOffset.UtcNow.AddMinutes(-5));

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        try
        {
            var claimedTypes = await recording.FirstClaim.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([provider.GetRequiredService<TestOrderSaga>().SagaType], claimedTypes);

            var stillPending = Assert.Single(await recording.ClaimDueAsync(DateTimeOffset.UtcNow, batchSize: 10));
            Assert.Equal(theirs, stillPending.CorrelationId);
            Assert.Equal("AnotherServicesSaga", stillPending.SagaType);
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Passes everything through to the real store and records the saga types the first claim was scoped to.</summary>
    private sealed class RecordingTimeoutStore(ISagaTimeoutStore inner) : ISagaTimeoutStore
    {
        private readonly TaskCompletionSource<IReadOnlyCollection<string>?> _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyCollection<string>?> FirstClaim => _firstClaim.Task;

        public Task ScheduleAsync(string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default) =>
            inner.ScheduleAsync(sagaType, correlationId, forState, dueAtUtc, cancellationToken);

        public Task CancelAsync(string sagaType, Guid correlationId, string forState, CancellationToken cancellationToken = default) =>
            inner.CancelAsync(sagaType, correlationId, forState, cancellationToken);

        public Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, IReadOnlyCollection<string>? sagaTypes = null, CancellationToken cancellationToken = default)
        {
            _firstClaim.TrySetResult(sagaTypes?.ToList());
            return inner.ClaimDueAsync(asOf, batchSize, sagaTypes, cancellationToken);
        }
    }
}
