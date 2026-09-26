using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.Conformance;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis.Tests;

/// <summary>
/// What the conformance suite cannot express because it is Redis-specific: the script-cache discipline,
/// the torn-write sentinel, the configuration probe's verdicts, the search bound, and the change
/// poller's drain over rows sharing one instant.
/// </summary>
[Collection(RedisConformanceGroup.Name)]
public sealed class RedisProviderTests(RedisProviderFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ConformanceSagaState NewState(string sagaType = "OrderSaga", DateTimeOffset? updatedAtUtc = null, SagaStatus status = SagaStatus.Running) => new()
    {
        CorrelationId = Guid.NewGuid(),
        SagaType = sagaType,
        CurrentState = "Started",
        Status = status,
        CreatedAtUtc = T0,
        UpdatedAtUtc = updatedAtUtc ?? T0,
    };

    /// <summary>
    /// The script cache is volatile (a restart, SCRIPT FLUSH, a failover), and a NOSCRIPT that escaped
    /// would redeliver every in-flight message at once. Every write path is exercised with the cache
    /// emptied immediately before it, and none may fail.
    /// </summary>
    [Fact]
    public async Task ScriptFlush_BeforeEveryWritePath_CausesNoObservableFailure()
    {
        await using var stores = await fixture.CreateNamespaceAsync();
        var multiplexer = await fixture.Connection.GetMultiplexerAsync();
        var server = multiplexer.GetServers().First(s => s.IsConnected);
        var state = NewState();

        await using var uow = await stores.BeginAsync();
        await server.ScriptFlushAsync();
        await uow.Snapshots<ConformanceSagaState>().InsertAsync(state);
        await server.ScriptFlushAsync();
        await uow.Outbox.EnqueueAsync("OrderSaga", state.CorrelationId, "m1", "Reserved", "{}"u8.ToArray(), null, new Dictionary<string, string>(StringComparer.Ordinal), T0);
        state.CurrentState = "Next";
        await uow.Snapshots<ConformanceSagaState>().UpdateAsync(state, expectedVersion: 0);
        await server.ScriptFlushAsync();
        var sequence = await uow.EventLog.AppendAsync(SagaLogEntry.Create(state.CorrelationId, "OrderSaga", SagaEntryType.MessageReceived, messageId: "in-1", occurredAtUtc: T0));
        await server.ScriptFlushAsync();
        await uow.Timeouts.ScheduleAsync("OrderSaga", state.CorrelationId, "Next", T0);
        await server.ScriptFlushAsync();
        await uow.Timeouts.CancelAsync("OrderSaga", state.CorrelationId, "Next");
        await server.ScriptFlushAsync();
        var timeouts = await uow.Timeouts.ClaimDueAsync(T0.AddMinutes(1), 10);
        await server.ScriptFlushAsync();
        var outbox = await uow.Outbox.ClaimPendingAsync(T0.AddMinutes(1), 10);
        await server.ScriptFlushAsync();
        await uow.Outbox.MarkDispatchedAsync("m1");
        await server.ScriptFlushAsync();
        await uow.Admin.ResetStateAsync("OrderSaga", state.CorrelationId, "Started", SagaStatus.Running, expectedVersion: 1, T0.AddMinutes(1));

        Assert.Equal(1, sequence);
        Assert.Empty(timeouts);
        Assert.Equal("m1", Assert.Single(outbox).MessageId);
        Assert.Equal(2, (await uow.Summaries.GetAsync("OrderSaga", state.CorrelationId))!.Version);
        Assert.True(await uow.EventLog.IsDuplicateAsync("OrderSaga", state.CorrelationId, "in-1"));
    }

    /// <summary>
    /// A Lua script has no rollback. The sentinel is written first and deleted last, so a script that
    /// aborts between the snapshot write and the index writes leaves it behind, the health check goes
    /// Unhealthy naming the instance, and the live object still holds the version it expected (clause 1).
    /// </summary>
    [Fact]
    public async Task TornPersist_LeavesTheSentinel_AndTheHealthCheckNamesTheInstance()
    {
        await using var stores = await fixture.CreateNamespaceAsync();
        var state = NewState();
        await using var uow = await stores.BeginAsync();
        await uow.Snapshots<ConformanceSagaState>().InsertAsync(state);
        await WriteSchemaMarkerAsync(stores);
        Assert.True((await stores.Probe.ProbeAsync()).IsHealthy);

        stores.Scripts.FailAfterSnapshotWrite = true;
        state.CurrentState = "Torn";
        var thrown = await Record.ExceptionAsync(() => uow.Snapshots<ConformanceSagaState>().UpdateAsync(state, expectedVersion: 0));
        stores.Scripts.FailAfterSnapshotWrite = false;

        Assert.IsType<RedisServerException>(thrown);
        Assert.Equal(0, state.Version);
        var db = await fixture.Connection.GetDatabaseAsync();
        Assert.Equal(1, await db.HashLengthAsync(stores.Keys.Torn));

        var health = await new RedisPersistenceHealthCheck(stores.Probe).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        Assert.Contains(RedisKeySpace.Member(state.CorrelationId, "OrderSaga"), health.Description, StringComparison.Ordinal);
        Assert.Contains("torn write", health.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_OnATierAInstance_IsHealthy_AndReportsTierA()
    {
        await using var stores = await fixture.CreateNamespaceAsync();
        await WriteSchemaMarkerAsync(stores);

        var report = await stores.Probe.ProbeAsync();

        Assert.True(report.IsHealthy, report.Describe());
        Assert.Equal("A", report.DurabilityTier);
        Assert.Equal("yes", report.AppendOnly);
        Assert.Equal("everysec", report.AppendFsync);
        Assert.Equal("noeviction", report.MaxMemoryPolicy);
        Assert.Equal(0, report.TornWrites);
        Assert.NotNull(report.ServerVersion);
    }

    /// <summary>Until the bootstrapper has written the schema marker, the key space is not known to be this provider's layout.</summary>
    [Fact]
    public async Task Probe_WithoutTheSchemaMarker_IsUnhealthy_AndTheBootstrapperWritesIt()
    {
        await using var stores = await fixture.CreateNamespaceAsync();
        var before = await stores.Probe.ProbeAsync();
        Assert.Contains(before.Failures, f => f.Contains("schema marker", StringComparison.Ordinal));

        var bootstrapper = new RedisPersistenceBootstrapper(stores.Connection, stores.Keys, stores.Probe, stores.Options, NullLogger<RedisPersistenceBootstrapper>.Instance);
        await bootstrapper.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (ReferenceEquals(stores.Probe.Latest, before))
                await Task.Delay(50, timeout.Token);
        }
        finally
        {
            await bootstrapper.StopAsync(CancellationToken.None);
        }

        Assert.True(stores.Probe.Latest!.IsHealthy, stores.Probe.Latest.Describe());
        var db = await fixture.Connection.GetDatabaseAsync();
        Assert.Equal(RedisPersistenceBootstrapper.SchemaVersion, (string?)await db.HashGetAsync(stores.Keys.Meta, RedisPersistenceBootstrapper.SchemaVersionField));
    }

    /// <summary>The pre-flight gate refuses a persist before any write once the last probe put the server above the threshold.</summary>
    [Fact]
    public async Task Persist_AboveTheMemoryThreshold_IsRefusedBeforeAnyWrite()
    {
        await using var stores = await fixture.CreateNamespaceAsync(new VSagaRedisOptions { WriteMemoryThreshold = 0.0 });
        await WriteSchemaMarkerAsync(stores);
        var db = await fixture.Connection.GetDatabaseAsync();
        var server = (await fixture.Connection.GetMultiplexerAsync()).GetServers().First(s => s.IsConnected);

        // A maxmemory far above use, so nothing is evicted or rejected server-side; only the provider's own gate fires.
        await server.ConfigSetAsync("maxmemory", "1gb");
        try
        {
            var report = await stores.Probe.ProbeAsync();
            Assert.Contains(report.Failures, f => f.Contains("Memory pressure", StringComparison.Ordinal));

            await using var uow = await stores.BeginAsync();
            var state = NewState();
            await Assert.ThrowsAsync<RedisMemoryPressureException>(() => uow.Snapshots<ConformanceSagaState>().InsertAsync(state));
            Assert.False(await db.KeyExistsAsync(stores.Keys.Saga(state.CorrelationId, "OrderSaga")));
        }
        finally
        {
            await server.ConfigSetAsync("maxmemory", "0");
        }
    }

    /// <summary>A search over more candidates than the bound throws rather than truncating a page, which the change poller would read as "drained".</summary>
    [Fact]
    public async Task Search_AboveTheScanBound_Throws_AndANarrowingFilterBringsItBack()
    {
        await using var stores = await fixture.CreateNamespaceAsync(new VSagaRedisOptions { MaxSearchScanMembers = 5 });
        await using var uow = await stores.BeginAsync();
        for (var i = 0; i < 6; i++)
            await uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState(i < 3 ? "OrderSaga" : "ShippingSaga"));

        var thrown = await Assert.ThrowsAsync<RedisSearchScanLimitExceededException>(() => uow.Summaries.ListAsync(new SagaListFilter { Search = "saga" }));
        Assert.Equal(6, thrown.Candidates);
        Assert.Equal(5, thrown.Limit);

        var narrowed = await uow.Summaries.ListAsync(new SagaListFilter { Search = "saga", SagaType = "OrderSaga" });
        Assert.Equal(3, narrowed.TotalCount);
    }

    /// <summary>
    /// The change poller's shape (ascending UpdatedAt, UpdatedSince, page after page under one watermark)
    /// over 2000 rows all updated at the same instant: each row exactly once, no short page while rows
    /// remain, and the watermark the drain ends on excludes every one of them on the next tick.
    /// </summary>
    [Fact]
    public async Task UpdatedSinceDrain_OverRowsSharingOneInstant_VisitsEachRowExactlyOnce()
    {
        const int rows = 2000;
        const int pageSize = 100;
        await using var stores = await fixture.CreateNamespaceAsync();
        var scripts = new RedisPersistScripts(stores.Connection, stores.Keys, stores.Options, stores.Probe);
        var inserts = Enumerable.Range(0, rows).Select(_ => new RedisSagaSnapshotStore<ConformanceSagaState>(stores.Connection, stores.Keys, scripts, new RedisSagaUnitOfWork()).InsertAsync(NewState()));
        await Task.WhenAll(inserts);

        await using var uow = await stores.BeginAsync();
        var seen = new List<Guid>();
        for (var page = 1; page <= 25; page++)
        {
            var result = await uow.Summaries.ListAsync(new SagaListFilter { UpdatedSince = T0.AddSeconds(-1), SortBy = SagaSortColumn.UpdatedAt, Page = page, PageSize = pageSize });
            Assert.Equal(rows, result.TotalCount);
            seen.AddRange(result.Items.Select(s => s.CorrelationId));
            if (result.Items.Count < pageSize)
                break;
        }

        Assert.Equal(rows, seen.Count);
        Assert.Equal(rows, seen.Distinct().Count());
        var next = await uow.Summaries.ListAsync(new SagaListFilter { UpdatedSince = T0, SortBy = SagaSortColumn.UpdatedAt, PageSize = pageSize });
        Assert.Empty(next.Items);
        Assert.Equal(0, next.TotalCount);
    }

    /// <summary>The Status sort pages across buckets by arithmetic over their counts; a page straddling two buckets takes the tail of one and the head of the next.</summary>
    [Fact]
    public async Task StatusSort_PagesAcrossBuckets_WithAndWithoutAResidualFilter()
    {
        await using var stores = await fixture.CreateNamespaceAsync();
        await using var uow = await stores.BeginAsync();
        var expected = new List<Guid>();
        foreach (var status in new[] { SagaStatus.Running, SagaStatus.Completed, SagaStatus.Failed })
        {
            for (var i = 3; i >= 0; i--)
            {
                var state = NewState(updatedAtUtc: T0.AddSeconds(i), status: status);
                state.Kind = SagaKind.Choreographed;
                await uow.Snapshots<ConformanceSagaState>().InsertAsync(state);
                expected.Add(state.CorrelationId);
            }

            await uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState("Orchestrated", status: status));
        }

        foreach (var filter in new[] { new SagaListFilter { Kind = SagaKind.Choreographed }, new SagaListFilter { Kind = SagaKind.Choreographed, Search = "order" } })
        {
            var walked = new List<Guid>();
            for (var page = 1; page <= 3; page++)
            {
                var result = await uow.Summaries.ListAsync(new SagaListFilter { Kind = filter.Kind, Search = filter.Search, SortBy = SagaSortColumn.Status, Page = page, PageSize = 5 });
                Assert.Equal(12, result.TotalCount);
                walked.AddRange(result.Items.Select(s => s.CorrelationId));
            }

            Assert.Equal(expected, walked);
        }

        var unfiltered = await uow.Summaries.ListAsync(new SagaListFilter { SortBy = SagaSortColumn.Status, PageSize = 100 });
        Assert.Equal(15, unfiltered.TotalCount);
        Assert.Equal([SagaStatus.Running, SagaStatus.Completed, SagaStatus.Failed], unfiltered.Items.Select(s => s.Status).Distinct());
    }

    private static async Task WriteSchemaMarkerAsync(RedisProviderStores stores)
    {
        var db = await stores.Connection.GetDatabaseAsync();
        await db.HashSetAsync(stores.Keys.Meta, RedisPersistenceBootstrapper.SchemaVersionField, RedisPersistenceBootstrapper.SchemaVersion);
    }
}
