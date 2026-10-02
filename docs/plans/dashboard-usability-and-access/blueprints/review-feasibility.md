# Review: feasibility

No blockers. The engine-snapshots and detail-ux blueprints and the scoped merge can be built as written. I found two major cost gaps and nine refinements. Nothing was built or run; line numbers are from the current tree under C:/Users/rafae/Documents/Projects/vSaga.

**engine-snapshots**
- **Insertion points:** all four exist as described (SagaOrchestrator.cs:761, 679, 316-321, 161) and each follows its persist. No append sits between outbox staging and the persist. The failure path never stages the deferred queue, so the snapshot ahead of DiscardDeferredPublishesAsync cannot commit a row.
- **When the snapshot append throws:**
  - EF Core: the entity stays Added in the scoped context. With the planned detach, the step only loses its snapshot. Without it, the next SaveChangesAsync in the scope re-sends the row, and a row that keeps failing escapes the drain's catch. The detach commit therefore has to land before the engine commit, as sequenced.
  - MongoDB: the counter is already incremented, so a failed insert leaves a gap in seq and nothing else.
  - Redis: one Lua script, RPUSH only for this entry type. A rejection changes nothing; a client timeout may still have appended the entry.
  - In-memory: cannot throw.
- **Unaffected:** compensation order (ToState is null), dedupe (all four providers count only SagaStarted and MessageReceived), both retry paths (they select StepFailed and SagaStarted by type), and the map (filtered at the source).
- **Existing .NET tests:** none break. Every engine-driven timeline assertion filters by entry type. The three count assertions run over seeded timelines or one filtered type. The flaky event-log decorators spend their failures before any persist.
- **Snapshot text equals the stored blob:** same generic Serialize call on the same object, with nothing between the store returning and the helper.
- **Cost with the defaults:** fine for the sample, unbounded per saga (first finding).

**detail-ux**
- **Fold:** it covers every append site (orchestrator, SagaContext, CompensationRunner, HttpCallExecutor, the retry endpoint) for sequential handling. Two gaps are in the findings: `pending` never expires, and entries without ids are guessed under concurrent handlers.
- **Budgets:** realistic. The last build leaves 96,135 B of initial headroom and saga-detail.scss shrinks.
- **SagaMap focus input:** it survives recreation. A constructor effect is attached to the declaring view and runs after the inputs are set and before the child's first template pass (refreshView in @angular/core 21.2.24, fesm2022/_debug_node-chunk.mjs:5773-5796).
- **Specs:** the plan covers what breaks; I found no missed spec.

**Scoped merge (auth-backend)**
- **Order:** a deterministic total order, identical to the provider's inside a type. Cross-type ties are ordinal ascending on every provider. That differs from MongoDB's and Redis's descending arms and from Postgres collation, which the ListAsync contract allows.
- **TotalCount and paging:** exact over unchanged data inside the bound; the edge cases are in the findings.
- **Cost at the bound:** about 70 ListAsync calls and 35,000 summaries for one page on EF Core or MongoDB. On Redis it depends on the list shape (second finding).

### Critical Files for Implementation
- C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs
- C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.EFCore/EfCoreSagaEventLogStore.cs
- C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.Redis/RedisSagaSummaryReader.cs
- C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs
- C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/components/saga-map/saga-map-layout.ts

## 1. [major] (engine-snapshots)
With the defaults, snapshot volume per saga is unbounded and the engine re-reads all of it before every step. MaxStateSnapshotBytes caps one snapshot (256 KiB), not the instance. GetVisitedStatesAsync loads the whole timeline, payloads included, on every message and timeout.

Two ordinary shapes show the growth:
- A saga whose state grows as it collects replies (500 items at about 200 B each) stores about 25 MB of snapshots and reads tens of MB per late message.
- A 200 KB state over 50 steps stores 10 MB.

Redis is the exposed provider. The entry is stored as JSON inside JSON, so every quote in the blob costs six bytes (about 1.6 to 2 times the raw size). It is all held in RAM and read back with one LRANGE per message. It also counts toward WriteMemoryThreshold, above which every persist is refused and messages dead-letter.

