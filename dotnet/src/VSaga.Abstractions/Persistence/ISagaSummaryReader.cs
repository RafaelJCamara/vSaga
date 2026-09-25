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
    /// </para>
    /// <para>
    /// The total order is <b>per-provider</b> deterministic, not byte-identical across providers:
    /// <c>SagaType</c> sorts under database collation on EF Core versus
    /// <c>StringComparer.Ordinal</c> in-memory, and a relational provider orders <c>Guid</c> keys
    /// however its column type does — SQL Server's <c>uniqueidentifier</c>, a documented target,
    /// compares byte groups in a different order than <c>Comparer&lt;Guid&gt;.Default</c>. Nothing
    /// consumes a cross-provider-identical order, so the contract deliberately does not require one.
    /// </para>
    /// </remarks>
    Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default);

    Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Raw serialized business state (the TState JSON), for the dashboard's saga detail "Data" tab — generic access without knowing the concrete TState type.</summary>
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
}
