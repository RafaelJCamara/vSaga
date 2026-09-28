# ADR 0003: Write the persistence contracts down, and fix what that surfaces

**Status:** **Accepted** — 2026-09-25. **Implemented** 2026-09-26: all 21 commits of the plan's sequence have landed, every fix and both engine bugs with them, and the suite is green
(progress in the plan's §6.2).
**Amended:** 2026-09-25, after auditing the plan against the code; and again the same day, after the
conformance suite's review, to add fixes F13 and F14 and restate which commits are red.
**Relates to:** [`0001-mongodb-persistence-provider.md`](0001-mongodb-persistence-provider.md) and
[`0002-redis-persistence-provider.md`](0002-redis-persistence-provider.md), both of which depend on this
and neither of which this depends on.
**Implementation plan:** [`../design/persistence-contracts.md`](../design/persistence-contracts.md)

---

## Context

Planning a MongoDB provider (ADR 0001) and a Redis provider (ADR 0002) both surfaced the same
precondition: the engine places requirements on a persistence provider that the seven contracts in
`dotnet/src/VSaga.Abstractions/Persistence/` largely do not state. The status is mixed rather than
uniformly absent, and the distinction matters: one clause is already stated correctly, one is stated,
**two are stated wrongly and must be corrected** (`ISagaAdminStore.cs:5-12` and
`ISagaEventLogStore.cs:19`), and the rest are absent -- `ISagaEventLogStore.cs:17` has no doc comment at
all.

That is not a documentation gap, it is a correctness one. The two shipped providers were written by
copying from each other and have **eight verified divergences and three shared defects** today. Among
them: an in-memory `ResetStateAsync` that leaves `Version` stale inside the blob it is read back from;
in-memory claim methods returning rows in arbitrary order; an in-memory `GetTimelineAsync` that returns
its list **unsorted** while `AppendAsync` takes its sequence number before appending, so ascending
timeline order — which feeds compensation ordering — holds only by luck; and a null state blob that EF
reports as "no such saga" (so the orchestrator would start a duplicate) while in-memory throws.

Most survived because there is no shared conformance suite. `Search` is starker still: it has **no test
on either provider**, so its divergence was never a matter of overlapping coverage.

[2026-09-27: all of this has since been fixed. The clauses are written into the interfaces (the
corrected text is at `ISagaAdminStore.cs:5-39` and `ISagaEventLogStore.cs:17-43`), every divergence and
shared defect named above is fixed, and `Search` has conformance cases that run against every provider.
The `file:line` citations above point at the pre-fix code.]

Planning also turned up two engine bugs unrelated to any provider, both concerning `ChildSagaFinished`
being published for a transition no snapshot recorded.

The decision to make first was therefore not "MongoDB or Redis" but "does the shared groundwork land
first, and how far does it go".

---

## Decision

**Land the groundwork as standalone work, before committing to any new provider.** Both provider ADRs
stay `Proposed`; this one is `Accepted`. The plan is
[`../design/persistence-contracts.md`](../design/persistence-contracts.md).

Scope is the **full** version: twelve contract clauses written into `VSaga.Abstractions`, a new
cross-provider conformance suite, the fourteen behaviour fixes that suite surfaces, and the two engine
bugs. (Ten clauses and seven fixes at first draft; auditing the plan against the code added clauses
11-12 and fixes F10-F12; reviewing the built suite added F13, which gives outbox headers a stated copy
rule instead of pinning in-memory's aliasing, and F14, which gives clause 11 the fix it lacked.) The
alternative of writing the clauses only, or deferring the fixes, was rejected: the clauses without the
suite are unenforced prose, and the suite without the fixes is red on arrival.

Five substantive questions were settled along the way. [2026-09-27: where §1–§4 below say *today*,
*currently* or *never stated*, they describe the tree before the plan's commits; every defect they name
is now fixed (see Status), and their citations of that pre-fix code — `SagaSummary.cs:34`,
`SagaOrchestrator.cs:126`/`:130`/`:134`, `ISagaOutboxStore.cs:69-76`, `EfCoreSagaSummaryReader.cs:117`,
`InMemorySagaStore.cs:249`, `SagaEndpoints.cs:192`, `ISagaTimeoutStore.cs:32` and
`SagaTimeoutDispatcherHostedService.cs:34-38` — no longer match the current files.]

### 1. `Search` is case-insensitive, and EF is the one that is wrong

`SagaSummary.cs:34` has said "Case-insensitive substring match against SagaType and CorrelationId" since
the field existed. In-memory obeys it (`OrdinalIgnoreCase`); EF's `EF.Functions.Like` maps to Postgres
`LIKE`, which is case-**sensitive**.

The doc comment is the contract, and the dashboard ships a live search box bound to it
(`dashboard-web/src/app/pages/saga-list/saga-list.html:23-26`) that any user would read as
case-insensitive.

**The mechanism is provider-agnostic lowering, not `EF.Functions.ILike`.** An earlier draft of this ADR
specified `ILike`; that does not compile. `ILike` is an Npgsql extension method, and
`VSaga.Persistence.EFCore.csproj:14-16` references only EFCore, `.Relational` and DI.Abstractions -- a
provider-agnosticism [`docs/persistence.md`](../persistence.md#ef-core--postgres) documents as
deliberate. EF instead lowers both the column and the term, on **both** disjuncts: the second one,
`x.CorrelationId.ToString().Contains(search)`, is equally case-sensitive today and an `ILike` fix would
have missed it.

*Rejected:* redefining the contract as case-sensitive to match EF — cheaper, but it makes a shipped UI
behave like it has a bug. *Rejected:* a normalised lowercase column — it buys index usability that
nothing has yet shown to be needed, at the cost of a migration and a second field both providers must
keep in sync.

### 2. A staged outbox row is committed by the next commit in the same unit of work, and dropped if none succeeds

`ISagaOutboxStore` never stated what happens to a staged row when the persist that should commit it
throws. EF's shared `DbContext` means a later persist — or even an `AppendAsync` — flushes it, and the
orchestrator reasons about exactly that at `SagaOrchestrator.cs:668-673`, treating it as intended. But
`ISagaOutboxStore.cs:69-76` only ever describes the ambient flush as a *hazard*, never as a guarantee.

The contract is now the stated rule above — *any* commit in the unit of work, not only a persist, since
six EF call sites issue `SaveChangesAsync` and `ScheduleAsync` does so on the ordinary success path
(`SagaOrchestrator.cs:286`). [2026-09-27: seven, as the plan's §9.2 corrections record: the commits on the
shared context that `ISagaOutboxStore.cs:48-51` lists — the snapshot store's insert and update, the
event log's append, the timeout store's schedule and cancel, the outbox's `MarkDispatchedAsync` and
the admin reset. The provider's other three `SaveChangesAsync` calls are the two claim fallbacks and
the topology store.] Its consequence is that EF's behaviour on
`RecordDeliveryExhaustedAsync`'s guard-false branch — where the `LogAsync` at `SagaOrchestrator.cs:126`
flushes a staged `ChildSagaFinished` and the `PersistAsync` at `:134` is then skipped entirely — is a
**defect**, not the contract. It publishes "this child finished" for a saga whose terminal status was
never recorded — or, when the status is already terminal, announcing `Failed` for a saga that is
`Completed`. That is precisely the class of lie `DiscardStagedChildSagaFinishedAsync` (`:877-892`)
exists to prevent. Note the guard-**true** branch is *not* a defect: the comment at `:668-673` reasons
it through correctly.

*Rejected:* pinning EF's current behaviour as the contract. It is safest against unknown regressions,
but it makes the `:130`-false publish a guarantee rather than a bug, and it would force any non-EF
provider to expose an explicit flush hook reachable from `VSaga.Core`.

### 3. `ResetStateAsync` is version-guarded and fails rather than retries

Today EF throws a raw `DbUpdateConcurrencyException` (`Version` is a concurrency token,
`VSagaDbContext.cs:105`) while in-memory retries in a loop until it wins.

Neither is right. The contract is: version-guarded, throwing `SagaConcurrencyException` on a concurrent
write. An operator clicking Retry in the dashboard while the saga is actively being processed should be
told, not have their reset silently clobber a concurrent step's committed transition.

**This forces two signature changes, landed as one.** `SagaConcurrencyException`'s only constructor
requires `expectedVersion` (`SagaExceptions.cs:7`) and `ResetStateAsync` has no version parameter, so it
gains `int expectedVersion` -- the dashboard passes the version it rendered, which is the only thing
that makes the 409 mean "the saga changed since you looked". [2026-09-27: as built, the SPA sends no
version: the retry endpoint passes the `Version` its own `GetAsync` read at the start of the request
(`SagaEndpoints.cs:130`, `:181`), so the 409 covers a write between that read and the reset, not a
change since the page rendered.] `UpdatedAtUtc` likewise moves to the caller's `TimeProvider`: both
providers currently stamp `DateTimeOffset.UtcNow` (`EfCoreSagaSummaryReader.cs:117`,
`InMemorySagaStore.cs:249`), bypassing the clock every other engine write uses.

**And the dashboard needs a mapping commit.** Today `SagaEndpoints.cs:192` is a bare `await` and the API
has no exception-to-status mapping, so without fix F12 this decision turns a raced retry into an
unhandled 500, not a 409.

*Rejected:* adopting in-memory's retry loop — the operator's reset always applies, but it can overwrite
live progress with no signal to anyone. *Rejected:* dropping the guard entirely.

### 4. `ClaimDueAsync` gains an optional saga-type filter — `ClaimPendingAsync` does not

`ISagaTimeoutStore.cs:32` takes only `asOf` and `batchSize`, so a dispatcher claims and marks Fired
**every** due row in the store, then silently drops any whose `SagaType` has no registered runtime in
this process (`SagaTimeoutDispatcherHostedService.cs:34-38`) — *after* the row is already terminal, so it
can never fire again. Any deployment sharing a store across services burns timeouts permanently.

**`ClaimDueAsync` gains `IReadOnlyCollection<string>? sagaTypes = null`. `ClaimPendingAsync` does
not.** An earlier draft scoped the change to both; that would have been a bug.
`SagaOutboxDispatcherHostedService` has no saga-type registry at all -- its constructor (`:17-22`) takes
`(scopeFactory, transport, timeProvider, options, logger)` and `RedispatchAsync` (`:59-66`) republishes
raw bytes by type name -- so filtering its claim by types it cannot enumerate would **strand rows
permanently**. The registry exists only at `SagaTimeoutDispatcherHostedService.cs:20`.

Source-compatible via the optional-parameter precedent `SagaLogEntry.cs:18-22` sets, so no external
`ISagaTimeoutStore` implementation breaks. [2026-09-27: not source-compatible for implementers, nor for
every caller. That precedent is a record constructor, which has no implementers, and it appends its
defaulted parameters after every existing one; `sagaTypes` sits before `cancellationToken`, so a
caller passing the token positionally no longer compiles (`c8eb78c` updated the two in-tree ones, the
timeout dispatcher and `SagaTestHarness`). An implementer must add the parameter, as both in-tree
stores did in the same commit, so an external implementation breaks at compile time.]

*Rejected:* documenting a "one database per service" rule instead — it leaves a real bug live and pushes
correctness into deployment convention. *Rejected:* having the dispatcher re-schedule rows it cannot
handle — it avoids the abstractions change but turns a clean filter into a claim-and-put-back dance and
still burns a claim cycle.

### 5. `docs/adr/` is kept

This repo had no ADR convention before these three files. Numbered decision records stay distinct from
the long-form plans in `docs/design/`; an ADR links to its plan rather than repeating it.

---

## Consequences

### Positive

- The contracts a provider must satisfy exist in **executable** form, green on two providers before any
  third is written — so a new provider's failure is unambiguous rather than a contract argument.
- Eight cross-provider divergences and three shared defects are fixed, along with two engine bugs
  unrelated to any provider.
- `ListAsync` gains a stable **total** order on every provider, closing a tie-group paging hazard that
  can silently drop a dashboard live update — a hazard that exists on Postgres today, just narrower.
- The conformance extraction is a coverage win in both directions: EF gains four behaviours only
  in-memory tested, in-memory gains two only EF tested.
- A timeout for a saga type this process does not host is no longer permanently burned.
- Whichever provider is eventually chosen starts from a smaller, better-specified surface — and neither
  has to own this work, which removes the "whichever lands first authors it" coupling between the two
  provider plans.

### Negative

- **Two shipped providers change observable behaviour and a published abstractions package changes,
  before any new provider exists.** `Directory.Build.props:21` ships every packable project in lockstep
  from one MinVer tag, so this moves the whole surface.
- `ResetStateAsync` gains a failure mode it did not have: a dashboard retry racing a live message now
  returns 409 where in-memory previously always succeeded.
- `SagaEndpoints.cs:98`'s new `pageSize` clamp is a visible API behaviour change — a request for
  `pageSize=10000` now returns 500 rows.
- `ISagaAdminStore.ResetStateAsync` gains a parameter: a breaking change to a published contract.
- Two clauses touch code outside the providers: the claim filter reaches `VSaga.Core`'s timeout
  dispatcher, and clause 7 reaches `VSaga.Dashboard.Api`.
- The sequence is deliberately red from commit 6, which lands every failing case at once, until the
  last fix: each fix commit turns only its own cases green, so the suite stays red in the shrinking set
  of cases not yet fixed.

### Neutral

- No provider is chosen. Both provider ADRs stay `Proposed` and their blocking questions stay open.
  [2026-09-27: since superseded — ADRs 0001 and 0002 were both accepted and implemented on 2026-09-26,
  with their blocking questions resolved.]
- The `Persistence:Provider` switch and the `PostgresHealthCheck` move stay unowned, deferred to
  whichever provider effort lands first. `PostgresHealthCheck.cs:19-21`'s fail-open behaviour is
  harmless while EF is the only production provider. [2026-09-27: the Redis provider authored both
  seams (`19c6303`), the second as a host registration rather than a move. Each host switches on
  `Persistence:Provider` (`Postgres`, `Redis`, `MongoDb`), and the Dashboard API registers one store
  check under the provider-neutral name `persistence`: `PostgresHealthCheck` only on the Postgres
  branch, each other provider's own check otherwise (`Dashboard.Api/Program.cs:106-118`).]

---

## What would invalidate this decision later

1. **A conformance clause turns out to be wrong rather than merely unwritten.** These clauses were
   derived from implementation comments and engine call sites, not from a specification — if one
   contradicts intended behaviour, the clause changes, not the engine.
2. **An external `ISagaSummaryReader`/`ISagaTimeoutStore` implementation exists in the wild.** The repo
   designs for that possibility (`SagaChangePollingService.cs:33-38`, `:113-118`), and the behaviour
   changes here — total-order sorts, case-insensitive search, the `ResetStateAsync` failure mode — are
   observable to one even though the source signatures stay compatible. [2026-09-27: not for
   `ISagaTimeoutStore`, whether implemented or called with a positional token — see decision 4's note.]
3. **Both provider ADRs are rejected.** The work still stands on its own (it fixes four live bugs), but
   the conformance suite's third fixture would never arrive, and the `SupportsAtomicUnitOfWork`
   capability flag would have only one shape to describe.
