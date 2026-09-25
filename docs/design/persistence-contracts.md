# Design: persistence contracts and the conformance suite

**Status: planned, nothing built. Decisions accepted.** Recorded in
[`../adr/0003-persistence-contract-clauses.md`](../adr/0003-persistence-contract-clauses.md); what
remains is execution. Nothing here depends on a MongoDB or Redis provider ever being built.

This was previously carried as "Stage 0" inside [`mongodb-persistence.md`](mongodb-persistence.md) §3.
It was extracted because it is **not** a provider's work: it writes down contracts the engine already
depends on, extracts a cross-provider conformance suite, and fixes the divergences that suite surfaces
in the two providers shipping today. Both provider plans consume it rather than owning it.

**This plan does not extract a shared persistence package.** That was evaluated and rejected — see §8.

Every claim carries a `file:line`, verified against commit `b2b99fd`. Re-grep once the tree moves.

---

## 1. Why this exists

The engine places requirements on a persistence provider that the contracts largely do not state. The
status is more mixed than "undocumented", and the distinction matters because two clauses must
*correct* existing text rather than add to it:

| Clause status | Which |
| --- | --- |
| Stated and correct | 9 (`SagaSummary.cs:34` already says `Search` is case-insensitive) |
| Stated | 4 (`ISagaOutboxStore.cs:41-49`, "Does not commit on its own") |
| **Stated wrongly — must be corrected** | 7 (`ISagaAdminStore.cs:5-12` claims the reset happens "without touching its business DataJson", which is false) and 6 (`ISagaEventLogStore.cs:19` says "an entry with this (sagaType, correlationId, messageId)" — false for a `MessagePublished` entry) |
| Unstated | 1, 2, 3, 5, 8, 10, 11, 12 — `ISagaEventLogStore.cs:17` has no doc comment at all |

The consequence is not hypothetical. The two shipped providers were written by copying from each other,
and they have **eight verified divergences and three shared defects** today.

### 1.1 Divergences — the providers disagree

| # | Behaviour | EF Core | In-memory | Fix |
| --- | --- | --- | --- | --- |
| D1 | `ResetStateAsync` patches `Version` into the state blob | Yes (`EfCoreSagaSummaryReader.cs:107-117`) | **No** (`InMemorySagaStore.cs:239-241` patches only `CurrentState`/`Status`) | F3 |
| D2 | Claims return earliest-first | Yes (`EfCoreSagaTimeoutStore.cs:60`/`:102`, `EfCoreSagaOutboxStore.cs:90`/`:132`) | **No** — arbitrary `ConcurrentDictionary` order (`:312-330`, `:378-396`) | F6 |
| D3 | `Update` preserves the `UpdatedAtUtc` `PersistAsync` stamped | Yes | **No** — restamps (`InMemorySagaStore.cs:112`) | F5 |
| D4 | `Search` is case-insensitive | **No** — *both* disjuncts are case-sensitive (`EfCoreSagaSummaryReader.cs:28`) | Yes, both halves `OrdinalIgnoreCase` (`:158`) | F2 |
| D5 | `ResetStateAsync` under a concurrent write | Throws a raw `DbUpdateConcurrencyException` (`Version` is a concurrency token, `VSagaDbContext.cs:105`) | Retries in a CAS loop (`InMemorySagaStore.cs:232-254`, CAS at `:252`) | F3, F4 |
| D6 | `Update` restores `state.Version` on throw | Yes (`EfCoreSagaSnapshotStore.cs:86`) | **No** — `:111` bumps, CAS fails at `:115`, loop rethrows from `:90-91` with the version still bumped. Violates clause 1 | **F10** |
| D7 | `UpdateAsync` maps a business-key collision | **No** — catches only `DbUpdateConcurrencyException` (`EfCoreSagaSnapshotStore.cs:80-88`) | Yes — `SagaAlreadyExistsException` (`:107-109`) | F7 |
| D8 | `GetTimelineAsync` returns ascending append order | Yes — `.OrderBy(x => x.Id)` (`EfCoreSagaEventLogStore.cs:37-40`) | **No** — returns the list unsorted (`:270-274`), and `AppendAsync` takes its sequence at `:259` *before* the `AddOrUpdate` at `:262-265`, so two concurrent appends can land out of order. Clause 5 holds by luck | **F11** |

