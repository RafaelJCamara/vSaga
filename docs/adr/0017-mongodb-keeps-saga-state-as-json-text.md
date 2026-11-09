# ADR 0017: The MongoDB provider keeps saga state as JSON text; native BSON storage is not built, and a derived projection is the shape to build if operators need to query it

**Status:** **Proposed** — 2026-11-09. Not accepted; only the maintainer accepts. This is an investigation
record: nothing here is implemented, and the recommendation is **not to build native BSON storage**.
**Date:** 2026-11-09
**Relates to:** [`0001-mongodb-persistence-provider.md`](0001-mongodb-persistence-provider.md) (names native
BSON as a deferred follow-up, "Neutral" section, and as invalidation 8);
[`0005-saga-state-storage-model.md`](0005-saga-state-storage-model.md) (invalidation 1 says native storage
"warrants its own ADR", which is this one);
[`0003-persistence-contract-clauses.md`](0003-persistence-contract-clauses.md) (clauses 11 and 12, the two
that pin the blob);
[`0007-state-snapshots-in-the-event-log.md`](0007-state-snapshots-in-the-event-log.md) (a second consumer
that needs the blob to be the exact engine text).
**Design:** [`../design/mongodb-persistence.md`](../design/mongodb-persistence.md), Q12 and §10 (decided
"string", native BSON deferred with "a fidelity test suite as its entry gate"). This ADR is that gate's
first pass, and it did not pass.

**Recommendation in one paragraph.** Native BSON for `TState` would be a storage-format change that every
existing Mongo database must be migrated across, that the cross-provider conformance suite cannot pin as it
pins today (its two corruption cases cannot be expressed), and that buys nothing the engine, the dashboard
or any shipped feature uses. A first-hand spike (below) found no size advantage, found the driver's own
class mapping unusable for the contract, and found a byte-identity hazard in the only workable approach.
The one real benefit, being able to query and index business fields from `mongosh`, is available more
cheaply as an opt-in, read-only, derived projection stored beside the unchanged blob (option D), which
should be built only when a named operator need exists. Until then: do nothing.

---

## Context

### How state is stored today

`SagaInstanceDocument` (`dotnet/src/VSaga.Persistence.MongoDB/MongoDocuments.cs:19-105`) is the snapshot
document. It carries the projected fields `ISagaSummaryReader` reads (`sagaType`, `correlationId`, `kind`,
`currentState`, `status`, `version`, the parent pair, `businessKey`, the `createdAt`/`updatedAt` Date +
Ticks pairs) and one `dataJson` string (`:48`), the whole `TState` serialised by
`JsonSerializer.Serialize(state)` with default options. The document is mapped per class by attributes, on
purpose: the header comment (`:8-15`) refuses `BsonDefaults` and global `BsonSerializer` registration
because the registry is process-wide and settable once. Every document carries `sv` (`:21`, `:78`), the
layout version, currently 1; the schema marker in `meta` holds the same number
(`MongoPersistenceBootstrapper.cs:18-19`), and the probe reports the database Unhealthy for any marker
other than the one the provider expects (`MongoServerProbe.cs:110-111`). No read path dispatches on a
document's own `sv` (`[BsonIgnoreExtraElements]`, and nothing reads `SchemaVersion` off a saga document).

The blob is authoritative; the projected fields are derived from it (ADR 0005). Reads deserialise the
blob: `FindAsync` and `FindByBusinessKeyAsync` project `BlobOnly(SagaType, CorrelationId, DataJson)` and
call `JsonSerializer.Deserialize<TState>` (`MongoSagaSnapshotStore.cs:18-50`). Writes are `insertOne` or a
version-guarded `replaceOne` of the whole document (`:53-120`), inside the outbox transaction when rows
are staged (`MongoPersistWrites.cs:37-60`).

### Who depends on the exact text

The blob is not an implementation detail; four things hold the provider to its precise bytes.

