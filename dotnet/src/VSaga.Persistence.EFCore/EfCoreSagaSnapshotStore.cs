using System.Text.Json;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore;

public sealed class EfCoreSagaSnapshotStore<TState>(VSagaDbContext db) : ISagaSnapshotStore<TState>
    where TState : SagaState
{
    public async Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var entity = await db.SagaInstances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SagaType == sagaType && x.CorrelationId == correlationId, cancellationToken);

        return entity is null ? null : JsonSerializer.Deserialize<TState>(entity.DataJson);
    }

    // FirstOrDefaultAsync (not SingleOrDefaultAsync) is correct here and not a bug: the partial unique
    // index on (SagaType, BusinessKey) already guarantees at most one non-null-BusinessKey row per
    // SagaType, so "first" and "single" are equivalent, and First avoids an unnecessary extra
    // uniqueness check EF would run for Single.
    public async Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default)
    {
        var entity = await db.SagaInstances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SagaType == sagaType && x.BusinessKey == businessKey, cancellationToken);

        return entity is null ? null : JsonSerializer.Deserialize<TState>(entity.DataJson);
    }

    public async Task InsertAsync(TState state, CancellationToken cancellationToken = default)
    {
        var entity = ToEntity(state);
        db.SagaInstances.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Not necessarily the (SagaType, BusinessKey) unique-constraint violation this is meant to
            // signal -- any DbUpdateException lands here. Chained as InnerException (production-readiness.md
            // §8.14's review) so a caller that finds this was actually an unrelated infra failure, not a
            // real collision, can still see what really happened instead of a bare "already exists".
            throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId, ex);
        }
    }

    public async Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
    {
        var entity = await db.SagaInstances.FirstOrDefaultAsync(x => x.SagaType == state.SagaType && x.CorrelationId == state.CorrelationId, cancellationToken)
                     ?? throw new SagaNotFoundException(state.SagaType, state.CorrelationId);

        if (entity.Version != expectedVersion)
            throw new SagaConcurrencyException(state.SagaType, state.CorrelationId, expectedVersion);

        // Bump state.Version BEFORE serializing DataJson from it — otherwise the JSON blob embeds
        // the stale version even though the entity's own Version column is correct, and FindAsync
        // (which deserializes from DataJson) would silently return the old version.
        state.Version = expectedVersion + 1;
        var updated = ToEntity(state);
        // SagaType is deliberately not reassigned: it's half the primary key now, and the row was
        // located by it above, so it is already equal by construction — writing to a key property
        // would only be a no-op that reads as if the type were mutable per update.
        entity.Kind = updated.Kind;
        entity.CurrentState = updated.CurrentState;
        entity.Status = updated.Status;
        entity.DataJson = updated.DataJson;
        entity.UpdatedAtUtc = updated.UpdatedAtUtc;
        entity.Version = updated.Version;
        // Copied even though a parent link never changes after creation, for the same reason Kind is:
        // these columns are a projection of what DataJson already holds, and the one invariant this
        // file exists to protect is that the two never disagree. Leaving them out of the update would
        // make that hold only by argument rather than by construction.
        entity.ParentSagaType = updated.ParentSagaType;
        entity.ParentCorrelationId = updated.ParentCorrelationId;
        entity.BusinessKey = updated.BusinessKey;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            state.Version = expectedVersion;
            throw new SagaConcurrencyException(state.SagaType, state.CorrelationId, expectedVersion);
        }
        catch (DbUpdateException ex)
        {
            // An update moving BusinessKey onto a key another instance of this saga type holds trips the
            // partial unique index on (SagaType, BusinessKey), exactly as InsertAsync's reservation
            // does, and is reported the same way (ISagaSnapshotStore's collision rule). Ordered after
            // the concurrency catch above, whose exception derives from this one. Clause 1's restore
            // applies on this throw path as much as the other: without it the live object would sit at
            // the version this call bumped to and failed to record. The same caveat as InsertAsync
            // applies -- any DbUpdateException lands here, so the original is chained for diagnosis.
            state.Version = expectedVersion;
            throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId, ex);
        }
    }

    private static SagaInstanceEntity ToEntity(TState state) => new()
    {
        CorrelationId = state.CorrelationId,
        SagaType = state.SagaType,
        Kind = state.Kind,
        CurrentState = state.CurrentState,
        Status = state.Status,
        Version = state.Version,
        DataJson = JsonSerializer.Serialize(state),
        ParentSagaType = state.ParentSagaType,
        ParentCorrelationId = state.ParentCorrelationId,
        BusinessKey = state.BusinessKey,
        CreatedAtUtc = state.CreatedAtUtc,
        UpdatedAtUtc = state.UpdatedAtUtc,
    };
}
