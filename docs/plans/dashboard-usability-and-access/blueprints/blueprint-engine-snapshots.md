# StatePersisted entries: engine, options, map builder and API (improvement 4, server side)

## Summary

- `SagaEntryType.StatePersisted` (appended last, value 21) carries the state blob exactly as stored, in the existing `PayloadJson`. No contract signature, schema or migration changes.
- `SagaOrchestrator` appends it straight after each transition-committing persist (step success, step failure, timeout final persist, delivery exhaustion) through one best-effort helper: after the commit, before the drain, never between outbox staging and the persist.
- Core serialises with the same call every snapshot store makes, on the same untouched object. A golden-text test and a blob-equality test pin it.
- One provider fix rides along (not a contract change): `EfCoreSagaEventLogStore.AppendAsync` detaches its entity when the save throws, so a failed append cannot poison later commits in the scoped `DbContext`.
- Dashboard API: `SagaMapBuilder` drops snapshot entries, the timeline endpoint gains a payload-redaction seam, and the retry endpoint records the blob a reset leaves.
- `LogAsync` starts stamping the stored sequence number onto the entry it notifies with.
- No existing .NET assertion has to change. Read cost grows with steps × state size; I recommend no mitigation in this change beyond the cap and the off switch, and name the follow-up.

## Design

### 1. Entry type and shared builder (`VSaga.Abstractions`)

`SagaEntryType.cs`: add after `ChildSagaFinished` (line 40; the append-only rule is at 24-26):

```csharp
/// <summary>
/// The state a persist just committed, exactly as the snapshot store wrote it: PayloadJson is that blob,
/// or {"$vsagaStateOmitted":true,"bytes":N,"limit":L} when it exceeds the recorder's size cap. Appended
/// after the persist, never before, so it only describes a state that was stored; a transition that lost
/// its race, or a process that died between the commit and this append, leaves none. MessageType/MessageId
/// name the inbound message whose step it follows (null after a timeout or a dashboard reset). FromState
/// and ToState are always null: compensation order is read from ToState, and a snapshot must not add to it.
/// Not a correctness input: neither compensation nor dedupe reads it.
/// </summary>
StatePersisted,
```

New `Persistence/SagaStateSnapshot.cs`, the single definition of the entry shape and marker. Core and the dashboard both call it; it needs no package.

```csharp
public static class SagaStateSnapshot
{
    public const int DefaultMaxBytes = 262_144;

    public static SagaLogEntry CreateEntry(Guid correlationId, string sagaType, string stateJson, int maxBytes,
        string? messageType = null, string? messageId = null) =>
        SagaLogEntry.Create(correlationId, sagaType, SagaEntryType.StatePersisted,
            messageType: messageType, messageId: messageId, payloadJson: ToPayload(stateJson, maxBytes));

    public static string ToPayload(string stateJson, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(stateJson);
        return bytes <= maxBytes ? stateJson
            : string.Create(CultureInfo.InvariantCulture, $"{{\"$vsagaStateOmitted\":true,\"bytes\":{bytes},\"limit\":{maxBytes}}}");
    }
}
```

The marker mirrors Mongo's `$vsagaPayloadOmitted` (`MongoSagaEventLogStore.cs:51`) under its own key. A cap of 0 therefore records size-only markers.

### 2. Options

`SagaOrchestratorOptions` gains `bool RecordStateSnapshots { get; set; } = true` and `int MaxStateSnapshotBytes { get; set; } = SagaStateSnapshot.DefaultMaxBytes` (UTF-8 bytes; a larger state becomes the marker). No validation, like `MaxDeliveryAttempts`. `SagaEngineBuilder.ConfigureOrchestrator` (`ServiceCollectionExtensions.cs:76-82`) needs no code change, only its doc comment ("currently just MaxDeliveryAttempts") and the class summary. Existing `new SagaOrchestratorOptions { MaxDeliveryAttempts = n }` registrations keep the defaults. The sample host binds the class with `.ConfigureOrchestrator(o => builder.Configuration.GetSection("Orchestrator").Bind(o))`, so compose can set `Orchestrator__MaxStateSnapshotBytes`.

### 3. Engine helper and insertion points (`SagaOrchestrator.cs`)