1. **A stated contract.** `ISagaSnapshotStore.cs:40-51` says the blob is `System.Text.Json` with default
   options, that "the exact text matters, not just round-trip fidelity", and that providers deliberately
   share no serialisation code so a golden-blob test pins each one.
2. **Conformance clause 12.** `StateBlob_IsSystemTextJsonWithDefaultOptions`
   (`dotnet/tests/VSaga.Persistence.Conformance/SnapshotStoreConformanceTests.cs:651-661`) asserts
   `GetDataJsonAsync` equals a literal (`:20-21`) byte for byte. The literal contains `&` for `&`
   and a raw `+` in `"2026-01-01T00:00:00+00:00"`.
3. **The engine's own snapshots.** `SagaOrchestrator.RecordStateSnapshotAsync`
   (`dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:984-1006`) serialises the state with the same
   `JsonSerializer.Serialize(state)` call and writes it as a `StatePersisted` entry (ADR 0007), and
   `StateSnapshotTests` (`dotnet/tests/VSaga.Core.Tests/StateSnapshotTests.cs:85`, `:252`, `:286`, `:412`,
   `:440`) asserts that text equals `GetDataJsonAsync`. The dashboard's retry records another such snapshot
   after a reset by reading `GetDataJsonAsync` (`SagaResetSnapshotRecorder.cs:83`), and the detail page
   returns it verbatim (`SagaEndpoints.cs:129`).
4. **The reset patch.** `ISagaAdminStore.ResetStateAsync` patches four PascalCase properties inside the
   blob by exact name (`ISagaAdminStore.cs:23-32`); the Mongo implementation does it with `JsonNode` on the
   string (`MongoSagaSummaryReader.cs:86-112`), because "MongoDB's update language cannot patch properties
   inside a string".

`GetDataJsonAsync(sagaType, correlationId)` is non-generic (`ISagaSummaryReader.cs:55`). The dashboard holds
no saga definitions (ADR 0005), so with native storage the provider would have to rebuild the engine's
JSON text from BSON **without** knowing `TState`.

