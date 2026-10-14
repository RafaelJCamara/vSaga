using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>Conformance cases for <see cref="ISagaSnapshotStore{TState}"/>.</summary>
public abstract class SnapshotStoreConformanceTests(IProviderFixture fixture) : StoreConformanceTests(fixture)
{
    private static readonly Guid GoldenCorrelationId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    /// <summary>How long a rival write racing a store's own update is given to land inside the store's window.</summary>
    private static readonly TimeSpan RivalWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Clause 12: what <see cref="GoldenState"/> must serialise to, byte for byte — <c>System.Text.Json</c>
    /// with default options. A naming policy, a string-enum converter, dropped nulls, indentation or a
    /// relaxed encoder (<c>&amp;</c> is escaped by default) each change this text. Property order is the
    /// serializer's own: the derived type's properties first, then <see cref="SagaState"/>'s.
    /// </summary>
    private const string GoldenBlob =
        """{"OrderId":"ORD-1 \u0026 co","Amount":12.50,"CorrelationId":"0f8fad5b-d9cb-469f-a165-70867728950e","SagaType":"OrderSaga","Kind":0,"CurrentState":"AwaitingPayment","Status":0,"Version":0,"ParentSagaType":null,"ParentCorrelationId":null,"BusinessKey":null,"CreatedAtUtc":"2026-01-01T00:00:00+00:00","UpdatedAtUtc":"2026-01-01T00:00:00.1234567+00:00"}""";

    private static ConformanceSagaState GoldenState() => new()
    {
        CorrelationId = GoldenCorrelationId,
        SagaType = "OrderSaga",
        Kind = SagaKind.Orchestrated,
        CurrentState = "AwaitingPayment",
        Status = SagaStatus.Running,
        CreatedAtUtc = T0,
        UpdatedAtUtc = T0.AddTicks(1_234_567),
        OrderId = "ORD-1 & co",
        Amount = 12.50m,
    };

    [Fact]
    public async Task Insert_ThenFind_RoundTripsEveryFieldExactly()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var parentId = Guid.NewGuid();
        var state = NewState("OrderSaga", currentState: "AwaitingPayment", businessKey: "ORD-1");
        state.Kind = SagaKind.Choreographed;
        state.ParentSagaType = "FulfilmentSaga";
        state.ParentCorrelationId = parentId;
        // Sub-microsecond ticks on purpose. Clause 2 has Find deserialise the blob, which keeps full
        // precision on every provider; a store that reassembled the state from projected fields instead
        // would hand back a provider-truncated value (Postgres keeps microseconds) and fail here.
        state.CreatedAtUtc = T0.AddTicks(7);
        state.UpdatedAtUtc = T0.AddTicks(1_234_567);
        state.OrderId = "ORD-1";
        state.Amount = 99.95m;

        await InsertAsync(stores, state);
        var found = await FindAsync(stores, "OrderSaga", state.CorrelationId);

