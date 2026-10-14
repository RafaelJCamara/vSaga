using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VSaga.Persistence.EFCore.Tests;

public sealed class TestState : SagaState
{
    public string? OrderId { get; set; }
}

/// <summary>
/// What the EF Core store tests keep after the provider-neutral conformance suite (<c>VSaga.Persistence.Conformance</c>,
/// run on SQLite by the <c>Sqlite*ConformanceTests</c> beside this class and on Postgres by the <c>Postgres*</c> ones) took
/// over the cases it makes too. Every test left here guards something a suite case does not fail for: the change
/// tracker after a save that fails for a reason no contract exposes (three tests inject the failure with a
/// <see cref="SaveChangesInterceptor"/>, a fourth is an append the database rejects on a null saga type, and no
/// contract names an input an append must refuse; a rejected insert or update, a business key another instance
/// holds, is a suite case now, clause 4), SQLite's own error text (a SQLite trigger, <see cref="SagaErrorText"/>),
/// the <c>DbUpdateException</c> chained under <see cref="SagaAlreadyExistsException"/>, the order <c>FindByCorrelationIdAsync</c> returns (the contract promises
/// none, so the suite sorts before comparing), and a discarded outbox row that must not reach the table at all (the
/// suite reads Pending rows only, and no contract reads back a Dispatched row that nothing claims). What
/// <c>ListAsync</c> and <c>FindChildrenAsync</c> project, a null destination and null service-map fields, an update
/// that must leave a second saga type's row alone and the update-written projection columns are suite cases now.
/// See persistence-contracts.md Q4.
/// </summary>
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

    private static TestState NewState(Guid correlationId, string sagaType, string currentState) => new()
    {
        CorrelationId = correlationId,
        SagaType = sagaType,
        CurrentState = currentState,
        Status = SagaStatus.Running,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    // The contract promises FindByCorrelationIdAsync no order, so the suite sorts before comparing; this pins the
    // one EF Core returns (SagaType ascending), which is its own, not a clause.
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

    /// <summary>
    /// The context is scoped to the whole unit of work, so an append whose save failed must not stay
    /// Added in it: the next SaveChangesAsync on the same context (the next log entry, MarkDispatchedAsync)
    /// would write it too, after entries logged later, and the timeline would hold it out of order.
    /// </summary>
    [Fact]
    public async Task EventLog_AppendThatFailedToSave_IsNotWrittenByALaterAppendOnTheSameContext()
    {
        var correlationId = Guid.NewGuid();
        var interceptor = new ThrowOnceSaveInterceptor();

        await using (var db = new VSagaDbContext(new DbContextOptionsBuilder<VSagaDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options))
        {
            var log = new EfCoreSagaEventLogStore(db);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => log.AppendAsync(
                SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepSucceeded, messageId: "lost")));
            Assert.Same(interceptor.Thrown, thrown); // rethrown untouched, not wrapped

            await log.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished, messageId: "later"));
        }

        await using var db2 = NewContext();
        var timeline = await new EfCoreSagaEventLogStore(db2).GetTimelineAsync("OrderSaga", correlationId);

        Assert.Equal("later", Assert.Single(timeline).MessageId);
    }

    /// <summary>
    /// A row the database rejects on its own would otherwise be re-sent, and rejected again, by every
    /// later save on the context, failing each later append and the outbox drain with it.
    /// </summary>
    [Fact]
    public async Task EventLog_AppendTheDatabaseRejects_DoesNotFailTheNextAppendOnTheSameContext()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var log = new EfCoreSagaEventLogStore(db);

            // SagaType is a required column, so SQLite's NOT NULL constraint rejects this row.
            await Assert.ThrowsAsync<DbUpdateException>(() => log.AppendAsync(
                SagaLogEntry.Create(correlationId, null!, SagaEntryType.StepSucceeded, messageId: "rejected")));

            await log.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished, messageId: "next"));
        }

        await using var db2 = NewContext();
        var timeline = await new EfCoreSagaEventLogStore(db2).GetTimelineAsync("OrderSaga", correlationId);

        // The rejected row can never be stored (NOT NULL), so there is nothing to look for: the point is
        // that the next append on the same context saved instead of re-sending it and failing too.
        Assert.Equal("next", Assert.Single(timeline).MessageId);
    }

    /// <summary>
    /// The detach drops only the failed log entry. Outbox rows staged earlier in the unit of work stay
    /// staged and are made durable by the next successful commit (clause 4 of persistence-contracts.md);
    /// clearing the whole change tracker would silently lose them.
    /// </summary>
    [Fact]
    public async Task EventLog_AppendThatFailedToSave_LeavesStagedOutboxRowsForTheNextCommit()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = new VSagaDbContext(new DbContextOptionsBuilder<VSagaDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new ThrowOnceSaveInterceptor())
            .Options))
        {
            await new EfCoreSagaOutboxStore(db).EnqueueAsync("OrderSaga", correlationId, "staged-1", "InventoryReserved",
                "{}"u8.ToArray(), destination: null, new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow);

            var log = new EfCoreSagaEventLogStore(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => log.AppendAsync(
                SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepSucceeded, messageId: "lost")));

            await log.AppendAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished, messageId: "later"));
        }

        await using var db2 = NewContext();
        var timeline = await new EfCoreSagaEventLogStore(db2).GetTimelineAsync("OrderSaga", correlationId);

        Assert.Equal("staged-1", Assert.Single(db2.SagaOutboxMessages).MessageId);
        Assert.Equal("later", Assert.Single(timeline).MessageId);
    }

    /// <summary>
    /// The scoped context spans the whole unit of work, so a snapshot update whose save failed must not stay
    /// Modified in it: the dead-letter record the engine appends next (RecordDeliveryExhaustedAsync) saves on the
    /// same context and would write the failed step's state to the row, committing what was reported as failed.
    /// </summary>
    [Fact]
    public async Task Snapshot_UpdateThatFailedToSave_IsNotWrittenByALaterAppendOnTheSameContext()
    {
        var correlationId = Guid.NewGuid();
        await using (var seed = NewContext())
            await new EfCoreSagaSnapshotStore<TestState>(seed).InsertAsync(NewState(correlationId, "TestSaga", "Started"));

        var interceptor = new ThrowOnceSaveInterceptor();
        await using (var db = new VSagaDbContext(new DbContextOptionsBuilder<VSagaDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options))
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            var state = await store.FindAsync("TestSaga", correlationId);
            state!.CurrentState = "StepThatFailed";

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(state, expectedVersion: 0));
            Assert.Same(interceptor.Thrown, thrown); // rethrown untouched, not wrapped
            Assert.Equal(0, state.Version); // the bump of the failed call is undone

            await new EfCoreSagaEventLogStore(db).AppendAsync(
                SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.DeliveryExhausted, messageId: "m-1"));
        }

        await using var db2 = NewContext();
        var stored = await new EfCoreSagaSnapshotStore<TestState>(db2).FindAsync("TestSaga", correlationId);
        Assert.Equal("Started", stored!.CurrentState);
        Assert.Equal(0, stored.Version);
        Assert.Equal("m-1", Assert.Single(await new EfCoreSagaEventLogStore(db2).GetTimelineAsync("TestSaga", correlationId)).MessageId);
    }

    /// <summary>
    /// Both stores still map every <c>DbUpdateException</c> to <see cref="SagaAlreadyExistsException"/> (persistence-
    /// contracts.md Q3 is open), so what a reader of the timeline sees for a failure that is not a collision is the
    /// text <see cref="SagaErrorText"/> builds: it must carry the database's own error, as a Postgres trigger that
    /// raised in the UPDATE did in the live run, not only "already exists".
    /// </summary>
    [Fact]
    public async Task Snapshot_UpdateThatFailsForAnotherReasonThanACollision_RecordsTheDatabasesOwnError()
    {
        var correlationId = Guid.NewGuid();
        await using (var seed = NewContext())
            await new EfCoreSagaSnapshotStore<TestState>(seed).InsertAsync(NewState(correlationId, "TestSaga", "Started"));

        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailEveryUpdate BEFORE UPDATE ON SagaInstances BEGIN SELECT RAISE(ABORT, 'injected transient database failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);
        var state = await store.FindAsync("TestSaga", correlationId);
        state!.CurrentState = "StepThatFailed";

        var thrown = await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => store.UpdateAsync(state, expectedVersion: 0));

        var recorded = SagaErrorText.Describe(thrown);
        Assert.Contains("injected transient database failure", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE constraint failed", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_InsertThatFailsForAnotherReasonThanACollision_RecordsTheDatabasesOwnError()
    {
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailEveryInsert BEFORE INSERT ON SagaInstances BEGIN SELECT RAISE(ABORT, 'injected transient database failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await using var db = NewContext();

        var thrown = await Assert.ThrowsAsync<SagaAlreadyExistsException>(
            () => new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(NewState(Guid.NewGuid(), "TestSaga", "Started")));

        Assert.Contains("injected transient database failure", SagaErrorText.Describe(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_UpdateThatCollidesOnTheBusinessKey_StillReadsAsACollision()
    {
        var first = NewState(Guid.NewGuid(), "TestSaga", "Started");
        first.BusinessKey = "key-1";
        var second = NewState(Guid.NewGuid(), "TestSaga", "Started");
        second.BusinessKey = "key-2";

        await using (var seed = NewContext())
        {
            var seedStore = new EfCoreSagaSnapshotStore<TestState>(seed);
            await seedStore.InsertAsync(first);
            await seedStore.InsertAsync(second);
        }

        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);
        var state = await store.FindAsync("TestSaga", second.CorrelationId);
        state!.BusinessKey = "key-1";

        var thrown = await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => store.UpdateAsync(state, expectedVersion: 0));

        var recorded = SagaErrorText.Describe(thrown);
        Assert.Contains("already exists", recorded, StringComparison.Ordinal);
        Assert.Contains("UNIQUE constraint failed", recorded, StringComparison.Ordinal);
    }

    private sealed class ThrowOnceSaveInterceptor : SaveChangesInterceptor
    {
        public InvalidOperationException Thrown { get; } = new("Simulated save failure.");

        private bool _thrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_thrown)
                return base.SavingChangesAsync(eventData, result, cancellationToken);

            _thrown = true;
            throw Thrown;
        }
    }
}
