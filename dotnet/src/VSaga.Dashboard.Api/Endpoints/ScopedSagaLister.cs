using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// A list across several saga types that the dashboard refuses to serve: too many visible types, or a page too
/// deep for the request's shape. The list endpoint answers 400 <c>{ error, maxPage }</c>; the message names
/// the <c>sagaType</c> filter, which always works.
/// </summary>
public sealed class ScopedSagaListBoundExceededException(string message, int maxPage) : Exception(message)
{
    /// <summary>The last page this request's shape can reach at its page size; 0 when no page can be served.</summary>
    public int MaxPage { get; } = maxPage;
}

/// <summary>
/// The saga list for a caller whose <c>sagas.view</c> may be scoped to named saga types
/// (docs/design/dashboard-usability-and-access.md §8.7, ADR 0006 decision 7). A caller who sees several types is
/// served by one <see cref="ISagaSummaryReader.ListAsync"/> call carrying <see cref="SagaListFilter.SagaTypes"/>:
/// the provider filters, sorts, pages and counts the set in its own order, inside bounds that keep the cost of
/// one request in check and that the HTTP contract states (400 with the last reachable page).
/// </summary>
public sealed class ScopedSagaLister(ISagaSummaryReader reader, SagaTypeNameCache typeNames)
{
    /// <summary>
    /// The most saga types a list served from a rank can cover, and the most scoped names such a list uses as
    /// they stand without asking the store which types have run.
    /// </summary>
    public const int MaxMergedTypes = 50;

    /// <summary>The deepest row (<c>page × pageSize</c>) a list across several types served from a rank can reach.</summary>
    public const int MaxMergeDepth = 10_000;

    /// <summary>
    /// The most saga types a list of any other shape can cover, and the most scoped names such a list uses as
    /// they stand.
    /// </summary>
    public const int MaxUnrankedTypes = 10;

    /// <summary>
    /// The deepest row any other shape can reach. On Redis such a request reads every candidate member of the
    /// status or kind index it starts from (ADR 0006).
    /// </summary>
    public const int MaxUnrankedDepth = 500;

    /// <summary>The largest page a provider is asked for; the list endpoint's own page cap.</summary>
    public const int MaxChunk = SagaEndpoints.MaxPageSize;