Two further divergences are **decided, not fixed** — they need a clause before a fix exists:

| # | Behaviour | EF Core | In-memory |
| --- | --- | --- | --- |
| D9 | A null/corrupt state blob | Returns `null` (`EfCoreSagaSnapshotStore.cs:16`) — indistinguishable from "no such saga", so the orchestrator would start a duplicate | Throws `InvalidOperationException` (`:47-48`), so the message redelivers |
| D10 | `InsertAsync` failure classification | Maps *every* `DbUpdateException` to `SagaAlreadyExistsException` (`:40-47`; its own comment admits this) | Only a failed `TryAdd` |

And two where the difference is **legitimate but must be pinned by the suite**, not unified:

- `DiscardPendingAsync` scope — EF detaches `Added` entries only (`EfCoreSagaOutboxStore.cs:50-65`);
  in-memory removes any Pending row including a durable one (`:362-375`), because its `EnqueueAsync`
  commits immediately (`:332-346`).
- Outbox headers — EF rebuilds through JSON into a fresh `StringComparer.Ordinal` dictionary
  (`:26`/`:139`); in-memory stores the caller's **live reference** (`:343-344`), so a caller mutating
  that dictionary after enqueue changes the stored row on one provider only.

### 1.2 Shared defects — both providers are wrong the same way

| # | Defect | Fix |
| --- | --- | --- |
| S1 | Neither patches `UpdatedAtUtc` into the state blob on `ResetStateAsync`, so blob and projection disagree | F3, F4 |
| S2 | The two `UpdatedAt` sort arms apply **no** tiebreak at all (`EfCoreSagaSummaryReader.cs:51-52`, `InMemorySagaStore.cs:176-177`); only the `Status` arms tiebreak, and the class comment describes only those | F1 |
| S3 | Both stamp `UpdatedAtUtc` with `DateTimeOffset.UtcNow` in `ResetStateAsync` (`EfCoreSagaSummaryReader.cs:117`, `InMemorySagaStore.cs:249`), bypassing the `TimeProvider` every engine write goes through (`SagaOrchestrator.cs:895`) | F4, F3 |

**`Search` has zero tests on either provider** — no `Search =` appears anywhere under `dotnet/tests/`.
So D4 is not explained by "each provider has its own tests that happen to overlap"; it was never tested
at all.

---

## 2. Contract clauses

XML documentation on `VSaga.Abstractions`. **Clauses 7 and 10 are not doc-only** — see their notes.

1. **`UpdateAsync` mutates `state.Version` in place** to `expectedVersion + 1` *before* serialising, and
   restores it on **every** throw path. `HandleTimeoutAsync` persists the same live object twice
   (`SagaOrchestrator.cs:240`, `:289` → `:339`); a store that writes the bumped version but forgets the
   in-place mutation makes every timeout reaching its final persist report a race it did not lose — and
   that branch only logs (`:342-348`), so the saga stalls silently with side effects already sent.
2. **`FindAsync`/`FindByBusinessKeyAsync` deserialise from the stored blob**, not projected fields.
3. **Snapshot stores preserve `state.UpdatedAtUtc`** rather than restamping it.
4. **Staged-row lifecycle.** A staged outbox row is committed by **the next commit in the same unit of
   work, whichever store issues it** — not only a persist. On EF that is any of
   `EfCoreSagaSnapshotStore.cs:38`/`:82`, `EfCoreSagaEventLogStore.cs:30`,
   `EfCoreSagaTimeoutStore.cs:19`/`:34`, `EfCoreSagaOutboxStore.cs:41`, `EfCoreSagaSummaryReader.cs:119`.
   A commit that throws leaves staged rows uncommitted and still staged; a unit of work ending with rows
   still staged must not leave them durable. (`ScheduleAsync` runs on the ordinary success path at
   `SagaOrchestrator.cs:259`, so "the next *persist*" was never literally true.)