```csharp
private async Task PersistAndSnapshotAsync(TState state, bool isNew, int expectedVersion,
    string? messageType, string? messageId, CancellationToken cancellationToken)
{
    await PersistAsync(state, isNew, expectedVersion, cancellationToken);
    await RecordStateSnapshotAsync(state, messageType, messageId, cancellationToken);
}

private async Task RecordStateSnapshotAsync(TState state, string? messageType, string? messageId, CancellationToken cancellationToken)
{
    if (!options.RecordStateSnapshots)
        return;

    try
    {
        var stateJson = JsonSerializer.Serialize(state);
        await LogAsync(SagaStateSnapshot.CreateEntry(state.CorrelationId, SagaType, stateJson,
            options.MaxStateSnapshotBytes, messageType, messageId), cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not record the state snapshot for saga {SagaType} correlation {CorrelationId} at version {Version}; the transition is committed and processing continues",
            SagaType, state.CorrelationId, state.Version);
    }
}
```

A persist that throws, a lost race included, propagates out of `PersistAndSnapshotAsync` untouched. Every existing catch block behaves as today and no snapshot is written.

The wrapper exists for two reasons. Nothing can be inserted between the commit and the snapshot. And `HandleStepFailureAsync` is 59 lines brace to brace (653-712), tied with `SagaChangePollingService.PollOnceAsync` (88-147) as the longest body in `dotnet/src`, which is where Meziantou's 60-line default leaves them. Treat it as having no headroom: a call replacement is the only edit that adds no line.

| Path | Edit | Order around it |
|---|---|---|
| Step success, `PersistAndFinalizeStepSuccessAsync` | Line 761: `PersistAsync(state, needsInsert, expectedVersion, ct)` becomes `PersistAndSnapshotAsync(state, needsInsert, expectedVersion, messageTypeName, messageId, ct)`. The method gains those two parameters from the call at 743. | `EnqueueOutboxRowsAsync` (757) → **persist + snapshot** → duration metric (785) → `DrainDeferredPublishesAsync` (789) → `notifier.SagaUpdatedAsync` (804) |
| Step failure, `HandleStepFailureAsync` | Line 679, same replacement; both values are already in scope. | `StepFailed` (663) → `StageChildSagaFinishedAsync` (674) → **persist + snapshot** → `onChildFinishedStaged(null)` → `RecordSagaFailed` (698) → `DiscardDeferredPublishesAsync` (704) → notifier (706) → `PublishChildSagaFinishedAsync` (711) |
| Timeout, `CommitAndDispatchTimeoutAsync` | After the `if (!await TryPersistOrLogRaceLossAsync(..., sideEffectsAlreadyRan: true, ...)) { ...; return; }` block (316-321), add `await RecordStateSnapshotAsync(state, messageType: null, messageId: null, cancellationToken);`. | staging (304-307) → final persist (316) → **snapshot** → drain (323) → `RecordTimeoutOutcomeAsync` (324). The claim at 267 shares `TryPersist…`, which is why the call is explicit here. |
| Delivery exhaustion, `RecordDeliveryExhaustedAsync` | Line 161 becomes `PersistAndSnapshotAsync(state, isNew: false, expectedVersion, received.MessageTypeName, received.MessageId, ct)`. | `DeliveryExhausted` (154) → **persist + snapshot** → `RecordSagaFailed` (168) → notifier (170) |

Why each position is safe:

- **Outbox staging.** At every site staging precedes the persist, and the persist commits everything staged (`ISagaOutboxStore.cs:42-53`). Nothing is staged again before the drain, so on EF the append's `SaveChangesAsync` flushes only its own row. On the failure path the deferred queue was never staged (`EnqueueOutboxRowsAsync` is not called there), so appending before `DiscardDeferredPublishesAsync` cannot commit a row the discard exists to drop.
- **Drain.** The snapshot goes first because the in-memory transport dispatches synchronously from inside `publish.SendAsync()`. After the drain, a nested step's entries and its higher-version snapshot would be sequenced ahead of this step's snapshot; `TimeoutDrainTestSaga` reproduces that. On brokers and the HTTP pump a reply can only exist once the drain has published, so the snapshot also precedes anything the drain causes. A drain failure cannot lose it either.
- **Notifier.** `SagaUpdatedAsync` fires after the snapshot, so an in-process subscriber that refetches on it finds the entry.
- **`PublishChildSagaFinishedAsync`.** Its transport publish is unguarded and re-enters the parent. The snapshot is already stored by then.

Nothing is recorded for the timeout claim (267), the business-key reservation insert (511), `UnexpectedEvent`, duplicates, an unhandled timeout or any lost race.

`LogAsync` (1017-1021) should stamp the sequence number:

```csharp
var sequenceNumber = await eventLog.AppendAsync(entry, cancellationToken);
await notifier.TimelineEntryAddedAsync(entry.SagaType, entry.CorrelationId, entry with { SequenceNumber = sequenceNumber }, cancellationToken);
```

Decision B joins timeline to map on sequence numbers, the SPA appends pushed entries (`saga-detail.ts:103-107`) and tracks rows by `sequenceNumber`, and every pushed entry carries 0 today. Only the in-process notifier is affected, and no test observes it yet.

### 4. Why the JSON equals the stored blob

- **Same call.** All four providers store `JsonSerializer.Serialize(state)` with `state` typed `TState` (`EfCoreSagaSnapshotStore.cs:119`, `MongoSagaSnapshotStore.cs:56,93`, `RedisSagaSnapshotStore.cs:41,60`, `InMemorySagaStore.cs:45`), which clause 12 makes the contract. The helper makes the identical generic call with default options.
- **Same object, unmodified.** `PersistAsync` stamps `UpdatedAtUtc` (933) before the store call. `UpdateAsync` bumps `Version` before serialising and leaves it bumped on success (clause 1). `InsertAsync` changes nothing. No statement runs between the store returning and the helper.
- If serialisation could throw, the persist would have thrown first.
- **Pinned twice.** An exact golden text for a known state under a fixed clock, because Core becomes an independent serialisation site in the sense of `ISagaSnapshotStore.cs:44-48`. And `snapshot.PayloadJson == GetDataJsonAsync(...)` after every kind of persist. Each other provider is tied to the same text by its own golden-blob conformance case.

### 5. When the append throws

The helper swallows everything, cancellation included, and logs a warning. On success the metrics, drain, notifier and ack proceed. On failure `onChildFinishedStaged(null)`, the discard, the notifier and the `ChildSagaFinished` publish proceed. On timeout the drain and outcome proceed. On exhaustion the metrics and notifier proceed. The only effect is a step with no snapshot. Without containment, the success path would escape to `HandleAsync`, redeliver, hit the dedupe check and skip, leaving the committed step's deferred publishes to the recovery poller.

EF Core is the provider where the try/catch is not enough on its own. `AppendAsync` adds the entity and saves (`EfCoreSagaEventLogStore.cs:29-30`). When the save throws, the entity stays `Added` in the scoped context, and every later `SaveChangesAsync` in the unit of work (`MarkDispatchedAsync`, each `MessagePublished` log) retries it. A row that keeps failing breaks the drain; one that succeeds later is resurrected out of order. The fix has the shape `persistence-contracts.md` B3 sketches for `UpdateAsync`:

```csharp
db.SagaEventLog.Add(entity);
try { await db.SaveChangesAsync(cancellationToken); }
catch { db.Entry(entity).State = EntityState.Detached; throw; }
```

Staged outbox rows are untouched, so clause 4 still holds. Mongo and Redis appends take no part in the unit of work (`MongoSagaEventLogStore.cs:14-21`, `RedisSagaEventLogStore.cs:12-17`), and in-memory cannot fail.

A row-specific failure is unlikely on EF in any case: the payload is the text just stored in `SagaInstances.DataJson` (same unbounded column type), and `MessageType`/`MessageId` repeat values this step already stored.

Rejected alternative: appending through a fresh DI scope. It isolates the failure, but it costs a scope and a `DbContext` per step, bypasses the injected store, and leaves every other append exposed.

### 6. Interactions

- **`GetVisitedStatesAsync` (940-949):** unaffected; `ToState` is null.
- **`IsDuplicateAsync`:** unaffected in all four providers, although the entry carries the inbound `MessageId`. Redis feeds its dedupe set only from `SagaStarted`/`MessageReceived` (`RedisSagaEventLogStore.cs:29-31`). Pinned by conformance.
- **Engine `RetryAsync` (213-246):** runs through `RunStepAsync`, so it snapshots like any step, under the retried message's original id. Several entries can share one `MessageId`.
- **`SagaChangePollingService`:** unchanged. It can push `SagaUpdated` between the persist and the append, one round trip apart.
- **Notifier:** the engine hands over the entry it stored, payload included. `SignalRSagaChangeNotifier` drops the payload (section 7).
- **`SagaTestHarness`:** records by default, and `GetTimelineAsync` (127-131) returns the entries. Opt out with `new SagaTestHarness<,>(s => s.AddSingleton(new SagaOrchestratorOptions { RecordStateSnapshots = false }))`. `OccurredAtUtc` stays wall-clock like every entry, while the payload's `UpdatedAtUtc` follows the fake clock.
- **MongoDB:** its 12 MiB `GuardPayload` still applies and would substitute `$vsagaPayloadOmitted` if the cap were raised past it.
- **Redis:** the entry is one list element with the blob JSON-escaped inside it, held in RAM.
- **Version skew:** an API older than this change serialises the unknown value as `21`. Deploy the dashboard first.

