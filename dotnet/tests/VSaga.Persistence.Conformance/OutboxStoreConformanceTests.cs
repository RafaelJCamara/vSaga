using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>Conformance cases for <see cref="ISagaOutboxStore"/>.</summary>
public abstract class OutboxStoreConformanceTests(IProviderFixture fixture) : StoreConformanceTests(fixture)
{
    /// <summary>Stages and commits rows in a unit of work of their own, as a committed step would have left them.</summary>
    private static async Task EnqueueCommittedAsync(IProviderStores stores, params (string MessageId, DateTimeOffset CreatedAtUtc)[] rows)
    {
        await using var uow = await stores.BeginAsync();
        foreach (var (messageId, createdAtUtc) in rows)
            await EnqueueAsync(uow, messageId, createdAtUtc);
        await uow.CommitAsync();
    }

    private static async Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(IProviderStores stores, DateTimeOffset olderThan, int batchSize)
    {
        await using var uow = await stores.BeginAsync();
        return await uow.Outbox.ClaimPendingAsync(olderThan, batchSize);
    }

    private static IEnumerable<string> MessageIds(IEnumerable<SagaOutboxMessage> messages) => messages.Select(m => m.MessageId);

    [Fact]
    public async Task ClaimPending_RoundTripsEveryField_MarksTheRowDispatched_AndNeverReturnsItTwice()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        var createdAtUtc = T0.AddTicks(1_234_567);
        var body = "{\"OrderId\":\"ORD-1\"}"u8.ToArray();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["x-vsaga-source-service"] = "OrderSaga", ["traceparent"] = "00-abc-def-01" };

        await using (var uow = await stores.BeginAsync())
        {
            await uow.Outbox.EnqueueAsync("OrderSaga", correlationId, "m1", "InventoryReserved", body, "inventory", headers, createdAtUtc);
            await uow.CommitAsync();
        }

        var claimed = Assert.Single(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));

        Assert.Equal(correlationId, claimed.CorrelationId);
        Assert.Equal("OrderSaga", claimed.SagaType);
        Assert.Equal("m1", claimed.MessageId);
        Assert.Equal("InventoryReserved", claimed.MessageTypeName);
        Assert.Equal(body, claimed.Body.ToArray());
        Assert.Equal("inventory", claimed.Destination);
        Assert.Equal(headers.OrderBy(h => h.Key, StringComparer.Ordinal), claimed.Headers.OrderBy(h => h.Key, StringComparer.Ordinal));
        Assert.Equal(SagaOutboxStatus.Dispatched, claimed.Status);
        AssertSameInstant(createdAtUtc, claimed.CreatedAtUtc);
        Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));
    }

    [Fact]
    public async Task ClaimPending_IncludesARowCreatedExactlyAtTheCutoff()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await EnqueueCommittedAsync(stores, ("m1", T0.AddSeconds(30)));

        Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddSeconds(29)));
        Assert.Single(await ClaimAllPendingAsync(stores, T0.AddSeconds(30)));
    }

    [Fact]
    public async Task ClaimPending_NeverReturnsMoreThanTheBatchSize_AndLeavesTheRestForTheNextClaim()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await EnqueueCommittedAsync(stores, Enumerable.Range(0, 5).Select(i => ($"m{i}", T0.AddSeconds(i))).ToArray());

        var first = await ClaimPendingAsync(stores, T0.AddMinutes(1), batchSize: 3);
        var second = await ClaimPendingAsync(stores, T0.AddMinutes(1), batchSize: 3);
        var third = await ClaimPendingAsync(stores, T0.AddMinutes(1), batchSize: 3);

        Assert.Equal(3, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(third);
        Assert.Equal(5, MessageIds(first.Concat(second)).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Clause 8 (fix F6): earliest-created first. Rows are enqueued newest first and given message ids whose
    /// ordinal order also runs against creation order, so neither insertion, row-id nor message-id order can
    /// pass for it; and the batch is smaller than the backlog — the oldest crash-recovered publish must
    /// never wait behind newer ones.
    /// </summary>
    [Fact]
    public async Task ClaimPending_ReturnsTheEarliestCreatedRowsFirst()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await EnqueueCommittedAsync(stores, Enumerable.Range(0, 5).Reverse().Select(i => ($"m{4 - i}", T0.AddSeconds(i))).ToArray());

        var claimed = await ClaimPendingAsync(stores, T0.AddMinutes(1), batchSize: 3);

        Assert.Equal(["m4", "m3", "m2"], MessageIds(claimed), StringComparer.Ordinal);
    }

    /// <summary>
    /// Fix F13: the row records the headers as they were at the enqueue. A caller mutating its dictionary
    /// afterwards — while the row is still staged, or after it is committed — changes nothing claimed, and
    /// the claimed headers are keyed ordinally whatever comparer the caller's dictionary used, never the
    /// caller's dictionary itself (<see cref="ISagaOutboxStore"/>'s clauses on both methods).
    /// </summary>
    [Fact]
    public async Task ClaimPending_ReturnsHeadersIndependentOfTheEnqueuedDictionary()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-vsaga-source-service"] = "OrderSaga" };

        await using (var uow = await stores.BeginAsync())
        {
            await uow.Outbox.EnqueueAsync("OrderSaga", Guid.NewGuid(), "m1", "InventoryReserved", "{}"u8.ToArray(), destination: null, headers, T0);
            headers["x-vsaga-source-service"] = "Tampered";
            await uow.CommitAsync();
        }

        headers["x-added-later"] = "1";
        var claimed = Assert.Single(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));

        Assert.NotSame(headers, claimed.Headers);
        Assert.Equal("OrderSaga", claimed.Headers["x-vsaga-source-service"]);
        Assert.False(claimed.Headers.ContainsKey("x-added-later"));
        Assert.False(claimed.Headers.ContainsKey("X-VSAGA-SOURCE-SERVICE"));
    }

    /// <summary>The inline drain's path: a row it already sent is never the recovery poller's to republish.</summary>
    [Fact]
    public async Task MarkDispatched_ExcludesTheRowFromTheRecoveryClaim()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await EnqueueCommittedAsync(stores, ("sent", T0), ("unsent", T0));

        await using (var uow = await stores.BeginAsync())
            await uow.Outbox.MarkDispatchedAsync("sent");

        Assert.Equal(["unsent"], MessageIds(await ClaimAllPendingAsync(stores, T0.AddMinutes(1))), StringComparer.Ordinal);
    }

    /// <summary>
    /// Clause 4: a staged row becomes durable at the next successful commit in its unit of work. Which
    /// store writes count as commits is the provider's own business — the contract lists EF Core's — but a
    /// successful persist is the one every provider must count: it is the pairing the orchestrator relies
    /// on, staging a step's rows immediately before its own <c>UpdateAsync</c>. Before it, a provider
    /// declaring <see cref="IProviderFixture.SupportsAtomicUnitOfWork"/> must not have let the row out.
    /// </summary>
    [Fact]
    public async Task StagedRow_BecomesDurableAtTheNextUpdateInItsUnitOfWork()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);

        await using var uow = await stores.BeginAsync();
        var live = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
        await EnqueueAsync(uow, "m1", T0, state.CorrelationId);

        if (Fixture.SupportsAtomicUnitOfWork)
            Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));

        live.CurrentState = "Next";
        await uow.Snapshots<ConformanceSagaState>().UpdateAsync(live, expectedVersion: 0);

        Assert.Equal(["m1"], MessageIds(await ClaimAllPendingAsync(stores, T0.AddMinutes(1))), StringComparer.Ordinal);
    }

    /// <summary>The same rule for the other persist: a saga's first step stages its rows before the <c>InsertAsync</c> that creates it.</summary>
    [Fact]
    public async Task StagedRow_BecomesDurableAtTheNextInsertInItsUnitOfWork()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");

        await using var uow = await stores.BeginAsync();
        await EnqueueAsync(uow, "m1", T0, state.CorrelationId);

        if (Fixture.SupportsAtomicUnitOfWork)
            Assert.Empty(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));

        await uow.Snapshots<ConformanceSagaState>().InsertAsync(state);

        Assert.Equal(["m1"], MessageIds(await ClaimAllPendingAsync(stores, T0.AddMinutes(1))), StringComparer.Ordinal);
    }

    /// <summary>
    /// The abandon path in miniature: stage two rows, lose the version race, discard one of them, and let
    /// a later commit in the same unit of work run. Exactly the row that was not discarded is durable —
    /// a discard that only marked rows, or left them staged, would be resurrected by that commit.
    /// </summary>
    [Fact]
    public async Task SelectiveDiscard_SurvivesALaterCommit()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var state = NewState("OrderSaga");
        await InsertAsync(stores, state);

        await using (var uow = await stores.BeginAsync())
        {
            var stale = (await uow.Snapshots<ConformanceSagaState>().FindAsync("OrderSaga", state.CorrelationId))!;
            await EnqueueAsync(uow, "kept", T0, state.CorrelationId);
            await EnqueueAsync(uow, "dropped", T0, state.CorrelationId);
            await Assert.ThrowsAsync<SagaConcurrencyException>(() =>
                uow.Snapshots<ConformanceSagaState>().UpdateAsync(stale, expectedVersion: 5));

            await uow.Outbox.DiscardPendingAsync(["dropped"]);
            await uow.CommitAsync();
        }

        Assert.Equal(["kept"], MessageIds(await ClaimAllPendingAsync(stores, T0.AddMinutes(1))), StringComparer.Ordinal);
    }

    /// <summary>
    /// A difference the suite pins rather than unifies. With an atomic unit of work, discard drops only
    /// what this unit of work still holds staged, so a row an earlier unit of work committed is out of
    /// its reach. Without one, every enqueued row is already durable, so discard has to remove the
    /// Pending row itself — the only way its abandon paths can suppress a publish at all.
    /// </summary>
    [Fact]
    public async Task DiscardPending_ReachesAnEarlierUnitOfWorksRowOnlyWithoutAnAtomicUnitOfWork()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await EnqueueCommittedAsync(stores, ("m1", T0));

        await using (var uow = await stores.BeginAsync())
        {
            await uow.Outbox.DiscardPendingAsync(["m1"]);
            await uow.CommitAsync();
        }

        var remaining = MessageIds(await ClaimAllPendingAsync(stores, T0.AddMinutes(1)));
        if (Fixture.SupportsAtomicUnitOfWork)
            Assert.Equal(["m1"], remaining, StringComparer.Ordinal);
        else
            Assert.Empty(remaining);
    }
}
