using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.InMemory;

/// <summary>
/// Single backing store shared by <see cref="InMemorySagaSnapshotStore{TState}"/> (one instance per
/// saga TState, all delegating here), <see cref="ISagaSummaryReader"/>, <see cref="ISagaEventLogStore"/>,
/// <see cref="ISagaTimeoutStore"/>, <see cref="ISagaOutboxStore"/>, and <see cref="ISagaAdminStore"/> —
/// mirrors how the EF Core provider's single DbContext backs the same contracts. Registered as a
/// singleton. Not a toy: it round-trips state through JSON on every read/write, exactly like a real
/// persistence provider, so tests exercise real snapshot isolation instead of accidentally sharing
/// object references with the orchestrator.
/// </summary>
public sealed class InMemorySagaStore : ISagaSummaryReader, ISagaEventLogStore, ISagaTimeoutStore, ISagaOutboxStore, ISagaAdminStore, IServiceTopologyStore
{
    // Keyed by (SagaType, CorrelationId), mirroring the EF provider's composite primary key: a
    // correlation id alone does not identify an instance once two saga types may track the same one.
    private readonly ConcurrentDictionary<(string SagaType, Guid CorrelationId), StoredSnapshot> _snapshots = new();
    // Mirrors the EF provider's partial unique index on (SagaType, BusinessKey): reserved at Insert
    // time so two concurrent initiates for the same business key can't both win. A ConcurrentDictionary
    // has no cross-dictionary transaction, so Insert below reserves here FIRST, then adds to _snapshots,
    // and rolls the reservation back if the second add somehow fails -- see Insert's comment for why
    // that path is defense-in-depth rather than reachable today (CorrelationId is always freshly minted,
    // so _snapshots.TryAdd cannot collide once the business-key reservation has already succeeded).
    // Update also keeps this in sync when state.BusinessKey changes between reads (see Update's
    // comment) -- StoredSnapshot.BusinessKey is the source of truth mirrored from DataJson, and this
    // dictionary is the index over it, so the two must never disagree with each other either.
    private readonly ConcurrentDictionary<(string SagaType, string BusinessKey), Guid> _businessKeyReservations = new();
    private readonly ConcurrentDictionary<(string SagaType, Guid CorrelationId), ImmutableList<SagaLogEntry>> _timelines = new();
    private readonly ConcurrentDictionary<long, SagaTimeout> _timeouts = new();
    private readonly ConcurrentDictionary<long, SagaOutboxMessage> _outboxMessages = new();
    private readonly ConcurrentDictionary<(string ServiceName, string MessageType), ServiceTopologyEntry> _topology = new();
    private long _sequence;
    private long _timeoutId;
    private long _outboxMessageId;

    private sealed record StoredSnapshot(string Json, string SagaType, SagaKind Kind, string CurrentState, SagaStatus Status, int Version,
        string? ParentSagaType, Guid? ParentCorrelationId, string? BusinessKey, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

    private static string Serialize<TState>(TState state) where TState : SagaState => JsonSerializer.Serialize(state);

    private static TState Deserialize<TState>(string json) where TState : SagaState =>
        JsonSerializer.Deserialize<TState>(json) ?? throw new InvalidOperationException("Failed to deserialize saga snapshot.");

    internal TState? Find<TState>(string sagaType, Guid correlationId) where TState : SagaState =>
        _snapshots.TryGetValue((sagaType, correlationId), out var stored) ? Deserialize<TState>(stored.Json) : null;

    internal TState? FindByBusinessKey<TState>(string sagaType, string businessKey) where TState : SagaState =>
        _businessKeyReservations.TryGetValue((sagaType, businessKey), out var correlationId) &&
        _snapshots.TryGetValue((sagaType, correlationId), out var stored)
            ? Deserialize<TState>(stored.Json)
            : null;

    internal void Insert<TState>(TState state) where TState : SagaState
    {
        var stored = ToStored(state);

        if (state.BusinessKey is { } businessKey)
        {
            if (!_businessKeyReservations.TryAdd((state.SagaType, businessKey), state.CorrelationId))
                throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId);

            if (!_snapshots.TryAdd((state.SagaType, state.CorrelationId), stored))
            {
                _businessKeyReservations.TryRemove((state.SagaType, businessKey), out _);
                throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId);
            }

            return;
        }

