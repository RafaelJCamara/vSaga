using Microsoft.Extensions.Time.Testing;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Identity.Services;
using VSaga.Persistence.Redis;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// <see cref="ScopedSagaLister"/> (docs/design/dashboard-usability-and-access.md §8.7) against a reader that
/// orders and pages exactly as the in-memory and EF Core providers do: a caller who sees several types is one
/// provider call carrying the set, every sort arm walked page by page equals one full sort of the visible
/// rows, the bounds refuse with the last reachable page, and nothing outside the scope gets through.
/// </summary>
public sealed class ScopedSagaListerTests
{
    // Ordinal order is Alpha < Gamma < beta; a culture-aware sort would put beta second.
    private const string Alpha = "Alpha";
    private const string Beta = "beta";
    private const string Gamma = "Gamma";
    private const string Hidden = "Hidden";

    private static readonly DateTimeOffset Base = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<SagaSortColumn?, bool> SortArms => new()
    {
        { null, false },
        { null, true },
        { SagaSortColumn.UpdatedAt, false },
        { SagaSortColumn.UpdatedAt, true },
        { SagaSortColumn.Status, false },
        { SagaSortColumn.Status, true },
    };

    [Theory]
    [MemberData(nameof(SortArms))]
    public async Task EverySortArm_WalkedPageByPage_EqualsAFullSortOfTheVisibleRows(SagaSortColumn? sortBy, bool descending)
    {
        var reader = new FakeReader(Rows((Alpha, 30), (Beta, 20), (Gamma, 10), (Hidden, 15)));
        var lister = Lister(reader);
        var scope = SagaTypeScope.Of([Alpha, Beta, Gamma]);
        var expected = Oracle(reader.Data.Where(r => !string.Equals(r.SagaType, Hidden, StringComparison.Ordinal)), sortBy, descending);
        const int pageSize = 7;

        var walked = new List<SagaSummary>();
        for (var page = 1; page <= 10; page++)
        {
            var result = await lister.ListAsync(Filter(page, pageSize, sortBy, descending), scope, CancellationToken.None);
            Assert.Equal(expected.Skip((page - 1) * pageSize).Take(pageSize), result.Items);
            Assert.Equal(60, result.TotalCount);
            walked.AddRange(result.Items);
        }

        Assert.Equal(expected, walked);
    }

    [Fact]
    public async Task TiesAcrossTypes_BreakBySagaTypeOrdinal()
    {
        var reader = new FakeReader(
        [
            Row(Beta, Base, SagaStatus.Running),
            Row(Gamma, Base, SagaStatus.Running),
            Row(Alpha, Base, SagaStatus.Running),
        ]);

        var result = await Lister(reader).ListAsync(Filter(1, 10), SagaTypeScope.Of([Alpha, Beta, Gamma]), CancellationToken.None);

        Assert.Equal([Alpha, Gamma, Beta], result.Items.Select(s => s.SagaType), StringComparer.Ordinal);
    }

    [Fact]
    public async Task SeveralVisibleTypes_AreOneProviderCallCarryingTheSet_AndTheProvidersTotalStands()
    {
        var reader = new FakeReader(Rows((Alpha, 12), (Beta, 3), (Hidden, 40)));

        var result = await Lister(reader).ListAsync(Filter(1, 5), SagaTypeScope.Of([Beta, Alpha]), CancellationToken.None);

        Assert.Equal(15, result.TotalCount);
        var call = Assert.Single(reader.Calls);
        Assert.Null(call.SagaType);
        Assert.Equal([Alpha, Beta], call.SagaTypes, StringComparer.Ordinal);
        Assert.Equal((1, 5), (call.Page, call.PageSize));
    }

