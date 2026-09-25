using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.Conformance;

/// <summary>Conformance cases for <see cref="ISagaSummaryReader"/>.</summary>
public abstract class SummaryReaderConformanceTests(IProviderFixture fixture) : StoreConformanceTests(fixture)
{
    private static async Task<PagedResult<SagaSummary>> ListAsync(IProviderStores stores, SagaListFilter filter)
    {
        await using var uow = await stores.BeginAsync();
        return await uow.Summaries.ListAsync(filter);
    }

    private static IEnumerable<Guid> Ids(PagedResult<SagaSummary> page) => page.Items.Select(s => s.CorrelationId);

    [Fact]
    public async Task Get_ProjectsTheWrittenState()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var parentId = Guid.NewGuid();
        var state = NewState("OrderSaga", currentState: "AwaitingPayment", status: SagaStatus.Compensating);
        state.Kind = SagaKind.Choreographed;
        state.ParentSagaType = "FulfilmentSaga";
        state.ParentCorrelationId = parentId;
        state.CreatedAtUtc = T0.AddTicks(1_234_567);
        state.UpdatedAtUtc = T0.AddMinutes(1).AddTicks(7_654_321);
        await InsertAsync(stores, state);

        var summary = await GetSummaryAsync(stores, "OrderSaga", state.CorrelationId);

