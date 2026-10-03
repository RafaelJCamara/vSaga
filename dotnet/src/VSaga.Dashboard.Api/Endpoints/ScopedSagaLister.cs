using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// A list across several saga types that the merge refuses to serve: too many visible types, or a page too
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
/// (docs/design/dashboard-usability-and-access.md §8.7, ADR 0006 decision 7). <see cref="ISagaSummaryReader.ListAsync"/>
/// filters by at most one saga type, so a caller who sees several is served by merging one stream per type in
/// the API, inside bounds that keep the cost of one request in check.
/// </summary>
public sealed class ScopedSagaLister(ISagaSummaryReader reader, SagaTypeNameCache typeNames)
{
    /// <summary>
    /// The most saga types a list served from a rank can merge, and the most scoped names such a list uses as
    /// they stand without asking the store which types have run.
    /// </summary>
    public const int MaxMergedTypes = 50;

    /// <summary>The deepest row (<c>page × pageSize</c>) a merged list served from a rank can reach.</summary>
    public const int MaxMergeDepth = 10_000;

    /// <summary>
    /// The most saga types a merged list of any other shape can cover, and the most scoped names such a list
    /// uses as they stand.
    /// </summary>
    public const int MaxUnrankedTypes = 10;

    /// <summary>
    /// The deepest row any other shape can reach: one chunk per type, so no stream is ever refilled. On Redis
    /// such a request reads every member of every visible type (ADR 0006).
    /// </summary>
    public const int MaxUnrankedDepth = 500;

    /// <summary>The largest refill a merged stream asks its provider for; the list endpoint's own page cap.</summary>
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
        if (types.Count == 0)
            return Empty(page, pageSize);
        if (types.Count == 1)
            return WithinScope(await reader.ListAsync(For(filter, types[0], filter.Page, filter.PageSize), cancellationToken), scope);

        CheckBounds(types.Count, page, pageSize, rankServed);

