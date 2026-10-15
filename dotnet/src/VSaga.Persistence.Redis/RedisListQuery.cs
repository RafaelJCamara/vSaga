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
        BeforeMicroseconds = filter.UpdatedBefore is { } before ? RedisTimestamps.ToMicroseconds(before) : null;
        ByStatus = filter.SortBy == SagaSortColumn.Status;
        // EfCoreSagaSummaryReader.ApplySort: a Status sort follows the flag; on UpdatedAt only an explicit
        // ascending sort ascends, and the default (no column) descends whatever the flag says.
        Descending = ByStatus ? filter.SortDescending : filter.SortBy != SagaSortColumn.UpdatedAt || filter.SortDescending;

        // Status and Statuses both restrict the status: a row must satisfy both. One status left is that
        // status's own index (the cheapest, and every existing path); two or more are a union of buckets.
        var statuses = EffectiveStatuses(filter);
        if (statuses is { Count: 0 })
            MatchesNothing = true;
        StatusFilter = statuses is { Count: 1 } ? statuses[0] : null;
        StatusSet = statuses is { Count: > 1 } ? statuses : null;

        // SagaType and SagaTypes both restrict the type: a row must satisfy both. A set that reduces to one name
        // is that name's own index (the cheapest, ranked), so only two or more names need the member-suffix and
        // union paths.
        var sagaType = string.IsNullOrWhiteSpace(filter.SagaType) ? null : filter.SagaType;
        if (filter.SagaTypes is { } sagaTypes)
        {
            // A null name matches nothing in every other provider, so it is dropped here rather than read as "no type".
            var names = new HashSet<string>(sagaTypes.Where(name => name is not null), StringComparer.Ordinal);
            if (sagaType is not null)
            {
                MatchesNothing |= !names.Contains(sagaType);
            }
            else if (names.Count == 0)
            {
                MatchesNothing = true;
            }
            else if (names.Count == 1)
            {
                sagaType = names.First();
            }
            else
            {
                TypeSet = names;
                TypeIndexes = [.. names.Select(keys.IndexType)];
            }
        }

        var indexes = new List<RedisKey>(3);
        if (sagaType is not null)
            indexes.Add(keys.IndexType(sagaType));
        if (StatusFilter is { } status)
            indexes.Add(keys.IndexStatus(status));
        if (filter.Kind is { } kind)
            indexes.Add(keys.IndexKind(kind));
        EqualityIndexes = indexes;

        // For the Status sort, each status bucket is itself the driving index, so the status filter (if
        // any) only decides which buckets are walked and the other indexes intersect with each bucket.
        NonStatusIndexes = indexes.Where(i => StatusFilter is not { } s || i != keys.IndexStatus(s)).ToList();
        Driving = indexes.Count > 0 ? indexes[0] : keys.IndexUpdated;
    }

    /// <summary>True when the filter cannot match any row (an empty set, or a type outside the set): the answer is an empty page.</summary>
    public bool MatchesNothing { get; }

    /// <summary>Two or more saga type names every row must be one of; null when the filter names at most one type.</summary>
    public HashSet<string>? TypeSet { get; }

    /// <summary>The per-type indexes of <see cref="TypeSet"/>, empty when there is none.</summary>
    public IReadOnlyList<RedisKey> TypeIndexes { get; } = [];

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;

    public string? Search { get; }

    public long? SinceMicroseconds { get; }

    public long? BeforeMicroseconds { get; }

    public bool ByStatus { get; }

    public bool Descending { get; }

    /// <summary>The one status every row must have, when the filter leaves exactly one; null otherwise.</summary>
    public SagaStatus? StatusFilter { get; }

    /// <summary>Two or more statuses a row must be one of, in <see cref="SagaStatus"/>'s declared order; null when the filter leaves at most one.</summary>
    public IReadOnlyList<SagaStatus>? StatusSet { get; }

    public IReadOnlyList<RedisKey> EqualityIndexes { get; }

    public IReadOnlyList<RedisKey> NonStatusIndexes { get; }

    public RedisKey Driving { get; }

    /// <summary>The lower score bound: exclusive of the watermark's microsecond, which is exactly the contract's strict <c>&gt;</c> at storage resolution.</summary>
    public double MinScore => SinceMicroseconds ?? double.NegativeInfinity;

    /// <summary>The upper score bound: exclusive of <c>UpdatedBefore</c>'s microsecond, the strict <c>&lt;</c> at storage resolution as the lower bound is the strict <c>&gt;</c>.</summary>
    public double MaxScore => BeforeMicroseconds ?? double.PositiveInfinity;

    public Exclude Exclude =>
        (SinceMicroseconds is null ? Exclude.None : Exclude.Start) | (BeforeMicroseconds is null ? Exclude.None : Exclude.Stop);

    /// <summary>True when one sorted set answers the whole query by rank, with no client-side filtering.</summary>
    public bool IsRankable => Search is null && EqualityIndexes.Count <= 1 && TypeSet is null && StatusSet is null;

    /// <summary>
    /// True when a set of saga types alone (no status, kind or search) restricts an <c>UpdatedAt</c> list: each
    /// type has its own sorted set that answers its window by rank, and the windows merge, so the cost follows
    /// the page and not the number of rows the types hold.
    /// </summary>
    public bool IsTypeRankable => TypeSet is not null && Search is null && EqualityIndexes.Count == 0 && StatusSet is null;

    /// <summary>
    /// True when the saga type of <paramref name="member"/>, the part after its first <c>|</c>, is in
    /// <see cref="TypeSet"/>, or when there is no set.
    /// </summary>
    public bool InTypeSet(ReadOnlySpan<char> member)
    {
        if (TypeSet is null)
            return true;

        var separator = member.IndexOf('|');
        return separator >= 0 && TypeSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(member[(separator + 1)..]);
    }

    /// <summary>The buckets a Status sort walks, in <see cref="SagaStatus"/>'s declared order, reversed when descending.</summary>
    public IEnumerable<SagaStatus> StatusBuckets()
    {
        IEnumerable<SagaStatus> buckets = StatusFilter is { } only ? [only] : StatusSet ?? Enum.GetValues<SagaStatus>().Order().ToList();
        return Descending ? buckets.Reverse() : buckets;
    }

    /// <summary>
    /// The statuses a row may have once <see cref="SagaListFilter.Status"/> and <see cref="SagaListFilter.Statuses"/> are
    /// both applied, in declared order and without repeats; null when neither is set (no restriction), empty when
    /// they exclude every status.
    /// </summary>
    private static List<SagaStatus>? EffectiveStatuses(SagaListFilter filter)
    {
        var statuses = filter.Statuses?.Distinct().Order().ToList();
        if (filter.Status is not { } only)
            return statuses;

        return statuses is null ? [only] : statuses.Where(s => s == only).ToList();
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