        Assert.NotNull(summary);
        Assert.Equal(state.CorrelationId, summary.CorrelationId);
        Assert.Equal("OrderSaga", summary.SagaType);
        Assert.Equal(SagaKind.Choreographed, summary.Kind);
        Assert.Equal("AwaitingPayment", summary.CurrentState);
        Assert.Equal(SagaStatus.Compensating, summary.Status);
        Assert.Equal(0, summary.Version);
        Assert.Equal("FulfilmentSaga", summary.ParentSagaType);
        Assert.Equal(parentId, summary.ParentCorrelationId);
        AssertSameInstant(state.CreatedAtUtc, summary.CreatedAtUtc);
        AssertSameInstant(state.UpdatedAtUtc, summary.UpdatedAtUtc);
    }

    [Fact]
    public async Task GetAndGetDataJson_ForAnUnknownInstance_ReturnNull()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        await InsertAsync(stores, NewState("OrderSaga", correlationId));

        await using var uow = await stores.BeginAsync();
        Assert.Null(await uow.Summaries.GetAsync("OrderSaga", Guid.NewGuid()));
        Assert.Null(await uow.Summaries.GetAsync("ShippingChoreography", correlationId));
        Assert.Null(await uow.Summaries.GetDataJsonAsync("OrderSaga", Guid.NewGuid()));
        Assert.Null(await uow.Summaries.GetDataJsonAsync("ShippingChoreography", correlationId));
    }

    [Fact]
    public async Task List_FiltersByStatusKindAndSagaType_AndCountsTheFilteredSet()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var runningOrder = NewState("OrderSaga");
        var failedOrder = NewState("OrderSaga", status: SagaStatus.Failed);
        var choreographed = NewState("ShippingChoreography");
        choreographed.Kind = SagaKind.Choreographed;
        await InsertAsync(stores, runningOrder, failedOrder, choreographed);

        var failed = await ListAsync(stores, new SagaListFilter { Status = SagaStatus.Failed });
        var byKind = await ListAsync(stores, new SagaListFilter { Kind = SagaKind.Choreographed });
        var byType = await ListAsync(stores, new SagaListFilter { SagaType = "OrderSaga" });
        var combined = await ListAsync(stores, new SagaListFilter { SagaType = "OrderSaga", Status = SagaStatus.Running });

        Assert.Equal([failedOrder.CorrelationId], Ids(failed));
        Assert.Equal(1, failed.TotalCount);
        Assert.Equal([choreographed.CorrelationId], Ids(byKind));
        Assert.Equal(2, byType.TotalCount);
        Assert.Equal([runningOrder.CorrelationId], Ids(combined));
    }

    /// <summary>
    /// Strictly greater-than, applied before paging: the change poller's watermark is the timestamp of a
    /// row it already pushed, and <c>&gt;=</c> would re-push that row on every tick.
    /// </summary>
    [Fact]
    public async Task List_UpdatedSince_IsStrictlyGreaterThan_AndCountsOnlyWhatItKeeps()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var newest = NewState("OrderSaga", updatedAtUtc: T0.AddSeconds(2));
        await InsertAsync(stores, NewState("OrderSaga", updatedAtUtc: T0), NewState("OrderSaga", updatedAtUtc: T0.AddSeconds(1)), newest);

        var page = await ListAsync(stores, new SagaListFilter { UpdatedSince = T0.AddSeconds(1) });

        Assert.Equal([newest.CorrelationId], Ids(page));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task List_DefaultOrder_IsMostRecentlyUpdatedFirst()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var oldest = NewState("OrderSaga", updatedAtUtc: T0);
        var newest = NewState("OrderSaga", updatedAtUtc: T0.AddSeconds(2));
        var middle = NewState("OrderSaga", updatedAtUtc: T0.AddSeconds(1));
        await InsertAsync(stores, oldest, newest, middle);

        var page = await ListAsync(stores, new SagaListFilter());

        Assert.Equal([newest.CorrelationId, middle.CorrelationId, oldest.CorrelationId], Ids(page));
    }

    /// <summary>
    /// Each sort arm, in each direction, over keys that never tie; ties are not exercised here. Status
    /// sorts by <see cref="SagaStatus"/>'s declared order, per <see cref="SagaSortColumn.Status"/>.
    /// </summary>
    [Fact]
    public async Task List_SortsByEachColumnInEitherDirection()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        // Statuses and timestamps both distinct, and deliberately not in the same order, so an arm
        // sorting by the wrong column cannot pass.
        var running = NewState("OrderSaga", status: SagaStatus.Running, updatedAtUtc: T0.AddSeconds(2));
        var completed = NewState("OrderSaga", status: SagaStatus.Completed, updatedAtUtc: T0);
        var failed = NewState("OrderSaga", status: SagaStatus.Failed, updatedAtUtc: T0.AddSeconds(1));
        await InsertAsync(stores, failed, running, completed);

        Assert.Equal([completed.CorrelationId, failed.CorrelationId, running.CorrelationId],
            Ids(await ListAsync(stores, new SagaListFilter { SortBy = SagaSortColumn.UpdatedAt })));
        Assert.Equal([running.CorrelationId, failed.CorrelationId, completed.CorrelationId],
            Ids(await ListAsync(stores, new SagaListFilter { SortBy = SagaSortColumn.UpdatedAt, SortDescending = true })));
        Assert.Equal([running.CorrelationId, completed.CorrelationId, failed.CorrelationId],
            Ids(await ListAsync(stores, new SagaListFilter { SortBy = SagaSortColumn.Status })));
        Assert.Equal([failed.CorrelationId, completed.CorrelationId, running.CorrelationId],
            Ids(await ListAsync(stores, new SagaListFilter { SortBy = SagaSortColumn.Status, SortDescending = true })));
    }

    /// <summary>Paging is over the whole sorted, filtered set — not a re-sort of whichever page was asked for.</summary>
    [Fact]
    public async Task List_PagesThroughTheSortedSet_ReportingTheTotalOnEveryPage()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var states = Enumerable.Range(0, 5).Select(i => NewState("OrderSaga", updatedAtUtc: T0.AddSeconds(i))).ToArray();
        await InsertAsync(stores, states[3], states[0], states[4], states[1], states[2]);

        var pages = new List<PagedResult<SagaSummary>>();
        for (var page = 1; page <= 3; page++)
            pages.Add(await ListAsync(stores, new SagaListFilter { SortBy = SagaSortColumn.UpdatedAt, Page = page, PageSize = 2 }));

        Assert.Equal(states.Select(s => s.CorrelationId), pages.SelectMany(Ids));
        Assert.All(pages, p => Assert.Equal(5, p.TotalCount));
        Assert.Equal([1, 2, 3], pages.Select(p => p.Page));
        Assert.All(pages, p => Assert.Equal(2, p.PageSize));
    }

    [Fact]
    public async Task Search_MatchesASubstringOfTheSagaType()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var order = NewState("OrderFulfilmentSaga");
        await InsertAsync(stores, order, NewState("ShippingChoreography"));

        var page = await ListAsync(stores, new SagaListFilter { Search = "Fulfilment" });

        Assert.Equal([order.CorrelationId], Ids(page));
        Assert.Equal(1, page.TotalCount);
    }

    /// <summary>
    /// The correlation id is matched on its own, independently of the saga type: a term inside the id
    /// matches, and a term straddling the two fields matches neither. The id is all digits and the
    /// straddling term carries non-hex letters, so this holds regardless of how a provider cases a Guid's
    /// text — case-insensitivity is not what it tests.
    /// </summary>
    [Fact]
    public async Task Search_MatchesTheCorrelationIdIndependentlyOfTheSagaType()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var target = NewState("OrderSaga", Guid.Parse("12345678-9012-3456-7890-123456789012"));
        await InsertAsync(stores, target, NewState("OrderSaga", Guid.Parse("98765432-1098-7654-3210-987654321098")));

        var insideTheId = await ListAsync(stores, new SagaListFilter { Search = "9012-3456" });
        var acrossBothFields = await ListAsync(stores, new SagaListFilter { Search = "Saga1234" });

        Assert.Equal([target.CorrelationId], Ids(insideTheId));
        Assert.Empty(acrossBothFields.Items);
        Assert.Equal(0, acrossBothFields.TotalCount);
    }

    [Fact]
    public async Task Search_MatchingNeitherField_ReturnsNothing()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        await InsertAsync(stores, NewState("OrderSaga"), NewState("ShippingChoreography"));

        var page = await ListAsync(stores, new SagaListFilter { Search = "Invoice" });

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task FindByCorrelationId_ReturnsEverySagaTypeTrackingIt()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var correlationId = Guid.NewGuid();
        await InsertAsync(stores, NewState("OrderSaga", correlationId), NewState("ShippingChoreography", correlationId), NewState("OrderSaga"));

        await using var uow = await stores.BeginAsync();
        var matches = await uow.Summaries.FindByCorrelationIdAsync(correlationId);

        Assert.All(matches, m => Assert.Equal(correlationId, m.CorrelationId));
        Assert.Equal(["OrderSaga", "ShippingChoreography"], matches.Select(m => m.SagaType).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>One level only, oldest first — a grandchild is its own parent's child, not this one's.</summary>
    [Fact]
    public async Task FindChildren_ReturnsOnlyDirectChildren_OldestFirst()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var parentId = Guid.NewGuid();
        var older = Child("InvoiceSaga", "FulfilmentSaga", parentId, T0);
        var newer = Child("ShippingSaga", "FulfilmentSaga", parentId, T0.AddSeconds(1));
        var grandchild = Child("LabelSaga", "ShippingSaga", newer.CorrelationId, T0);
        var stranger = Child("InvoiceSaga", "FulfilmentSaga", Guid.NewGuid(), T0);
        await InsertAsync(stores, newer, grandchild, stranger, older);

        await using var uow = await stores.BeginAsync();
        var children = await uow.Summaries.FindChildrenAsync("FulfilmentSaga", parentId);

        Assert.Equal([older.CorrelationId, newer.CorrelationId], children.Select(c => c.CorrelationId));
        Assert.Empty(await uow.Summaries.FindChildrenAsync("OrderSaga", parentId));
    }

    private static ConformanceSagaState Child(string sagaType, string parentSagaType, Guid parentCorrelationId, DateTimeOffset createdAtUtc)
    {
        var state = NewState(sagaType);
        state.ParentSagaType = parentSagaType;
        state.ParentCorrelationId = parentCorrelationId;
        state.CreatedAtUtc = createdAtUtc;
        return state;
    }

    [Fact]
    public async Task GetSagaTypes_ReturnsEachDistinctTypeWithItsKind()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var choreographed = NewState("ShippingChoreography");
        choreographed.Kind = SagaKind.Choreographed;
        await InsertAsync(stores, NewState("OrderSaga"), NewState("OrderSaga"), choreographed);

        await using var uow = await stores.BeginAsync();
        var types = await uow.Summaries.GetSagaTypesAsync();

        Assert.Equal(
            [new SagaTypeInfo("OrderSaga", SagaKind.Orchestrated), new SagaTypeInfo("ShippingChoreography", SagaKind.Choreographed)],
            types.OrderBy(t => t.SagaType, StringComparer.Ordinal));
    }
}