        Assert.NotNull(found);
        Assert.Equal(state.CorrelationId, found.CorrelationId);
        Assert.Equal("OrderSaga", found.SagaType);
        Assert.Equal(SagaKind.Choreographed, found.Kind);
        Assert.Equal("AwaitingPayment", found.CurrentState);
        Assert.Equal(SagaStatus.Running, found.Status);
        Assert.Equal(0, found.Version);
        Assert.Equal("FulfilmentSaga", found.ParentSagaType);
        Assert.Equal(parentId, found.ParentCorrelationId);
        Assert.Equal("ORD-1", found.BusinessKey);
        Assert.Equal(T0.AddTicks(7), found.CreatedAtUtc);
        Assert.Equal(T0.AddTicks(1_234_567), found.UpdatedAtUtc);
        Assert.Equal("ORD-1", found.OrderId);
        Assert.Equal(99.95m, found.Amount);
    }

    /// <summary>
    /// Clause 11 (fix F14): a row whose blob is JSON null is an error, not "no such saga" — a store
    /// reporting null would have the orchestrator start a fresh instance over the live row. The blob is
    /// planted through the ordinary <c>InsertAsync</c> by <see cref="NullBlobSagaState"/>. Clause 11 names no
    /// exception type, so only a throw is asserted.
    /// </summary>
    [Fact]
    public async Task Find_ForARowWhoseBlobIsJsonNull_Throws()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = await InsertNullBlobAsync(stores, businessKey: null);

        await using var uow = await stores.BeginAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => uow.Snapshots<NullBlobSagaState>().FindAsync("OrderSaga", correlationId));
    }

    /// <summary>Clause 11 (fix F14) for the business-key lookup, which reads the same blob.</summary>
    [Fact]
    public async Task FindByBusinessKey_ForARowWhoseBlobIsJsonNull_Throws()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await InsertNullBlobAsync(stores, businessKey: "ORD-NULL-BLOB");

        await using var uow = await stores.BeginAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => uow.Snapshots<NullBlobSagaState>().FindByBusinessKeyAsync("OrderSaga", "ORD-NULL-BLOB"));
    }

    private static async Task<Guid> InsertNullBlobAsync(IProviderStores stores, string? businessKey)
    {
        var state = new NullBlobSagaState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = "OrderSaga",
            CurrentState = "Started",
            BusinessKey = businessKey,
            CreatedAtUtc = T0,
            UpdatedAtUtc = T0,
        };

        await using var uow = await stores.BeginAsync();
        await uow.Snapshots<NullBlobSagaState>().InsertAsync(state);
        return state.CorrelationId;
    }

    [Fact]
    public async Task Find_ForAnUnknownInstance_ReturnsNull()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await InsertAsync(stores, NewState("OrderSaga"));

        Assert.Null(await FindAsync(stores, "OrderSaga", Guid.NewGuid()));
    }

    [Fact]
    public async Task Insert_ForAnExistingSagaTypeAndCorrelationId_ThrowsSagaAlreadyExists()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        await InsertAsync(stores, NewState("OrderSaga", correlationId));

        await using var uow = await stores.BeginAsync();
        await Assert.ThrowsAsync<SagaAlreadyExistsException>(() =>
            uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState("OrderSaga", correlationId)));
    }

    [Fact]
    public async Task Insert_TheSameCorrelationIdUnderAnotherSagaType_IsAnIndependentInstance()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();

        await InsertAsync(stores,
            NewState("OrderSaga", correlationId, currentState: "Submitted"),
            NewState("ShippingChoreography", correlationId, currentState: "Tracking"));

        Assert.Equal("Submitted", (await FindAsync(stores, "OrderSaga", correlationId))!.CurrentState);
        Assert.Equal("Tracking", (await FindAsync(stores, "ShippingChoreography", correlationId))!.CurrentState);
    }

    /// <summary>
    /// Clause 1, bump half: the live object is moved to <c>expectedVersion + 1</c> in place, and the blob
    /// written from it embeds the new version — so a later Find (which reads the blob) agrees with the
    /// projection.
    /// </summary>
    [Fact]
    public async Task Update_BumpsTheVersionInPlaceBeforeWritingTheBlob()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            live.CurrentState = "Next";
            await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, expectedVersion: 0);

            Assert.Equal(1, live.Version);
        }

        var reloaded = await FindAsync(stores, "OrderSaga", state.CorrelationId);
        Assert.Equal("Next", reloaded!.CurrentState);
        Assert.Equal(1, reloaded.Version);
        Assert.Equal(1, (await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId))!.Version);
    }

    /// <summary>
    /// Clause 1's reason for the in-place bump: a timeout persists the same live object twice, passing
    /// <c>state.Version</c> each time. If the first persist left the object at the old version, the
    /// second would report a race it did not lose.
    /// </summary>
    [Fact]
    public async Task Update_TheSameLiveObjectTwice_PassingItsOwnVersion_Succeeds()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            var store = uow.Snapshots<ConformanceSagaState>();
            var live = (await store.FindAsync("OrderSaga", state.CorrelationId))!;

            live.CurrentState = "Claimed";
            await store.UpdateAsync(live, live.Version);
            live.CurrentState = "TimedOut";
            await store.UpdateAsync(live, live.Version);

            Assert.Equal(2, live.Version);
        }

        var reloaded = await FindAsync(stores, "OrderSaga", state.CorrelationId);
        Assert.Equal("TimedOut", reloaded!.CurrentState);
        Assert.Equal(2, reloaded.Version);
    }

    /// <summary>Clause 1, restore half, on the version check itself: the loser still holds the version it expected.</summary>
    [Fact]
    public async Task Update_WithAStaleVersion_ThrowsAndLeavesTheLiveObjectAtTheExpectedVersion()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);

        await using var winnerUow = await stores.BeginAsync();
        await using var loserUow = await stores.BeginAsync();
        var winner = (await winnerUow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
        var loser = (await loserUow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;

        winner.CurrentState = "Won";
        await winnerUow.Snapshots<ConformanceSagaState>().UpdateAsync(winner, expectedVersion: 0);

        loser.CurrentState = "Lost";
        var thrown = await Assert.ThrowsAsync<SagaConcurrencyException>(() =>
            loserUow.Snapshots<ConformanceSagaState>().UpdateAsync(loser, expectedVersion: 0));

        Assert.Equal(0, thrown.ExpectedVersion);
        Assert.Equal(0, loser.Version);
        Assert.Equal("Won", (await FindAsync(stores, "OrderSaga", state.CorrelationId))!.CurrentState);
    }

    /// <summary>
    /// Clause 1's restore half after an earlier persist: the live object already persisted once in this
    /// unit of work — the timeout's claim-then-commit shape — and another unit of work wrote in between. A
    /// store that keeps the row it already wrote (EF Core's identity map) only discovers the race at the
    /// commit, after bumping the live object, which is the path that must restore it. Either way the loser
    /// must still hold the version it expected, not the one it failed to write.
    /// </summary>
    [Fact]
    public async Task Update_ThatLosesARaceAfterAnEarlierPersist_ThrowsAndLeavesTheLiveObjectAtTheExpectedVersion()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);

        await using var uow = await stores.BeginAsync();
        var store = uow.Snapshots<ConformanceSagaState>();
        var live = (await store.FindAsync("OrderSaga", state.CorrelationId))!;
        live.CurrentState = "Claimed";
        await store.UpdateAsync(live, live.Version);

        await using (var rival = await stores.BeginAsync())
        {
            var other = (await rival.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            other.CurrentState = "Won";
            await rival.Snapshots<ConformanceSagaState>().UpdateAsync(other, other.Version);
        }

        live.CurrentState = "Lost";
        var thrown = await Assert.ThrowsAsync<SagaConcurrencyException>(() => store.UpdateAsync(live, live.Version));

        Assert.Equal(1, thrown.ExpectedVersion);
        Assert.Equal(1, live.Version);
        var stored = await FindAsync(stores, "OrderSaga", state.CorrelationId);
        Assert.Equal("Won", stored!.CurrentState);
        Assert.Equal(2, stored.Version);
    }

    /// <summary>
    /// Clause 1's restore half (fix F10) where the race lands inside the store's own write: a rival unit of
    /// work commits while the store is serialising the bumped state, after the bump and before the write.
    /// The store must still detect the race at the write and hand the live object back at the version it
    /// expected — not the one it bumped to and failed to record. (A store that locks across serialising
    /// keeps the rival out instead; its update then simply succeeds, and the case checks that.)
    /// </summary>
    [Fact]
    public async Task Update_WhenARivalWritesMidUpdate_ThrowsAndLeavesTheLiveObjectAtTheExpectedVersion()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = new RaceProbeSagaState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "Started", CreatedAtUtc = T0, UpdatedAtUtc = T0 };
        await using (var uow = await stores.BeginAsync())
            await uow.Snapshots<RaceProbeSagaState>().InsertAsync(state);

        await using var loserUow = await stores.BeginAsync();
        var live = (await loserUow.Snapshots<RaceProbeSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
        // Blocking is unavoidable: the hook runs inside a synchronous property getter. The rival runs on
        // the thread pool, so its continuations never need the thread blocked here. The wait is bounded
        // because a store may hold a lock across serialising, which clause 1 allows: the rival then cannot
        // land inside the window at all, and the case must neither hang on that nor fail it.
        live.OnNextSerialize = () => Task.WhenAny(Task.Run(() => RivalUpdateAsync(stores, state.CorrelationId)), Task.Delay(RivalWindow))
            .GetAwaiter().GetResult();
        live.CurrentState = "Lost";

        var thrown = await Record.ExceptionAsync(() => loserUow.Snapshots<RaceProbeSagaState>().UpdateAsync(live, expectedVersion: 0));

        if (thrown is null)
        {
            // The store kept the rival out until its own write was done, so there was no race to lose.
            Assert.Equal(1, live.Version);
            return;
        }

        Assert.IsType<SagaConcurrencyException>(thrown);
        Assert.Equal(0, live.Version);
        await using var reader = await stores.BeginAsync();
        var stored = await reader.Snapshots<RaceProbeSagaState>().FindAsync("OrderSaga", state.CorrelationId);
        Assert.Equal("Won", stored!.CurrentState);
        Assert.Equal(1, stored.Version);
    }

    /// <summary>
    /// Clause 4 where the race is lost at the commit rather than at a version pre-check (B3, Q2): a rival
    /// unit of work commits after this update's read and before its write, so the store only learns of it
    /// when it writes. The unit of work must still be usable afterwards — the dead-letter record the engine
    /// appends next is a later commit in the same unit of work. EF Core's change tracker is the provider
    /// this guards: an entity left Modified by the failed save would be re-sent by that commit, which
    /// throws a raw <c>DbUpdateConcurrencyException</c> and takes the staged row down with it. The loser gets
    /// <see cref="SagaConcurrencyException"/>, the live object is back at its expected version, the log entry
    /// and the row staged before the failed update become durable, and the stored row keeps the winner's
    /// value. (A store that locks across serialising keeps the rival out, so there is no race to lose and
    /// the case checks only that the update then succeeded.)
    /// </summary>
    [Fact]
    public async Task Update_ThatLosesItsRaceAtTheCommit_LeavesALaterCommitInTheSameUnitOfWorkFree()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = new RaceProbeSagaState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "Started", CreatedAtUtc = T0, UpdatedAtUtc = T0 };
        await using (var uow = await stores.BeginAsync())
            await uow.Snapshots<RaceProbeSagaState>().InsertAsync(state);

        await using (var loserUow = await stores.BeginAsync())
        {
            var live = (await loserUow.Snapshots<RaceProbeSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            await EnqueueAsync(loserUow, "staged-before", T0, state.CorrelationId);
            // See Update_WhenARivalWritesMidUpdate for why the hook blocks, and why the wait is bounded.
            live.OnNextSerialize = () => Task.WhenAny(Task.Run(() => RivalUpdateAsync(stores, state.CorrelationId)), Task.Delay(RivalWindow))
                .GetAwaiter().GetResult();
            live.CurrentState = "Lost";

            var thrown = await Record.ExceptionAsync(() => loserUow.Snapshots<RaceProbeSagaState>().UpdateAsync(live, expectedVersion: 0));

            if (thrown is null)
            {
                Assert.Equal(1, live.Version);
                return;
            }

            Assert.IsType<SagaConcurrencyException>(thrown);
            Assert.Equal(0, live.Version);

            await loserUow.EventLog.AppendAsync(SagaLogEntry.Create(state.CorrelationId, "OrderSaga", SagaEntryType.DeliveryExhausted, messageId: "m-1", occurredAtUtc: T0));
            await loserUow.CommitAsync();
        }

        await using var reader = await stores.BeginAsync();
        var stored = await reader.Snapshots<RaceProbeSagaState>().FindAsync("OrderSaga", state.CorrelationId);
        Assert.Equal("Won", stored!.CurrentState);
        Assert.Equal(1, stored.Version);
        Assert.Equal("m-1", Assert.Single(await reader.EventLog.GetTimelineAsync("OrderSaga", state.CorrelationId)).MessageId);
        Assert.Equal(["staged-before"], (await reader.Outbox.ClaimPendingAsync(T0.AddMinutes(1), batchSize: 1000)).Select(m => m.MessageId), StringComparer.Ordinal);
    }

    private static async Task RivalUpdateAsync(IProviderStores stores, Guid correlationId)
    {
        await using var rival = await stores.BeginAsync();
        var other = (await rival.Snapshots<RaceProbeSagaState>().FindAsync("OrderSaga", correlationId))!;
        other.CurrentState = "Won";
        await rival.Snapshots<RaceProbeSagaState>().UpdateAsync(other, other.Version);
    }

    /// <summary>
    /// Clause 3 on update (fix F5): the store writes the caller's <c>UpdatedAtUtc</c> and leaves the live
    /// object's alone — the orchestrator stamps it from its own clock immediately before every persist.
    /// </summary>
    [Fact]
    public async Task Update_KeepsTheCallersUpdatedAtUtc()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);
        var stamp = T0.AddMinutes(3).AddTicks(1_234_567);

        await using (var uow = await stores.BeginAsync())
        {
            var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            live.UpdatedAtUtc = stamp;
            await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, expectedVersion: 0);

            Assert.Equal(stamp, live.UpdatedAtUtc);
        }

        Assert.Equal(stamp, (await FindAsync(stores, "OrderSaga", state.CorrelationId))!.UpdatedAtUtc);
        AssertSameInstant(stamp, (await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId))!.UpdatedAtUtc);
    }

    /// <summary>
    /// Fix F7: an update moving the business key onto one another instance of the saga type holds collides
    /// on the reservation exactly as an insert would — <see cref="SagaAlreadyExistsException"/>, not a
    /// provider exception — and, per clause 1, leaves the live object at the version it expected. Neither
    /// reservation moves.
    /// </summary>
    [Fact]
    public async Task Update_ToABusinessKeyAnotherInstanceHolds_ThrowsSagaAlreadyExistsAndRestoresTheVersion()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var holder = NewState("OrderSaga", businessKey: "TAKEN-KEY");
        var mover = NewState("OrderSaga", businessKey: "MOVER-KEY");
        await InsertAsync(stores, holder, mover);

        await using (var uow = await stores.BeginAsync())
        {
            var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", mover.CorrelationId))!;
            live.BusinessKey = "TAKEN-KEY";

            await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, expectedVersion: 0));
            Assert.Equal(0, live.Version);
        }

        await using var reader = await stores.BeginAsync();
        var store = reader.Snapshots<ConformanceSagaState>();
        Assert.Equal(holder.CorrelationId, (await store.FindByBusinessKeyAsync("OrderSaga", "TAKEN-KEY"))!.CorrelationId);
        Assert.Equal(mover.CorrelationId, (await store.FindByBusinessKeyAsync("OrderSaga", "MOVER-KEY"))!.CorrelationId);
        Assert.Equal("MOVER-KEY", (await store.FindAsync("OrderSaga", mover.CorrelationId))!.BusinessKey);
    }

    /// <summary>
    /// Clause 3 on insert, projection half: the projected timestamps are the caller's, compared at storage
    /// resolution. The blob half is <see cref="Insert_ThenFind_RoundTripsEveryFieldExactly"/>.
    /// </summary>
    [Fact]
    public async Task Insert_KeepsTheCallersTimestamps()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        state.CreatedAtUtc = T0.AddTicks(1_234_567);
        state.UpdatedAtUtc = T0.AddMinutes(1).AddTicks(7_654_321);

        await InsertAsync(stores, state);
        var summary = await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId);

        AssertSameInstant(state.CreatedAtUtc, summary!.CreatedAtUtc);
        AssertSameInstant(state.UpdatedAtUtc, summary.UpdatedAtUtc);
    }

    /// <summary>
    /// An instance is <c>(sagaType, correlationId)</c>, so updating one of two instances that share a
    /// correlation id under different saga types writes that instance only: the other keeps its state,
    /// status and version, and is still updatable at the version it had. Each is updated in turn, so a store
    /// that finds the row by correlation id alone is wrong whichever of the two it happens to pick.
    /// </summary>
    [Fact]
    public async Task Update_OnlyTouchesItsOwnSagaTypesInstance()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        await InsertAsync(stores,
            NewState("OrderSaga", correlationId, currentState: "Submitted"),
            NewState("ShippingChoreography", correlationId, currentState: "Tracking"));

        await UpdateAsync(stores, "OrderSaga", correlationId, s => { s.CurrentState = "Completed"; s.Status = SagaStatus.Completed; });

        var order = await FindAsync(stores, "OrderSaga", correlationId);
        var shipping = await FindAsync(stores, "ShippingChoreography", correlationId);
        Assert.Equal(("Completed", SagaStatus.Completed, 1), (order!.CurrentState, order.Status, order.Version));
        Assert.Equal(("Tracking", SagaStatus.Running, 0), (shipping!.CurrentState, shipping.Status, shipping.Version));
        Assert.Equal(("Tracking", 0), await ProjectionAsync(stores, "ShippingChoreography", correlationId));

        await UpdateAsync(stores, "ShippingChoreography", correlationId, s => s.CurrentState = "Delivered");

        order = await FindAsync(stores, "OrderSaga", correlationId);
        shipping = await FindAsync(stores, "ShippingChoreography", correlationId);
        Assert.Equal(("Completed", SagaStatus.Completed, 1), (order!.CurrentState, order.Status, order.Version));
        Assert.Equal(("Delivered", SagaStatus.Running, 1), (shipping!.CurrentState, shipping.Status, shipping.Version));
        Assert.Equal(("Completed", 1), await ProjectionAsync(stores, "OrderSaga", correlationId));
    }

    private static async Task<(string CurrentState, int Version)> ProjectionAsync(IProviderStores stores, string sagaType, Guid correlationId)
    {
        var summary = (await GetSummaryAsync(stores, sagaType, correlationId))!;
        return (summary.CurrentState, summary.Version);
    }

    /// <summary>Loads an instance in a unit of work of its own, applies <paramref name="change"/> and updates it at the version it was loaded at.</summary>
    private static async Task UpdateAsync(IProviderStores stores, string sagaType, Guid correlationId, Action<ConformanceSagaState> change)
    {
        await using var uow = await stores.BeginAsync();
        var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync(sagaType, correlationId))!;
        change(live);
        await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, live.Version);
    }

    /// <summary>
    /// The queryable columns <see cref="ISagaSummaryReader"/> reads are a projection of the blob
    /// (<see cref="ISagaSnapshotStore{TState}.FindAsync"/>'s remarks), so an update that changes a saga's
    /// status and current state changes what a summary and a status-filtered list say about it — not only
    /// what the blob does, which is all <see cref="Update_BumpsTheVersionInPlaceBeforeWritingTheBlob"/> reads
    /// back. The dashboard lists by exactly these columns.
    /// </summary>
    [Fact]
    public async Task Update_ProjectsTheNewStatusAndCurrentState()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "Submitted", status: SagaStatus.Running);
        await InsertAsync(stores, state);

        await UpdateAsync(stores, "OrderSaga", state.CorrelationId, s => { s.CurrentState = "Completed"; s.Status = SagaStatus.Completed; });

        var summary = await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId);
        Assert.Equal("Completed", summary!.CurrentState);
        Assert.Equal(SagaStatus.Completed, summary.Status);
        Assert.Equal(1, summary.Version);

        await using var uow = await stores.BeginAsync();
        var completed = await uow.Summaries.ListAsync(new SagaListFilter { Status = SagaStatus.Completed });
        var running = await uow.Summaries.ListAsync(new SagaListFilter { Status = SagaStatus.Running });
        var listed = Assert.Single(completed.Items);
        Assert.Equal(state.CorrelationId, listed.CorrelationId);
        Assert.Equal("Completed", listed.CurrentState);
        Assert.Equal(1, completed.TotalCount);
        Assert.Empty(running.Items);
        Assert.Equal(0, running.TotalCount);
    }

    /// <summary>
    /// An update that leaves a non-null business key alone succeeds and keeps the key resolving to the
    /// instance: only <i>another</i> instance holding the key is a collision
    /// (<see cref="ISagaSnapshotStore{TState}.UpdateAsync"/>'s remarks), never the instance's own
    /// reservation. Every case that updates a keyed saga elsewhere changes the key, so the unchanged key is
    /// held across two updates here, which also reads the key and the new state back through the lookup.
    /// </summary>
    [Fact]
    public async Task Update_LeavingTheBusinessKeyAlone_KeepsItResolvable()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "Submitted", businessKey: "ORD-BK-5");
        await InsertAsync(stores, state);

        await UpdateAsync(stores, "OrderSaga", state.CorrelationId, s => s.CurrentState = "Paid");
        await UpdateAsync(stores, "OrderSaga", state.CorrelationId, s => s.CurrentState = "Completed");

        await using var uow = await stores.BeginAsync();
        var found = await uow.Snapshots<ConformanceSagaState>().FindByBusinessKeyAsync("OrderSaga", "ORD-BK-5");
        Assert.NotNull(found);
        Assert.Equal(state.CorrelationId, found.CorrelationId);
        Assert.Equal("ORD-BK-5", found.BusinessKey);
        Assert.Equal("Completed", found.CurrentState);
        Assert.Equal(2, found.Version);
    }

    /// <summary>Clause 12: the stored blob is <c>System.Text.Json</c> with default options, pinned as exact text.</summary>
    [Fact]
    public async Task StateBlob_IsSystemTextJsonWithDefaultOptions()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await InsertAsync(stores, GoldenState());

        await using var uow = await stores.BeginAsync();
        Assert.Equal(GoldenBlob, await uow.Summaries.GetDataJsonAsync("OrderSaga", GoldenCorrelationId));
    }

    /// <summary>Clause 2: the business key only selects the row; the state comes from its blob, as Find's does.</summary>
    [Fact]
    public async Task FindByBusinessKey_ReturnsTheReservingInstance()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", currentState: "Submitted", businessKey: "ORD-BK-1");
        state.OrderId = "ORD-BK-1";
        state.UpdatedAtUtc = T0.AddTicks(1_234_567);
        await InsertAsync(stores, state, NewState("OrderSaga", businessKey: "ORD-BK-2"));

        await using var uow = await stores.BeginAsync();
        var found = await uow.Snapshots<ConformanceSagaState>().FindByBusinessKeyAsync("OrderSaga", "ORD-BK-1");

        Assert.NotNull(found);
        Assert.Equal(state.CorrelationId, found.CorrelationId);
        Assert.Equal("Submitted", found.CurrentState);
        Assert.Equal("ORD-BK-1", found.OrderId);
        Assert.Equal(T0.AddTicks(1_234_567), found.UpdatedAtUtc);
    }

    [Fact]
    public async Task FindByBusinessKey_ForAnUnreservedKey_ReturnsNull()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await InsertAsync(stores, NewState("OrderSaga", businessKey: "ORD-BK-1"));

        await using var uow = await stores.BeginAsync();
        Assert.Null(await uow.Snapshots<ConformanceSagaState>().FindByBusinessKeyAsync("OrderSaga", "no-such-key"));
        Assert.Null(await uow.Snapshots<ConformanceSagaState>().FindByBusinessKeyAsync("ShippingChoreography", "ORD-BK-1"));
    }

    [Fact]
    public async Task Insert_ABusinessKeyAlreadyReservedUnderTheSameSagaType_ThrowsSagaAlreadyExists()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await InsertAsync(stores, NewState("OrderSaga", businessKey: "ORD-BK-1"));

        await using (var uow = await stores.BeginAsync())
        {
            await Assert.ThrowsAsync<SagaAlreadyExistsException>(() =>
                uow.Snapshots<ConformanceSagaState>().InsertAsync(NewState("OrderSaga", businessKey: "ORD-BK-1")));
        }

        await using var reader = await stores.BeginAsync();
        var page = await reader.Summaries.ListAsync(new SagaListFilter { SagaType = "OrderSaga" });
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Insert_TheSameBusinessKeyUnderAnotherSagaType_Succeeds()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var order = NewState("OrderSaga", businessKey: "ORD-BK-1");
        var shipping = NewState("ShippingChoreography", businessKey: "ORD-BK-1");

        await InsertAsync(stores, order, shipping);

        await using var uow = await stores.BeginAsync();
        var store = uow.Snapshots<ConformanceSagaState>();
        Assert.Equal(order.CorrelationId, (await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-1"))!.CorrelationId);
        Assert.Equal(shipping.CorrelationId, (await store.FindByBusinessKeyAsync("ShippingChoreography", "ORD-BK-1"))!.CorrelationId);
    }

    /// <summary>
    /// The reservation is only for non-null keys: nearly every saga declares none, and a store that
    /// reserved null would collide on every saga type's second instance.
    /// </summary>
    [Fact]
    public async Task Insert_ManyInstancesWithoutABusinessKey_NeverCollide()
    {
        await using var stores = await Fixture.CreateStoresAsync();

        await InsertAsync(stores, NewState("OrderSaga"), NewState("OrderSaga"), NewState("OrderSaga"));

        await using var uow = await stores.BeginAsync();
        Assert.Equal(3, (await uow.Summaries.ListAsync(new SagaListFilter { SagaType = "OrderSaga" })).TotalCount);
    }

    /// <summary>
    /// The blob is authoritative and the business-key column is a projection derived from it
    /// (<see cref="ISagaSnapshotStore{TState}.FindAsync"/>'s remarks), so after an update the reservation
    /// follows the blob's key, not the key the instance was inserted with.
    /// </summary>
    [Fact]
    public async Task Update_ChangingTheBusinessKey_MovesTheReservation()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga", businessKey: "OLD-KEY");
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            live.BusinessKey = "NEW-KEY";
            await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, expectedVersion: 0);
        }

        await using var reader = await stores.BeginAsync();
        var store = reader.Snapshots<ConformanceSagaState>();
        Assert.Null(await store.FindByBusinessKeyAsync("OrderSaga", "OLD-KEY"));
        Assert.Equal(state.CorrelationId, (await store.FindByBusinessKeyAsync("OrderSaga", "NEW-KEY"))!.CorrelationId);
        Assert.Equal("NEW-KEY", (await store.FindAsync("OrderSaga", state.CorrelationId))!.BusinessKey);
    }
}