For the sample (400 B state) the cost is small: about 1 KB per snapshot, roughly half again on an OrderSaga's 7.3 KB timeline. The dashboard pays as well: each push re-reads the timeline twice on the server and downloads it once.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:616 and :272 call GetVisitedStatesAsync (:940-949), which reads every entry. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.Redis/RedisSagaEventLogStore.cs:33-35 (payload serialised as an escaped string) and :44 (LRANGE of the whole list). C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.Redis/RedisPersistScripts.cs:57, 74-78 (persist refused above the threshold). C:/Users/rafae/Documents/Projects/vSaga/docs/persistence.md:323-345 (LRANGE blocks the server; 5 to 10 KB per saga today; past the threshold every persist is refused). C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs:44, 122. C:/Users/rafae/Documents/Projects/vSaga/docker-compose.redis.yml:25 (maxmemory 256mb).

**Fix:** Add a per-instance budget. It needs no contract or schema change, because the engine already holds the timeline at that point.
- Have GetVisitedStatesAsync also sum the StatePersisted payload lengths.
- Carry the sum on SagaContext, which already reaches all three persist sites.
- Add SagaOrchestratorOptions.MaxStateSnapshotBytesPerSaga (suggested default 1 MiB, the ADR's own trigger; 0 for unlimited).
- Past the budget, success-path and timeout snapshots become the existing size marker. Step-failure and exhaustion snapshots are still recorded in full, which answers the author's objection that a budget drops the snapshots an investigation needs.

Early snapshots are kept, so "Data at start" still works; "Data at end" never depended on a snapshot.

If the lead prefers no new option, state the worst case in ADR 0007 and docs/persistence.md, and set Orchestrator__MaxStateSnapshotBytes explicitly in docker-compose.redis.yml.

## 2. [major] (auth-backend)
The stated bounds (50 types, 10,000 rows) do not bound the cost on Redis for any list other than the plain UpdatedAt sort. With a saga-type filter plus a Status sort, or plus a status or kind filter, the Redis reader intersects indexes and returns every matching member to the client before paging. The merge issues that once per visible type, and again on every refill.

A Status-sorted page 1 for a caller with 50 visible types is 350 ZINTER calls returning every member of those types, about 70 MB for a million sagas. The single-threaded server is blocked while each one runs. The unscoped Status sort reads only seven counts and one page. Only the unfiltered UpdatedAt sort stays rank-served per type.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.Redis/RedisListQuery.cs:36, 68 (rank-served only with at most one equality index; the type filter is that one). C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.Redis/RedisSagaSummaryReader.cs:62-70, 85-104, 116-141 (full materialisation per call). C:/Users/rafae/Documents/Projects/vSaga/docs/persistence.md:323-327 (head-of-line blocking).

**Fix:** Keep the merge as designed for the rank-served shape. For the other shapes:
- Refuse depth beyond one chunk (page × pageSize ≤ 500) so no refill happens.
- Lower the type bound, with the 400 naming the saga-type filter.
- Say in ADR 0006 that on Redis these requests read every member of every visible type.

Record the real fix as the follow-up: a multi-type filter on SagaListFilter. Redis can serve it by filtering the member suffix, since the member string already carries the saga type, with no per-type intersection.

## 3. [minor] (engine-snapshots)
The snapshot append is best-effort, but it sits between the commit and the first deferred publish and is limited only by the store's own timeout. If it stalls, every deferred publish and the ack wait behind a diagnostic write. If it stalls for DispatchGracePeriod (30 s), the recovery poller republishes the rows and the inline drain then sends them again. Npgsql's default command timeout is also 30 s, and nothing in the repo sets one.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:757-789 (stage, persist, drain). C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOutboxOptions.cs:35. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOutboxDispatcherHostedService.cs:79.

**Fix:** Run the append under its own short deadline: a linked CancellationTokenSource with CancelAfter of a few seconds, exposed as an option beside the other two. Treat a timeout like any other swallowed failure. The EF detach fix already makes a cancelled append safe. The Redis append ignores the token, but its client timeout is 5 s.

## 4. [minor] (engine-snapshots)
The reset recorder applies the dashboard's own cap (Dashboard:StateSnapshots:MaxBytes, default 262144), not the engine host's. The blueprint offers MaxStateSnapshotBytes = 0 as the way to keep sensitive state out of the log. A host that sets it still has StatePersisted markers in its timelines, so the "timeline already contains a StatePersisted entry" test passes, and a retry reset writes the full state blob into the log.

**Evidence:** Blueprint section 7: "Append SagaStateSnapshot.CreateEntry(correlationId, sagaType, dataJson, options.MaxBytes)", and its risk list: "MaxStateSnapshotBytes=0 for size-only markers". C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs:137, 181.

**Fix:** Derive the cap from the saga's own timeline. If the most recent StatePersisted payload is a $vsagaStateOmitted marker, use the smaller of its limit and the dashboard cap; otherwise use the dashboard cap. The dashboard key then acts only as an upper bound and the two hosts cannot disagree. Add this case to RetryStateSnapshotTests.

## 5. [minor] (auth-backend)
On EF Core and MongoDB the cost at the depth bound is 50 primes plus up to 20 refills, each a COUNT and a page query, run one after another (about 140 statements). Up to 35,000 summaries are materialised to return at most 100 rows. Three things make it heavier than it needs to be:
- Every scoped list request first calls GetSagaTypesAsync. That is a DISTINCT over SagaType and Kind that no index covers on EF, and a $group over the whole collection on MongoDB.
- Priming with chunk = page × pageSize loads k × page × pageSize rows even on shallow pages. Page 20 at 25 rows loads 25,000 rows across 50 types.
- EF has no index on (SagaType, UpdatedAtUtc), so a per-type page either walks the UpdatedAtUtc index filtering by type or sorts every row of the type.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.EFCore/EfCoreSagaSummaryReader.cs:105-117 and :43-48. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.MongoDB/MongoSagaSummaryReader.cs:67-77. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Persistence.EFCore/VSagaDbContext.cs:101-112.

**Fix:** - When the caller's scope has at most MaxMergedTypes names, query those names directly and skip GetSagaTypesAsync; otherwise cache its result for a few seconds.
- Prime each stream with pageSize rows and grow the refill size, doubling up to 500.
- Take TotalCount from the primes only.
- Add a (SagaType, UpdatedAtUtc) index in a new Postgres migration, following 20260924142250_AddSagaInstanceUpdatedAtUtcIndex.

## 6. [minor] (auth-backend)
Paging is exact only over unchanged data and inside the bound.
- Refills are separate offset queries. A saga of that type updated between two fetches shifts the offsets, so the same row can be emitted twice in one response. The unscoped path can only repeat a row across requests. The list tracks rows by sagaType plus correlationId, so a repeat is a duplicate key.
- TotalCount still counts rows beyond the depth bound, so the pager offers pages that answer 400. The list shows "Could not reach the vSaga Dashboard API. Is it running?" for every error, including that 400 and today's Redis scan-limit 400. The auth-frontend blueprint special-cases only 403.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-list/saga-list.html:79. C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-list/saga-list.ts:43, 242-245. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/Endpoints/SagaEndpoints.cs:107-113.

**Fix:** - In the merge, keep a set of emitted (sagaType, correlationId) pairs and skip repeats.
- In the SPA, show the body's error text for a 400 and return to the last good page.
- Put the bound in the 400 body (for example maxPage) so the pager can stop offering pages past it.

## 7. [minor] (detail-ux)
`pending` is decided by position, not age, so it never expires. A final step with no snapshot on a Running saga reads "Not recorded yet. The step may still be committing; this view refreshes by itself." Two engine paths leave exactly that state for good:
- A step that lost its persist race. Its MessageReceived and StepSucceeded stay in the log, the redelivery is skipped as a duplicate, and the saga stays Running on the winner's state. This is the lost update that resiliency work most needs named.
- A timeout that was claimed but unhandled, or whose handler threw. Only TimeoutFired is logged.

The 1500 ms follow-up fetch has the same trigger, so it also fires after every push for sagas recorded without snapshots.

**Evidence:** Blueprint: "effectiveSnapshotState reports the final step's missing as pending while the saga is Running or Compensating". C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:763-778 and :425-429 (loser rethrows, redelivery skipped), :267-278 (claim, then return when unhandled). C:/Users/rafae/Documents/Projects/vSaga/dotnet/tests/VSaga.Core.Tests/SagaOrchestratorConcurrencyRedeliveryTests.cs:223-239.

**Fix:** Report `pending` only while the step's newest entry is younger than a few seconds; occurredAtUtc is on every row. After that, fall back to `missing`, or to `not-persisted` for a timeout with no outcome. Schedule the follow-up fetch under the same age test, and only when the timeline holds at least one StatePersisted entry. Add both cases to saga-transitions.spec.ts.

## 8. [minor] (detail-ux)
The orchestrator highlight for plain entries does not show in the sagas this feature is for. computeNodeStates keeps a node failed once the replay has passed failureEventIndex. The server sets that index at the first StepFailed, TimeoutFired or DeliveryExhausted, including a timeout that was handled normally. After a failure and a retry, or after any timeout, jumping to a later StepSucceeded shows the orchestrator in the failed style while the banner says it is highlighted.

Routing the highlight through computeNodeStates also changes ordinary playback: the orchestrator turns active on every plain event.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/components/saga-map/saga-map-layout.ts:219-227. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs:122-123, 184-190.

**Fix:** Leave computeNodeStates alone and render focus as its own state. Apply a `node--focus` class (an outline) in SagaMap when focus() resolves to an event with neither an edge nor a node. It stays visible on a failed node, and no existing layout spec changes.

## 9. [minor] (detail-ux, engine-snapshots)
The fold is exact for sequential handling. Under concurrent handlers of one instance (the sample's fan-out and choreography sagas) it guesses.
- TimeoutScheduled, SagaCompleted, the Compensation entries and a .CallHttp request carry no message id, so they follow "the step touched last".
- A .CallHttp reply logged after another handler's outcome opens a step of its own that stays in flight.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:736, 741 (no messageId passed). C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Dsl/CompensationRunner.cs:30, 54, 58. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Http/HttpCallDefinition.cs:54-55, 60-62. C:/Users/rafae/Documents/Projects/vSaga/dotnet/src/VSaga.Core/Runtime/SagaContext.cs:173.

**Fix:** Stamp the ids at the source, as two small edits in the engine change:
- Pass messageId to the TimeoutScheduled and SagaCompleted entries in HandleStepSuccessAsync.
- Have SagaContext's ISagaContextLogSink.LogAsync fill a null CausationId with the inbound message id.

Neither feeds dedupe (only SagaStarted and MessageReceived count), visited states (ToState is unchanged) or the map (only MessageReceived causation is stitched).

In the fold, attach an entry by messageId, then by causationId, before falling back to adjacency. Send a MessageReceived with no outcome of its own to the step holding the outbound entry its causationId names.

## 10. [minor] (detail-ux)
Open inspectors are lost whenever the user uses the jump. SagaTimeline owns openKeys, but the timeline subtree is destroyed when the Map tab shows, and every row click switches to the Map tab. A user who opens a step's data, jumps to the map and comes back finds every inspector closed.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dashboard-web/src/app/pages/saga-detail/saga-detail.html:109-147 (tabs are exclusive @if branches). Blueprint: "It owns openKeys" and "showOnMap(sequence): tab 'map'".

**Fix:** Hold openKeys in SagaDetail and pass it to SagaTimeline as a model input, so it outlives the tab switch. Reset it when the saga changes, with the other per-saga state.

## 11. [minor] (engine-snapshots)
One in-repo timeline consumer is missing from the blueprint's list. The four persistence samples print every timeline entry with a count. With snapshots on they will print StatePersisted lines described by the inbound message type, which reads as the message arriving twice.

**Evidence:** C:/Users/rafae/Documents/Projects/vSaga/dotnet/samples/Persistence/VSaga.Samples.Persistence.Common/CheckoutDemo.cs:140-154, 164-172. C:/Users/rafae/Documents/Projects/vSaga/dotnet/samples/Persistence/README.md:49, 64.

**Fix:** Give Describe a StatePersisted arm (for example "state saved, N bytes") or filter the type out, and mention the entry in that README. No sample calls ConfigureOrchestrator today, so the Orchestrator binding in the OrderProcessing host will be the first use of it.