        if (!_snapshots.TryAdd((state.SagaType, state.CorrelationId), stored))
            throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId);
    }

    internal void Update<TState>(TState state, int expectedVersion) where TState : SagaState
    {
        var key = (state.SagaType, state.CorrelationId);

        while (true)
        {
            if (!_snapshots.TryGetValue(key, out var current))
                throw new SagaNotFoundException(state.SagaType, state.CorrelationId);

            if (current.Version != expectedVersion)
                throw new SagaConcurrencyException(state.SagaType, state.CorrelationId, expectedVersion);

            // BusinessKey has a plain public setter on SagaState (nothing enforces "set once at
            // creation" at the type level), so Update must not assume it is unchanged from Insert --
            // doing so is exactly how the reservation dictionary and DataJson silently disagreed
            // before this fix. Mirror the EF provider's unconditional column reassignment
            // (EfCoreSagaSnapshotStore.UpdateAsync:74), but since this store's "unique index" is a
            // second ConcurrentDictionary rather than a database constraint, moving the reservation is
            // this method's job: reserve the new key BEFORE the swap becomes visible (so a concurrent
            // writer targeting the same new key collides here, the same guarantee Insert gives), and
            // only release the old key AFTER the swap succeeds (so a losing CAS below can retry while
            // still holding it, instead of exposing a window where neither writer holds the old key).
            var oldBusinessKey = current.BusinessKey;
            var newBusinessKey = state.BusinessKey;
            var businessKeyChanged = !string.Equals(oldBusinessKey, newBusinessKey, StringComparison.Ordinal);

            if (businessKeyChanged && newBusinessKey is not null &&
                !_businessKeyReservations.TryAdd((state.SagaType, newBusinessKey), state.CorrelationId))
                throw new SagaAlreadyExistsException(state.SagaType, state.CorrelationId);

            state.Version = expectedVersion + 1;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var updated = ToStored(state);

            if (_snapshots.TryUpdate(key, updated, current))
            {
                if (businessKeyChanged && oldBusinessKey is not null)
                    _businessKeyReservations.TryRemove((state.SagaType, oldBusinessKey), out _);

                return;
            }

            // Another writer beat us to it between the read and the compare-and-swap. Release the new
            // reservation we just took (if any) so it doesn't leak while we retry -- the retry re-reads
            // current and re-derives businessKeyChanged from scratch, so re-reserving is safe.
            if (businessKeyChanged && newBusinessKey is not null)
                _businessKeyReservations.TryRemove((state.SagaType, newBusinessKey), out _);
        }
    }

    // Mirrors the EF provider's real columns, parent pointer and business key included: both stores
    // have to answer the same saga-type-agnostic queries without deserializing Json, so whatever
    // ISagaSummaryReader can filter on there has to be projected out here too, and per
    // production-readiness.md §5.2 this projection -- not the reservation dictionary -- is BusinessKey's
    // source of truth on this provider, exactly as it is on EF Core's DataJson/column pair.
    private static StoredSnapshot ToStored<TState>(TState state) where TState : SagaState =>
        new(Serialize(state), state.SagaType, state.Kind, state.CurrentState, state.Status, state.Version,
            state.ParentSagaType, state.ParentCorrelationId, state.BusinessKey, state.CreatedAtUtc, state.UpdatedAtUtc);

    public Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
    {
        var query = _snapshots.Select(kvp => ToSummary(kvp.Key.CorrelationId, kvp.Value));

        if (filter.Status is { } status)
            query = query.Where(s => s.Status == status);
        if (filter.Kind is { } kind)
            query = query.Where(s => s.Kind == kind);
        if (!string.IsNullOrWhiteSpace(filter.SagaType))
            query = query.Where(s => string.Equals(s.SagaType, filter.SagaType, StringComparison.Ordinal));
        // Strictly greater-than — see SagaListFilter.UpdatedSince. Applied before ApplySort below, so
        // the materialized list this pages over (and reports Count from) is the filtered set, matching
        // the EF provider's filter-then-count-then-page order exactly.
        if (filter.UpdatedSince is { } updatedSince)
            query = query.Where(s => s.UpdatedAtUtc > updatedSince);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(s => s.SagaType.Contains(search, StringComparison.OrdinalIgnoreCase) || s.CorrelationId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = ApplySort(query, filter).ToList();
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Max(filter.PageSize, 1);
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Task.FromResult(new PagedResult<SagaSummary>(items, page, pageSize, ordered.Count));
    }

    /// <summary>Ties (e.g. many sagas sharing a Status) always break by UpdatedAtUtc descending, so
    /// paging through a sorted list stays stable instead of reshuffling ties between pages.</summary>
    private static IOrderedEnumerable<SagaSummary> ApplySort(IEnumerable<SagaSummary> query, SagaListFilter filter) =>
        filter.SortBy switch
        {
            SagaSortColumn.Status when filter.SortDescending => query.OrderByDescending(s => s.Status).ThenByDescending(s => s.UpdatedAtUtc),
            SagaSortColumn.Status => query.OrderBy(s => s.Status).ThenByDescending(s => s.UpdatedAtUtc),
            SagaSortColumn.UpdatedAt when !filter.SortDescending => query.OrderBy(s => s.UpdatedAtUtc),
            _ => query.OrderByDescending(s => s.UpdatedAtUtc),
        };

    public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (!_snapshots.TryGetValue((sagaType, correlationId), out var s))
            return Task.FromResult<SagaSummary?>(null);

        return Task.FromResult<SagaSummary?>(ToSummary(correlationId, s));
    }

    private static SagaSummary ToSummary(Guid correlationId, StoredSnapshot s) =>
        new(correlationId, s.SagaType, s.Kind, s.CurrentState, s.Status, s.CreatedAtUtc, s.UpdatedAtUtc, s.Version, s.ParentSagaType, s.ParentCorrelationId);

    public Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SagaSummary> matches = _snapshots
            .Where(kvp => kvp.Key.CorrelationId == correlationId)
            .OrderBy(kvp => kvp.Key.SagaType, StringComparer.Ordinal)
            .Select(kvp => ToSummary(correlationId, kvp.Value))
            .ToList();

        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SagaSummary> children = _snapshots
            .Where(kvp => string.Equals(kvp.Value.ParentSagaType, parentSagaType, StringComparison.Ordinal) &&
                          kvp.Value.ParentCorrelationId == parentCorrelationId)
            .OrderBy(kvp => kvp.Value.CreatedAtUtc).ThenBy(kvp => kvp.Value.SagaType, StringComparer.Ordinal)
            .Select(kvp => ToSummary(kvp.Key.CorrelationId, kvp.Value))
            .ToList();

        return Task.FromResult(children);
    }

    public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SagaTypeInfo> types = _snapshots.Values
            .Select(s => new SagaTypeInfo(s.SagaType, s.Kind))
            .Distinct()
            .OrderBy(t => t.SagaType, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(types);
    }

    public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshots.TryGetValue((sagaType, correlationId), out var s) ? s.Json : null);

    public Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, CancellationToken cancellationToken = default)
    {
        var key = (sagaType, correlationId);

        while (true)
        {
            if (!_snapshots.TryGetValue(key, out var current))
                throw new SagaNotFoundException(sagaType, correlationId);

            // Patch the embedded JSON's CurrentState/Status by property name rather than deserializing
            // into a concrete TState (unknown here) — keeps this store genuinely saga-type-agnostic.
            var node = JsonNode.Parse(current.Json)!.AsObject();
            node["CurrentState"] = currentState;
            node["Status"] = (int)status;

            var updated = current with
            {
                Json = node.ToJsonString(),
                CurrentState = currentState,
                Status = status,
                Version = current.Version + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            if (_snapshots.TryUpdate(key, updated, current))
                return Task.CompletedTask;
        }
    }

    public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        var sequenceNumber = Interlocked.Increment(ref _sequence);
        var stamped = entry with { SequenceNumber = sequenceNumber };

        _timelines.AddOrUpdate(
            (entry.SagaType, entry.CorrelationId),
            _ => ImmutableList.Create(stamped),
            (_, list) => list.Add(stamped));

        return Task.FromResult(sequenceNumber);
    }

    public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SagaLogEntry> result = _timelines.TryGetValue((sagaType, correlationId), out var list) ? list : [];
        return Task.FromResult(result);
    }

    // Narrowed to inbound entry types — see EfCoreSagaEventLogStore.IsDuplicateAsync for why: outbound
    // entries now also carry a MessageId, and HandleInfrastructureFailureAsync's redelivery path
    // deliberately relies on this check recognizing only a reused *inbound* MessageId.
    public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default)
    {
        var isDuplicate = _timelines.TryGetValue((sagaType, correlationId), out var list) &&
                           list.Any(e => string.Equals(e.MessageId, messageId, StringComparison.Ordinal) &&
                                         (e.EntryType == SagaEntryType.SagaStarted || e.EntryType == SagaEntryType.MessageReceived));
        return Task.FromResult(isDuplicate);
    }

    public Task ScheduleAsync(string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _timeoutId);
        _timeouts[id] = new SagaTimeout(id, correlationId, sagaType, forState, dueAtUtc, SagaTimeoutStatus.Pending);
        return Task.CompletedTask;
    }

    public Task CancelAsync(string sagaType, Guid correlationId, string forState, CancellationToken cancellationToken = default)
    {
        foreach (var (id, timeout) in _timeouts)
        {
            // SagaType included: state names are only unique within a saga type, so without it one
            // saga would cancel another's pending timeout for a same-named state.
            if (string.Equals(timeout.SagaType, sagaType, StringComparison.Ordinal) &&
                timeout.CorrelationId == correlationId &&
                string.Equals(timeout.ForState, forState, StringComparison.Ordinal) &&
                timeout.Status == SagaTimeoutStatus.Pending)
            {
                _timeouts[id] = timeout with { Status = SagaTimeoutStatus.Cancelled };
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, CancellationToken cancellationToken = default)
    {
        var claimed = new List<SagaTimeout>();

        foreach (var (id, timeout) in _timeouts)
        {
            if (claimed.Count >= batchSize)
                break;

            if (timeout.Status != SagaTimeoutStatus.Pending || timeout.DueAtUtc > asOf)
                continue;

            var fired = timeout with { Status = SagaTimeoutStatus.Fired };
            if (_timeouts.TryUpdate(id, fired, timeout))
                claimed.Add(fired);
        }

        return Task.FromResult<IReadOnlyList<SagaTimeout>>(claimed);
    }

    // Writes immediately, unlike the EF provider, which stages the row in a shared unit of work its
    // caller then commits atomically with the snapshot: a ConcurrentDictionary has no unit of work to
    // enlist in, so the residual gap production-readiness.md §4.4 documents applies here — a crash
    // between this and the snapshot write is not covered. DiscardPendingAsync below still removes the
    // rows on the abandon paths, so the observable behaviour matches the EF provider's everywhere
    // except an actual process crash. Dev/test-only provider.
    public Task EnqueueAsync(string sagaType, Guid correlationId, string messageId, string messageTypeName,
        ReadOnlyMemory<byte> body, string? destination, IReadOnlyDictionary<string, string> headers,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _outboxMessageId);
        _outboxMessages[id] = new SagaOutboxMessage(id, correlationId, sagaType, messageId, messageTypeName,
            body, destination, headers, SagaOutboxStatus.Pending, createdAtUtc);
        return Task.CompletedTask;
    }

    public Task MarkDispatchedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        foreach (var (id, message) in _outboxMessages)
        {
            if (string.Equals(message.MessageId, messageId, StringComparison.Ordinal))
            {
                _outboxMessages.TryUpdate(id, message with { Status = SagaOutboxStatus.Dispatched }, message);
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    public Task DiscardPendingAsync(IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
            return Task.CompletedTask;

        var ids = messageIds as HashSet<string> ?? new HashSet<string>(messageIds, StringComparer.Ordinal);

        foreach (var (id, message) in _outboxMessages)
        {
            if (message.Status == SagaOutboxStatus.Pending && ids.Contains(message.MessageId))
                _outboxMessages.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default)
    {
        var claimed = new List<SagaOutboxMessage>();

        foreach (var (id, message) in _outboxMessages)
        {
            if (claimed.Count >= batchSize)
                break;

            if (message.Status != SagaOutboxStatus.Pending || message.CreatedAtUtc > olderThan)
                continue;

            var dispatched = message with { Status = SagaOutboxStatus.Dispatched };
            if (_outboxMessages.TryUpdate(id, dispatched, message))
                claimed.Add(dispatched);
        }

        return Task.FromResult<IReadOnlyList<SagaOutboxMessage>>(claimed);
    }

    public Task RecordAsync(string serviceName, string messageType, string queueName, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        _topology[(serviceName, messageType)] = new ServiceTopologyEntry(serviceName, messageType, queueName, seenAtUtc);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServiceTopologyEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ServiceTopologyEntry> result = _topology.Values.ToList();
        return Task.FromResult(result);
    }
}
