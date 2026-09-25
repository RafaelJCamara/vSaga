using System.Text.Json.Nodes;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore;

public sealed class EfCoreSagaSummaryReader(VSagaDbContext db) : ISagaSummaryReader, ISagaAdminStore
{
    public async Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.SagaInstances.AsNoTracking().AsQueryable();

        if (filter.Status is { } status)
            query = query.Where(x => x.Status == status);
        if (filter.Kind is { } kind)
            query = query.Where(x => x.Kind == kind);
        if (!string.IsNullOrWhiteSpace(filter.SagaType))
            query = query.Where(x => x.SagaType == filter.SagaType);
        // Strictly greater-than — see SagaListFilter.UpdatedSince. Folded into the same composable
        // query the CountAsync below runs, so TotalCount describes the filtered set and the caller's
        // Skip/Take arithmetic stays consistent with it.
        if (filter.UpdatedSince is { } updatedSince)
            query = query.Where(x => x.UpdatedAtUtc > updatedSince);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x => EF.Functions.Like(x.SagaType, $"%{search}%") || x.CorrelationId.ToString().Contains(search));
        }

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Max(filter.PageSize, 1);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await ApplySort(query, filter)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new SagaSummary(x.CorrelationId, x.SagaType, x.Kind, x.CurrentState, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version, x.ParentSagaType, x.ParentCorrelationId))
            .ToListAsync(cancellationToken);

        return new PagedResult<SagaSummary>(items, page, pageSize, totalCount);
    }

    /// <summary>Ties (e.g. many sagas sharing a Status) always break by UpdatedAtUtc descending, so
    /// paging through a sorted list stays stable instead of reshuffling ties between pages.</summary>
    private static IOrderedQueryable<SagaInstanceEntity> ApplySort(IQueryable<SagaInstanceEntity> query, SagaListFilter filter) =>
        filter.SortBy switch
        {
            SagaSortColumn.Status when filter.SortDescending => query.OrderByDescending(x => x.Status).ThenByDescending(x => x.UpdatedAtUtc),
            SagaSortColumn.Status => query.OrderBy(x => x.Status).ThenByDescending(x => x.UpdatedAtUtc),
            SagaSortColumn.UpdatedAt when !filter.SortDescending => query.OrderBy(x => x.UpdatedAtUtc),
            _ => query.OrderByDescending(x => x.UpdatedAtUtc),
        };

    public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        return db.SagaInstances.AsNoTracking()
            .Where(x => x.SagaType == sagaType && x.CorrelationId == correlationId)
            .Select(x => new SagaSummary(x.CorrelationId, x.SagaType, x.Kind, x.CurrentState, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version, x.ParentSagaType, x.ParentCorrelationId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
        db.SagaInstances.AsNoTracking()
            .Where(x => x.SagaType == sagaType && x.CorrelationId == correlationId)
            .Select(x => x.DataJson)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
        await db.SagaInstances.AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .OrderBy(x => x.SagaType)
            .Select(x => new SagaSummary(x.CorrelationId, x.SagaType, x.Kind, x.CurrentState, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version, x.ParentSagaType, x.ParentCorrelationId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default) =>
        await db.SagaInstances.AsNoTracking()
            .Where(x => x.ParentSagaType == parentSagaType && x.ParentCorrelationId == parentCorrelationId)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.SagaType)
            .Select(x => new SagaSummary(x.CorrelationId, x.SagaType, x.Kind, x.CurrentState, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version, x.ParentSagaType, x.ParentCorrelationId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default)
    {
        // Project into an anonymous type first: Npgsql's provider can't translate Distinct() over a
        // Select() projecting straight into SagaTypeInfo's record constructor. Anonymous types (and
        // Distinct/OrderBy over them) translate to plain `SELECT DISTINCT` fine; map to the public
        // record client-side afterward.
        var rows = await db.SagaInstances.AsNoTracking()
            .Select(x => new { x.SagaType, x.Kind })
            .Distinct()
            .OrderBy(t => t.SagaType)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SagaTypeInfo(r.SagaType, r.Kind)).ToList();
    }

    // expectedVersion and updatedAtUtc are accepted but not yet honoured: the conformance suite's
    // deliberately-red cases land first, then fix F4 (docs/design/persistence-contracts.md §3) wires
    // in the version guard, the SagaConcurrencyException mapping, and the caller's timestamp as one
    // red-to-green change.
    public async Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        var entity = await db.SagaInstances.FirstOrDefaultAsync(x => x.SagaType == sagaType && x.CorrelationId == correlationId, cancellationToken)
                     ?? throw new SagaNotFoundException(sagaType, correlationId);

        // DataJson embeds its own CurrentState/Status (it's the full serialized TState) — patch by
        // property name rather than deserializing into a concrete TState (unknown here), same fix as
        // EfCoreSagaSnapshotStore's Version bug: the entity columns and DataJson must never disagree,
        // since FindAsync<TState> reads CurrentState/Status back out of DataJson, not the columns.
        var newVersion = entity.Version + 1;
        var node = JsonNode.Parse(entity.DataJson)!.AsObject();
        node["CurrentState"] = currentState;
        node["Status"] = (int)status;
        node["Version"] = newVersion;

        entity.DataJson = node.ToJsonString();
        entity.CurrentState = currentState;
        entity.Status = status;
        entity.Version = newVersion;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }
}
