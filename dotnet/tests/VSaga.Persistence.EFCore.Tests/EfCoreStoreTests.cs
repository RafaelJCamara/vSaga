using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore.Tests;

public sealed class TestState : SagaState
{
    public string? OrderId { get; set; }
}

public sealed class EfCoreStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<VSagaDbContext> _options;

    public EfCoreStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<VSagaDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new VSagaDbContext(_options);
        db.Database.EnsureCreated();
    }

    private VSagaDbContext NewContext() => new(_options);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    [Fact]
    public async Task InsertAndFind_RoundTripsTypedState()
    {
        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);

        var correlationId = Guid.NewGuid();
        var state = new TestState
        {
            CorrelationId = correlationId,
            SagaType = "TestSaga",
            Kind = SagaKind.Orchestrated,
            CurrentState = "Started",
            Status = SagaStatus.Running,
            OrderId = "ORD-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.InsertAsync(state);

        await using var db2 = NewContext();
        var found = await new EfCoreSagaSnapshotStore<TestState>(db2).FindAsync("TestSaga", correlationId);

        Assert.NotNull(found);
        Assert.Equal("ORD-1", found.OrderId);
        Assert.Equal("Started", found.CurrentState);
        Assert.Equal(0, found.Version);
    }

    [Fact]
    public async Task Update_WithCorrectVersion_SucceedsAndBumpsVersion()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Started",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        TestState? state;
        await using (var db2 = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db2);
            state = await store.FindAsync("TestSaga", correlationId);
            Assert.NotNull(state);

            state.CurrentState = "Next";
            await store.UpdateAsync(state, expectedVersion: 0);

            Assert.Equal(1, state.Version);
        }

        await using var db3 = NewContext();
        var reloaded = await new EfCoreSagaSnapshotStore<TestState>(db3).FindAsync("TestSaga", correlationId);
        Assert.Equal("Next", reloaded!.CurrentState);
        Assert.Equal(1, reloaded.Version);
    }

    [Fact]
    public async Task Update_WithStaleVersion_ThrowsSagaConcurrencyException()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Started",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using var db2 = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db2);
        var state = await store.FindAsync("TestSaga", correlationId);

        await Assert.ThrowsAsync<SagaConcurrencyException>(() => store.UpdateAsync(state!, expectedVersion: 5));
    }

    // --- (SagaType, CorrelationId) scoping -------------------------------------------------
    // These target the EF provider specifically: the composite primary key and the SagaType
    // predicate on every query live here, and without them a store that dropped the sagaType
    // filter entirely would still pass every other test in this file (they all seed one type).

    private static TestState NewState(Guid correlationId, string sagaType, string currentState) => new()
    {
        CorrelationId = correlationId,
        SagaType = sagaType,
        CurrentState = currentState,
        Status = SagaStatus.Running,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Insert_SameCorrelationIdUnderDifferentSagaType_Succeeds()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(NewState(correlationId, "OrderSaga", "Submitted"));
            await store.InsertAsync(NewState(correlationId, "ShippingChoreography", "Tracking"));
        }

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSnapshotStore<TestState>(db2);

        // Each type resolves to its own row, not to whichever one happened to be written first.
        Assert.Equal("Submitted", (await reader.FindAsync("OrderSaga", correlationId))!.CurrentState);
        Assert.Equal("Tracking", (await reader.FindAsync("ShippingChoreography", correlationId))!.CurrentState);
    }

    [Fact]
    public async Task Update_OnlyAffectsItsOwnSagaTypesRow()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(NewState(correlationId, "OrderSaga", "Submitted"));
            await store.InsertAsync(NewState(correlationId, "ShippingChoreography", "Tracking"));
        }

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            var order = await store.FindAsync("OrderSaga", correlationId);
            order!.CurrentState = "Completed";
            await store.UpdateAsync(order, expectedVersion: 0);
        }

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSnapshotStore<TestState>(db2);

        Assert.Equal("Completed", (await reader.FindAsync("OrderSaga", correlationId))!.CurrentState);
        Assert.Equal("Tracking", (await reader.FindAsync("ShippingChoreography", correlationId))!.CurrentState);
    }

    [Fact]
    public async Task TimelineAndDuplicateCheck_AreScopedToOneSagaType()
    {
        var correlationId = Guid.NewGuid();

        await using var db = NewContext();
        var log = new EfCoreSagaEventLogStore(db);

        await log.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived, messageId: "m1"));
        await log.AppendAsync(SagaLogEntry.Create(correlationId, "ShippingChoreography", SagaEntryType.MessageReceived, messageId: "m2"));

        var orderTimeline = await log.GetTimelineAsync("OrderSaga", correlationId);
        var choreoTimeline = await log.GetTimelineAsync("ShippingChoreography", correlationId);

        Assert.Equal("m1", Assert.Single(orderTimeline).MessageId);
        Assert.Equal("m2", Assert.Single(choreoTimeline).MessageId);

        // A message id already seen by one saga type must not look like a duplicate to the other —
        // the same broadcast legitimately reaches both, and each must process its own copy.
        Assert.True(await log.IsDuplicateAsync("OrderSaga", correlationId, "m1"));
        Assert.False(await log.IsDuplicateAsync("ShippingChoreography", correlationId, "m1"));
    }

    [Fact]
    public async Task CancelTimeout_DoesNotCancelAnotherSagaTypesSameNamedState()
    {
        var correlationId = Guid.NewGuid();
        var dueAt = DateTimeOffset.UtcNow.AddMinutes(5);

        await using var db = NewContext();
        var timeouts = new EfCoreSagaTimeoutStore(db);

        // "Reserved" means two different things in two different saga types.
        await timeouts.ScheduleAsync("OrderSaga", correlationId, "Reserved", dueAt);
        await timeouts.ScheduleAsync("ShippingChoreography", correlationId, "Reserved", dueAt);

        await timeouts.CancelAsync("OrderSaga", correlationId, "Reserved");

        var due = await timeouts.ClaimDueAsync(dueAt.AddMinutes(1), batchSize: 10);

        var survivor = Assert.Single(due, t => t.CorrelationId == correlationId);
        Assert.Equal("ShippingChoreography", survivor.SagaType);
    }

    [Fact]
    public async Task FindByCorrelationId_ReturnsEverySagaTypeTrackingIt()
    {
        var correlationId = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);
        await store.InsertAsync(NewState(correlationId, "OrderSaga", "Submitted"));
        await store.InsertAsync(NewState(correlationId, "ShippingChoreography", "Tracking"));
        await store.InsertAsync(NewState(unrelated, "OrderSaga", "Submitted"));

        var matches = await new EfCoreSagaSummaryReader(db).FindByCorrelationIdAsync(correlationId);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(correlationId, m.CorrelationId));
        Assert.Equal(["OrderSaga", "ShippingChoreography"], matches.Select(m => m.SagaType), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResetState_OnlyAffectsItsOwnSagaTypesRow()
    {
        var correlationId = Guid.NewGuid();

        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);
        await store.InsertAsync(NewState(correlationId, "OrderSaga", "Failed"));
        await store.InsertAsync(NewState(correlationId, "ShippingChoreography", "Tracking"));

        await new EfCoreSagaSummaryReader(db).ResetStateAsync("OrderSaga", correlationId, "Submitted", SagaStatus.Running, expectedVersion: 0, DateTimeOffset.UtcNow);

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db2);

        Assert.Equal("Submitted", (await reader.GetAsync("OrderSaga", correlationId))!.CurrentState);
        Assert.Equal("Tracking", (await reader.GetAsync("ShippingChoreography", correlationId))!.CurrentState);
    }

    [Fact]
    public async Task Insert_DuplicateCorrelationId_ThrowsSagaAlreadyExistsException()
    {
        var correlationId = Guid.NewGuid();
        var makeState = () => new TestState { CorrelationId = correlationId, SagaType = "TestSaga", CurrentState = "Started", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };

        await using (var db = NewContext())
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(makeState());

        await using var db2 = NewContext();
        var ex = await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => new EfCoreSagaSnapshotStore<TestState>(db2).InsertAsync(makeState()));

        // production-readiness.md §8.14's review: InsertAsync converts ANY DbUpdateException into this
        // exception, not just a genuine unique-constraint collision -- preserving the original as
        // InnerException keeps an unrelated infra failure's real cause diagnosable instead of discarding it.
        Assert.IsType<DbUpdateException>(ex.InnerException);
    }

    [Fact]
    public async Task EventLog_AppendAndGetTimeline_PreservesOrderAndDetectsDuplicates()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var log = new EfCoreSagaEventLogStore(db);
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.SagaStarted, toState: "Started", messageId: "m1"));
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.MessageReceived, messageId: "m1"));
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.StepSucceeded, fromState: "Started", toState: "Next", messageId: "m1"));
        }

        await using var db2 = NewContext();
        var log2 = new EfCoreSagaEventLogStore(db2);
        var timeline = await log2.GetTimelineAsync("TestSaga", correlationId);

        Assert.Equal(3, timeline.Count);
        Assert.Equal(SagaEntryType.SagaStarted, timeline[0].EntryType);
        Assert.Equal(SagaEntryType.StepSucceeded, timeline[2].EntryType);
        Assert.True(timeline[0].SequenceNumber < timeline[2].SequenceNumber);

        Assert.True(await log2.IsDuplicateAsync("TestSaga", correlationId, "m1"));
        Assert.False(await log2.IsDuplicateAsync("TestSaga", correlationId, "unknown"));
    }

    [Fact]
    public async Task Timeouts_ScheduleClaimAndCancel()
    {
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaTimeoutStore(db);
            await store.ScheduleAsync("TestSaga", correlationId, "AwaitingPayment", now.AddMinutes(-1), CancellationToken.None);
        }

        await using (var db2 = NewContext())
        {
            var store2 = new EfCoreSagaTimeoutStore(db2);
            var due = await store2.ClaimDueAsync(now, batchSize: 10);
            var claimed = Assert.Single(due);
            Assert.Equal(correlationId, claimed.CorrelationId);
            Assert.Equal(SagaTimeoutStatus.Fired, claimed.Status);

            var dueAgain = await store2.ClaimDueAsync(now, batchSize: 10);
            Assert.Empty(dueAgain); // already fired, shouldn't be claimed twice
        }
    }

    [Fact]
    public async Task Outbox_EnqueueRoundTripsEveryFieldAndClaimNeverReturnsItTwice()
    {
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["x-vsaga-source-service"] = "OrderSaga" };
        var body = "{\"OrderId\":\"ORD-1\"}"u8.ToArray();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaOutboxStore(db);
            await store.EnqueueAsync("OrderSaga", correlationId, "m1", "InventoryReserved", body, destination: null, headers, now.AddMinutes(-1));
            // EnqueueAsync only stages; in production the snapshot store's own PersistAsync is what
            // commits this, atomically with the snapshot.
            await db.SaveChangesAsync();
        }

        await using (var db2 = NewContext())
        {
            var store2 = new EfCoreSagaOutboxStore(db2);
            var claimed = Assert.Single(await store2.ClaimPendingAsync(now, batchSize: 10));

            Assert.Equal(correlationId, claimed.CorrelationId);
            Assert.Equal("OrderSaga", claimed.SagaType);
            Assert.Equal("m1", claimed.MessageId);
            Assert.Equal("InventoryReserved", claimed.MessageTypeName);
            Assert.Equal(body, claimed.Body.ToArray());
            Assert.Null(claimed.Destination);
            Assert.Equal("OrderSaga", claimed.Headers["x-vsaga-source-service"]);
            Assert.Equal(SagaOutboxStatus.Dispatched, claimed.Status);

            Assert.Empty(await store2.ClaimPendingAsync(now, batchSize: 10)); // already dispatched, shouldn't be claimed twice
        }
    }

    [Fact]
    public async Task Outbox_MarkDispatchedDirectly_ExcludesItFromTheRecoveryClaim()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaOutboxStore(db);
            await store.EnqueueAsync("OrderSaga", Guid.NewGuid(), "m2", "ChargePayment", "{}"u8.ToArray(),
                destination: "payments", new Dictionary<string, string>(StringComparer.Ordinal), now.AddMinutes(-1));
            await db.SaveChangesAsync();
        }

        // The inline drain's path: mark dispatched directly by message id, right after sending it
        // synchronously. It never goes through ClaimPendingAsync at all.
        await using (var db2 = NewContext())
            await new EfCoreSagaOutboxStore(db2).MarkDispatchedAsync("m2");

        await using var db3 = NewContext();
        Assert.Empty(await new EfCoreSagaOutboxStore(db3).ClaimPendingAsync(now, batchSize: 10));
    }

    /// <summary>
    /// production-readiness.md §4.1 step 2's whole point: the row must not reach the database until the
    /// caller's own PersistAsync commits it alongside the snapshot. An EnqueueAsync that saved on its
    /// own would reopen the dual-write window — a persist that then threw would leave a durable Pending
    /// row describing a transition that never committed, which the recovery poller would publish.
    /// </summary>
    [Fact]
    public async Task Outbox_EnqueueAsync_StagesWithoutCommitting_SoAnAbandonedUnitOfWorkLeavesNoRow()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            await new EfCoreSagaOutboxStore(db).EnqueueAsync("OrderSaga", Guid.NewGuid(), "m3", "InventoryReserved",
                "{}"u8.ToArray(), destination: null, new Dictionary<string, string>(StringComparer.Ordinal), now.AddMinutes(-1));

            // Disposed without ever calling SaveChangesAsync — standing in for a persist that threw.
        }

        await using var db2 = NewContext();
        Assert.Empty(db2.SagaOutboxMessages);
    }

    /// <summary>
    /// The discard path's real hazard: SagaOrchestrator.DiscardDeferredPublishesAsync writes one event-log
    /// entry per dropped publish through this same context, and that AppendAsync calls SaveChangesAsync —
    /// so without an explicit detach, the abandoned rows would be committed by the very code that exists
    /// to suppress them, and the poller would publish them anyway.
    /// </summary>
    [Fact]
    public async Task Outbox_DiscardPendingAsync_SurvivesALaterSaveOnTheSameContext()
    {
        var now = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaOutboxStore(db);
            await store.EnqueueAsync("OrderSaga", correlationId, "m4", "InventoryReserved", "{}"u8.ToArray(),
                destination: null, new Dictionary<string, string>(StringComparer.Ordinal), now.AddMinutes(-1));

            await store.DiscardPendingAsync(["m4"]);

            // Exactly what the discard path does next, through the same shared context.
            await new EfCoreSagaEventLogStore(db).AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga",
                SagaEntryType.DeliveryExhausted, messageType: "InventoryReserved"));
        }

        await using var db2 = NewContext();
        Assert.Empty(db2.SagaOutboxMessages);
    }

    [Fact]
    public async Task SummaryReader_ListsWithFiltering()
    {
        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "A", Status = SagaStatus.Running, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "B", Status = SagaStatus.Failed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db2);

        var all = await reader.ListAsync(new SagaListFilter());
        Assert.Equal(2, all.TotalCount);

        var failedOnly = await reader.ListAsync(new SagaListFilter { Status = SagaStatus.Failed });
        Assert.Equal(1, failedOnly.TotalCount);
        Assert.Equal(SagaStatus.Failed, failedOnly.Items[0].Status);
    }

    /// <summary>
    /// The dashboard's change poller drains UpdatedAtUtc ascending once a second. Without a server-side
    /// timestamp predicate it had to fetch and discard the entire unchanged head of the table before
    /// reaching the changed tail — O(table size) round trips per tick. UpdatedSince pushes that into
    /// SQL, and this pins the two properties the poller's loop depends on: the predicate is strictly
    /// greater-than (a row stamped exactly at the watermark was already delivered, so `&gt;=` would
    /// re-push it forever), and TotalCount describes the *filtered* set, because the poller's
    /// "consumed everything?" stop check is `page * PageSize &gt;= TotalCount`.
    /// </summary>
    [Fact]
    public async Task SummaryReader_UpdatedSince_KeepsOnlyStrictlyNewerRowsAndScopesTotalCount()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            for (var i = 0; i < 5; i++)
            {
                await store.InsertAsync(new TestState
                {
                    CorrelationId = Guid.NewGuid(),
                    SagaType = "OrderSaga",
                    CurrentState = "A",
                    Status = SagaStatus.Running,
                    CreatedAtUtc = t0,
                    UpdatedAtUtc = t0.AddSeconds(i),
                });
            }
        }

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db2);

        // Baseline: an absent UpdatedSince leaves every existing caller's result untouched.
        Assert.Equal(5, (await reader.ListAsync(new SagaListFilter())).TotalCount);

        var changed = await reader.ListAsync(new SagaListFilter
        {
            UpdatedSince = t0.AddSeconds(2),
            SortBy = SagaSortColumn.UpdatedAt,
            SortDescending = false,
        });

        // t0+2 itself is excluded — strictly greater — leaving only t0+3 and t0+4.
        Assert.Equal(2, changed.TotalCount);
        Assert.Equal([t0.AddSeconds(3), t0.AddSeconds(4)], changed.Items.Select(x => x.UpdatedAtUtc).ToList());

        // Paging is over the filtered set too: page 2 of size 1 is the second *changed* row, not the
        // second row of the table, and TotalCount stays scoped so the poller's stop arithmetic counts
        // changed rows rather than the whole store.
        var secondChanged = await reader.ListAsync(new SagaListFilter
        {
            UpdatedSince = t0.AddSeconds(2),
            SortBy = SagaSortColumn.UpdatedAt,
            SortDescending = false,
            Page = 2,
            PageSize = 1,
        });

        Assert.Equal(2, secondChanged.TotalCount);
        Assert.Equal(t0.AddSeconds(4), Assert.Single(secondChanged.Items).UpdatedAtUtc);

        // A watermark sitting on the newest row selects nothing, which is what makes a quiet tick cost
        // exactly one empty round trip instead of a full scan.
        var none = await reader.ListAsync(new SagaListFilter { UpdatedSince = t0.AddSeconds(4) });
        Assert.Equal(0, none.TotalCount);
        Assert.Empty(none.Items);
    }

    /// <summary>
    /// UpdatedSince has to compose with the other predicates rather than replace them — the same
    /// IQueryable feeds CountAsync, so a bug that dropped one of them would silently break both the
    /// returned page and TotalCount together, and look self-consistent while doing it.
    /// </summary>
    [Fact]
    public async Task SummaryReader_UpdatedSince_ComposesWithTheOtherFilters()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "A", Status = SagaStatus.Running, CreatedAtUtc = t0, UpdatedAtUtc = t0 });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "B", Status = SagaStatus.Running, CreatedAtUtc = t0, UpdatedAtUtc = t0.AddSeconds(5) });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "ShippingSaga", CurrentState = "C", Status = SagaStatus.Running, CreatedAtUtc = t0, UpdatedAtUtc = t0.AddSeconds(5) });
        }

        await using var db2 = NewContext();
        var result = await new EfCoreSagaSummaryReader(db2).ListAsync(new SagaListFilter
        {
            SagaType = "OrderSaga",
            UpdatedSince = t0,
        });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("B", Assert.Single(result.Items).CurrentState);
    }

    [Fact]
    public async Task GetSagaTypesAsync_ReturnsDistinctTypesAcrossInstances()
    {
        // Regression test: GetSagaTypesAsync originally projected straight into the SagaTypeInfo
        // record inside Distinct()/OrderBy(), which Npgsql's provider could not translate to SQL —
        // only caught against a real Postgres, not SQLite. Keeping this test here (and the sibling
        // Postgres Testcontainers test) so both providers are exercised for this exact query shape.
        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", Kind = SagaKind.Orchestrated, CurrentState = "A", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", Kind = SagaKind.Orchestrated, CurrentState = "B", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "ShippingSaga", Kind = SagaKind.Choreographed, CurrentState = "A", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

        await using var db2 = NewContext();
        var types = await new EfCoreSagaSummaryReader(db2).GetSagaTypesAsync();

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => string.Equals(t.SagaType, "OrderSaga", StringComparison.Ordinal) && t.Kind == SagaKind.Orchestrated);
        Assert.Contains(types, t => string.Equals(t.SagaType, "ShippingSaga", StringComparison.Ordinal) && t.Kind == SagaKind.Choreographed);
    }

    [Fact]
    public async Task GetDataJsonAsync_ReturnsSerializedState()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Started",
                OrderId = "ORD-9",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using var db2 = NewContext();
        var json = await new EfCoreSagaSummaryReader(db2).GetDataJsonAsync("TestSaga", correlationId);

        Assert.NotNull(json);
        Assert.Contains("\"OrderId\":\"ORD-9\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetStateAsync_UpdatesColumnsAndKeepsEmbeddedJsonInSync()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Failed",
                Status = SagaStatus.Failed,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using (var db2 = NewContext())
            await new EfCoreSagaSummaryReader(db2).ResetStateAsync("TestSaga", correlationId, "Submitted", SagaStatus.Running, expectedVersion: 0, DateTimeOffset.UtcNow);

        // The entity-level columns (read via ISagaSummaryReader) and the embedded DataJson (read via
        // ISagaSnapshotStore<TState>, which is what the orchestrator actually uses) must agree —
        // this is exactly the class of bug that once let Version drift out of sync with DataJson.
        await using var db3 = NewContext();
        var summary = await new EfCoreSagaSummaryReader(db3).GetAsync("TestSaga", correlationId);
        Assert.Equal("Submitted", summary!.CurrentState);
        Assert.Equal(SagaStatus.Running, summary.Status);

        await using var db4 = NewContext();
        var typedState = await new EfCoreSagaSnapshotStore<TestState>(db4).FindAsync("TestSaga", correlationId);
        Assert.Equal("Submitted", typedState!.CurrentState);
        Assert.Equal(SagaStatus.Running, typedState.Status);
    }

    [Fact]
    public async Task EventLog_ServiceMapFields_RoundTrip()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var log = new EfCoreSagaEventLogStore(db);
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.MessagePublished,
                messageType: "ReserveInventory", messageId: "out-1",
                sourceService: "TestSaga", destinationService: "InventoryService", causationId: "in-1"));
        }

        await using var db2 = NewContext();
        var timeline = await new EfCoreSagaEventLogStore(db2).GetTimelineAsync("TestSaga", correlationId);

        var entry = Assert.Single(timeline);
        Assert.Equal("TestSaga", entry.SourceService);
        Assert.Equal("InventoryService", entry.DestinationService);
        Assert.Equal("in-1", entry.CausationId);
    }

    [Fact]
    public async Task EventLog_PreMigrationRow_ReadsWithNullServiceMapFields()
    {
        var correlationId = Guid.NewGuid();

        // Simulates a row written before this feature: no SourceService/DestinationService/CausationId
        // at all, exactly what EF leaves the new nullable columns as for pre-existing data.
        await using (var db = NewContext())
        {
            await new EfCoreSagaEventLogStore(db).AppendAsync(
                SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.SagaStarted, toState: "Started", messageId: "m1"));
        }

        await using var db2 = NewContext();
        var timeline = await new EfCoreSagaEventLogStore(db2).GetTimelineAsync("TestSaga", correlationId);

        var entry = Assert.Single(timeline);
        Assert.Null(entry.SourceService);
        Assert.Null(entry.DestinationService);
        Assert.Null(entry.CausationId);
    }

    [Fact]
    public async Task IsDuplicateAsync_IgnoresOutboundEntries()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var log = new EfCoreSagaEventLogStore(db);
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.SagaStarted, toState: "Started", messageId: "in-1"));
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.MessagePublished, messageType: "Reserve", messageId: "shared-id"));
        }

        await using var db2 = NewContext();
        var log2 = new EfCoreSagaEventLogStore(db2);

        Assert.False(await log2.IsDuplicateAsync("TestSaga", correlationId, "shared-id")); // only an outbound row carries this id
        Assert.True(await log2.IsDuplicateAsync("TestSaga", correlationId, "in-1"));
    }

    [Fact]
    public async Task ServiceTopologyStore_RecordAsync_IsIdempotentAndUpdatesQueueName()
    {
        await using (var db = NewContext())
        {
            var store = new EfCoreServiceTopologyStore(db);
            await store.RecordAsync("InventoryService", "ReserveInventory", "vsaga.participant.inventory", DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        await using (var db2 = NewContext())
        {
            var store2 = new EfCoreServiceTopologyStore(db2);
            await store2.RecordAsync("InventoryService", "ReserveInventory", "vsaga.participant.inventory.v2", DateTimeOffset.UtcNow);
        }

        await using var db3 = NewContext();
        var entries = await new EfCoreServiceTopologyStore(db3).GetAllAsync();

        var entry = Assert.Single(entries);
        Assert.Equal("InventoryService", entry.ServiceName);
        Assert.Equal("ReserveInventory", entry.MessageType);
        Assert.Equal("vsaga.participant.inventory.v2", entry.QueueName); // refreshed, not duplicated
    }

    // Unlike SubSagaCompositionTests, these do set the parent pointer by hand — correctly so, because
    // the subject here is the store, not the orchestrator: whether a link that exists on a TState is
    // written to real columns and can be queried back. Whether the link ever gets set in the first
    // place is what the header-driven tests over there are for.
    [Fact]
    public async Task ParentLinkage_IsWrittenToQueryableColumns_NotJustTheDataJsonBlob()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = childId,
                SagaType = "InvoiceDeliverySaga",
                CurrentState = "Requested",
                ParentSagaType = "PostShipmentChoreography",
                ParentCorrelationId = parentId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using var db2 = NewContext();

        // Read straight off the entity: DataJson would answer this too, but only after deserializing
        // into a concrete TState the saga-type-agnostic reader does not have.
        var row = await db2.SagaInstances.AsNoTracking().SingleAsync(x => x.CorrelationId == childId);
        Assert.Equal("PostShipmentChoreography", row.ParentSagaType);
        Assert.Equal(parentId, row.ParentCorrelationId);

        var found = await new EfCoreSagaSnapshotStore<TestState>(db2).FindAsync("InvoiceDeliverySaga", childId);
        Assert.NotNull(found);
        Assert.Equal(parentId, found.ParentCorrelationId);
    }

    [Fact]
    public async Task FindChildrenAsync_ReturnsOnlyTheSagasStartedByThatExactParent()
    {
        var parentId = Guid.NewGuid();
        var otherParentId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(NewChild("ChildSaga", parentId, "ParentSaga"));
            await store.InsertAsync(NewChild("OtherChildSaga", parentId, "ParentSaga"));
            await store.InsertAsync(NewChild("ChildSaga", otherParentId, "ParentSaga"));      // same type, different parent instance
            await store.InsertAsync(NewChild("ChildSaga", parentId, "DifferentParentSaga"));  // same id, different parent type
            await store.InsertAsync(NewChild("RootSaga", parentCorrelationId: null, parentSagaType: null));
        }

        await using var db2 = NewContext();
        var children = await new EfCoreSagaSummaryReader(db2).FindChildrenAsync("ParentSaga", parentId);

        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(parentId, c.ParentCorrelationId));
        Assert.All(children, c => Assert.Equal("ParentSaga", c.ParentSagaType));
        Assert.Contains(children, c => string.Equals(c.SagaType, "OtherChildSaga", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindChildrenAsync_IsEmptyForASagaThatStartedNothing()
    {
        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(NewChild("RootSaga", parentCorrelationId: null, parentSagaType: null));
        }

        await using var db2 = NewContext();
        Assert.Empty(await new EfCoreSagaSummaryReader(db2).FindChildrenAsync("RootSaga", Guid.NewGuid()));
    }

    private static TestState NewChild(string sagaType, Guid? parentCorrelationId, string? parentSagaType) => new()
    {
        CorrelationId = Guid.NewGuid(),
        SagaType = sagaType,
        CurrentState = "Started",
        Status = SagaStatus.Running,
        ParentSagaType = parentSagaType,
        ParentCorrelationId = parentCorrelationId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    // --- BusinessKey / partial unique index (production-readiness.md §5.2, item 12) --------

    [Fact]
    public async Task Insert_TwoInstancesOfTheSameSagaTypeWithNullBusinessKey_BothSucceed()
    {
        // THE most important test in this file: every one of the 231+ existing tests never sets
        // BusinessKey, so it stays null on every insert. If the unique index on (SagaType,
        // BusinessKey) were not partial (WHERE BusinessKey IS NOT NULL), this would be the first
        // thing to break -- and it must not, since nothing today has any way to populate BusinessKey.
        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);

        await store.InsertAsync(NewState(Guid.NewGuid(), "TestSaga", "Started"));
        await store.InsertAsync(NewState(Guid.NewGuid(), "TestSaga", "Started"));

        Assert.Equal(2, await db.SagaInstances.CountAsync(x => x.SagaType == "TestSaga"));
    }

    [Fact]
    public async Task InsertWithBusinessKey_ThenFindByBusinessKeyAsync_ReturnsTheRightInstance()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "OrderSaga",
                CurrentState = "Submitted",
                Status = SagaStatus.Running,
                OrderId = "ORD-BK-1",
                BusinessKey = "ORD-BK-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using var db2 = NewContext();
        var found = await new EfCoreSagaSnapshotStore<TestState>(db2).FindByBusinessKeyAsync("OrderSaga", "ORD-BK-1");

        Assert.NotNull(found);
        Assert.Equal(correlationId, found.CorrelationId);
        Assert.Equal("ORD-BK-1", found.OrderId);
        Assert.Equal("Submitted", found.CurrentState);
    }

    [Fact]
    public async Task FindByBusinessKeyAsync_ForAnUnclaimedKey_ReturnsNull()
    {
        await using var db = NewContext();
        await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            BusinessKey = "ORD-BK-2",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        Assert.Null(await new EfCoreSagaSnapshotStore<TestState>(db).FindByBusinessKeyAsync("OrderSaga", "no-such-key"));
    }

    [Fact]
    public async Task Insert_SameSagaTypeAndBusinessKeyAsAnExistingInstance_ThrowsSagaAlreadyExistsException()
    {
        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);

        await store.InsertAsync(new TestState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            BusinessKey = "ORD-BK-3",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => store.InsertAsync(new TestState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            BusinessKey = "ORD-BK-3",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }));
    }

    [Fact]
    public async Task Insert_SameBusinessKeyUnderADifferentSagaType_Succeeds()
    {
        // The index is scoped per SagaType, matching the composite primary key's own
        // SagaType-first precedent (see Insert_SameCorrelationIdUnderDifferentSagaType_Succeeds above).
        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);

        await store.InsertAsync(new TestState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            BusinessKey = "ORD-BK-4",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await store.InsertAsync(new TestState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = "ShippingChoreography",
            CurrentState = "Tracking",
            BusinessKey = "ORD-BK-4",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        Assert.NotNull(await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-4"));
        Assert.NotNull(await store.FindByBusinessKeyAsync("ShippingChoreography", "ORD-BK-4"));
    }

    [Fact]
    public async Task Update_KeepsBusinessKeyResolvableAfterAnUnrelatedFieldChanges()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "OrderSaga",
                CurrentState = "Submitted",
                OrderId = "ORD-BK-5",
                BusinessKey = "ORD-BK-5",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using (var db2 = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db2);
            var state = await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-5");
            state!.CurrentState = "Completed";
            await store.UpdateAsync(state, expectedVersion: 0);
        }

        await using var db3 = NewContext();
        var reloaded = await new EfCoreSagaSnapshotStore<TestState>(db3).FindByBusinessKeyAsync("OrderSaga", "ORD-BK-5");
        Assert.NotNull(reloaded);
        Assert.Equal(correlationId, reloaded.CorrelationId);
        Assert.Equal("Completed", reloaded.CurrentState);
        Assert.Equal(1, reloaded.Version);
    }
}
