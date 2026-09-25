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

    public string? SagaType { get; init; }

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

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    /// <summary>Null keeps the default ordering (most-recently-updated first).</summary>
    public SagaSortColumn? SortBy { get; init; }

    public bool SortDescending { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
