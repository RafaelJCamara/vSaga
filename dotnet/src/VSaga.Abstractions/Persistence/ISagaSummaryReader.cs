using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Persistence;

public sealed record SagaTypeInfo(string SagaType, SagaKind Kind);

/// <summary>
/// Non-generic, cross-saga-type read access. Powers the dashboard's saga list/detail views without
/// needing to know each saga's concrete TState type.
/// </summary>
public interface ISagaSummaryReader
{
    /// <summary>
    /// One page of instances matching <paramref name="filter"/>, sorted by its
    /// <see cref="SagaListFilter.SortBy"/>/<see cref="SagaListFilter.SortDescending"/> (default:
    /// most-recently-updated first).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every sort arm — the default included — applies a <b>stable total order</b>: a deterministic
    /// tiebreak follows the requested column so that, over unchanged data, fetching the same page
    /// twice returns identical rows and walking consecutive pages visits every matching row exactly
    /// once — no skips, no repeats. Without a tiebreak, rows tying on the sort column can swap sides
    /// of a page boundary between two fetches — and the dashboard's change poller pages through
    /// exactly such results, so a row swapping across its boundary is a live update silently dropped.
    /// The final tiebreak is on each row's own identity, so the relative order of two matching rows
    /// depends only on their own values: writes to rows outside the result never reorder the rows inside
    /// it, which is what lets the poller keep paging while other sagas advance.
    /// </para>
    /// <para>
    /// The total order is <b>per-provider</b> deterministic, not byte-identical across providers:
    /// <c>SagaType</c> sorts under database collation on EF Core versus
    /// <c>StringComparer.Ordinal</c> in-memory, and a relational provider orders <c>Guid</c> keys
    /// however its column type does — SQL Server's <c>uniqueidentifier</c>, a documented target,
    /// compares byte groups in a different order than <c>Comparer&lt;Guid&gt;.Default</c>. Nothing
    /// consumes a cross-provider-identical order, so the contract deliberately does not require one.
    /// </para>
    /// <para>
    /// Every filter member of <see cref="SagaListFilter"/> (<c>Status</c>, <c>Statuses</c>, <c>SagaType</c>,
    /// <c>SagaTypes</c>, <c>Kind</c>, <c>Search</c>, <c>UpdatedSince</c>, <c>UpdatedBefore</c>) must be honoured by the provider, applied before
    /// the count, the sort and the page, so that <see cref="PagedResult{T}.TotalCount"/> is over the
    /// filtered set. That includes <see cref="SagaListFilter.SagaTypes"/>, which was added after the others:
    /// it has no opt-out and nothing in the dashboard falls back to one call per type, so a reader written
    /// before it existed must add it. A reader that ignores it never shows a caller a row outside their
    /// scope (the API drops those), but gives a caller scoped to a few saga types pages that are mostly
    /// rows of other types, and a total over the wrong set. The <c>VSaga.Persistence.Conformance</c>
    /// suite fails such a reader.
    /// </para>
    /// </remarks>
    Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default);

    Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Raw serialized business state (the TState JSON), for the dashboard's saga detail page (the Saga data bar's end state and comparison) — generic access without knowing the concrete TState type.</summary>
    Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every saga instance sharing <paramref name="correlationId"/>, across all saga types. Powers the
    /// dashboard's "this correlation id is also tracked by N other sagas" cross-links, and is what a
    /// caller holding only a correlation id (e.g. an old bookmarked URL) uses to resolve it to a
    /// concrete instance. Ordinarily returns exactly one; more than one means several saga types are
    /// tracking the same business transaction.
    /// </summary>
    Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every saga instance started by <paramref name="parentSagaType"/>/<paramref name="parentCorrelationId"/>
    /// via <c>ISagaContext.StartChildAsync</c>, oldest first. Empty for the usual case of a saga that
    /// started none.
    /// <para>
    /// One level only — a grandchild is a child of its own parent and is not returned here. Callers
    /// wanting a whole tree walk it themselves; nothing in the engine limits the depth, so a recursive
    /// query would need its own cycle handling and that is not something a single instance lookup should
    /// be quietly doing.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct saga types that have ever run, derived from persisted data rather than requiring the
    /// dashboard process to register every saga definition — powers the Orchestrated/Choreographed
    /// badges and the list view's type filter dropdown.
    /// </summary>
    Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// How many instances are in <paramref name="status"/>, per saga type, keyed by the saga type name as stored (the key
    /// comparer is ordinal; a database whose collation folds two names that differ only by case folds them here too). A
    /// type with none is <b>absent</b>, not zero, so an empty store answers an empty dictionary. Powers the <c>vsaga.saga.running</c> gauge (ADR 0013), which reads it on an interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A count, not a page: a provider answers it from its own aggregate (a grouped query, a set-intersection
    /// cardinality) without sorting, paging or materialising a row, which is the whole reason it is a member of its
    /// own and not a <see cref="ListAsync"/> call. It sees what a <see cref="ListAsync"/> filtered to the same status
    /// sees, so the counts sum to <see cref="PagedResult{T}.TotalCount"/> of that list.
    /// </para>
    /// <para>
    /// The default implementation reads <see cref="GetSagaTypesAsync"/> and takes the <c>TotalCount</c> of one
    /// <c>ListAsync(Status, SagaType, PageSize = 1)</c> per type. It is correct for any reader that honours
    /// <see cref="SagaListFilter"/>, which is what lets a reader written before this member existed keep compiling,
    /// and it is expensive (one sort and one page read per type, and on a provider that materialises a filtered
    /// list, the whole list). Every provider in this repository overrides it, and the
    /// <c>VSaga.Persistence.Conformance</c> suite holds the default and each override to the same answers.
    /// </para>
    /// </remarks>
    async Task<IReadOnlyDictionary<string, long>> CountByTypeAsync(SagaStatus status, CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var sagaType in (await GetSagaTypesAsync(cancellationToken)).Select(type => type.SagaType))
        {
            var page = await ListAsync(new SagaListFilter { Status = status, SagaType = sagaType, PageSize = 1 }, cancellationToken);
            if (page.TotalCount > 0)
                counts[sagaType] = page.TotalCount;
        }

        return counts;
    }
}
