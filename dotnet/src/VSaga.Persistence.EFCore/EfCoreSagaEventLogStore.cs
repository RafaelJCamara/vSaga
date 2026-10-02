using VSaga.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace VSaga.Persistence.EFCore;

public sealed class EfCoreSagaEventLogStore(VSagaDbContext db) : ISagaEventLogStore
{
    public async Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        var entity = new SagaEventLogEntity
        {
            CorrelationId = entry.CorrelationId,
            SagaType = entry.SagaType,
            EntryType = entry.EntryType,
            FromState = entry.FromState,
            ToState = entry.ToState,
            MessageType = entry.MessageType,
            MessageId = entry.MessageId,
            PayloadJson = entry.PayloadJson,
            ErrorMessage = entry.ErrorMessage,
            TraceId = entry.TraceId,
            SpanId = entry.SpanId,
            OccurredAtUtc = entry.OccurredAtUtc,
            SourceService = entry.SourceService,
            DestinationService = entry.DestinationService,
            CausationId = entry.CausationId,
        };

        db.SagaEventLog.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The context is scoped to the whole unit of work. An entity left Added after a failed save
            // is re-sent by every later SaveChangesAsync on it (MarkDispatchedAsync, the next log entry):
            // a row that keeps failing would break the outbox drain, and one that later succeeds would be
            // resurrected out of order. Detaching drops only this entry: staged outbox rows are left
            // staged, as clause 4 of docs/design/persistence-contracts.md requires. The exception is
            // rethrown untouched.
            db.Entry(entity).State = EntityState.Detached;
            throw;
        }

        return entity.Id;
    }

    public async Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var entities = await db.SagaEventLog.AsNoTracking()
            .Where(x => x.SagaType == sagaType && x.CorrelationId == correlationId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(ToLogEntry).ToList();
    }

    // Narrowed to inbound entry types: outbound entries (MessagePublished/MessageSent) now also carry a
    // MessageId, and this check must keep recognizing only a genuine redelivery of an inbound message —
    // see HandleInfrastructureFailureAsync, which deliberately relies on this with a reused MessageId.
    // Scoped by SagaType as well: the same broadcast message legitimately reaches several saga types,
    // and each must process its own copy rather than the second one being discarded as a duplicate.
    public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
        db.SagaEventLog.AsNoTracking().AnyAsync(x => x.SagaType == sagaType && x.CorrelationId == correlationId && x.MessageId == messageId &&
            (x.EntryType == SagaEntryType.SagaStarted || x.EntryType == SagaEntryType.MessageReceived), cancellationToken);

    private static SagaLogEntry ToLogEntry(SagaEventLogEntity e) =>
        new(e.Id, e.CorrelationId, e.SagaType, e.EntryType, e.FromState, e.ToState, e.MessageType, e.MessageId,
            e.PayloadJson, e.ErrorMessage, e.TraceId, e.SpanId, e.OccurredAtUtc, e.SourceService, e.DestinationService, e.CausationId);
}