Two conformance cases plant their corrupt rows through ordinary `InsertAsync` using a state whose
converter writes JSON `null` (`NullBlobSagaState.cs`) or raw, cut-off JSON with validation off
(`MalformedBlobSagaState.cs`), pinning clause 11 ("a row whose blob cannot be read is an error, never
`null`"). Both are expressed in terms of a text blob.

### What was deferred, and why it is back

ADR 0001 shipped the string "for parity with EF" and named native BSON a follow-up whose entry gate is a
fidelity suite. ADR 0005 said it "would make the promotion rule partly unnecessary — enough of a shift to
warrant its own ADR". The maintainer has now asked for the case to be made or refuted.

---

## What native BSON would buy

Stated plainly, with whether anything in the repository uses it.

| Benefit | Real? | Used by the engine, dashboard or a shipped feature? |
| --- | --- | --- |
| Query and index business fields in `mongosh` / Compass (`db.sagaInstances.find({"data.OrderId": "..."})`) | Yes, for operators | No. No contract queries business fields; `ISagaSummaryReader` is saga-type-agnostic by design (ADR 0005). |
| Smaller documents | **No** (measured, below) | n/a |
| Cheaper reads (no JSON parse on the server side) | No. `FindAsync` projects the field and STJ parses it either way | n/a |
| `ResetStateAsync` as one server-side `$set` instead of read, `JsonNode` patch, guarded replace (`MongoSagaSummaryReader.cs:86-112`) | Yes, a small simplification of a rare, operator-initiated path | Only that path. |
| Typed values (a `Date` for a business timestamp, `Decimal128` for money) | Partly: only for values the transcoder can type, and dates stay strings (below) | No |
| Dropping some promoted fields, "the promotion rule partly unnecessary" (ADR 0005) | Not for Mongo: the promoted fields exist for indexes and for `ISagaSummaryReader`, which must keep working | No |

The honest reading: one operator convenience and one internal simplification. Neither is a capability the
product exposes.

---

## Evidence: a spike, run outside the repository

Throwaway console project in the session scratchpad
(`.../scratchpad/bsonspike/Program.cs`; not in the tree, nothing committed), `MongoDB.Bson` 3.6.0 (the
version this repo resolves) from the local NuGet cache, no server, no Docker. It measures four things on
states shaped like `OrderSagaState` plus an "awkward" state with the values clause 12 and ADR 0001's Q12
name (`long` above 2^53, `ulong.MaxValue`, `double` extremes, `decimal` scale, `DateTimeOffset`, `Guid`,
enum, `byte[]`, a dictionary with `a.b` and `$x` keys, `&`/`<`/non-ASCII/NUL strings, `[JsonPropertyName]`,
`[JsonIgnore]`, null, nested arrays). Run on 2026-11-09; every number below is from that run.

**1. Size: no win.** For an `OrderSagaState`-shaped value: the STJ JSON is 420 bytes; today's document
field (the same JSON as a BSON string) is 440 bytes; a lossless JSON-to-BSON transcode is 421 bytes; the
driver's class-map of the same object is 433 bytes. Field names are stored per document in BSON as in JSON,
so there is nothing to amortise. Beyond that, ADR 0007's `StatePersisted` entries put a full copy of the
state, as JSON strings, into the event log on every step (the Redis capacity model measures a completed
sample `OrderSaga`'s timeline at 12 528 bytes, 4 216 of them the four snapshots,
[`../persistence.md`](../persistence.md#capacity-model)), and those stay strings regardless. The
current-state document is the smaller part of what a saga costs.

**2. The driver's class mapping (option B below) cannot meet the contract.**
- With the driver default, serialising a `SagaState` throws: `GuidSerializer cannot serialize a Guid when
  GuidRepresentation is Unspecified`. Fixing that needs a process-wide serializer registration, and the
  spike hit the other half of the rule `MongoDocuments.cs:8-15` states: a second registration throws
  `There is already a serializer registered for type Guid`. Referencing the package must not break a host
  that has its own MongoDB usage, so the provider cannot do this.
- `ulong.MaxValue` throws on the class-mapped path (`Arithmetic operation resulted in an overflow`);
  `System.Text.Json` writes `18446744073709551615`.
- A `DateTimeOffset` becomes a subdocument `{DateTime, Ticks, Offset}` where STJ writes
  `"2026-01-01T00:00:00+00:00"`; `MongoDocuments.cs:10-12` already records why that cannot be sorted or
  compared and avoids it for its own fields.
- The class map ignores STJ's attributes: `[JsonIgnore]` is stored, `[JsonPropertyName("renamed_prop")]`
  is stored under the C# name, and property order differs (base class first, where STJ writes the derived
  type first).
- And, decisively, nothing can turn that BSON back into the engine's JSON text without the CLR type, which
  `GetDataJsonAsync` does not have.

**3. A JSON-to-BSON transcode (option C) is workable for most values and hazardous for one common one.**
Parsing the STJ text with `Utf8JsonReader` into a `BsonDocument` (ints as `Int32`/`Int64`, other numbers as
`Decimal128` when they print back as the same lexeme) and writing it back with `Utf8JsonWriter` reproduced
byte for byte: every `double` tried (`0.1`, `1E-07`, `5E-324`, `1.7976931348623157E+308`, `1E+21`, `-0`,
`0.30000000000000004`), every `decimal` scale tried (`12.50`, `0.0000001`, `1.000`, 28-digit values), the
`long` extremes, `ulong.MaxValue`, a 40-digit integer, duplicate and empty keys, deep nesting, and strings
with `&`, `<`, quotes, `é`, an emoji and NUL (the default encoder is deterministic, so escapes survive).
Not all of that is *native*: values that are not integers and do not print back identically must be kept as
their lexeme in a tagged wrapper, the spike's `{"$lexeme": "..."}`, which defeats the point for exactly the
awkward values.

It **failed byte identity on both test states, including the sample order**: STJ writes a
`DateTimeOffset` with a raw `+` (`"2026-01-01T00:00:00+00:00"`), while a transcoder that sees only a JSON
string and writes it back escapes `+` as `+`. The reader cannot tell the two apart, and a user
`string` property containing `+` is escaped by STJ, so no generic rule is right for both. Every real saga
state has `CreatedAtUtc` and `UpdatedAtUtc`, so this is not an edge case: **the clause-12 golden literal
itself would fail.** Possible repairs (carry the raw lexeme of every string, or a per-document "verified
identical" fallback to the old layout, which every document would take) each give back what the native form
was meant to add.

**Not verified.** Whether a MongoDB 6.0+ server accepts, indexes and queries top-level and nested field
names such as `$x` and `a.b` that a `Dictionary<string, T>` state would turn into BSON field names (I did
not run a server). The spike did not test `float`/`Half`/`BigInteger` shapes, polymorphic `JsonDerivedType`
states, or a custom `JsonConverter` that calls `WriteRawValue` (clause 11's malformed-blob state is one
such, and a transcoder cannot represent it at all). Actual document-size effect on a production state
class, and any read or write latency, were not measured.

---

## Options weighed

### A. Do nothing — keep `dataJson` as a string — **proposed**

*Pros.* Zero migration, zero conformance change, the blob contract and ADR 0007's equality stay true by
construction, and the Mongo document stays the same shape as the Redis hash field and the EF `text`
column, so the model "ports almost unchanged" (ADR 0005). `ResetStateAsync`'s `JsonNode` patch is the only
code that pays for the string, and it is 27 lines on a rare path.

*Cons.* Business fields stay invisible to `mongosh`. An operator wanting "all `OrderSaga` where `OrderId`
is X" must use the promoted `businessKey` (if the saga declared one) or fetch and parse.

### B. Map `TState` with the driver's class map (`BsonClassMap`) as the stored shape

*Pros.* The idiomatic MongoDB approach; no transcoder to write.

*Cons.* **Rejected.** The spike's section 2: needs a process-wide serializer registration this provider
refuses to make, overflows on `ulong`, ignores STJ's attributes and property order, stores
`DateTimeOffset` as a subdocument, and cannot rebuild the engine's JSON text for the non-generic
`GetDataJsonAsync`. It would make the stored state and the engine's `StatePersisted` snapshot two
independent serialisations of one object, which ADR 0007 requires to be equal.

### C. Transcode STJ's text to BSON and back; the BSON is the stored shape

*Pros.* `System.Text.Json` stays the one serializer, `TState` semantics (attributes, converters, enums as
numbers) are untouched, and `GetDataJsonAsync` stays non-generic (BSON to JSON needs no CLR type). The
document becomes queryable at `data.OrderId`.

*Cons.* **Rejected as the primary layout.** A custom transcoder is a new fidelity-critical component with
its own test matrix, and the spike broke it on the first realistic input (the raw `+`). Dates remain
strings, so "typed values" is not delivered. Numbers outside Int32/Int64 need a lexeme wrapper to stay
byte-identical. Dotted and `$`-prefixed dictionary keys become field names (server behaviour not verified).
The two corruption cases of clause 11 cannot be planted (a malformed blob has no BSON form). Dual-write
during the migration doubles the state's footprint (below). Size is unchanged.

### D. Keep the blob authoritative; add an opt-in, derived, read-only `data` projection beside it

The document keeps `dataJson` exactly as now. When `VSagaMongoOptions.ProjectState` (hypothetical name) is
on, each persist also writes `data`, a best-effort BSON rendering of the same JSON (the spike's transcode,
**lossy is acceptable**: nothing reads it back). A value the rendering cannot represent, or a key the server
refuses, omits the projection for that document and logs once; the blob is unaffected.

*Pros.* Delivers the one real benefit (query and index business fields in `mongosh`; optional user-created
indexes on `data.*`) with **no change** to any contract, to clause 11 or 12, to `GetDataJsonAsync`, to
`ResetStateAsync` (which would also rewrite `data`, or drop it so the next persist rebuilds it), or to
what an older provider binary reads. No migration is required: documents without `data` are valid, and
`sv` stays 1. Rolling upgrades and rollbacks are safe because the field is additive and ignored
(`[BsonIgnoreExtraElements]`).

*Cons.* Off by default or it doubles the state portion of every write and of the 16 MB budget (ADR 0001 has
`dataJson` under that cap already). Two copies can disagree for a document written while the option was off
or after a failed render, so it must be documented as advisory ("never use it for decisions"), the opposite
of ADR 0005's never-disagree invariant, which holds because the blob stays authoritative. Needs a
backfill story for old documents if someone wants them queryable (an operator script, not the provider).
It is a Mongo-only feature, which is fine for a convenience and would be wrong for a contract.

*Not recommended yet.* There is no named user asking for it; the ADR records it as the agreed shape so the
next request starts here instead of at B or C.

### E. Let a saga definition declare extra promoted fields (ADR 0005's rule, generalised)

*Pros.* Works on every provider, not only Mongo, and queryable fields are what the promotion rule is for.

*Cons.* **Rejected for this ADR.** It is a DSL feature plus a dynamic schema story: EF needs real columns
and so a migration per declared field, which ADR 0005 chose to avoid ("adding a saga type must not require a
schema migration"). Out of scope; mentioned so it is not rediscovered as free.

### F. Switch the whole provider to documents-per-saga-type collections

**Rejected.** It reopens ADR 0005's reasons for one shared table (the dashboard's type-agnostic reads, no
schema step for a new saga type) for a benefit that is only (D)'s.

---

## Decision

**Do not store `TState` natively.** The Mongo provider keeps `dataJson` as the `System.Text.Json` string,
byte-identical to EF Core's `DataJson`, as ADR 0001 and design Q12 decided. This ADR closes Q12's
follow-up with a measured "no", and ADR 0001's invalidation 8 and ADR 0005's invalidation 1 stay
unfired.

**If operators ask to query business fields, build option D** (an opt-in derived projection), not B or C.
D needs its own small ADR at that time (it adds a public option and a stored field), but its shape and
trade-offs are the ones recorded here.

The rest of this section answers the maintainer's questions about the options that *would* change storage,
so the cost of saying yes is on record.

### The migration story, if B or C were built anyway

The data is durable, in production-tier Mongo databases, with in-flight sagas, and `Persistence:Provider`
is documented as a greenfield choice with no provider-to-provider migration (`docs/persistence.md`). A
layout change needs a version-to-version one, which the provider has not had to write yet.

1. **A reader that understands both layouts.** `sv` exists on every document but nothing reads it. The
   reader would branch on `sv`: 1 means `dataJson`, 2 means native.
2. **The schema marker.** A database with marker 1 makes a version-2 provider report Unhealthy
   (`MongoServerProbe.cs:110-111`), and the reverse. The marker policy must become "accepts {1, 2}", and
   the bootstrapper must write 2 only when the operator opts in.
3. **Rolling upgrades.** A version-1 binary reading a version-2 document finds no `dataJson`; its
   projection yields an empty string and `JsonSerializer.Deserialize<TState>("")` throws (by reading
   `MongoSagaSnapshotStore.cs:48-50`, not run). So during a rolling deploy there must be a **dual-write**
   phase where new binaries write both `dataJson` and the native form, which doubles the state portion of
   every write for as long as old binaries may run, followed by a release that stops writing `dataJson`.
4. **Back-fill.** Idle and completed sagas are never rewritten by the engine. They stay `sv` 1 forever
   unless a back-fill (a script or a background job) rewrites them; the dual reader makes that optional,
   but an index on `data.*` would silently cover only back-filled documents.
5. **Rollback.** Once `dataJson` stops being written, a rollback to the previous release loses every
   saga written since. Dual-write is the only safe window.

### The conformance changes, if B or C were built anyway

- Clause 12's golden blob stays and must still pass through `GetDataJsonAsync`; with C it fails today (the
  `+` finding), so C needs the lexeme fallback or a per-string raw form first.
- The two clause-11 cases (`Find_ForARowWhoseBlobIsJsonNull_Throws`, the malformed-blob case) plant bad
  rows through `InsertAsync` with a converter. They have no BSON form. Mongo would need a provider-level
  test that plants the bad row directly, and the shared suite would lose its "without reaching into any
  provider's storage" property for this provider, or a new capability flag would exempt it (and
  `ConformanceCoverage` pins flags so none can be added quietly).
- A new fidelity suite is required, as Q12 said: `long` beyond 2^53, `ulong`, `decimal` scale, double
  extremes, `DateTimeOffset`, enum, `byte[]`, null versus missing, `[JsonIgnore]`/`[JsonPropertyName]`,
  dotted and `$` keys, NUL and astral characters, and the Mongo-specific "no-converter" check.
- `ResetStateAsync`'s admin case gains a native variant; the `StatePersisted` equality cases in
  `VSaga.Core.Tests` need a Mongo-backed run, and they currently run only against the in-memory store
  (`StateSnapshotFixtures.cs:119`).
- Per `AGENTS.md` rule 3, anything touching a persistence provider is live-verified against
  `docker-compose.mongo.yml`, including a rolling-upgrade run across both layouts and a kill-host outbox
  check.

---

## Consequences

### Positive

- No storage format change, no migration, no new test matrix; ADR 0001, 0003 (clauses 11, 12), 0005 and
  0007 stay true as written.
- The engine's single-serializer property is preserved: the stored blob, the `StatePersisted` snapshot and
  the dashboard's `dataJson` are one `JsonSerializer.Serialize(state)` result.
- The investigation leaves a measured record where there was a one-line deferral, and an agreed
  fallback (D) for the case that motivated it.

### Negative

- Business fields stay opaque to Mongo tooling until D is built.
- `ResetStateAsync` keeps its read, patch and guarded-replace shape on Mongo.
- The question can be asked again without new evidence unless the triggers below are used.

### Neutral

- `payloadJson` and `headersJson` stay strings in any case (open header-name set with dots and dashes;
  ADR 0001 Q12).
- Redis and EF Core are unaffected by any option here.

---

## What would invalidate this decision later

1. **A shipped feature queries business fields**, for example a dashboard search or filter on saga data.
   It would have to be provider-agnostic (ADR 0005 invalidation 3), which points at E, not at native Mongo
   storage, but it would end the "no consumer" argument.
2. **A named operator need to query or index `TState` fields in Mongo.** That triggers option D.
3. **The blob contract is relaxed**: if clause 12 stops requiring exact text (for example the engine stops
   requiring `StatePersisted` to equal the stored blob), the transcode's `+` hazard stops being fatal and
   C is worth re-measuring.
4. **The 16 MB document cap becomes the binding limit** for real state sizes, and a native or split layout
   would relieve it. Nothing in the repository suggests that today (about 400 bytes per sample state).
5. **MongoDB or the driver gains a JSON-text-preserving native type**, or STJ gains a way to emit a
   document's exact original lexemes; either removes the fidelity problem.

---

## Open questions

1. **Is there a concrete operator request behind this?** If one exists and names the queries, option D
   becomes a feature ADR rather than a footnote, and its option name, default and backfill story are the
   questions to settle. If none exists, accepting this ADR as written is enough.
2. **Server acceptance of `$`-prefixed and dotted dictionary keys** on a 6.0+ replica set, for D's
   projection and for any future C. Needs a one-off check against the compose Mongo (not run here; Docker
   was not used).
3. **Should clause 12 be reworded** to say what really matters (the engine's snapshot text equals the
   stored blob; `ResetStateAsync` patches by name) rather than "exact bytes of default STJ"? Accepting that
   would be a contract change for all five providers, and is not proposed here; it is the only thing that
   would make C viable.
