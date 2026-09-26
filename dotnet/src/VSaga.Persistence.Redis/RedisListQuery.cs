using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// A <see cref="SagaListFilter"/> resolved against the key space: which indexes drive it, in
/// selectivity order (saga type, then status, then kind), and how the rest is evaluated.
/// </summary>
internal sealed class RedisListQuery
{
    private RedisListQuery(SagaListFilter filter, RedisKeySpace keys)
    {
        Page = Math.Max(filter.Page, 1);
        PageSize = Math.Max(filter.PageSize, 1);
        Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search;
        SinceMicroseconds = filter.UpdatedSince is { } since ? RedisTimestamps.ToMicroseconds(since) : null;
        ByStatus = filter.SortBy == SagaSortColumn.Status;
        // EfCoreSagaSummaryReader.ApplySort: a Status sort follows the flag; on UpdatedAt only an explicit
        // ascending sort ascends, and the default (no column) descends whatever the flag says.
        Descending = ByStatus ? filter.SortDescending : filter.SortBy != SagaSortColumn.UpdatedAt || filter.SortDescending;
        StatusFilter = filter.Status;

        var indexes = new List<RedisKey>(3);
        if (!string.IsNullOrWhiteSpace(filter.SagaType))
            indexes.Add(keys.IndexType(filter.SagaType));
        if (filter.Status is { } status)
            indexes.Add(keys.IndexStatus(status));
        if (filter.Kind is { } kind)
            indexes.Add(keys.IndexKind(kind));
        EqualityIndexes = indexes;

        // For the Status sort, each status bucket is itself the driving index, so the status filter (if
        // any) only decides which buckets are walked and the other indexes intersect with each bucket.
        NonStatusIndexes = indexes.Where(i => filter.Status is not { } s || i != keys.IndexStatus(s)).ToList();
        Driving = indexes.Count > 0 ? indexes[0] : keys.IndexUpdated;
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;

    public string? Search { get; }

    public long? SinceMicroseconds { get; }

    public bool ByStatus { get; }

    public bool Descending { get; }

    public SagaStatus? StatusFilter { get; }

    public IReadOnlyList<RedisKey> EqualityIndexes { get; }

    public IReadOnlyList<RedisKey> NonStatusIndexes { get; }

    public RedisKey Driving { get; }

    /// <summary>The lower score bound: exclusive of the watermark's microsecond, which is exactly the contract's strict <c>&gt;</c> at storage resolution.</summary>
    public double MinScore => SinceMicroseconds ?? double.NegativeInfinity;

    public Exclude Exclude => SinceMicroseconds is null ? Exclude.None : Exclude.Start;

    /// <summary>True when one sorted set answers the whole query by rank, with no client-side filtering.</summary>
    public bool IsRankable => Search is null && EqualityIndexes.Count <= 1;

    /// <summary>The buckets a Status sort walks, in <see cref="SagaStatus"/>'s declared order, reversed when descending.</summary>
    public IEnumerable<SagaStatus> StatusBuckets()
    {
        IEnumerable<SagaStatus> buckets = StatusFilter is { } only ? [only] : Enum.GetValues<SagaStatus>().Order();
        return Descending ? buckets.Reverse() : buckets;
    }

    public static RedisListQuery From(SagaListFilter filter, RedisKeySpace keys) => new(filter, keys);

    /// <summary>
    /// The search predicate over a member string, without a snapshot read: the member carries both
    /// fields <c>Search</c> matches, tested independently and case-insensitively (clause 9).
    /// </summary>
    public static bool Matches(string member, string search)
    {
        var separator = member.IndexOf('|', StringComparison.Ordinal);
        return member.AsSpan(0, separator).Contains(search, StringComparison.OrdinalIgnoreCase)
               || member.AsSpan(separator + 1).Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}