        var merged = await MergeAsync(filter, types, page, pageSize, rankServed, cancellationToken);
        return WithinScope(merged, scope);
    }

    /// <summary>
    /// The shape Redis serves from its per-type rank (<c>RedisListQuery.IsRankable</c>), so paging deeper there
    /// costs a range read, not a scan: the <c>UpdatedAt</c> sort (the default included) with no status or kind
    /// filter and no search. Redis never serves a search from a rank, and EF Core matches one with <c>LIKE</c>,
    /// so a searched list gets the tighter bounds of every other shape.
    /// </summary>
    internal static bool IsRankServed(SagaListFilter filter) =>
        filter.SortBy is null or SagaSortColumn.UpdatedAt && filter.Status is null && filter.Kind is null && string.IsNullOrWhiteSpace(filter.Search);

    /// <summary>
    /// The order the merge picks heads in: the providers' own order for the requested sort arm
    /// (<c>EfCoreSagaSummaryReader.ApplySort</c>), then saga type, ordinal ascending, across types. Rows of one
    /// type are never compared with each other, so each provider's own tie-break inside a type stands.
    /// </summary>
    internal static Comparison<SagaSummary> ComparerFor(SagaListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Comparison<SagaSummary> arm = filter.SortBy switch
        {
            SagaSortColumn.Status when filter.SortDescending => (a, b) => StatusThenNewest(b.Status, a.Status, a, b),
            SagaSortColumn.Status => (a, b) => StatusThenNewest(a.Status, b.Status, a, b),
            SagaSortColumn.UpdatedAt when !filter.SortDescending => (a, b) => a.UpdatedAtUtc.CompareTo(b.UpdatedAtUtc),
            _ => (a, b) => b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc),
        };

        return (a, b) =>
        {
            var byArm = arm(a, b);
            return byArm != 0 ? byArm : string.CompareOrdinal(a.SagaType, b.SagaType);
        };
    }

    private static int StatusThenNewest(SagaStatus first, SagaStatus second, SagaSummary a, SagaSummary b)
    {
        var byStatus = ((int)first).CompareTo((int)second);
        return byStatus != 0 ? byStatus : b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc);
    }

    /// <summary>
    /// The non-blank saga types the scope names, ordinal ascending: as they stand when there are at most
    /// <paramref name="typeBound"/> of them, the request shape's type bound (types that have not run yet just
    /// give an empty stream), otherwise the types that have run, read through a short-lived cache, kept when
    /// the scope covers them. So a caller granted more named types than the shape can merge is refused only
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
    /// The k-way merge. Each type's stream is primed with one chunk, read one after another (the reader is
    /// scoped, and an EF Core context cannot run queries in parallel): <c>pageSize</c> rows for the rank-served
    /// shape, refilled with doubling chunks up to <see cref="MaxChunk"/>; <c>page × pageSize</c> rows for every
    /// other shape, which the bounds keep to one chunk. <c>TotalCount</c> is the sum of the primes' counts. A
    /// row that a refill returns again, because its saga was updated between two reads, is skipped.
    /// </summary>
    private async Task<PagedResult<SagaSummary>> MergeAsync(
        SagaListFilter filter, IReadOnlyList<string> types, int page, int pageSize, bool rankServed, CancellationToken cancellationToken)
    {
        var prime = rankServed ? pageSize : page * pageSize;
        var streams = new List<TypeStream>(types.Count);
        var totalCount = 0;
        foreach (var type in types)
        {
            var stream = new TypeStream(type);
            totalCount += await FillAsync(stream, filter, prime, cancellationToken);
            streams.Add(stream);
        }

        var compare = ComparerFor(filter);
        var emitted = new HashSet<(string SagaType, Guid CorrelationId)>();
        var toSkip = (page - 1) * pageSize;
        var items = new List<SagaSummary>(pageSize);
        while (items.Count < pageSize && await NextHeadAsync(streams, filter, compare, cancellationToken) is { } head)
        {
            var row = head.Rows.Dequeue();
            if (!emitted.Add((row.SagaType, row.CorrelationId)))
                continue;
            if (toSkip > 0)
                toSkip--;
            else
                items.Add(row);
        }

        return new PagedResult<SagaSummary>(items, page, pageSize, totalCount);
    }

    /// <summary>The stream whose head comes first, refilling any that ran dry; null when every stream is drained.</summary>
    private async Task<TypeStream?> NextHeadAsync(List<TypeStream> streams, SagaListFilter filter, Comparison<SagaSummary> compare, CancellationToken cancellationToken)
    {
        TypeStream? best = null;
        foreach (var stream in streams)
        {
            if (stream.Rows.Count == 0 && !stream.Drained)
                await FillAsync(stream, filter, NextChunk(stream.Fetched), cancellationToken);
            if (stream.Rows.Count > 0 && (best is null || compare(stream.Rows.Peek(), best.Rows.Peek()) < 0))
                best = stream;
        }

        return best;
    }

    /// <summary>
    /// The next refill size: as many rows as the stream has read so far, so each refill doubles what it holds,
    /// up to <see cref="MaxChunk"/>, and always a divisor of the rows read so far, so the refill is a whole
    /// provider page that starts exactly where the stream stopped.
    /// </summary>
    internal static int NextChunk(int fetched)
    {
        var chunk = Math.Clamp(fetched, 1, MaxChunk);
        while (fetched % chunk != 0)
            chunk--;
        return chunk;
    }

    /// <summary>Reads the stream's next <paramref name="chunk"/> rows; returns the provider's total for the type.</summary>
    private async Task<int> FillAsync(TypeStream stream, SagaListFilter filter, int chunk, CancellationToken cancellationToken)
    {
        var result = await reader.ListAsync(For(filter, stream.SagaType, (stream.Fetched / chunk) + 1, chunk), cancellationToken);
        foreach (var row in result.Items)
            stream.Rows.Enqueue(row);

        stream.Fetched += result.Items.Count;
        stream.Drained = result.Items.Count < chunk;
        return result.TotalCount;
    }

    private static SagaListFilter For(SagaListFilter filter, string sagaType, int page, int pageSize) => new()
    {
        Status = filter.Status,
        SagaType = sagaType,
        Kind = filter.Kind,
        Search = filter.Search,
        UpdatedSince = filter.UpdatedSince,
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

    /// <summary>One saga type's rows in the provider's order: those read and not yet merged, and how far the reads got.</summary>
    private sealed class TypeStream(string sagaType)
    {
        public string SagaType { get; } = sagaType;

        public Queue<SagaSummary> Rows { get; } = new();

        public int Fetched { get; set; }

        public bool Drained { get; set; }
    }
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
