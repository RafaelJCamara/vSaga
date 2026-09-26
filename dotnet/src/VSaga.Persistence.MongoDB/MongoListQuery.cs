using System.Text.RegularExpressions;
using VSaga.Abstractions.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// A <see cref="SagaListFilter"/> as a MongoDB filter and sort. The filter is one <c>find</c> predicate;
/// the sort is a total order whose tiebreakers are the row's own identity, in the direction of the lead
/// key so the supporting index can serve it (see <see cref="MongoIndexes"/>).
/// </summary>
internal static class MongoListQuery
{
    private static readonly FilterDefinitionBuilder<SagaInstanceDocument> Filter = Builders<SagaInstanceDocument>.Filter;
    private static readonly SortDefinitionBuilder<SagaInstanceDocument> Sort = Builders<SagaInstanceDocument>.Sort;

    public static FilterDefinition<SagaInstanceDocument> ToFilter(SagaListFilter filter)
    {
        var predicate = Filter.Empty;
        if (filter.Status is { } status)
            predicate &= Filter.Eq(d => d.Status, status);
        if (filter.Kind is { } kind)
            predicate &= Filter.Eq(d => d.Kind, kind);
        if (!string.IsNullOrWhiteSpace(filter.SagaType))
            predicate &= Filter.Eq(d => d.SagaType, filter.SagaType);
        // Strictly greater-than, on the exact ticks (SagaListFilter.UpdatedSince): applied before the
        // count and the page alike, so TotalCount describes the filtered set.
        if (filter.UpdatedSince is { } since)
            predicate &= Filter.Gt(d => d.UpdatedAtTicks, MongoTimestamps.ToTicks(since));
        if (!string.IsNullOrWhiteSpace(filter.Search))
            predicate &= Filter.Or(Filter.Regex(d => d.SagaTypeLower, SearchPattern(filter.Search)), Filter.Regex(d => d.CorrelationId, SearchPattern(filter.Search)));

        return predicate;
    }

    /// <summary>
    /// The search term as a non-anchored substring pattern over the lower-cased fields: two independent
    /// matches, one per field, exactly as the contract states, and never one concatenated field that would
    /// match a term straddling the two. Left unindexed on purpose -- a non-anchored regex cannot seek, and
    /// the planner can serve the sort or the search from an index but not both, so the sort index streams
    /// and the search is a residual filter, as Postgres's <c>LIKE '%x%'</c> is.
    /// </summary>
    public static BsonRegularExpression SearchPattern(string search) =>
        new(Regex.Escape(search.ToLowerInvariant()));

    /// <summary>
    /// Every arm ends in a total order: the requested column, then (for a Status sort) UpdatedAt
    /// descending as EF Core does, then the row's own identity, (sagaType, correlationId). The identity
    /// tiebreak follows the lead direction rather than staying ascending as EF Core's does -- the contract
    /// asks for a per-provider deterministic order, and MongoDB serves a sort from an index only when the
    /// pattern equals the index or its exact inverse.
    /// </summary>
    public static SortDefinition<SagaInstanceDocument> ToSort(SagaListFilter filter) => filter.SortBy switch
    {
        SagaSortColumn.Status when filter.SortDescending =>
            Sort.Descending(d => d.Status).Descending(d => d.UpdatedAtTicks).Descending(d => d.SagaType).Descending(d => d.CorrelationId),
        SagaSortColumn.Status =>
            Sort.Ascending(d => d.Status).Descending(d => d.UpdatedAtTicks).Descending(d => d.SagaType).Descending(d => d.CorrelationId),
        // EfCoreSagaSummaryReader.ApplySort: on UpdatedAt only an explicit ascending sort ascends; the
        // default (no column) descends whatever the flag says.
        SagaSortColumn.UpdatedAt when !filter.SortDescending =>
            Sort.Ascending(d => d.UpdatedAtTicks).Ascending(d => d.SagaType).Ascending(d => d.CorrelationId),
        _ => Sort.Descending(d => d.UpdatedAtTicks).Descending(d => d.SagaType).Descending(d => d.CorrelationId),
    };
}
