# History: the MongoDB persistence provider

> Describes the commit sequence that built `VSaga.Persistence.MongoDB` on 2026-09-26, following
> [`../adr/0001-mongodb-persistence-provider.md`](../adr/0001-mongodb-persistence-provider.md) and the plan in
> [`../design/mongodb-persistence.md`](../design/mongodb-persistence.md). See
> [`../persistence.md`](../persistence.md#mongodb) for the current reference documentation.

---

## What was built

A fourth persistence provider on the native `MongoDB.Driver` 3.x — not the MongoDB EF Core provider,
which the relational-only `VSaga.Persistence.EFCore` cannot host. The snapshot store is the sole
committer: `EnqueueAsync` stages outbox rows into a Scoped unit of work with zero MongoDB operations,
and the persist writes the snapshot bare when nothing is staged or inside one short multi-document
transaction with the staged rows when something is, peek-commit-clear, so a persist that throws or loses
its version guard leaves the rows staged for the next discard or the next persist in the same scope.
Event-log appends take no dependency on the unit of work — a per-instance `seq` from `sagaSequences`,
then one insert, both at majority write concern outside any transaction — because the redelivery dedupe
rests on that entry surviving an aborted persist. Claims are a `findOneAndUpdate` loop; the business-key
race is a unique partial index; every timestamp keeps its exact ticks beside a readable BSON `Date`; read
preference, read concern and write concern are pinned on every handle and an explicit contradiction in
the connection string is reported by name.

The replica set is a hard prerequisite with no escape hatch. A retrying bootstrapper creates the named
indexes (which doubles as the partial-index capability check) and writes a schema marker once they
exist; the probe runs `hello` and `buildInfo`, lists the indexes and reads the marker, and the health
check re-runs it on every call, Unhealthy on a standalone `mongod`, a `mongos`, a missing primary, a
server below 6.0, a missing index or a contradicting URI.

Both hosts gained a `MongoDb` arm on the `Persistence:Provider` switch the Redis provider had authored,
and `docker-compose.mongo.yml` runs the reference stack against a single-member MongoDB 8 replica set
whose healthcheck initiates the set idempotently.

## How it was verified

**The restore gate (R-5) bit exactly as predicted.** `MongoDB.Driver` 3.6 declares `SharpCompress`
0.30.1 and `Snappier` 1.0.0, both under NuGet advisories, and `TreatWarningsAsErrors` turned the first
solution restore into two errors. Both are pinned centrally to patched versions (0.50.4 and 1.3.1);
transitive pinning promotes them to direct dependencies of the package, which the pack inspection
confirmed.

**Conformance.** The whole `VSaga.Persistence.Conformance` suite — all nine abstract classes, the
atomic-unit-of-work and racing-dispatcher suites included — runs against a MongoDB 8.0 single-member
replica set in a Testcontainer (`MongoDbBuilder.WithReplicaSet`, the plan's Stage 1b spike), one
indexed database per case. It was green on the first run.

**The explain gate caught a real defect.** The plan's `$type: "string"` partial filter on
`ux_sagaType_businessKey` is right, but the first explain of `FindByBusinessKeyAsync`'s query showed the
planner scanning every instance of the saga type through `ix_sagaType_updated`: it does not infer a
string from an equality, so the partial index was ineligible. The lookup now carries the same `$type`
predicate and the explain case pins the index by name — the kind of silent O(n) the plan's "explain
shows no COLLSCAN" gate exists for.

**MongoDB-specific cases**, in `VSaga.Persistence.MongoDB.Tests`: sixteen parallel inserts on one
business key with exactly one winner; a staged row surviving an insert that collides, with no outbox
document written and the row committed by the next persist; the two explain plans; the change poller's
drain over 2000 rows sharing one instant, each visited once and excluded by the following watermark; the
payload guard's marker; an explicit-null destination; the bootstrapper preparing an unprepared database;
the health check's description and stranded-row count; per-instance sequence numbers; a standalone
`mongod` reported Unhealthy naming the replica-set prerequisite while a staged persist fails loudly and
the host never crashes; three connection-string contradictions reported by name; and the document
encodings, the escaped search pattern and the untouched global serializer registry. 109 tests; the whole
solution is 855 tests across 15 test projects, all green. Mutation-checked: inverting the committer's
abort condition fails exactly the five staged-row and atomic-unit-of-work cases.

**Live, under `docker-compose.mongo.yml`.** The sample submitted orders continuously against MongoDB
8.0.32 for several minutes: 426 sagas across all seven of the sample's types by the end (326 Completed,
67 Failed, 32 TimedOut, 1 Running), 95 timeouts fired, sub-sagas completing and their parents finishing on
the child's report, `search=invoice` matching, the Status sort paging across buckets, zero
`Infrastructure error` lines, zero stranded outbox rows, and the dashboard's `/health` reporting
`MongoDB 8.0.32, replica set rs0 (primary), indexes in place, 0 stranded outbox row(s)` throughout.

Then the Stage 9 gate. Two plain `docker kill -s KILL`s on the saga host landed in quiet instants — no
committed row Pending, a clean restart each time — because deferred publishes are rare in the sample.
So the window was opened deliberately: the RabbitMQ container was paused so no inline drain could
complete, and ten seconds later the host was killed with nine sagas running and **one committed outbox
row Pending** — `PaymentAuthorized`, the loopback reply of a `MixedFulfilmentSaga` that had just
transitioned to `AwaitingAuthorization`. The broker was unpaused and the host started. The recovery poller
claimed the row after its grace period and republished it, and the saga's timeline records what
followed: a `MessageReceived` entry for `PaymentAuthorized` carrying the **outbox row's own message id**
(the inline drain's closure had died with the process, so only the poller could have sent it), the
transition to `AwaitingStock`, the 30-second reply timeout that then fired, the compensation that
released stock and voided the payment over REST, and the saga ending `Failed` — the entire downstream
story on the recovered message. The receive arrived two and a half minutes after the kill rather than
seconds, because the throwaway chaos overlay used to widen the window also delayed every inbound message
by 3–6 s and had backed the saga queues up to 29 and 39 messages; with the overlay removed the queues
drained to zero within a minute.

Finally the persisted-volume check the overlay's header promises: `docker compose down` (volume kept)
and `up -d` again. The `mongo` container reported healthy 12 seconds later with `rs.status()` showing
`rs0` in state PRIMARY, its log carrying no second `replSetInitiate`, all 456 saga instances still
present, and `dashboard-api` Healthy — the idempotent healthcheck's `try { rs.status() } catch {
rs.initiate() }` shape took the first branch, as it must for a second `up` not to break the whole stack
behind `dashboard-api`'s gate.

**Measured storage**, by `collStats` after 153 sagas: a snapshot document averages 821 bytes, a
timeline entry 436 bytes (the sample's completed sagas write 10–15), a timeout 189 and an outbox row
506; 900 KB of data compressed to 580 KB on disk, with 1.2 MB of indexes on top. `persistence.md`
publishes it as roughly 6–8 KB of data per completed saga plus about the same in indexes.

## Where the build left the plan

Recorded in the plan's §8.2: the health check is registered by the host rather than from inside
`AddVSagaMongoDb`, following the Redis precedent so every provider package has the same dependency
footprint; the stranded-work mitigation reports only Pending outbox rows older than a threshold (the
plan's Fired-timeouts count would only ever grow); a `sagaCounters` collection mints the numeric row ids
the contracts' `long` ids need; the transaction callback aborts from inside on a version miss rather than
throwing; the Status sort's identity tiebreak follows the `UpdatedAt` direction so one index serves each
walk; `Search` needs no `i` option because the saga type is stored lower-cased beside the original; no
in-process orchestrator-sequence test was added against MongoDB, the live run standing in for it; and the
minimum server version is 6.0.
