using System.Text.Json.Nodes;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// The read side over hand-maintained indexes: one sorted set per status, per kind and per saga type,
/// one over every instance, all scored by updated-microseconds with the member
/// <c>{correlationId}|{sagaType}</c>. Every index write happens inside the persist script that writes
/// the snapshot, so drift is structurally impossible; every read here is read-only (<c>ZINTER</c>, never
/// <c>ZINTERSTORE</c>).
/// </summary>
/// <remarks>
/// <para>
/// The member string is the design's quiet win: it carries both fields <c>Search</c> matches, so a
/// search never reads a snapshot, and the tie order within one microsecond is the members' own
/// lexicographic order -- total and deterministic, which is the stable order clause 9 requires. It is
/// per-provider: ties on the sort column break by the member string, in the direction of the walk.
/// </para>
/// <para>
/// Three shapes: a sort by <c>UpdatedAt</c> over at most one equality filter and no search is answered by
/// rank from one sorted set, as the dashboard's change poller needs; a <c>Status</c> sort walks the seven
/// buckets in enum order; anything else materialises the candidate members, intersecting indexes
/// server-side, and pages client-side -- bounded, for a search, by
/// <see cref="VSagaRedisOptions.MaxSearchScanMembers"/>. The provider never returns a short page while
/// more matching rows exist, and <c>TotalCount</c> is exact over the same filtered set: the poller reads a
/// short page as "drained".
/// </para>
/// </remarks>
public sealed class RedisSagaSummaryReader(RedisConnection connection, RedisKeySpace keys, VSagaRedisOptions options, RedisPersistScripts scripts)
    : ISagaSummaryReader, ISagaAdminStore
{
    public async Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var query = RedisListQuery.From(filter, keys);

        (List<string> Members, int Total) page;
        if (query.ByStatus)
            page = await ListByStatusAsync(db, query);
        else if (query.IsRankable)
            page = await ListByRankAsync(db, query);
        else
            page = await ListMaterializedAsync(db, query);

        return new PagedResult<SagaSummary>(await LoadSummariesAsync(db, page.Members), query.Page, query.PageSize, page.Total);
    }

    /// <summary>One sorted set, one score range, offset paging: the change poller's shape, O(log n + page).</summary>
    private static async Task<(List<string> Members, int Total)> ListByRankAsync(IDatabase db, RedisListQuery query)
    {
        var total = (int)await db.SortedSetLengthAsync(query.Driving, query.MinScore, double.PositiveInfinity, query.Exclude);
        var members = await db.SortedSetRangeByScoreAsync(query.Driving, query.MinScore, double.PositiveInfinity, query.Exclude,
            query.Descending ? Order.Descending : Order.Ascending, query.Skip, query.PageSize);

        return (members.Select(m => m.ToString()).ToList(), total);
    }

    private async Task<(List<string> Members, int Total)> ListMaterializedAsync(IDatabase db, RedisListQuery query)
    {
        var candidates = await MaterializeAsync(db, query, query.EqualityIndexes.Count == 0 ? [keys.IndexUpdated] : query.EqualityIndexes);
        if (query.Descending)
            candidates.Reverse();

        var page = candidates.Skip(query.Skip).Take(query.PageSize).Select(e => e.Element.ToString()).ToList();
        return (page, candidates.Count);
    }

    /// <summary>
    /// The seven buckets in <see cref="SagaStatus"/>'s declared order, each read newest-first so the
    /// <c>UpdatedAtUtc</c> tiebreak descends in both directions, as EF Core's does; paging across
    /// buckets is arithmetic over their counts.
    /// </summary>
    private async Task<(List<string> Members, int Total)> ListByStatusAsync(IDatabase db, RedisListQuery query)
    {
        var window = new PageWindow(query.Skip, query.PageSize);
        var total = 0;

        foreach (var status in query.StatusBuckets())
        {
            var bucket = keys.IndexStatus(status);
            if (query.Search is null && query.NonStatusIndexes.Count == 0)
            {
                var count = (int)await db.SortedSetLengthAsync(bucket, query.MinScore, double.PositiveInfinity, query.Exclude);
                var (offset, take) = window.Slice(count);
                if (take > 0)
                {
                    var members = await db.SortedSetRangeByScoreAsync(bucket, query.MinScore, double.PositiveInfinity, query.Exclude, Order.Descending, offset, take);
                    window.Members.AddRange(members.Select(m => m.ToString()));
                }

                total += count;
            }
            else
            {
                var candidates = await MaterializeAsync(db, query, [bucket, .. query.NonStatusIndexes]);
                candidates.Reverse();
                var (offset, take) = window.Slice(candidates.Count);
                window.Members.AddRange(candidates.Skip(offset).Take(take).Select(e => e.Element.ToString()));
                total += candidates.Count;
            }
        }

        return (window.Members, total);
    }

    /// <summary>
    /// Every candidate of the given indexes, ascending by score then member: a server-side read-only
    /// intersection when there are several, one range read when there is one. <c>UpdatedSince</c> and
    /// <c>Search</c> are applied here, client-side, and a search over more members than the bound throws
    /// rather than truncating.
    /// </summary>
    private async Task<List<SortedSetEntry>> MaterializeAsync(IDatabase db, RedisListQuery query, IReadOnlyList<RedisKey> indexes)
    {
        var entries = indexes.Count >= 2
            ? await db.SortedSetCombineWithScoresAsync(SetOperation.Intersect, indexes.ToArray(), weights: null, Aggregate.Max)
            : await db.SortedSetRangeByScoreWithScoresAsync(indexes[0], query.MinScore, double.PositiveInfinity, query.Exclude);

        IEnumerable<SortedSetEntry> filtered = entries;
        if (indexes.Count >= 2 && query.SinceMicroseconds is { } since)
            filtered = filtered.Where(e => e.Score > since);

        if (query.Search is { } search)
        {
            if (entries.Length > options.MaxSearchScanMembers)
                throw new RedisSearchScanLimitExceededException(entries.Length, options.MaxSearchScanMembers);

            filtered = filtered.Where(e => RedisListQuery.Matches(e.Element.ToString(), search));
        }

        var candidates = filtered.ToList();
        candidates.Sort(static (a, b) =>
        {
            var byScore = a.Score.CompareTo(b.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Element.ToString(), b.Element.ToString());
        });
        return candidates;
    }

    private async Task<List<SagaSummary>> LoadSummariesAsync(IDatabase db, IReadOnlyList<string> members)
    {
        var reads = members.Select(member =>
        {
            var (correlationId, sagaType) = RedisKeySpace.ParseMember(member);
            return db.HashGetAsync(keys.Saga(correlationId, sagaType), RedisSnapshotRow.Fields);
        });

        var rows = await Task.WhenAll(reads);
        return rows.Select(RedisSnapshotRow.FromValues).Where(r => r is not null).Select(r => r!.ToSummary()).ToList();
    }

    public async Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var row = RedisSnapshotRow.FromValues(await db.HashGetAsync(keys.Saga(correlationId, sagaType), RedisSnapshotRow.Fields));
        return row?.ToSummary();
    }

    public async Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var dataJson = await db.HashGetAsync(keys.Saga(correlationId, sagaType), RedisSnapshotRow.DataJsonField);
        return dataJson.IsNull ? null : dataJson.ToString();
    }

    public async Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var sagaTypes = await db.SetMembersAsync(keys.IndexCorrelation(correlationId));
        var summaries = await LoadSummariesAsync(db, sagaTypes.Select(t => RedisKeySpace.Member(correlationId, t.ToString())).ToList());
        return summaries.OrderBy(s => s.SagaType, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var members = await db.SortedSetRangeByRankAsync(keys.IndexParent(parentSagaType, parentCorrelationId));
        var children = await LoadSummariesAsync(db, members.Select(m => m.ToString()).ToList());
        return children.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.SagaType, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var types = await db.HashGetAllAsync(keys.IndexTypes);
        return types
            .Select(t => new SagaTypeInfo(t.Name.ToString(), (SagaKind)(int)t.Value))
            .OrderBy(t => t.SagaType, StringComparer.Ordinal)
            .ToList();
    }

    /// <remarks>
    /// A single-shot read, patch, compare-and-set in C#: the blob is opaque bytes to Redis (no
    /// <c>cjson</c>), so the four engine-owned fields are patched here by exact property name -- the
    /// sibling of <c>EfCoreSagaSummaryReader.ResetStateAsync</c>, which must stay byte-identical in what
    /// it patches -- and written back through the persist script under the version guard. Never
    /// retried: a lost race is reported as <see cref="SagaConcurrencyException"/> (clause 7).
    /// </remarks>
    public async Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var row = RedisSnapshotRow.FromValues(await db.HashGetAsync(keys.Saga(correlationId, sagaType), RedisSnapshotRow.Fields))
                  ?? throw new SagaNotFoundException(sagaType, correlationId);

        if (row.Version != expectedVersion)
            throw new SagaConcurrencyException(sagaType, correlationId, expectedVersion);

        var newVersion = expectedVersion + 1;
        var node = JsonNode.Parse(row.DataJson)!.AsObject();
        node["CurrentState"] = currentState;
        node["Status"] = (int)status;
        node["Version"] = newVersion;
        node["UpdatedAtUtc"] = updatedAtUtc;

        var reset = row with
        {
            CurrentState = currentState,
            Status = status,
            Version = newVersion,
            DataJson = node.ToJsonString(),
            UpdatedMicroseconds = RedisTimestamps.ToMicroseconds(updatedAtUtc),
        };

        await scripts.UpdateAsync(RedisPersistScripts.Head.From(row), reset, expectedVersion, unitOfWork: null, cancellationToken);
    }

    /// <summary>Tracks how much of a page's offset and size the buckets walked so far have consumed.</summary>
    private sealed class PageWindow(int skip, int take)
    {
        private int _skip = skip;

        public List<string> Members { get; } = new(take);

        /// <summary>Given a bucket holding <paramref name="available"/> rows, the (offset, count) of them this page takes.</summary>
        public (int Offset, int Count) Slice(int available)
        {
            var wanted = take - Members.Count;
            if (wanted <= 0)
                return (0, 0);

            if (_skip >= available)
            {
                _skip -= available;
                return (0, 0);
            }

            var offset = _skip;
            _skip = 0;
            return (offset, Math.Min(wanted, available - offset));
        }
    }
}
