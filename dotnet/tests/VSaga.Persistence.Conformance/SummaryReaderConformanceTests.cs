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

    /// <summary>
    /// <see cref="SagaSummary"/> is the projection of one instance, so every item <c>ListAsync</c> returns
    /// projects the state that was written exactly as <c>GetAsync</c> does — whichever filter selected it.
    /// The four instances differ in every projected field (status, current state, kind, parent link,
    /// both timestamps), one of them has no parent, and the last two listings are filtered, so a reader
    /// whose list projection fills a field from the wrong column, or turns a root saga's null parent
    /// into empty text, fails here while its ids, counts and page numbers stay right. The second
    /// listing is the dashboard's status filter; the third composes a saga type with
    /// <see cref="SagaListFilter.UpdatedSince"/>, which keeps what is strictly newer and drops the other
    /// type's newer row.
    /// </summary>
    [Fact]
    public async Task List_ProjectsTheWrittenStateOfEveryItem_WhateverFilterSelectedIt()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var parentId = Guid.NewGuid();
        var order = NewState("OrderSaga", currentState: "AwaitingPayment", status: SagaStatus.Compensating);
        order.ParentSagaType = "FulfilmentSaga";
        order.ParentCorrelationId = parentId;
        order.CreatedAtUtc = T0.AddTicks(1_234_567);
        order.UpdatedAtUtc = T0.AddMinutes(1).AddTicks(7_654_321);
        var reorder = NewState("OrderSaga", currentState: "Submitted", status: SagaStatus.Running);
        reorder.CreatedAtUtc = T0.AddSeconds(10);
        reorder.UpdatedAtUtc = T0.AddMinutes(3);
        var shipping = NewState("ShippingChoreography", currentState: "Tracking", status: SagaStatus.Completed);
        shipping.Kind = SagaKind.Choreographed;
        shipping.CreatedAtUtc = T0.AddSeconds(20);
        shipping.UpdatedAtUtc = T0.AddMinutes(2);
        var invoice = NewState("InvoiceSaga", currentState: "Rejected", status: SagaStatus.Failed);
        invoice.CreatedAtUtc = T0.AddSeconds(5);
        invoice.UpdatedAtUtc = T0.AddSeconds(30);
        await InsertAsync(stores, order, reorder, shipping, invoice);

        var all = await ListAsync(stores, new SagaListFilter());
        var failed = await ListAsync(stores, new SagaListFilter { Status = SagaStatus.Failed });
        var newerOrders = await ListAsync(stores, new SagaListFilter { SagaType = "OrderSaga", UpdatedSince = T0.AddSeconds(90) });

        AssertProjectsInOrder([reorder, shipping, order, invoice], all);
        Assert.Equal(4, all.TotalCount);
        AssertProjectsInOrder([invoice], failed);
        Assert.Equal(1, failed.TotalCount);
        AssertProjectsInOrder([reorder], newerOrders);
        Assert.Equal(1, newerOrders.TotalCount);
    }

    private void AssertProjectsInOrder(IReadOnlyList<ConformanceSagaState> written, PagedResult<SagaSummary> page) =>
        AssertProjectsInOrder(written, page.Items);

    private void AssertProjectsInOrder(IReadOnlyList<ConformanceSagaState> written, IReadOnlyList<SagaSummary> summaries)
    {
        Assert.Equal(written.Select(w => w.CorrelationId), summaries.Select(s => s.CorrelationId));
        for (var i = 0; i < written.Count; i++)
        {
            var state = written[i];
            var summary = summaries[i];
            Assert.Equal(state.SagaType, summary.SagaType);
            Assert.Equal(state.Kind, summary.Kind);
            Assert.Equal(state.CurrentState, summary.CurrentState);
            Assert.Equal(state.Status, summary.Status);
            Assert.Equal(0, summary.Version);
            Assert.Equal(state.ParentSagaType, summary.ParentSagaType);
            Assert.Equal(state.ParentCorrelationId, summary.ParentCorrelationId);
            AssertSameInstant(state.CreatedAtUtc, summary.CreatedAtUtc);
            AssertSameInstant(state.UpdatedAtUtc, summary.UpdatedAtUtc);
        }
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
    /// <see cref="SagaListFilter.SagaTypes"/> keeps the rows of any of the named types, compared exactly, and
    /// counts the set it kept. An empty set matches nothing, null puts no limit on the type, a name that no row
    /// has is harmless, a null one matches nothing, and a repeated one counts once; with <see cref="SagaListFilter.SagaType"/> too, a row
    /// must satisfy both, and a blank <see cref="SagaListFilter.SagaType"/> is no filter.
    /// </summary>
    [Fact]
    public async Task List_SagaTypes_KeepsTheRowsOfAnyNamedType_ComparedExactly_AndCountsTheFilteredSet()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var orderOld = NewState("OrderSaga", updatedAtUtc: T0);
        var shipping = NewState("ShippingSaga", updatedAtUtc: T0.AddSeconds(1));
        var orderNew = NewState("OrderSaga", updatedAtUtc: T0.AddSeconds(2));
        var payment = NewState("PaymentSaga", updatedAtUtc: T0.AddSeconds(3));
        var other = NewState("OtherSaga", updatedAtUtc: T0.AddSeconds(4));
        await InsertAsync(stores, orderOld, shipping, orderNew, payment, other);

        var two = await ListAsync(stores, new SagaListFilter { SagaTypes = ["OrderSaga", "PaymentSaga"] });
        var paged = await ListAsync(stores, new SagaListFilter { SagaTypes = ["OrderSaga", "PaymentSaga"], Page = 2, PageSize = 2 });
        var wrongCase = await ListAsync(stores, new SagaListFilter { SagaTypes = ["ordersaga", "PAYMENTSAGA"] });
        var unknownToo = await ListAsync(stores, new SagaListFilter { SagaTypes = ["NoSuchSaga", "ShippingSaga", "ShippingSaga"] });
        var one = await ListAsync(stores, new SagaListFilter { SagaTypes = ["PaymentSaga"] });
        var empty = await ListAsync(stores, new SagaListFilter { SagaTypes = [] });
        var none = await ListAsync(stores, new SagaListFilter { SagaTypes = null });
        var withNullName = await ListAsync(stores, new SagaListFilter { SagaTypes = [null!, "PaymentSaga"] });
        var onlyNullName = await ListAsync(stores, new SagaListFilter { SagaTypes = [null!] });

        Assert.Equal([payment.CorrelationId, orderNew.CorrelationId, orderOld.CorrelationId], Ids(two));
        Assert.Equal(3, two.TotalCount);
        Assert.Equal([orderOld.CorrelationId], Ids(paged));
        Assert.Equal(3, paged.TotalCount);
        Assert.Empty(wrongCase.Items);
        Assert.Equal(0, wrongCase.TotalCount);
        Assert.Equal([shipping.CorrelationId], Ids(unknownToo));
        Assert.Equal(1, unknownToo.TotalCount);
        Assert.Equal([payment.CorrelationId], Ids(one));
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.TotalCount);
        Assert.Equal(5, none.TotalCount);
        Assert.Equal([payment.CorrelationId], Ids(withNullName));
        Assert.Empty(onlyNullName.Items);
        Assert.Equal(0, onlyNullName.TotalCount);

        string[] orderAndPayment = ["OrderSaga", "PaymentSaga"];
        var both = await ListAsync(stores, new SagaListFilter { SagaType = "OrderSaga", SagaTypes = orderAndPayment });
        var outsideTheSet = await ListAsync(stores, new SagaListFilter { SagaType = "ShippingSaga", SagaTypes = orderAndPayment });
        var blankType = await ListAsync(stores, new SagaListFilter { SagaType = "  ", SagaTypes = orderAndPayment });

        Assert.Equal([orderNew.CorrelationId, orderOld.CorrelationId], Ids(both));
        Assert.Equal(2, both.TotalCount);
        Assert.Empty(outsideTheSet.Items);
        Assert.Equal(0, outsideTheSet.TotalCount);
        Assert.Equal(3, blankType.TotalCount);
    }

    /// <summary>
    /// <see cref="SagaListFilter.SagaTypes"/> composes with the other filters as each of them does: the set
    /// is applied with them before the count, the sort and the page.
    /// </summary>
    [Fact]
    public async Task List_SagaTypes_ComposesWithStatusKindUpdatedSinceAndSearch()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var failedOrder = NewState("OrderSaga", status: SagaStatus.Failed, updatedAtUtc: T0.AddSeconds(1));
        var runningOrder = NewState("OrderSaga", status: SagaStatus.Running, updatedAtUtc: T0.AddSeconds(2));
        var failedShipping = NewState("ShippingSaga", status: SagaStatus.Failed, updatedAtUtc: T0.AddSeconds(3));
        var failedPayment = NewState("PaymentSaga", status: SagaStatus.Failed, updatedAtUtc: T0.AddSeconds(4));
        failedPayment.Kind = SagaKind.Choreographed;
        var failedOther = NewState("OtherSaga", status: SagaStatus.Failed, updatedAtUtc: T0.AddSeconds(5));
        await InsertAsync(stores, failedOrder, runningOrder, failedShipping, failedPayment, failedOther);
        string[] types = ["OrderSaga", "PaymentSaga", "OtherSaga"];

        var failed = await ListAsync(stores, new SagaListFilter { SagaTypes = types, Status = SagaStatus.Failed });
        var choreographed = await ListAsync(stores, new SagaListFilter { SagaTypes = types, Kind = SagaKind.Choreographed });
        var since = await ListAsync(stores, new SagaListFilter { SagaTypes = types, UpdatedSince = T0.AddSeconds(2) });
        var searched = await ListAsync(stores, new SagaListFilter { SagaTypes = types, Search = "pay" });
        var failedSince = await ListAsync(stores, new SagaListFilter { SagaTypes = types, Status = SagaStatus.Failed, UpdatedSince = T0.AddSeconds(1), PageSize = 1, Page = 2 });

        Assert.Equal([failedOther.CorrelationId, failedPayment.CorrelationId, failedOrder.CorrelationId], Ids(failed));
        Assert.Equal(3, failed.TotalCount);
        Assert.Equal([failedPayment.CorrelationId], Ids(choreographed));
        Assert.Equal([failedOther.CorrelationId, failedPayment.CorrelationId], Ids(since));
        Assert.Equal(2, since.TotalCount);
        Assert.Equal([failedPayment.CorrelationId], Ids(searched));
        Assert.Equal([failedPayment.CorrelationId], Ids(failedSince));
        Assert.Equal(2, failedSince.TotalCount);
    }

    /// <summary>
    /// The rows a set of types keeps come in the order they hold in the unfiltered list, for every sort arm and
    /// with ties on the sort column across the types, and every page holds the same rows as that list's slice:
    /// the relative order of two rows depends only on their own values (see
    /// <see cref="ISagaSummaryReader.ListAsync"/>), so restricting the set cannot reorder it. Nothing here
    /// asserts a particular order, only that the restriction keeps the provider's own.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(SagaSortColumn.UpdatedAt, false)]
    [InlineData(SagaSortColumn.UpdatedAt, true)]
    [InlineData(SagaSortColumn.Status, false)]
    [InlineData(SagaSortColumn.Status, true)]
    public async Task List_SagaTypes_KeepTheUnfilteredOrder_OverTiesAcrossTypes_PageByPage(SagaSortColumn? sortBy, bool sortDescending)
    {
        await using var stores = await Fixture.CreateStoresAsync();
        string[] kinds = ["OrderSaga", "PaymentSaga", "ShippingSaga", "OtherSaga"];
        SagaStatus[] statuses = [SagaStatus.Running, SagaStatus.Completed, SagaStatus.Failed];
        var states = new List<ConformanceSagaState>();
        for (var i = 0; i < 48; i++)
        {
            // Only a few distinct timestamps and statuses, and each timestamp and status spans every type, so rows
            // tie on the sort column across types.
            states.Add(NewState(kinds[i % kinds.Length], status: statuses[i % statuses.Length], updatedAtUtc: T0.AddSeconds((i / 4) % 4)));
        }

        await InsertAsync(stores, [.. states]);
        string[] wanted = ["OrderSaga", "ShippingSaga"];

        var unfiltered = await WalkAllAsync(stores, new SagaListFilter { SortBy = sortBy, SortDescending = sortDescending, PageSize = 5 });
        var expected = unfiltered.Where(k => wanted.Contains(k.SagaType, StringComparer.Ordinal)).ToList();
        var filtered = await WalkAllAsync(stores, new SagaListFilter { SortBy = sortBy, SortDescending = sortDescending, PageSize = 5, SagaTypes = wanted });

        Assert.Equal(48, unfiltered.Count);
        Assert.Equal(24, expected.Count);
        Assert.Equal(expected, filtered);
        var total = (await ListAsync(stores, new SagaListFilter { SortBy = sortBy, SortDescending = sortDescending, PageSize = 5, Page = 2, SagaTypes = wanted })).TotalCount;
        Assert.Equal(24, total);
    }

    private static async Task<List<(string SagaType, Guid CorrelationId)>> WalkAllAsync(IProviderStores stores, SagaListFilter first)
    {
        var walked = new List<(string SagaType, Guid CorrelationId)>();
        for (var page = 1; page <= 30; page++)
        {
            var result = await ListAsync(stores, new SagaListFilter
            {
                SortBy = first.SortBy,
                SortDescending = first.SortDescending,
                PageSize = first.PageSize,
                SagaTypes = first.SagaTypes,
                Page = page,
            });
            if (result.Items.Count == 0)
                break;
            walked.AddRange(Keys(result));
        }

        return walked;
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

        // A watermark sitting on the newest row selects nothing: the poller's quiet tick.
        var quiet = await ListAsync(stores, new SagaListFilter { UpdatedSince = T0.AddSeconds(2) });
        Assert.Empty(quiet.Items);
        Assert.Equal(0, quiet.TotalCount);
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

    /// <summary>
    /// Clause 9 (fix F1): every sort arm applies a stable total order whose relative order of two rows
    /// depends only on those rows' own values (<see cref="ISagaSummaryReader.ListAsync"/>'s remarks). The
    /// whole group ties on both Status and UpdatedAtUtc, and it comes in pairs sharing a correlation id
    /// across two saga types, so only a tiebreak on the full (SagaType, CorrelationId) identity orders it.
    /// A page walk visits each row exactly once, the same page fetched twice is identical, and writing
    /// rows outside the result reorders nothing inside it — what a dashboard poller paging while other
    /// sagas advance depends on. Only per-provider determinism is asserted, never a particular sequence.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(SagaSortColumn.UpdatedAt, false)]
    [InlineData(SagaSortColumn.UpdatedAt, true)]
    [InlineData(SagaSortColumn.Status, false)]
    [InlineData(SagaSortColumn.Status, true)]
    public async Task List_OrdersRowsThatTieOnTheSortColumnTotally(SagaSortColumn? sortBy, bool sortDescending)
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var tied = Enumerable.Range(0, 20)
            .Select(_ => Guid.NewGuid())
            .SelectMany(correlationId => new[] { Tied("TiedSagaA", correlationId), Tied("TiedSagaB", correlationId) })
            .ToArray();
        await InsertAsync(stores, tied);
        var firstPage = new SagaListFilter { Kind = SagaKind.Choreographed, SortBy = sortBy, SortDescending = sortDescending, PageSize = 7 };

        var walked = await WalkAsync(stores, firstPage);
        Assert.Equal(tied.Select(s => (s.SagaType, s.CorrelationId)).Order(), walked.Order());
        Assert.Equal(walked.Take(7), Keys(await ListAsync(stores, firstPage)));

        // Enough rows outside the result — several times the group — that a store keeping its rows in a hash
        // table is certain to grow and rehash it.
        await InsertAsync(stores, Enumerable.Range(0, 200).Select(_ => NewState("UnrelatedSaga")).ToArray());

        Assert.Equal(walked, await WalkAsync(stores, firstPage));
    }

    private static ConformanceSagaState Tied(string sagaType, Guid correlationId)
    {
        var state = NewState(sagaType, correlationId);
        state.Kind = SagaKind.Choreographed;
        return state;
    }

    private static IEnumerable<(string SagaType, Guid CorrelationId)> Keys(PagedResult<SagaSummary> page) =>
        page.Items.Select(s => (s.SagaType, s.CorrelationId));

    /// <summary>Every page of <paramref name="firstPage"/>'s query in turn, stopping at the first empty one (or a bound no walk here should reach).</summary>
    private static async Task<List<(string SagaType, Guid CorrelationId)>> WalkAsync(IProviderStores stores, SagaListFilter firstPage)
    {
        var walked = new List<(string SagaType, Guid CorrelationId)>();
        for (var page = 1; page <= 20; page++)
        {
            var result = await ListAsync(stores, new SagaListFilter
            {
                Kind = firstPage.Kind,
                SortBy = firstPage.SortBy,
                SortDescending = firstPage.SortDescending,
                PageSize = firstPage.PageSize,
                Page = page,
            });
            if (result.Items.Count == 0)
                break;
            walked.AddRange(Keys(result));
        }

        return walked;
    }

    /// <summary>
    /// Clause 9 (fix F2): Search is case-insensitive in the saga type — the dashboard's search box is bound
    /// to it, and nobody types a saga type in its exact casing.
    /// </summary>
    [Fact]
    public async Task Search_IgnoresCaseInTheSagaType()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var order = NewState("OrderFulfilmentSaga");
        await InsertAsync(stores, order, NewState("ShippingChoreography"));

        foreach (var term in new[] { "orderfulfilment", "ORDERFULFILMENT", "oRDERfULFILMENT" })
            Assert.Equal([order.CorrelationId], Ids(await ListAsync(stores, new SagaListFilter { Search = term })));
    }

    /// <summary>
    /// Clause 9 (fix F2), the other field: a correlation id is pasted in whatever case it was copied in, so
    /// its text is matched case-insensitively too, whatever case the provider happens to store a Guid in.
    /// </summary>
    [Fact]
    public async Task Search_IgnoresCaseInTheCorrelationId()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var target = NewState("OrderSaga", Guid.Parse("abcdef12-3456-4789-abcd-ef0123456789"));
        await InsertAsync(stores, target, NewState("OrderSaga", Guid.Parse("12345678-9012-3456-7890-123456789012")));

        foreach (var term in new[] { "abcdef12", "ABCDEF12", "aBcD-eF01" })
            Assert.Equal([target.CorrelationId], Ids(await ListAsync(stores, new SagaListFilter { Search = term })));
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

    /// <summary>
    /// Each child <c>FindChildrenAsync</c> returns is the projection of the instance that was written,
    /// parent link included — the link is what puts it in the result, and a caller walking a saga tree
    /// reads it back off the summary. The two children differ in saga type, kind, state, status and both
    /// timestamps. Left out: a child of the same parent id under another parent type, a child of the same
    /// parent type under another parent instance, and a root saga, none of which name this parent.
    /// </summary>
    [Fact]
    public async Task FindChildren_ProjectsTheWrittenStateOfEveryChild_ParentLinkIncluded()
    {
        await using var stores = await Fixture.CreateStoresAsync();
        var parentId = Guid.NewGuid();
        var invoice = Child("InvoiceSaga", "FulfilmentSaga", parentId, T0.AddTicks(1_234_567));
        invoice.CurrentState = "Rejected";
        invoice.Status = SagaStatus.Failed;
        invoice.UpdatedAtUtc = T0.AddMinutes(1).AddTicks(7_654_321);
        var shipping = Child("ShippingChoreography", "FulfilmentSaga", parentId, T0.AddSeconds(1));
        shipping.Kind = SagaKind.Choreographed;
        shipping.CurrentState = "Tracking";
        shipping.Status = SagaStatus.Completed;
        shipping.UpdatedAtUtc = T0.AddMinutes(2);
        var otherParentType = Child("InvoiceSaga", "BillingSaga", parentId, T0);
        var otherParentInstance = Child("InvoiceSaga", "FulfilmentSaga", Guid.NewGuid(), T0);
        var root = NewState("OrderSaga");
        await InsertAsync(stores, shipping, otherParentType, root, invoice, otherParentInstance);

        await using var uow = await stores.BeginAsync();
        var children = await uow.Summaries.FindChildrenAsync("FulfilmentSaga", parentId);

        AssertProjectsInOrder([invoice, shipping], children);
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