5. **`GetTimelineAsync` returns ascending `SequenceNumber` order.**
6. **`IsDuplicateAsync` is narrowed to `SagaStarted`/`MessageReceived` entries.** This **corrects**
   `ISagaEventLogStore.cs:19`, which currently describes a broader check than either provider performs
   (`EfCoreSagaEventLogStore.cs:45-52`, `InMemorySagaStore.cs:276-283`).
7. **`ResetStateAsync` is rewritten — not doc-only.** Corrects the false statement at
   `ISagaAdminStore.cs:5-12`. `CurrentState`, `Status`, `Version` **and** `UpdatedAtUtc` are patched
   inside the blob in lockstep with the projected fields; the write is version-guarded and throws
   `SagaConcurrencyException` rather than retrying. **The signature gains `int expectedVersion`** — the
   exception's only constructor requires it (`SagaExceptions.cs:7`) and the dashboard passes the version
   it rendered, which is the only thing that makes its 409 meaningful. `UpdatedAtUtc` comes from the
   caller's `TimeProvider`, not `DateTimeOffset.UtcNow`. Both signature changes land as **one** breaking
   change.
8. **`ClaimDueAsync`/`ClaimPendingAsync` return earliest-due / earliest-created first.**
9. **`Search` is case-insensitive** and matches `SagaType` and `CorrelationId` **independently**;
   **`ListAsync` applies a stable total order that is per-provider deterministic** — repeatable page
   fetches and skip-free, repeat-free page walks. It does **not** require a byte-identical sequence
   across providers: `SagaType` sorts under database collation on EF versus `StringComparer.Ordinal`
   in-memory, and `Guid` ordering differs between Postgres `uuid`, SQLite BLOB and
   `Comparer<Guid>.Default`. Nothing consumes a cross-provider-identical order.
10. **`ClaimDueAsync` gains `IReadOnlyCollection<string>? sagaTypes = null`** — not doc-only.
    **`ClaimPendingAsync` does not.** `SagaOutboxDispatcherHostedService` has no saga-type registry
    (constructor at `:17-22`; `RedispatchAsync` at `:59-66` republishes raw bytes by type name), so
    filtering its claim would strand rows permanently. The registry exists only at
    `SagaTimeoutDispatcherHostedService.cs:20`, and the drop it fixes is at `:34-38`.
11. **A null or corrupt state blob is an error, not a missing saga.** Decides D9: the store throws
    rather than returning `null`, because returning `null` is indistinguishable from "no such saga" and
    the orchestrator would start a duplicate instance over live data.
12. **The state blob is `System.Text.Json` with default options.** Pinned so the format is a stated
    contract rather than an accident of two call sites (`EfCoreSagaSnapshotStore.cs:99`,
    `InMemorySagaStore.cs:45`, both bare `JsonSerializer.Serialize`). Verified per provider by a
    **golden-blob test** — a known `TState` yields known JSON text — which catches a naming-policy
    mistake that shared serialization code structurally could not (see §8).

---

## 3. Behaviour fixes

Each lands as its own failing-test-then-fix commit. (That ordering is this plan's choice;
`CONTRIBUTING.md:83-87`'s mutation-testing rule is scoped to "anything envelope/header/linkage-adjacent"
and `:89-91` covers commit conventions — neither states a red-green rule.)

