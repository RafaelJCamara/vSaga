using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Persistence;

/// <summary>
/// Saga-type-agnostic projection of one instance, for the dashboard's list/detail views.
/// <para>
/// <see cref="ParentSagaType"/>/<see cref="ParentCorrelationId"/> are deliberately positional and
/// non-optional rather than defaulted: every projection site has to decide what to put there. The
/// same fields also ride along inside the snapshot's serialized state for free, but that blob is not
/// queryable, so a "which sagas did this one start?" lookup needs them projected here.
/// </para>
/// </summary>
public sealed record SagaSummary(
    Guid CorrelationId,
    string SagaType,
    SagaKind Kind,
    string CurrentState,
    SagaStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version,
    string? ParentSagaType,
    Guid? ParentCorrelationId);

public sealed class SagaListFilter
{
    public SagaStatus? Status { get; init; }

    /// <summary>
    /// Keeps only instances whose <see cref="SagaSummary.Status"/> is one of these; null (the default) puts no
    /// limit on the status, so existing callers are unaffected. An empty collection matches nothing, and a
    /// repeated status counts once. When <see cref="Status"/> is also set, an instance must satisfy both, so
    /// the two together keep at most that one status. It composes with every other member as a filter does:
    /// applied before the count, the sort and the page, so <see cref="PagedResult{T}.TotalCount"/> is over the
    /// filtered set.
    /// <para>
    /// Added for the stuck-saga sweep, which asks for the <c>Running</c> and <c>Compensating</c> instances in
    /// one query. Every <see cref="ISagaSummaryReader"/> must honour it: a reader that ignores it returns rows
    /// of the other statuses, which the sweep would report as stuck. The <c>VSaga.Persistence.Conformance</c>
    /// suite fails such a reader.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<SagaStatus>? Statuses { get; init; }

    public string? SagaType { get; init; }

    /// <summary>
    /// Keeps only instances whose <see cref="SagaSummary.SagaType"/> is one of these names, compared exactly
    /// (ordinal: <c>ordersaga</c> is not <c>OrderSaga</c>); null (the default) puts no limit on the type, so
    /// existing callers are unaffected. An empty collection matches nothing. When <see cref="SagaType"/> is also
    /// set, an instance must satisfy both. It composes with every other member as a filter does: applied before
    /// sorting and paging, so the page and <see cref="PagedResult{T}.TotalCount"/> arithmetic is over the
    /// filtered set, and the order of the rows kept is the order they hold in the unfiltered list.
    /// <para>
    /// The dashboard serves a caller whose access names several saga types with one call carrying this, instead
    /// of one call per type, and has no fallback for a reader that ignores it: every
    /// <see cref="ISagaSummaryReader"/> must honour it. A reader that does not still never shows a caller a row
    /// outside their scope (the API drops those), but gives them pages that are mostly rows of other types and a
    /// <see cref="PagedResult{T}.TotalCount"/> over the wrong set. The <c>VSaga.Persistence.Conformance</c>
    /// suite fails such a reader.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string>? SagaTypes { get; init; }

    public SagaKind? Kind { get; init; }

    /// <summary>
    /// Case-insensitive substring match against <see cref="SagaSummary.SagaType"/> and
    /// <see cref="SagaSummary.CorrelationId"/>'s string form, each tested independently — a row
    /// matches if either does.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// Keeps only instances whose <see cref="SagaSummary.UpdatedAtUtc"/> is <em>strictly</em> greater
    /// than this value; null (the default) keeps everything, so existing callers are unaffected.
    /// <para>
    /// Strictly-greater, not <c>&gt;=</c>, deliberately: this exists to serve the dashboard's change
    /// poller, whose watermark is the timestamp of a row it already pushed. With <c>&gt;=</c> that row
    /// would come back — and be re-pushed — on every subsequent tick, forever. Implementations must
    /// apply it before sorting and paging so the page/total arithmetic is over the filtered set.
    /// </para>
    /// </summary>
    public DateTimeOffset? UpdatedSince { get; init; }

    /// <summary>
    /// Keeps only instances whose <see cref="SagaSummary.UpdatedAtUtc"/> is <em>strictly</em> less than this
    /// value; null (the default) keeps everything, so existing callers are unaffected. The mirror of
    /// <see cref="UpdatedSince"/>, and composable with it: both set keeps the open interval between them.
    /// <para>
    /// Strictly-less, not <c>&lt;=</c>, so that <c>UpdatedSince = t</c> and <c>UpdatedBefore = t</c> split the
    /// rows at <c>t</c> with the row at exactly <c>t</c> in neither. The comparison is at the provider's
    /// declared timestamp resolution, as <see cref="UpdatedSince"/>'s is. Implementations must apply it before
    /// sorting and paging so the page/total arithmetic is over the filtered set. The stuck-saga sweep asks for
    /// the instances idle since before <c>now - threshold</c>, oldest first.
    /// </para>
    /// </summary>
    public DateTimeOffset? UpdatedBefore { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    /// <summary>Null keeps the default ordering (most-recently-updated first).</summary>
    public SagaSortColumn? SortBy { get; init; }

    public bool SortDescending { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
