using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Hubs;
using VSaga.Persistence.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The poller is what actually delivers live updates in the deployed topology: sagas run in a different
/// process (the OrderProcessing sample), so <see cref="SignalRSagaChangeNotifier"/> never fires in the
/// dashboard and this diff-and-push loop is the only path. Its watermark logic had no coverage at all.
///
/// <para>
/// Exercises <c>PollOnceAsync</c> directly rather than driving the <see cref="PeriodicTimer"/>, so the
/// assertions are about the diff and the watermark rather than about winning a race with a background
/// task's continuation.
/// </para>
/// </summary>
public sealed class SagaChangePollingServiceTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly RecordingSummaryReader _reader;
    private readonly RecordingHubContext _hub = new();
    private readonly SagaChangePollingService _service;

    public SagaChangePollingServiceTests()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryPersistence();
        // Wraps the real in-memory reader instead of replacing it, so every test below still runs
        // against genuine filtering/sorting/paging while the filters the poller sends stay observable.
        // The later ISagaSummaryReader registration wins, and the decorator is a pure pass-through.
        services.AddSingleton(sp => new RecordingSummaryReader(sp.GetRequiredService<InMemorySagaStore>()));
        services.AddSingleton<ISagaSummaryReader>(sp => sp.GetRequiredService<RecordingSummaryReader>());
        _provider = services.BuildServiceProvider();
        _reader = _provider.GetRequiredService<RecordingSummaryReader>();

        _service = new SagaChangePollingService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _hub,
            NullLogger<SagaChangePollingService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _provider.Dispose();
    }

    private async Task<SagaSummary> SeedAsync(string sagaType, DateTimeOffset updatedAtUtc, Guid? correlationId = null)
    {
        var id = correlationId ?? Guid.NewGuid();
        var state = new DashboardTestState
        {
            CorrelationId = id,
            SagaType = sagaType,
            Kind = SagaKind.Orchestrated,
            CurrentState = "Running",
            Status = SagaStatus.Running,
            CreatedAtUtc = updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

        await _provider.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>().InsertAsync(state);
        return new SagaSummary(id, sagaType, state.Kind, state.CurrentState, state.Status, updatedAtUtc, updatedAtUtc, 0,
            state.ParentSagaType, state.ParentCorrelationId);
    }

    private List<string> GroupsPushedTo() => _hub.Recorder.SagaUpdates.Select(c => c.Group).ToList();

    /// <summary>Correlation ids pushed to the list group, in push order — one entry per delivered change.</summary>
    private List<Guid> ListGroupPushes() => _hub.Recorder.SagaUpdates
        .Where(c => string.Equals(c.Group, SagaHub.ListGroup, StringComparison.Ordinal))
        .Select(c => c.Summary.CorrelationId)
        .ToList();

    private async Task<List<Guid>> SeedAscendingAsync(int count, DateTimeOffset firstUpdatedAtUtc)
    {
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
            ids.Add((await SeedAsync("OrderSaga", firstUpdatedAtUtc.AddMilliseconds(i))).CorrelationId);

        return ids;
    }

    [Fact]
    public async Task PushesEachChangedSagaToBothTheListAndItsInstanceGroup()
    {
        var since = DateTimeOffset.UtcNow;
        var saga = await SeedAsync("OrderSaga", since.AddSeconds(1));

        await _service.PollOnceAsync(since, CancellationToken.None);

        var groups = GroupsPushedTo();
        Assert.Contains(SagaHub.ListGroup, groups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.GroupForSaga("OrderSaga", saga.CorrelationId), groups, StringComparer.Ordinal);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public async Task IgnoresSagasNotUpdatedSinceTheWatermark()
    {
        var since = DateTimeOffset.UtcNow;
        await SeedAsync("OrderSaga", since.AddSeconds(-5));

        var next = await _service.PollOnceAsync(since, CancellationToken.None);

        Assert.Empty(_hub.Recorder.SagaUpdates);
        Assert.Equal(since, next);
    }

    /// <summary>
    /// The comparison is strictly greater-than, so a saga stamped exactly at the watermark is treated as
    /// already delivered. Pinned deliberately: this is the boundary that decides between re-pushing the
    /// previous tick's last saga forever and dropping one that landed on the same tick.
    /// </summary>
    [Fact]
    public async Task ASagaStampedExactlyAtTheWatermarkIsTreatedAsAlreadySeen()
    {
        var since = DateTimeOffset.UtcNow;
        await SeedAsync("OrderSaga", since);

        var next = await _service.PollOnceAsync(since, CancellationToken.None);

        Assert.Empty(_hub.Recorder.SagaUpdates);
        Assert.Equal(since, next);
    }

    [Fact]
    public async Task AdvancesTheWatermarkToTheNewestChangeSoTheNextTickIsQuiet()
    {
        var since = DateTimeOffset.UtcNow;
        await SeedAsync("OrderSaga", since.AddSeconds(1));
        var newest = since.AddSeconds(3);
        await SeedAsync("OtherSaga", newest);

        var next = await _service.PollOnceAsync(since, CancellationToken.None);
        Assert.Equal(newest, next);

        var pushesAfterFirstTick = _hub.Recorder.SagaUpdates.Count;

        // Nothing changed in between, so a second tick from the returned watermark must push nothing.
        var afterSecond = await _service.PollOnceAsync(next, CancellationToken.None);

        Assert.Equal(pushesAfterFirstTick, _hub.Recorder.SagaUpdates.Count);
        Assert.Equal(newest, afterSecond);
    }

    [Fact]
    public async Task PushesChangesOldestFirst()
    {
        var since = DateTimeOffset.UtcNow;
        var middle = await SeedAsync("B", since.AddSeconds(2));
        var oldest = await SeedAsync("A", since.AddSeconds(1));
        var newest = await SeedAsync("C", since.AddSeconds(3));

        await _service.PollOnceAsync(since, CancellationToken.None);

        // Two pushes per saga (list + instance); the per-saga order is what matters here.
        var order = _hub.Recorder.SagaUpdates
            .Select(c => c.Summary.CorrelationId)
            .Distinct()
            .ToList();

        Assert.Equal([oldest.CorrelationId, middle.CorrelationId, newest.CorrelationId], order);
    }

    /// <summary>
    /// The cross-process counterpart of the notifier test: when an orchestrated and a choreographed saga
    /// share one correlation id — exactly what the OrderProcessing sample now produces — the poller must
    /// address two distinct instance groups, not push both under one.
    /// </summary>
    [Fact]
    public async Task TwoSagaTypesSharingACorrelationIdArePushedToSeparateInstanceGroups()
    {
        var since = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid();
        await SeedAsync("OrderSaga", since.AddSeconds(1), correlationId);
        await SeedAsync("PostShipmentChoreography", since.AddSeconds(2), correlationId);

        await _service.PollOnceAsync(since, CancellationToken.None);

        var instanceGroups = GroupsPushedTo()
            .Where(g => !string.Equals(g, SagaHub.ListGroup, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, instanceGroups.Count);
        Assert.Contains(SagaHub.GroupForSaga("OrderSaga", correlationId), instanceGroups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.GroupForSaga("PostShipmentChoreography", correlationId), instanceGroups, StringComparer.Ordinal);
    }

    /// <summary>
    /// The regression this poller was rewritten for. A tick used to read one capped page of the
    /// most-recently-updated sagas and then advance the watermark to the newest row in it, so under the
    /// sample's continuous load every change past that page was pushed to nobody — and, being older than
    /// the new watermark, was never looked at again. One tick has to drain the whole change set.
    /// </summary>
    [Fact]
    public async Task DrainsEveryChangeWhenMoreThanOnePageChangedInOneTick()
    {
        var since = DateTimeOffset.UtcNow;
        var count = SagaChangePollingService.PageSize + 25;
        var seeded = await SeedAscendingAsync(count, since.AddSeconds(1));

        await _service.PollOnceAsync(since, CancellationToken.None);

        // Seeded oldest-first and pushed oldest-first, so the delivered order pins both completeness and
        // the ordering the dashboard relies on.
        Assert.Equal(seeded, ListGroupPushes());
    }

    /// <summary>
    /// A backlog larger than one tick's page budget must cost latency, not updates: the watermark a
    /// truncated tick returns sits on a row it actually pushed, so everything it never reached is still
    /// strictly newer than the watermark and stays inside the next tick's window.
    /// </summary>
    [Fact]
    public async Task ABacklogBiggerThanOneTicksBudgetIsFinishedByTheNextTickWithNothingDropped()
    {
        var since = DateTimeOffset.UtcNow;
        var budget = SagaChangePollingService.MaxChangePagesPerTick * SagaChangePollingService.PageSize;
        var seeded = await SeedAscendingAsync(budget + 50, since.AddSeconds(1));

        var afterFirstTick = await _service.PollOnceAsync(since, CancellationToken.None);
        Assert.Equal(budget, ListGroupPushes().Count);

        await _service.PollOnceAsync(afterFirstTick, CancellationToken.None);

        var delivered = ListGroupPushes().ToHashSet();
        Assert.All(seeded, id => Assert.Contains(id, delivered));
    }

    /// <summary>
    /// The cost regression that came with draining ascending. <see cref="SagaListFilter"/> could sort by
    /// UpdatedAt but not filter on it, so every tick paged through — and discarded — the entire
    /// unchanged head of the table before reaching the changed tail: O(table size) round trips, once a
    /// second, forever. The watermark now rides along in the filter, so a store that is mostly quiet
    /// costs one round trip rather than one per <see cref="SagaChangePollingService.PageSize"/> rows.
    /// <para>
    /// Also pins that the store, not the poller's client-side guard, is what discards the old rows:
    /// a single page came back and it held only the changed saga.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DrainsFromTheWatermarkInTheQueryInsteadOfPagingThroughTheUnchangedHead()
    {
        var since = DateTimeOffset.UtcNow;

        // A head comfortably larger than one page, all of it delivered by some earlier tick.
        await SeedAscendingAsync(SagaChangePollingService.PageSize + 50, since.AddMinutes(-5));
        var changed = await SeedAsync("OrderSaga", since.AddSeconds(1));

        await _service.PollOnceAsync(since, CancellationToken.None);

        var filter = Assert.Single(_reader.Filters);
        Assert.Equal(since, filter.UpdatedSince);
        Assert.Equal([changed.CorrelationId], ListGroupPushes());
    }
}

/// <summary>
/// Pass-through <see cref="ISagaSummaryReader"/> that records the filters it is asked for, so a test can
/// assert on the query the poller issues (how many round trips, and with what watermark) rather than only
/// on what came out the other end. Hand-written, matching this repo's fakes-over-mocking-library
/// convention; delegating to the real <see cref="InMemorySagaStore"/> keeps the filtering under test real.
/// </summary>
internal sealed class RecordingSummaryReader(ISagaSummaryReader inner) : ISagaSummaryReader
{
    public List<SagaListFilter> Filters { get; } = [];

    public Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
    {
        Filters.Add(filter);
        return inner.ListAsync(filter, cancellationToken);
    }

    public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(sagaType, correlationId, cancellationToken);

    public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
        inner.GetDataJsonAsync(sagaType, correlationId, cancellationToken);

    public Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
        inner.FindByCorrelationIdAsync(correlationId, cancellationToken);

    public Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default) =>
        inner.FindChildrenAsync(parentSagaType, parentCorrelationId, cancellationToken);

    public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default) =>
        inner.GetSagaTypesAsync(cancellationToken);
}