| # | Fix | Files |
| --- | --- | --- |
| F1 | Give the two `UpdatedAt` sort arms a tiebreak, and make all four arms per-provider deterministic | `EfCoreSagaSummaryReader.cs:46-53`, `InMemorySagaStore.cs:171-178` |
| F2 | Make `Search` case-insensitive **provider-agnostically**: lower both the column and the term on both disjuncts. **Not `EF.Functions.ILike`** — that is an Npgsql extension and `VSaga.Persistence.EFCore.csproj:14-16` references only EFCore, `.Relational` and DI.Abstractions | `EfCoreSagaSummaryReader.cs:28` |
| F3 | In-memory `ResetStateAsync`: patch `Version` and `UpdatedAtUtc` into the blob; replace the retry loop with a version guard that throws | `InMemorySagaStore.cs:232-254` |
| F4 | EF `ResetStateAsync`: patch `UpdatedAtUtc` into the blob, take it from `TimeProvider`, map `DbUpdateConcurrencyException` → `SagaConcurrencyException` | `EfCoreSagaSummaryReader.cs:98-120` |
| F5 | In-memory `Update`: stop overwriting `state.UpdatedAtUtc` | `InMemorySagaStore.cs:112` |
| F6 | In-memory claims: order earliest-first | `InMemorySagaStore.cs:312-330`, `:378-396` |
| F7 | EF `UpdateAsync`: map a business-key collision to `SagaAlreadyExistsException` **and restore `state.Version`** — today `:61` bumps and `:84-88` restores only inside `catch (DbUpdateConcurrencyException)`, so a mapped `DbUpdateException` would escape violating clause 1 | `EfCoreSagaSnapshotStore.cs` |
| F8 | Clamp `pageSize` to a server-side maximum of 500 | `SagaEndpoints.cs:31` |
| F9 | `SagaTimeoutDispatcherHostedService` passes `runtimesBySagaType.Keys` to `ClaimDueAsync` | `SagaTimeoutDispatcherHostedService.cs`, both providers |
| **F10** | In-memory `Update`: restore `state.Version` on the throw path | `InMemorySagaStore.cs:90-91`, `:111` |
| **F11** | In-memory `GetTimelineAsync`: order by `SequenceNumber` | `InMemorySagaStore.cs:270-274` |
| **F12** | Dashboard maps `SagaConcurrencyException` → `Results.Conflict`. Without it, clause 7 turns a raced retry into an unhandled 500: `SagaEndpoints.cs:192` is a bare `await` and the API has no exception-to-status mapping | `SagaEndpoints.cs` |

---

## 4. Two engine bugs

### B1 — the recovered `ChildSagaFinished` is republished under the wrong identity

`EnqueueOutboxRowsAsync` keys rows on `publish.Envelope.CorrelationId`, and
`SagaOrchestrator.cs:803-806` says why: "A row keyed on the publishing saga instead would have the
recovery poller republish the message under the wrong identity."
`SagaOutboxDispatcherHostedService.cs:61` rebuilds the envelope from the **stored** id.

`StageChildSagaFinishedAsync` does exactly that: envelope carries the **parent's** id (`:939`), row is
enqueued under `state.CorrelationId` — the **child's** (`:941`).

**This is a wrong-identity bug, not a phantom-transition one** — the transition itself *was* committed
(`:645` stage → `:649` persist → `:681` publish). A poller-recovered row is republished under the
child's id, the parent never receives it, and the parent hangs until its own state timeout.

**Fix:** enqueue under `envelope.CorrelationId`, plus a test driving the recovery path.

### B2 — a staged row commits on a branch that never persists

`RecordDeliveryExhaustedAsync`'s `LogAsync` (`SagaOrchestrator.cs:126`) commits whatever is staged in
the shared unit of work. The guard at `:130` can then skip the `PersistAsync` at `:134`.

**Only the guard-false branch is defective.** The comment at `:640-644` explicitly reasons that the
guard-true case is correct — "that path marks the saga Failed too, so the row it commits still matches
the outcome that was actually recorded" — and it is. Guard-false has two causes: `state is null` (no
snapshot at all), or an already-terminal status (a terminal status *was* recorded, so the announcement
is a **status mismatch** — announcing Failed for a saga that is Completed).

**Fix:** thread the `StagedChildSagaFinished` handle from `HandleStepFailureAsync` into
`RecordDeliveryExhaustedAsync` as a nullable parameter, evaluate the guard first, and discard on
guard-false **before** the `LogAsync` — matching `DiscardDeferredPublishesAsync`'s own
discard-before-log ordering (`:874-877`).

> Moving the `LogAsync` after the guard does **not** work: on guard-false the log still runs and still
> flushes. And a catch-all in `HandleStepFailureAsync` does not work either, because it cannot
> distinguish the guard-true case the comment defends. The handle has to reach the guard.

---

## 5. The conformance suite