    [Fact]
    public async Task ADeepRankedPage_IsOneCallAtThePageAsked_NotAWalkOfThePagesBeforeIt()
    {
        var reader = new FakeReader(Rows((Alpha, 200), (Beta, 1)));

        await Lister(reader).ListAsync(Filter(page: 8, pageSize: 10), SagaTypeScope.Of([Alpha, Beta]), CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.Equal((8, 10), (call.Page, call.PageSize));
    }

    [Fact]
    public async Task ATypeSetTheCallerAlsoNames_NarrowsTheScope_NeverWidensIt()
    {
        var reader = new FakeReader(Rows((Alpha, 3), (Beta, 3), (Gamma, 3), (Hidden, 3)));

        var narrowed = await Lister(reader).ListAsync(
            new SagaListFilter { SagaTypes = [Beta, Gamma, Hidden], PageSize = 50 }, SagaTypeScope.Of([Alpha, Beta, Gamma]), CancellationToken.None);

        Assert.Equal([Gamma, Beta], Assert.Single(reader.Calls).SagaTypes, StringComparer.Ordinal);
        Assert.Equal(6, narrowed.TotalCount);
        Assert.All(narrowed.Items, s => Assert.False(Is(s.SagaType, Hidden) || Is(s.SagaType, Alpha)));
    }

    [Fact]
    public async Task ASingleVisibleType_IsOneCallWithThatType_AndNoBound()
    {
        var reader = new FakeReader(Rows((Alpha, 3), (Hidden, 3)));

        var result = await Lister(reader).ListAsync(Filter(page: 5_000, pageSize: 100), SagaTypeScope.Of([Alpha]), CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.Equal((Alpha, 5_000, 100), (call.SagaType, call.Page, call.PageSize));
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ASuppliedSagaType_InScopeIsOneCall_OutOfScopeIsAnEmptyPage()
    {
        var reader = new FakeReader(Rows((Alpha, 3), (Hidden, 3)));
        var lister = Lister(reader);
        var scope = SagaTypeScope.Of([Alpha, Beta]);

        var inScope = await lister.ListAsync(Filter(1, 10, sagaType: Alpha), scope, CancellationToken.None);
        var outOfScope = await lister.ListAsync(Filter(1, 10, sagaType: Hidden), scope, CancellationToken.None);

        Assert.Equal(3, inScope.TotalCount);
        Assert.Equal(Alpha, Assert.Single(reader.Calls).SagaType);
        Assert.Empty(outOfScope.Items);
        Assert.Equal(0, outOfScope.TotalCount);
    }

    [Fact]
    public async Task AnUnscopedCaller_GetsTheProvidersAnswerAsItStands()
    {
        var reader = new FakeReader(Rows((Alpha, 3), (Hidden, 3)));
        var filter = Filter(1, 10);

        var result = await Lister(reader).ListAsync(filter, SagaTypeScope.All, CancellationToken.None);

        Assert.Same(filter, Assert.Single(reader.Calls));
        Assert.Equal(6, result.TotalCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(SagaSortColumn.UpdatedAt, true)]
    public async Task TheRankServedShape_AllowsFiftyTypesAndTenThousandRows(SagaSortColumn? sortBy, bool descending)
    {
        // Every type has run, so narrowing a long scope to the types that ran keeps all 51.
        var everyType = Enumerable.Range(0, ScopedSagaLister.MaxMergedTypes + 1).Select(i => ($"Type{i:00}", 1)).ToArray();
        var lister = Lister(new FakeReader(Rows(everyType)));

        await lister.ListAsync(Filter(100, 100, sortBy, descending), Types(ScopedSagaLister.MaxMergedTypes), CancellationToken.None);
        var tooManyTypes = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() =>
            lister.ListAsync(Filter(1, 25, sortBy, descending), Types(ScopedSagaLister.MaxMergedTypes + 1), CancellationToken.None));
        var tooDeep = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() =>
            lister.ListAsync(Filter(101, 100, sortBy, descending), Types(2), CancellationToken.None));

        Assert.Equal(0, tooManyTypes.MaxPage);
        Assert.Contains("sagaType", tooManyTypes.Message, StringComparison.Ordinal);
        Assert.Equal(100, tooDeep.MaxPage);
        Assert.Contains("sagaType", tooDeep.Message, StringComparison.Ordinal);
    }

    public static TheoryData<SagaListFilter> UnrankedShapes => new()
    {
        new SagaListFilter { SortBy = SagaSortColumn.Status },
        new SagaListFilter { SortBy = SagaSortColumn.Status, SortDescending = true },
        new SagaListFilter { Status = SagaStatus.Failed },
        new SagaListFilter { Kind = SagaKind.Choreographed },

        // Redis never serves a search from a rank and EF Core matches it with LIKE, so a search is unranked too.
        new SagaListFilter { Search = "x" },
    };

    [Theory]
    [MemberData(nameof(UnrankedShapes))]
    public async Task EveryOtherShape_AllowsTenTypesAndOneChunkOfDepth(SagaListFilter shape)
    {
        // Every type has run, so narrowing the eleven names to the types that ran keeps all eleven.
        var everyType = Enumerable.Range(0, ScopedSagaLister.MaxUnrankedTypes + 1).Select(i => ($"Type{i:00}", 3)).ToArray();
        var reader = new FakeReader(Rows(everyType));
        var lister = Lister(reader);

        await lister.ListAsync(Shaped(shape, 20, 25), Types(ScopedSagaLister.MaxUnrankedTypes), CancellationToken.None);
        var tooManyTypes = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() =>
            lister.ListAsync(Shaped(shape, 1, 25), Types(ScopedSagaLister.MaxUnrankedTypes + 1), CancellationToken.None));
        var tooDeep = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() =>
            lister.ListAsync(Shaped(shape, 21, 25), Types(2), CancellationToken.None));

        Assert.Equal(0, tooManyTypes.MaxPage);
        Assert.Equal(20, tooDeep.MaxPage);
    }