### 7. Dashboard API

- **Map.** `SagaMapBuilder.BuildMap` (line 75) becomes `timeline.Where(e => e.EntryType != SagaEntryType.StatePersisted).OrderBy(e => e.SequenceNumber).ToList()`. Filtering at the source keeps snapshots out of initiator resolution, stitching, failed-id resolution, `Events` and `FailureEventIndex`.
- **Timeline redaction seam.** New `Endpoints/SagaTimelineRedaction.cs` with `internal static IReadOnlyList<SagaLogEntry> Apply(IReadOnlyList<SagaLogEntry> timeline, bool includePayloads)`. It returns the list as is, or a copy with `PayloadJson = null` on entries that have one. Entries are never dropped, so sequence numbers, the SPA fold and the map join are the same for every caller. The lambda at `SagaEndpoints.cs:43-45` becomes a named `GetSagaTimelineAsync`. The auth assignment supplies the boolean (caller holds `sagas.data` for the route's saga type). The retry handler keeps reading payloads server-side.
- **Pushes.** `SignalRSagaChangeNotifier.TimelineEntryAddedAsync` (16-17) sends `entry.PayloadJson is null ? entry : entry with { PayloadJson = null }` (decision D). The conditional keeps `SignalRSagaChangeNotifierTests.cs:75` (`Assert.Same`) valid. This must land before the engine emits snapshots.
- **Retry reset snapshot.** New scoped `Endpoints/SagaResetSnapshotRecorder(ISagaSummaryReader, ISagaEventLogStore, DashboardStateSnapshotOptions, ILogger<>)` with `RecordAsync(sagaType, correlationId, resetVersion, ct)`, which never throws:
  1. Read `GetDataJsonAsync`.
  2. Skip when it is null or its `Version` property is not `resetVersion`. A step has then already moved the saga on, and its own snapshot records that state. `AdminStoreConformanceTests` pins the reset's bump at exactly one.
  3. Append `SagaStateSnapshot.CreateEntry(correlationId, sagaType, dataJson, options.MaxBytes)` with no message identity.

  `ResetAndRedriveAsync` calls it straight after `ResetStateAsync` succeeds (181), before the publish, and only when the timeline `RetrySagaAsync` already loaded contains a `StatePersisted` entry. The dashboard therefore never writes a saga's first snapshot, so an engine host with `RecordStateSnapshots = false` is honoured without a second switch. A technical redrive makes no reset and records nothing.
- **Configuration.** `Dashboard:StateSnapshots:MaxBytes` (default 262144), bound once in `Program.cs` into a `DashboardStateSnapshotOptions` singleton.

### 8. Hot-path cost and recommendation

- Each committed transition costs one more serialisation and one more append (one round trip on EF and Redis, two on MongoDB).
- `GetVisitedStatesAsync` reads the whole timeline, payloads included, on every message and timeout. With S persisted transitions of average blob size B, a late message reads about S·B more bytes and the saga reads about B·S²/2 over its life:

| Steps × blob | Extra per late message | Extra over the saga's life |
|---|---|---|
| 8 × 0.5 KB (the sample) | 4 KB | 16 KB |
| 30 × 4 KB | 120 KB | 1.8 MB |
| 200 × 32 KB | 6.4 MB | about 640 MB |

- On Redis the same bytes sit in RAM, and `LRANGE` blocks the server while returning them.
- The SPA refetches timeline and map on every push: two server-side timeline reads and one download, each now carrying every snapshot.

**Recommendation: no mitigation in this change.** The sample and typical sagas (tens of steps, a few KB of state) stay well under a megabyte per read. The real fix is a payload-free read for visited states and the map, which is a persistence-contract addition that decision C excludes. Ship the cap and the off switch, document the growth, and record the follow-up in the ADR with a trigger: a saga type whose steps × state size passes roughly 1 MB.

Two cheap measures sit outside this assignment: gzip for `application/json` in the nginx proxy, and the SPA skipping a refetch when the pushed `version` is unchanged. A per-instance byte budget was considered and rejected, because it would drop the newest snapshots, which are the ones a failure investigation needs.

### 9. Documentation

- `docs/observability.md:20-24`: add `StatePersisted` and a short "State snapshots" subsection (when written and not written, payload and markers, best-effort, the one entry type that is not a correctness input).
- `docs/configuration.md:49-53`: "One tunable" becomes three rows; note the sample's `Orchestrator` section; add `Dashboard:StateSnapshots:MaxBytes`; cross-reference `MaxPayloadJsonBytes` (262).
- `docs/concepts.md:63`: replace "visible in the saga's Data tab".
- `docs/persistence.md`: the `SagaEventLog` row (79); the Redis capacity model (329-348) re-measured with snapshots on, and the `LRANGE` note (324-327); MongoDB measured storage (622-628). My estimate, to verify live: for the sample's 400-byte states, per-saga storage grows by roughly 60-90%.
- `docs/dashboard.md:19-23`, `docs/testing.md:37`, and the ADR index in `docs/README.md`.
- Doc comments that mention the Data tab: `ISagaSnapshotStore.cs:43-45`, `ISagaSummaryReader.cs:43`.
- ADR `docs/adr/0007-state-snapshots-in-the-event-log.md`, structured like 0005:
  - Header: Status, Date, Relates to 0003, 0005 and the design document.
  - **Context:** the blob is overwritten at each persist; the timeline held no state; the constraints (staging window, appends independent of the persist, visited states, dedupe).
  - **Decision**, with subsections "Why an entry type rather than a field on existing entries or a versioned history store", "Why after the persist and before the drain", "Why Core serialises the state itself", and "What it costs, accepted knowingly" (one append per transition, storage of steps × state, quadratic read volume, best-effort recording, business data copied into a log with no retention).
  - **Consequences:** Positive, Negative, Neutral.
  - **What would invalidate this decision later:** a payload-free timeline read; event-log retention; native state storage (ADR 0005 item 1); a requirement that snapshots be atomic with the persist.

## Files

Create:

- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Abstractions/Persistence/SagaStateSnapshot.cs`: entry builder and marker.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaTimelineRedaction.cs`: redaction seam.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaResetSnapshotRecorder.cs`: recorder plus `DashboardStateSnapshotOptions`.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Core.Tests/StateSnapshotTests.cs`, `StateSnapshotContainmentTests.cs`, `SagaStateSnapshotTests.cs` (same folder).
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Dashboard.Api.Tests/SagaMapBuilderTests.cs`, `SagaTimelineRedactionTests.cs`, `RetryStateSnapshotTests.cs` (same folder).
- `C:/Users/rafae/Documents/Projects/vSaga/docs/adr/0007-state-snapshots-in-the-event-log.md`.
- `C:/Users/rafae/Documents/Projects/vSaga/docs/history/state-snapshots.md`: after live verification.

Modify:

- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs` (critical): helper, wrapper, four sites, `LogAsync`.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Abstractions/Persistence/SagaEntryType.cs` (critical): new member.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.EFCore/EfCoreSagaEventLogStore.cs` (critical): detach on failure.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs` (critical): skip snapshots.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs` (critical): named timeline handler, recorder call.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOrchestratorOptions.cs`: two options.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/ServiceCollectionExtensions.cs`: doc comment only.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Hubs/SignalRSagaChangeNotifier.cs`: strip payload.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Program.cs`: options singleton, scoped recorder.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/samples/VSaga.Samples.OrderProcessing/Program.cs`: bind `Orchestrator`.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Abstractions/Persistence/ISagaSnapshotStore.cs` and `ISagaSummaryReader.cs`: doc comments.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Persistence.Conformance/EventLogStoreConformanceTests.cs`.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Persistence.EFCore.Tests/EfCoreStoreTests.cs`.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Core.Tests/EngineOptionsConfigurationTests.cs`.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Dashboard.Api.Tests/SignalRSagaChangeNotifierTests.cs` and `SagaEndpointsTests.cs`.
- `C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Testing.Tests/SagaTestHarnessTests.cs`.
- `C:/Users/rafae/Documents/Projects/vSaga/docs/`: `observability.md`, `configuration.md`, `concepts.md`, `persistence.md`, `dashboard.md`, `testing.md`, `README.md`.

## Tests

**VSaga.Core.Tests** (fixtures reuse `TestOrderSaga`, `TimeoutDrainTestSaga` and the business-key race sagas):

- Step success: exactly one snapshot per handled message, sequenced after that step's `StepSucceeded`/`TimeoutScheduled`/`SagaCompleted`, carrying the inbound message type and id, with null `FromState`/`ToState`.
- Blob equality, a theory over first-step insert, update, business-key first step, step failure, timeout and exhaustion: `PayloadJson == GetDataJsonAsync`.
- Golden text: a fixed `TimeProvider` and correlation id give an exact literal.
- Step failure: the snapshot has `"Status":2` and the mutation made before the throw.
- Timeout: exactly one new snapshot, `Version` two above the pre-timeout value, null message identity. With `TimeoutDrainTestSaga`, snapshot versions ascend in timeline order and the timeout's snapshot precedes the nested `MessageReceived`.
- Unhandled timeout (a hand-built `SagaTimeout` for a state with no `WithTimeout`): version bumped, no snapshot.
- Delivery exhaustion (the shape at `DeliveryExhaustedChildSagaFinishedTests.cs:80`): a snapshot with the dead-lettered message's id and `"Status":2`.
- Lost race: in the `SagaOrchestratorConcurrencyRedeliveryTests` setup, one snapshot for `ConfirmationReceived`, carrying C's id and never B's. In `TimeoutDrainTests`' nudging setup the losing timeout adds none.
- No snapshot for `UnexpectedEvent` or a duplicate delivery.
- Options: off gives none; the cap gives the exact marker text, with a non-ASCII character proving bytes rather than chars; a state exactly at the limit is recorded; `ConfigureOrchestrator` sets both; defaults asserted by extending `EngineOptionsConfigurationTests.cs:56-66`.
- Containment, with an event-log decorator that throws only for `StatePersisted`:
  - success: state persisted, deferred publish sent, outbox empty, no redelivery message;
  - failure: saga `Failed`, `ChildSagaFinished` published;
  - timeout: transition persisted and drained;
  - exhaustion: saga `Failed`.
- Notifier: a recording `ISagaChangeNotifier` sees every entry, the snapshot included, with its stored non-zero sequence number.
- `SagaStateSnapshot` unit tests, and a pin of every `SagaEntryType` numeric value (0-21).

**VSaga.Persistence.Conformance:** new `GetTimeline_RoundTripsAStatePersistedEntry` (type, exact payload text including `\u0026`, null states). Add `[InlineData(SagaEntryType.StatePersisted)]` to the theory at `EventLogStoreConformanceTests.cs:149-153`.

**VSaga.Persistence.EFCore.Tests:** a save interceptor that throws once, after which the failed entry is absent following a later successful append on the same context; an entry with a null `SagaType` fails and the next append succeeds.

**VSaga.Dashboard.Api.Tests:**

- `SagaMapBuilderTests`, the direct unit tests the builder lacks: one event per non-snapshot entry carrying its sequence number; snapshots produce no event, node or edge and do not shift `FailureEventIndex`; ordering by sequence number; inbound edge versus orchestrator node event; stitched, topology-resolved and unresolved outbound; fan-out primary edge; compensation flag; orchestrator status; empty timeline.
- `SagaTimelineRedactionTests`.
- Retry: a reset records the blob (`PayloadJson == detail.DataJson`, `"Version":1`, no message identity, sequenced after `ManualRetryRequested`). None for a technical redrive, a raced reset (`RacedAdminStore`), a saga with no prior snapshot, or a version mismatch. The marker above `MaxBytes`. An append failure still returns 202 and publishes.
- Notifier: a payload-carrying entry is pushed without it.
- `GetMap` over HTTP with a snapshot in the timeline.

**VSaga.Testing.Tests:** the timeline contains snapshots by default, and the opt-out works.

**Existing tests that must change: none.** I checked every timeline count, index, `Assert.Empty` and `Assert.All` in the test projects:

- `ParallelFanOutJoinTests.cs:90` counts `MessagePublished` only.
- `SagaOrchestratorBusinessKeyRaceTests.cs:162` stays empty, because snapshots are keyed on the resolved instance.
- `ChildSagaFinishedOptInTests.cs:79` holds, because that parent never receives the message.
- `SubSagaCompositionTests.cs:179` and `SagaIdentityScopingTests.cs:107-108` hold.
- `EfCoreStoreTests.cs:304-307`, `SagaEndpointsTests.cs:325-327` and `MongoProviderTests.cs:151-152` seed their own timelines.
- The `Flaky*EventLogStore` failure budgets (`SagaOrchestratorInfrastructureFailureTests.cs:24,120`, `SagaOrchestratorTracingTests.cs:259`) are spent before any persist.

## Commit sequence

1. Add direct unit tests for `SagaMapBuilder` (no production change).
2. Detach a failed event-log append from EF's change tracker.
3. Add `SagaEntryType.StatePersisted` and `SagaStateSnapshot`, with the conformance cases and the enum pin.
4. Skip `StatePersisted` entries in the saga map.
5. Add the timeline payload-redaction seam and stop pushing payloads over SignalR.
6. Stamp the stored sequence number on pushed timeline entries.
7. Record a `StatePersisted` snapshot after every committed transition (options, helper, sites, Core and harness tests, sample binding).
8. Record the state a dashboard retry reset leaves behind.
9. Document state snapshots (docs, doc comments, ADR 0007).
10. After live verification: the history entry and measured capacity numbers.

## Verification

- `dotnet build dotnet/VSaga.slnx` with zero warnings, then `dotnet test dotnet/VSaga.slnx` (Docker is needed for the Postgres, MongoDB and Redis suites).
- Mutation checks, each restored afterwards:
  - snapshot before the persist: the equality and golden tests fail;
  - snapshot after the drain: the ordering test fails;
  - no try/catch: the containment tests fail;
  - no detach: the EF tests fail;
  - no builder filter: the map tests fail.
- `docker compose up -d --build`, then for a Completed `OrderSaga`:
  - `/timeline` has one `StatePersisted` per handled message;
  - the last one's `payloadJson` equals `dataJson` from the detail endpoint;
  - `/map` events contain none;
  - `select count(*) from "SagaEventLog" where "EntryType" = 21` is above zero, and in `Id` order each such row follows its step's `StepSucceeded` (5).
- Retry a business-failed saga, as a caller holding `sagas.retry` once auth lands: `ManualRetryRequested`, then a snapshot with the initial `CurrentState` and `"Status":0`, then the engine's `MessageReceived`.
- Restart `order-processing` with `Orchestrator__MaxStateSnapshotBytes=64`: new snapshots are markers. With `Orchestrator__RecordStateSnapshots=false`: none, and a retry reset adds none for sagas started after it.
- Repeat the first compose check on the mongo (5580) and redis (5680) overlays. Record `MEMORY USAGE` of a `{vsaga:vsaga}:log:*` key and Mongo `collStats` for the capacity docs.

## Cross-assignment contracts

- **Entry contract.**
  - `entryType: "StatePersisted"`.
  - `payloadJson` is the blob (PascalCase, enums as integers, engine fields included), or `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}`, or Mongo's `{"$vsagaPayloadOmitted":…}`, or null when redacted.
  - `messageType`/`messageId` are the inbound message for step success, failure and exhaustion; null for a timeout and a dashboard reset.
  - Every other optional field is null.
- **SPA join rule.** Attach a snapshot to the nearest preceding transition start with the same `messageId`. With a null `messageId`, attach it to the nearest preceding `TimeoutFired` or `ManualRetryRequested`. Adjacency alone is not safe:
  - concurrent messages interleave;
  - the engine retry reuses message ids;
  - `SagaStarted` and its `MessageReceived` share one id;
  - `.CallHttp` logs its reply as a mid-step `MessageReceived` with a fresh id (`HttpCallDefinition.cs:60-62`), which must not open a transition.
- **Gaps.** A transition may have no snapshot, and `Version` can jump by more than one between snapshots.
- **SPA model and pushes.** Add `'StatePersisted'` to the `SagaEntryType` union. A pushed `StatePersisted` arrives without a payload and should trigger a refetch, not an append. The poller can push before the snapshot lands, so for the newest transition fall back to `detail.dataJson`, refetching the detail on push.
- **Start and end data.** "Data at start" is the first snapshot plus `SagaStarted.payloadJson`. "Data at end" is `SagaDetail.dataJson`.
- **Map.** `events` no longer contains snapshot entries, so the timeline-to-map join must pass a rendered entry's sequence number.
- **Auth backend.** It supplies `includePayloads` for `SagaTimelineRedaction.Apply` and owns the `sagas.data` check. The SignalR payload strip is one line in a shared file; whoever lands first makes it. My retry edit is one call after `ResetStateAsync` plus one injected parameter; attribution edits the `ManualRetryRequested` append, a different line.
- **Configuration keys added.** `Dashboard:StateSnapshots:MaxBytes` (not in the shared sketch). Sample-only `Orchestrator:RecordStateSnapshots`, `Orchestrator:MaxStateSnapshotBytes`, `Orchestrator:MaxDeliveryAttempts`.
- **Packaging.** No new project, so no Dockerfile change. Please enable gzip for `application/json` in nginx.
- **ADR numbering.** I use 0007 on the assumption that authentication takes 0006.

## Objections

None. Two refinements stay inside decision C and are flagged for the lead: the EF store's failure-path detach is a provider fix rather than a contract or schema change, and the dashboard's reset snapshot is conditional on the saga already having one.

## Risks
- Storage and read volume grow with steps x state size: GetVisitedStatesAsync reads every snapshot on every message and timeout, the dashboard reads the timeline twice per push, and Redis keeps it all in RAM. Mitigation: the per-snapshot cap, RecordStateSnapshots=false, a documented cost model with re-measured Redis and MongoDB capacity tables, and an ADR follow-up for a payload-free read with a stated trigger (steps x state size around 1 MB).
- Saga state can hold personal or sensitive data, and snapshots copy it into an append-only log with no retention or erase path. Mitigation: sagas.data gates the API, SignalR pushes carry no payload, the off switch, MaxStateSnapshotBytes=0 for size-only markers, and an explicit note in the docs and ADR.
- A committed step can end up with no snapshot: a crash between the commit and the append, an append failure, a poller push that lands before the append, or a saga older than the upgrade. Mitigation: documented as best-effort; the SPA must tolerate gaps and fall back to the live blob for the newest transition; 'Data at end' never depends on a snapshot.
- A failed append left in EF Core's change tracker would be retried by every later commit in the unit of work and could break the drain. Mitigation: detach the entity on failure in EfCoreSagaEventLogStore, with two SQLite tests. Side effect to accept: a transiently failed append is no longer resurrected by a later commit, which is how the in-memory test decorators already behave.
- HandleStepFailureAsync has no room under the method-length analyzer (59 lines, inferred from the two longest bodies in dotnet/src both being exactly 59). Mitigation: the PersistAndSnapshotAsync wrapper replaces the existing call with no added line; the warnings-as-errors build confirms it.
- The UI can attribute a snapshot to the wrong step when messages interleave, when the engine retry reuses a message id, or when a .CallHttp reply is logged as a mid-step MessageReceived. Mitigation: the MessageId join rule in the cross-assignment contract, snapshot-before-drain placement, and an ordering test over re-entrant dispatch.
- Version skew: a dashboard API older than this change renders entry type 21 as a number and returns snapshot payloads to any authenticated caller. Mitigation: deploy the dashboard before the engine hosts (compose builds both together) and say so in the docs.
- State would leak over SignalR to subscribers without sagas.data if the payload strip did not land first. Mitigation: the commit order puts the strip (commit 5) before the engine change (commit 7), with a notifier test.
- A third-party or future provider whose blob is not default System.Text.Json text (ADR 0005 item 1) would break byte equality between snapshot and stored blob. Mitigation: the golden-blob conformance case already forces the format; the ADR lists it as an invalidating condition.
- Consumers of the timeline outside the SPA see a new entry type: VSaga.Testing users asserting on counts or the last entry, and scripts calling /timeline. Mitigation: a history note, docs/testing.md, and the opt-out through SagaOrchestratorOptions.
- The dashboard's reset snapshot could capture a later state if a message is processed between the reset and the read. Mitigation: the recorder skips the append unless the blob's Version equals the reset's version.
- Merge conflicts with the auth assignment in SagaEndpoints.cs, Program.cs and SignalRSagaChangeNotifier.cs. Mitigation: new logic lives in new files and each shared file gets a one-line touch point.
- Every committed step pays a second serialisation and one more store round trip, even when the cap turns the result into a marker. Mitigation: accepted and recorded in the ADR; hosts with very large states switch snapshots off.
- The storage-growth figures (60-90% for the sample) and the blob-size examples are estimates, not measurements. Mitigation: measure on the redis and mongo overlays during live verification before the capacity docs and the history entry are written.

## Open questions
- Recorded snapshots cannot be deleted: nothing in vSaga removes event-log entries, and saga state may hold personal data. Do you need an erase or retention path for snapshots (for example on a data-erasure request)? It would be a persistence-contract change, a new store operation across all four providers, which this round excludes.