New project **`dotnet/tests/VSaga.Persistence.Conformance`**: abstract xUnit base classes, one per
contract, parameterised by an `IProviderFixture` exposing `CreateStoresAsync`, capability flags
(`SupportsAtomicUnitOfWork`, `SupportsConcurrentClaim`) asserted in a review-visible per-provider test,
and a unit-of-work hook (`BeginAsync`/`CommitAsync`/`AbandonAsync`).

**Packaging must be set explicitly in the csproj.** `Directory.Build.props:111-114` keys
`IsPackable=false`/`IsTestProject=true` off `$(MSBuildProjectName.EndsWith('.Tests'))`, which this name
does not match — left alone it would publish to nuget.org as a package and not be recognised as a test
project. It **is** published deliberately: ADR 0003 names third-party store implementations as a live
possibility, and a packable suite is the only thing that lets one verify itself.

### 5.1 Tests that must fail a no-op implementation

- **Selective discard survives a later commit** — stage two rows, lose the version race, discard
  **one**, run a subsequent successful commit in the same scope, assert exactly the un-discarded row is
  durable.
- **B1 regression** — assert the staged `ChildSagaFinished` row carries the **parent's** correlation id,
  before the dead-letter path.
- **B2 regression** — after a non-concurrency persist failure plus exhausted redelivery on the
  guard-false branch, assert **zero** Pending rows.
- **Blob/projection agreement** — after `ResetStateAsync`, `GetDataJsonAsync`'s blob and `GetAsync`'s
  summary agree on `CurrentState`/`Status`/`Version`/`UpdatedAtUtc`, compared at the storage's own
  timestamp resolution (Postgres truncates `DateTimeOffset` to microseconds via
  `DateTimeOffsetToUtcDateTimeConverter.cs:11-13`), not by exact equality.
- **Golden blob, per provider** — a known `TState` serialises to known JSON text (clause 12).
- **Headers independence** — `ClaimPendingAsync` returns a dictionary that is an independent,
  `Ordinal`-compared copy, so a caller mutating the enqueued dictionary cannot change a stored row.

### 5.2 What the extraction genuinely adds

EF already has business-key tests (`EfCoreStoreTests.cs:826-980`) and claim tests (`:225`, `:328-333`,
`:358-392`), so the coverage win is narrower than first claimed: a **changed** `BusinessKey` on update,
a batch **larger** than `batchSize`, and `Search` — which has no test on either provider.

---

## 6. Commit sequence

**Prerequisites, all now settled:** F2's shape (provider-agnostic lowering), clause 10's scope (timeout
store only), clause 9's semantics (per-provider deterministic), clause 7's signature (`expectedVersion`
plus the `TimeProvider` clock, one breaking change), clauses 11 and 12, and B2's fix shape.

| # | Commit |
| --- | --- |
| 1 | Clauses 1–3, 5, 6, 8, plus a one-paragraph preamble on the seven-contract carve-up. Clause 6 **corrects** `ISagaEventLogStore.cs:19` |
| 2 | Clause 4 (enumerating all six EF commit sites) |
| 3 | Clause 7 + the `ResetStateAsync` signature change + F12 |
| 4 | Clauses 9, 11, 12 |
| 5 | `VSaga.Persistence.Conformance` + fixtures, with `IsPackable`/`IsTestProject` explicit |
| 6 | Failing cases for F1–F7, F10, F11 — one commit, deliberately red |
| 7–15 | F1 … F7, F10, F11, one each |
| 16 | F8 (`pageSize` clamp) — visible API change |
| 17 | Clause 10 + F9 — **timeout store only** |
| 18 | B1 |
| 19 | B2 |
| 20 | `docs/persistence.md` + `docs/observability.md` corrections |
| 21 | Fold the byte-identical `NpgsqlProviderName` constant and its duplicated guard prose (`EfCoreSagaTimeoutStore.cs:37`, `EfCoreSagaOutboxStore.cs:67`) into one internal constant; fix the two consecutive `<summary>` blocks at `SagaOrchestrator.cs:847-859`, where the first describes `DiscardDeferredPublishesAsync` but sits above `DiscardStagedChildSagaFinishedAsync` and `DiscardDeferredPublishesAsync` (`:872`) has none |

### 6.1 Verification