    [Fact]
    public async Task MoreNamesThanAnUnrankedShapeCanMerge_AreNarrowedToTheTypesThatRan()
    {
        // Eleven names are more than the ten a status filter can merge, but only two of them have run.
        var reader = new FakeReader(Rows(("Type00", 20), ("Type07", 20), (Hidden, 20)));
        var expected = reader.Data.Count(r => r.Status == SagaStatus.Failed && !Is(r.SagaType, Hidden));

        var result = await Lister(reader).ListAsync(
            Shaped(new SagaListFilter { Status = SagaStatus.Failed }, 1, 25), Types(ScopedSagaLister.MaxUnrankedTypes + 1), CancellationToken.None);

        Assert.Equal(1, reader.SagaTypeReads);
        Assert.Equal(["Type00", "Type07"], Assert.Single(reader.Calls).SagaTypes, StringComparer.Ordinal);
        Assert.Equal(expected, result.TotalCount);
        Assert.Equal(expected, result.Items.Count);
        Assert.All(result.Items, s => Assert.Equal(SagaStatus.Failed, s.Status));
    }

    [Fact]
    public async Task AnUnrankedShape_IsOneCallAtThePageAsked()
    {
        var reader = new FakeReader(Rows((Alpha, 60), (Beta, 60)));

        await Lister(reader).ListAsync(Filter(4, 25, SagaSortColumn.Status), SagaTypeScope.Of([Alpha, Beta]), CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.Equal((4, 25, SagaSortColumn.Status), (call.Page, call.PageSize, call.SortBy));
        Assert.Equal([Alpha, Beta], call.SagaTypes, StringComparer.Ordinal);
    }

    [Fact]
    public Task TheRedisScanLimit_StillReachesTheCaller()
    {
        var reader = new FakeReader(Rows((Alpha, 3), (Beta, 3))) { Refuse = true };

        return Assert.ThrowsAsync<RedisSearchScanLimitExceededException>(() =>
            Lister(reader).ListAsync(Filter(1, 10, search: "x"), SagaTypeScope.Of([Alpha, Beta]), CancellationToken.None));
    }

    [Fact]
    public async Task RowsOutsideTheScope_AreFilteredOut_EvenWhenTheProviderReturnsThem()
    {
        // A provider that ignores the saga-type filter: the lister's own scope check is all that is left.
        var reader = new FakeReader(Rows((Alpha, 4), (Beta, 4), (Hidden, 4))) { IgnoreTypeFilter = true };
        var lister = Lister(reader);

        var merged = await lister.ListAsync(Filter(1, 50), SagaTypeScope.Of([Alpha, Beta]), CancellationToken.None);
        var single = await lister.ListAsync(Filter(1, 50), SagaTypeScope.Of([Alpha]), CancellationToken.None);
        var supplied = await lister.ListAsync(Filter(1, 50, sagaType: Beta), SagaTypeScope.Of([Beta]), CancellationToken.None);

        Assert.NotEmpty(merged.Items);
        Assert.All(merged.Items, s => Assert.False(Is(s.SagaType, Hidden)));
        Assert.All(single.Items, s => Assert.True(Is(s.SagaType, Alpha)));
        Assert.All(supplied.Items, s => Assert.True(Is(s.SagaType, Beta)));

        // The rows removed come off the total too: the provider counted all twelve, four of them in scope.
        Assert.Equal(4, single.TotalCount);
        Assert.Equal(4, supplied.TotalCount);

        // One call: the provider counted all twelve rows and the four hidden ones on this page come off.
        Assert.Equal(12 - 4, merged.TotalCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("")]
    public async Task AGrantOfASingleSpace_CannotWidenTheScope(string? sagaType)
    {
        // Every provider reads a blank type filter as "every type"; a grant naming " " must not become that.
        var reader = new FakeReader(Rows((Alpha, 3), (Hidden, 3)));
        var lister = Lister(reader);

        var onlyBlank = await lister.ListAsync(Filter(1, 10, sagaType: sagaType), SagaTypeScope.Of([" "]), CancellationToken.None);
        var blankAndAlpha = await lister.ListAsync(Filter(1, 10, sagaType: sagaType), SagaTypeScope.Of([" ", Alpha]), CancellationToken.None);

        Assert.Empty(onlyBlank.Items);
        Assert.Equal(0, onlyBlank.TotalCount);
        Assert.All(blankAndAlpha.Items, s => Assert.Equal(Alpha, s.SagaType));
        Assert.Equal(3, blankAndAlpha.TotalCount);
        Assert.DoesNotContain(reader.Calls, c => string.IsNullOrWhiteSpace(c.SagaType) && c.SagaTypes is null);
    }

    [Fact]
    public async Task UpToFiftyScopedNames_AreUsedAsTheyStand_WithoutReadingTheSagaTypes()
    {
        var reader = new FakeReader(Rows((Alpha, 2)));

        await Lister(reader).ListAsync(Filter(1, 10), Types(ScopedSagaLister.MaxMergedTypes), CancellationToken.None);

        Assert.Equal(0, reader.SagaTypeReads);
        Assert.Equal(ScopedSagaLister.MaxMergedTypes, Assert.Single(reader.Calls).SagaTypes!.Count);
    }

    [Fact]
    public async Task MoreThanFiftyScopedNames_AreNarrowedToTheTypesThatRan_ReadAtMostOncePerFewSeconds()
    {
        var reader = new FakeReader(Rows(("Type00", 2), ("Type01", 2), (Hidden, 2)));
        var time = new FakeTimeProvider(Base);
        var lister = new ScopedSagaLister(reader, new SagaTypeNameCache(time));
        var scope = Types(ScopedSagaLister.MaxMergedTypes + 5);

        var first = await lister.ListAsync(Filter(1, 10), scope, CancellationToken.None);
        await lister.ListAsync(Filter(1, 10), scope, CancellationToken.None);
        Assert.Equal(1, reader.SagaTypeReads);

        time.Advance(SagaTypeNameCache.Lifetime);
        await lister.ListAsync(Filter(1, 10), scope, CancellationToken.None);

        Assert.Equal(2, reader.SagaTypeReads);
        Assert.Equal(4, first.TotalCount);
        Assert.All(reader.Calls, c => Assert.Equal(["Type00", "Type01"], c.SagaTypes, StringComparer.Ordinal));
    }

    private static ScopedSagaLister Lister(FakeReader reader) => new(reader, new SagaTypeNameCache(TimeProvider.System));

    private static bool Is(string? sagaType, string expected) => string.Equals(sagaType, expected, StringComparison.Ordinal);

    /// <summary>A scope of <paramref name="count"/> names: Type00, Type01, and so on.</summary>
    private static SagaTypeScope Types(int count) =>
        SagaTypeScope.Of(Enumerable.Range(0, count).Select(i => $"Type{i:00}"));

    private static SagaListFilter Filter(
        int page, int pageSize, SagaSortColumn? sortBy = null, bool descending = false, string? sagaType = null, string? search = null) => new()
    {
        Page = page,
        PageSize = pageSize,
        SortBy = sortBy,
        SortDescending = descending,
        SagaType = sagaType,
        Search = search,
    };

    private static SagaListFilter Shaped(SagaListFilter shape, int page, int pageSize) => new()
    {
        Status = shape.Status,
        Kind = shape.Kind,
        Search = shape.Search,
        SortBy = shape.SortBy,
        SortDescending = shape.SortDescending,
        Page = page,
        PageSize = pageSize,
    };

    /// <summary>
    /// Rows per type with few distinct timestamps and statuses, so every arm has ties on its column, across
    /// types and inside one.
    /// </summary>
    private static List<SagaSummary> Rows(params (string SagaType, int Count)[] types)
    {
        var rows = new List<SagaSummary>();
        var statuses = Enum.GetValues<SagaStatus>();
        var i = 0;
        foreach (var (sagaType, count) in types)
        {
            for (var n = 0; n < count; n++, i++)
                rows.Add(Row(sagaType, Base.AddMinutes(i * 7 % 5), statuses[i * 3 % statuses.Length]));
        }

        return rows;
    }

    private static SagaSummary Row(string sagaType, DateTimeOffset updatedAtUtc, SagaStatus status) =>
        new(Guid.NewGuid(), sagaType, SagaKind.Orchestrated, "State", status, Base.AddDays(-1), updatedAtUtc, 1, null, null);

    /// <summary>
    /// The expected order, written out independently of the lister: the arm's column, then <c>UpdatedAtUtc</c>
    /// descending for a Status sort, then saga type (ordinal) and correlation id, which is the reader's own
    /// identity tie-break inside a type.
    /// </summary>
    private static List<SagaSummary> Oracle(IEnumerable<SagaSummary> rows, SagaSortColumn? sortBy, bool descending)
    {
        var ordered = sortBy switch
        {
            SagaSortColumn.Status when descending => rows.OrderByDescending(r => (int)r.Status).ThenByDescending(r => r.UpdatedAtUtc),
            SagaSortColumn.Status => rows.OrderBy(r => (int)r.Status).ThenByDescending(r => r.UpdatedAtUtc),
            SagaSortColumn.UpdatedAt when !descending => rows.OrderBy(r => r.UpdatedAtUtc),
            _ => rows.OrderByDescending(r => r.UpdatedAtUtc),
        };

        return [.. ordered.ThenBy(r => r.SagaType, StringComparer.Ordinal).ThenBy(r => r.CorrelationId)];
    }

    /// <summary>
    /// A reader that filters, orders and pages like <c>InMemorySagaStore</c> and <c>EfCoreSagaSummaryReader</c>,
    /// a blank saga type included (no filter), and records each list request.
    /// </summary>
    private sealed class FakeReader(List<SagaSummary> rows) : ISagaSummaryReader
    {
        public List<SagaSummary> Data { get; } = rows;

        public List<SagaListFilter> Calls { get; } = [];

        public int SagaTypeReads { get; private set; }

        public bool Refuse { get; init; }

        public bool IgnoreTypeFilter { get; init; }

        public Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
        {
            Calls.Add(filter);
            if (Refuse)
                throw new RedisSearchScanLimitExceededException(candidates: 250_000, limit: 100_000);

            var matching = Data.Where(r =>
                (filter.Status is not { } status || r.Status == status)
                && (filter.Kind is not { } kind || r.Kind == kind)
                && (IgnoreTypeFilter || string.IsNullOrWhiteSpace(filter.SagaType) || string.Equals(r.SagaType, filter.SagaType, StringComparison.Ordinal))
                && (IgnoreTypeFilter || filter.SagaTypes is not { } types || types.Contains(r.SagaType, StringComparer.Ordinal)))
                .ToList();
            var page = Oracle(matching, filter.SortBy, filter.SortDescending).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
            return Task.FromResult(new PagedResult<SagaSummary>(page, filter.Page, filter.PageSize, matching.Count));
        }

        public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default)
        {
            SagaTypeReads++;
            return Task.FromResult<IReadOnlyList<SagaTypeInfo>>(
                [.. Data.Select(r => r.SagaType).Distinct(StringComparer.Ordinal).Select(t => new SagaTypeInfo(t, SagaKind.Orchestrated))]);
        }

        public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
