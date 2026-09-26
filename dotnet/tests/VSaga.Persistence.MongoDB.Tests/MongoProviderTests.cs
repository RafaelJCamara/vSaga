using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.Conformance;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB.Tests;

/// <summary>
/// What the conformance suite cannot express because it is MongoDB-specific: the plan's stage gates --
/// the business-key race under parallel inserts, a staged row surviving a duplicate-key insert, the
/// indexes actually serving the hot lookups, the change poller's drain over rows sharing one instant --
/// plus the payload guard, the explicit-null destination, the bootstrapper and the health check's verdicts.
/// </summary>
[Collection(MongoConformanceGroup.Name)]
public sealed class MongoProviderTests(MongoProviderFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ConformanceSagaState NewState(string sagaType = "OrderSaga", string? businessKey = null, DateTimeOffset? updatedAtUtc = null) => new()
    {
        CorrelationId = Guid.NewGuid(),
        SagaType = sagaType,
        CurrentState = "Started",
        Status = SagaStatus.Running,
        BusinessKey = businessKey,
        CreatedAtUtc = T0,
        UpdatedAtUtc = updatedAtUtc ?? T0,
    };

    /// <summary>Stage 2's gate: N parallel inserts on one business key -- one success, and the losers report the collision, not a driver error.</summary>
    [Fact]
    public async Task ParallelInserts_OnOneBusinessKey_ExactlyOneSucceeds()
    {
        const int racers = 16;
        await using var stores = await fixture.CreateDatabaseAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, racers).Select(async _ =>
        {
            await using var uow = await stores.BeginAsync();
            return await Record.ExceptionAsync(() => uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState(businessKey: "ORD-RACE")));
        }));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<SagaAlreadyExistsException>(o));
        await using var reader = await stores.BeginAsync();
        Assert.Equal(1, (await reader.Summaries.ListAsync(new SagaListFilter { SagaType = "OrderSaga" })).TotalCount);
    }

    /// <summary>
    /// The insert path's half of the atomicity crux: a staged row and an insert that collides on the
    /// business key -> the transaction aborts, no outbox document exists, and the row is still staged for a
    /// later persist or discard in the same unit of work (peek-commit-clear, never take-then-write).
    /// </summary>
    [Fact]
    public async Task StagedRow_SurvivesAnInsertThatCollides_AndIsNeverDurable()
    {
        await using var stores = await fixture.CreateDatabaseAsync();
        await using (var seed = await stores.BeginAsync())
            await seed.Snapshots<ConformanceSagaState>().InsertAsync(NewState(businessKey: "ORD-1"));

        await using var uow = await stores.BeginAsync();
        await uow.Outbox.EnqueueAsync("OrderSaga", Guid.NewGuid(), "m1", "Reserved", "{}"u8.ToArray(), null, new Dictionary<string, string>(StringComparer.Ordinal), T0);
        await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState(businessKey: "ORD-1")));

        Assert.Equal(0, await stores.Collections.Outbox.CountDocumentsAsync(Builders<SagaOutboxDocument>.Filter.Empty));

        // Still staged: the next successful persist in this unit of work commits it.
        await uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState(businessKey: "ORD-2"));
        Assert.Equal("m1", Assert.Single(await uow.Outbox.ClaimPendingAsync(T0.AddMinutes(1), 10)).MessageId);
    }

    /// <summary>Stage 2's and Stage 5's explain gates: the business-key lookup and the default listing are served by their indexes, never by a collection scan.</summary>
    [Fact]
    public async Task HotQueries_AreServedByTheirIndexes()
    {
        await using var stores = await fixture.CreateDatabaseAsync();
        await using var uow = await stores.BeginAsync();
        for (var i = 0; i < 5; i++)
            await uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState(businessKey: $"ORD-{i}"));

        // The exact predicate FindByBusinessKeyAsync issues: the $type clause is what makes the partial
        // index eligible, and this case is what caught its absence.
        var byKey = await ExplainAsync(stores, new BsonDocument { { "sagaType", "OrderSaga" }, { "businessKey", new BsonDocument { { "$type", "string" }, { "$eq", "ORD-3" } } } }, sort: null);
        var listing = await ExplainAsync(stores, new BsonDocument(), new BsonDocument { { "updatedAtTicks", -1 }, { "sagaType", -1 }, { "correlationId", -1 } });

        Assert.True(byKey.Contains(MongoIndexes.BusinessKey, StringComparison.Ordinal), byKey);
        Assert.True(!byKey.Contains("COLLSCAN", StringComparison.Ordinal), byKey);
        Assert.True(listing.Contains(MongoIndexes.UpdatedTotal, StringComparison.Ordinal), listing);
        Assert.True(!listing.Contains("\"stage\" : \"SORT\"", StringComparison.Ordinal), listing);
        Assert.True(!listing.Contains("COLLSCAN", StringComparison.Ordinal), listing);
    }

    private static async Task<string> ExplainAsync(MongoProviderStores stores, BsonDocument filter, BsonDocument? sort)
    {
        var find = new BsonDocument { { "find", MongoCollections.InstancesName }, { "filter", filter } };
        if (sort is not null)
            find["sort"] = sort;

        var explain = await stores.Connection.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "explain", find }, { "verbosity", "queryPlanner" } });
        return explain["queryPlanner"]["winningPlan"].ToJson();
    }

    /// <summary>
    /// Stage 5's gate, and the reason timestamps keep their exact ticks: the change poller's shape
    /// (ascending UpdatedAt, UpdatedSince, page after page under one watermark) over 2000 rows all updated
    /// at the same instant -- each row exactly once, no short page while rows remain, and the watermark
    /// the drain ends on excludes every one of them on the next tick.
    /// </summary>
    [Fact]
    public async Task UpdatedSinceDrain_OverRowsSharingOneInstant_VisitsEachRowExactlyOnce()
    {
        const int rows = 2000;
        const int pageSize = 100;
        await using var stores = await fixture.CreateDatabaseAsync();
        await stores.Collections.Instances.InsertManyAsync(Enumerable.Range(0, rows).Select(_ => SagaInstanceDocument.From(NewState(), "{}")));

        await using var uow = await stores.BeginAsync();
        var seen = new List<Guid>();
        for (var page = 1; page <= 25; page++)
        {
            var result = await uow.Summaries.ListAsync(new SagaListFilter { UpdatedSince = T0.AddTicks(-1), SortBy = SagaSortColumn.UpdatedAt, Page = page, PageSize = pageSize });
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

    /// <summary>The 16 MB cap (R-15): an oversized payload is replaced by the marker and the entry still lands, so the saga can still start.</summary>
    [Fact]
    public async Task Append_WithAPayloadAboveTheLimit_StoresTheMarkerInstead()
    {
        await using var stores = await fixture.CreateDatabaseAsync(new VSagaMongoOptions { MaxPayloadJsonBytes = 64 });
        var correlationId = Guid.NewGuid();
        var oversized = "{\"blob\":\"" + new string('x', 200) + "\"}";

        await using var uow = await stores.BeginAsync();
        await uow.EventLog.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted, messageId: "m1", payloadJson: oversized, occurredAtUtc: T0));
        await uow.EventLog.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StateEntered, payloadJson: "{\"small\":1}", occurredAtUtc: T0));

        var timeline = await uow.EventLog.GetTimelineAsync("OrderSaga", correlationId);
        Assert.Equal("{\"$vsagaPayloadOmitted\":true,\"bytes\":211,\"limit\":64}", timeline[0].PayloadJson);
        Assert.Equal("{\"small\":1}", timeline[1].PayloadJson);
        Assert.True(await uow.EventLog.IsDuplicateAsync("OrderSaga", correlationId, "m1"));
    }

    /// <summary>A null destination round-trips as null -- the dispatcher branches on it to choose a broadcast over an addressed send -- and the document carries it as an explicit null field.</summary>
    [Fact]
    public async Task ClaimPending_ReturnsANullDestinationAsNull_StoredExplicitly()
    {
        await using var stores = await fixture.CreateDatabaseAsync();
        await using var uow = await stores.BeginAsync();
        await uow.Outbox.EnqueueAsync("OrderSaga", Guid.NewGuid(), "m1", "Reserved", "{}"u8.ToArray(), null, new Dictionary<string, string>(StringComparer.Ordinal), T0);
        await uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState());

        var raw = await stores.Connection.Database.GetCollection<BsonDocument>(MongoCollections.OutboxName).Find(new BsonDocument()).SingleAsync();
        Assert.True(raw.Contains("destination"));
        Assert.True(raw["destination"].IsBsonNull);
        Assert.Null(Assert.Single(await uow.Outbox.ClaimPendingAsync(T0.AddMinutes(1), 10)).Destination);
    }

    /// <summary>Until the bootstrapper has created the indexes and written the marker, the database is not known to have this provider's layout; the bootstrapper makes it so and the probe passes.</summary>
    [Fact]
    public async Task Probe_OnAnUnpreparedDatabase_IsUnhealthy_AndTheBootstrapperPreparesIt()
    {
        await using var stores = await fixture.CreateDatabaseAsync();
        await stores.Connection.Database.DropCollectionAsync(MongoCollections.InstancesName);

        var before = await stores.Probe.ProbeAsync();
        Assert.Contains(before.Failures, f => f.Contains(MongoIndexes.BusinessKey, StringComparison.Ordinal));
        Assert.Contains(before.Failures, f => f.Contains("schema marker", StringComparison.Ordinal));

        var bootstrapper = new MongoPersistenceBootstrapper(stores.Collections, stores.Probe, stores.Options, NullLogger<MongoPersistenceBootstrapper>.Instance);
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

        var after = stores.Probe.Latest!;
        Assert.True(after.IsHealthy, after.Describe());
        Assert.Equal("replica set", after.Topology);
        Assert.Equal("rs0", after.ReplicaSetName);
        Assert.True(after.IndexesInPlace);
    }

    [Fact]
    public async Task HealthCheck_OnAPreparedReplicaSet_IsHealthyNamingTheTopology()
    {
        await using var stores = await fixture.CreateDatabaseAsync();
        var bootstrapper = new MongoPersistenceBootstrapper(stores.Collections, stores.Probe, stores.Options, NullLogger<MongoPersistenceBootstrapper>.Instance);
        await bootstrapper.EnsureLayoutAsync(CancellationToken.None);

        var health = await new MongoPersistenceHealthCheck(stores.Probe).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Contains("replica set rs0 (primary)", health.Description, StringComparison.Ordinal);
        Assert.Equal("replica set", health.Data["topology"]);
        Assert.Equal(0L, health.Data["strandedOutboxRows"]);
    }

    /// <summary>A Pending row older than the threshold is counted as stranded and reported in the health data, without failing the check.</summary>
    [Fact]
    public async Task HealthCheck_CountsStrandedOutboxRows_WithoutFailing()
    {
        await using var stores = await fixture.CreateDatabaseAsync(new VSagaMongoOptions { StrandedOutboxThreshold = TimeSpan.FromMinutes(5) });
        await new MongoPersistenceBootstrapper(stores.Collections, stores.Probe, stores.Options, NullLogger<MongoPersistenceBootstrapper>.Instance).EnsureLayoutAsync(CancellationToken.None);
        await using var uow = await stores.BeginAsync();
        await uow.Outbox.EnqueueAsync("OrderSaga", Guid.NewGuid(), "old", "Reserved", "{}"u8.ToArray(), null, new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow.AddHours(-1));
        await uow.Outbox.EnqueueAsync("OrderSaga", Guid.NewGuid(), "fresh", "Reserved", "{}"u8.ToArray(), null, new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
        await uow.CommitAsync();

        var health = await new MongoPersistenceHealthCheck(stores.Probe).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal(1L, health.Data["strandedOutboxRows"]);
        Assert.Contains("1 stranded outbox row(s)", health.Description, StringComparison.Ordinal);
    }

    /// <summary>The timeline counter is the instance's own: two saga types on one correlation id, and two instances of one type, each count from 1.</summary>
    [Fact]
    public async Task SequenceNumbers_ArePerInstance()
    {
        await using var stores = await fixture.CreateDatabaseAsync();
        var correlationId = Guid.NewGuid();
        await using var uow = await stores.BeginAsync();

        Assert.Equal(1, await uow.EventLog.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StateEntered, occurredAtUtc: T0)));
        Assert.Equal(2, await uow.EventLog.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StateEntered, occurredAtUtc: T0)));
        Assert.Equal(1, await uow.EventLog.AppendAsync(SagaLogEntry.Create(correlationId, "ShippingChoreography", SagaEntryType.StateEntered, occurredAtUtc: T0)));
        Assert.Equal(1, await uow.EventLog.AppendAsync(SagaLogEntry.Create(Guid.NewGuid(), "OrderSaga", SagaEntryType.StateEntered, occurredAtUtc: T0)));
    }
}