    /// <summary>
    /// One page of the sagas <paramref name="scope"/> lets the caller see, matching <paramref name="filter"/>. An
    /// unscoped caller gets the provider's answer as it stands. Every other answer is filtered by the scope
    /// (ordinal) before it is returned, so a provider that reads a filter more widely than asked cannot widen it.
    /// </summary>
    /// <exception cref="ScopedSagaListBoundExceededException">A list across several types is past a bound.</exception>
    public async Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, SagaTypeScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.IsAll)
            return await reader.ListAsync(filter, cancellationToken);

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxChunk);

        // Blank means "all types" to every provider, so it is no filter here either: a grant naming a blank
        // type must not turn into a request for everything.
        if (!string.IsNullOrWhiteSpace(filter.SagaType))
        {
            return scope.Contains(filter.SagaType)
                ? WithinScope(await reader.ListAsync(filter, cancellationToken), scope)
                : Empty(page, pageSize);
        }

        var rankServed = IsRankServed(filter);
        var types = await VisibleTypesAsync(scope, rankServed ? MaxMergedTypes : MaxUnrankedTypes, cancellationToken);
        if (filter.SagaTypes is { } requested)
            types = [.. types.Intersect(requested, StringComparer.Ordinal)];

        if (types.Count == 0)
            return Empty(page, pageSize);
        if (types.Count == 1)
            return WithinScope(await reader.ListAsync(For(filter, types, filter.Page, filter.PageSize), cancellationToken), scope);

        CheckBounds(types.Count, page, pageSize, rankServed);

        return WithinScope(await reader.ListAsync(For(filter, types, page, pageSize), cancellationToken), scope);
    }

    /// <summary>
    /// The shape Redis serves from a rank (<c>RedisListQuery.IsRankable</c> and its type-set counterpart), so
    /// paging deeper there costs a range read, not a scan: the <c>UpdatedAt</c> sort (the default included) with
    /// no status or kind filter and no search. Redis never serves a search from a rank, and EF Core matches one
    /// with <c>LIKE</c>, so a searched list gets the tighter bounds of every other shape.
    /// </summary>
    internal static bool IsRankServed(SagaListFilter filter) =>
        filter.SortBy is null or SagaSortColumn.UpdatedAt && filter.Status is null && filter.Statuses is null && filter.Kind is null && string.IsNullOrWhiteSpace(filter.Search);

    /// <summary>
    /// The non-blank saga types the scope names, ordinal ascending: as they stand when there are at most
    /// <paramref name="typeBound"/> of them, the request shape's type bound (types that have not run yet just
    /// match nothing), otherwise the types that have run, read through a short-lived cache, kept when
    /// the scope covers them. So a caller granted more named types than the shape can cover is refused only
    /// when more than that many of them have actually run.
    /// </summary>
    private async Task<IReadOnlyList<string>> VisibleTypesAsync(SagaTypeScope scope, int typeBound, CancellationToken cancellationToken)
    {
        var named = scope.SagaTypes.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var types = named.Count <= typeBound
            ? named
            : (await typeNames.GetAsync(reader, cancellationToken)).Where(t => !string.IsNullOrWhiteSpace(t) && scope.Contains(t));

        return [.. types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static void CheckBounds(int typeCount, int page, int pageSize, bool rankServed)
    {
        var (maxTypes, maxDepth) = rankServed ? (MaxMergedTypes, MaxMergeDepth) : (MaxUnrankedTypes, MaxUnrankedDepth);
        var shape = rankServed ? "sorted by last update" : "sorted by status, filtered by status or kind, or searched";

        if (typeCount > maxTypes)
        {
            throw new ScopedSagaListBoundExceededException(
                $"You can see {typeCount} saga types, more than the {maxTypes} a list {shape} can combine. Choose a saga type with the sagaType filter.",
                maxPage: 0);
        }

        if ((long)page * pageSize > maxDepth)
        {
            var maxPage = maxDepth / pageSize;
            throw new ScopedSagaListBoundExceededException(
                $"Page {page} is past the last page ({maxPage}) a list across several saga types {shape} can reach at {pageSize} rows per page. Choose a saga type with the sagaType filter to page further.",
                maxPage);
        }
    }

    /// <summary>
    /// <paramref name="filter"/> restricted to <paramref name="types"/>: the one type as <c>SagaType</c> (every
    /// provider's cheapest path), several as <c>SagaTypes</c>.
    /// </summary>
    private static SagaListFilter For(SagaListFilter filter, IReadOnlyList<string> types, int page, int pageSize) => new()
    {
        Status = filter.Status,
        Statuses = filter.Statuses,
        SagaType = types.Count == 1 ? types[0] : null,
        SagaTypes = types.Count == 1 ? null : types,
        Kind = filter.Kind,
        Search = filter.Search,
        UpdatedSince = filter.UpdatedSince,
        UpdatedBefore = filter.UpdatedBefore,
        Page = page,
        PageSize = pageSize,
        SortBy = filter.SortBy,
        SortDescending = filter.SortDescending,
    };

    /// <summary>
    /// <paramref name="result"/> without the rows outside the scope. The provider counted those rows in its
    /// total too, so the ones removed from this page come off <c>TotalCount</c> (never below the rows this page
    /// returns plus the ones before it). Out-of-scope rows on other pages cannot be seen here, so for such a
    /// provider the total stays an upper bound.
    /// </summary>
    private static PagedResult<SagaSummary> WithinScope(PagedResult<SagaSummary> result, SagaTypeScope scope)
    {
        var kept = result.Items.Where(s => scope.Contains(s.SagaType)).ToList();
        var removed = result.Items.Count - kept.Count;
        if (removed == 0)
            return result;

        var floor = Math.Min(((long)Math.Max(result.Page, 1) - 1) * result.PageSize + kept.Count, int.MaxValue);
        return result with { Items = kept, TotalCount = (int)Math.Max(result.TotalCount - (long)removed, floor) };
    }

    private static PagedResult<SagaSummary> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}

/// <summary>
/// The names of the saga types that have run, read with <see cref="ISagaSummaryReader.GetSagaTypesAsync"/> at
/// most once per <see cref="Lifetime"/>: on EF Core that is a DISTINCT no index covers and on MongoDB a group
/// over the whole collection, too heavy to repeat for every list request of a caller with a long scope.
/// </summary>
public sealed class SagaTypeNameCache(TimeProvider timeProvider)
{
    /// <summary>How long one read is reused. A type that starts running meanwhile shows up at the next read.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private Snapshot? _snapshot;

    /// <summary>The distinct names, from the cache when it is fresh, otherwise read through <paramref name="reader"/>.</summary>
    public async Task<IReadOnlyList<string>> GetAsync(ISagaSummaryReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var now = timeProvider.GetUtcNow();
        if (Volatile.Read(ref _snapshot) is { } cached && now - cached.ReadAtUtc < Lifetime)
            return cached.Names;

        var types = await reader.GetSagaTypesAsync(cancellationToken);
        var names = types.Select(t => t.SagaType).Distinct(StringComparer.Ordinal).ToList();
        Volatile.Write(ref _snapshot, new Snapshot(now, names));
        return names;
    }

    private sealed record Snapshot(DateTimeOffset ReadAtUtc, IReadOnlyList<string> Names);
}