Build clean with zero warnings and `dotnet test dotnet/VSaga.slnx` green at every commit except 6, with
the suite running against SQLite, Postgres-Testcontainers and in-memory.

Commits 17–19 touch message flow and timing, so `CONTRIBUTING.md:69-81` makes live verification against
`docker compose up` a prerequisite. Mutation-test F1–F7, F10, F11, B1 and B2 per `:83-87`.

---

## 7. What this does not do

- It does not add a provider. Both provider ADRs stay `Proposed`.
- It does not move `PostgresHealthCheck` or add a `Persistence:Provider` switch — shared seams that only
  matter once a second production provider exists.
- It does not add a fault-injection tier (Redis-specific; see [`redis-persistence.md`](redis-persistence.md) §8).
- It does not add retention, migration, or a stranded-timeout tool.
- **It does not extract a shared persistence package** — §8.

---

## 8. Why there is no `VSaga.Persistence.Common`

Extracting shared code across the providers was evaluated and **rejected**.

1. **`VSaga.Transport.Common` is the wrong precedent.** It exists for **dependency direction** — without
   it a new transport adapter would need a project reference to a sibling adapter.
   `VSaga.Persistence.EFCore` and `.InMemory` each reference only `VSaga.Abstractions` and would never
   reference each other. There is no dependency problem to solve.
2. **A package is unnecessary even if sharing were wanted.** `VSaga.Abstractions.csproj` has **zero**
   references of any kind and already hosts behaviour: `VSagaDiagnostics` is a `public static class`
   whose own comment says it is "defined once here so Core, Persistence, and Transport packages all emit
   against the same names without depending on each other". Any shared static belongs there.
3. **Sharing the serializer would make things worse, not better.** Both call sites already use bare
   `JsonSerializer.Serialize` with ambient defaults, and no code path reads one provider's blob through
   another — so the divergence it would guard has **zero reachable instances**. Its only real effect
   would be a shared `JsonSerializerOptions`, which converts a one-provider mistake (a
   `JsonNamingPolicy`, which `System.Text.Json` does not throw on) into simultaneous all-provider silent
   data loss that **no fixture could catch**, because every fixture would round-trip through the same
   code. Clause 12's per-provider golden-blob test catches exactly that; shared code cannot.
4. **A shared `ResetStateAsync` patch helper returns only the blob**, leaving the four matching
   projection writes unbound — converting the invariant `EfCoreSagaSnapshotStore.cs:72-78` names ("the
   two never disagree… by construction, not by argument") into the by-argument form that comment
   rejects.

**The binding mechanism is the suite, not shared code:** clause 12's golden blob, §5.1's
blob/projection-agreement case, and a comment in each `ResetStateAsync` naming the other as the sibling
that must stay byte-identical. That is the end state, not a holding pattern.

**Permanently excluded, so silence does not read as oversight:** claim/atomicity helpers (four
mechanisms by design — `SupportsConcurrentClaim` exists to *expose* the difference); sort comparers (EF
sorts `IOrderedQueryable<SagaInstanceEntity>` before its `Select`, in-memory sorts
`IOrderedEnumerable<SagaSummary>`; unifying changes generated SQL); `Search` matching (three
incompatible semantics); business-key reservation (four mechanisms); exception mapping (the mapping *is*
where the divergence lives); header round-tripping (comparer and mutability semantics differ
deliberately); pagination clamping (F8 moves the policy to the endpoint); a
`SagaSnapshotStoreBase<TState>` (would make clause 1 look enforced by construction while being false for
Mongo, whose transaction callback may re-run); a source generator (no infrastructure, and it undercuts
Source Link stepping).

**What would reopen it:** both Mongo *and* Redis shipping the same single-shot C# read-patch-CAS. The next step
then is one static class in `VSaga.Abstractions` beside `VSagaDiagnostics` — still not a package. A
package only becomes right if the shared surface grows past one cohesive type.

Separately, and worth more than the package would have been: fold the byte-identical
`NpgsqlProviderName` constant and its duplicated guard/fallback prose **inside**
`VSaga.Persistence.EFCore` into one internal constant (commit 21). That removes more duplicated text
than the whole proposed package, at no packaging cost.
