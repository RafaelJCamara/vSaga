# Design: production readiness (packaging, outbox, correlation, tracing, docs)

**Status: shipped.** All 19 items in §8 are committed as of 2026-08-28, plus a follow-up fix round
(same day) closing bugs an adversarial-review workflow found in items 14/15/18 — see §8's own progress
note and §9 for what "shipped" was actually verified against. This file is kept as the historical
design record rather than rewritten past tense throughout; treat every "would"/"is planned" below as
describing intent that was in fact carried out, not a still-open proposal, unless a progress note says
otherwise.

**Status correction (one workstream is incomplete): the release half of §3 never shipped.** There is no
`release.yml`, nothing is published to nuget.org or npm, and tagging/publishing are manual. That does not
contradict the §8 claim above — `release.yml` was only ever described in §3's prose and was never given a
numbered §8 item — which is exactly why the gap is invisible from here. See the status note at the end of §3.

Written to be picked up cold in a later session: every claim about the current codebase carries a
`file:line` so it can be re-checked rather than trusted. Line numbers were accurate at commit
`e5ff42f` and will drift — re-grep rather than trusting them.

The four product decisions in §1 were taken by the user directly, when asked. A later session
wanting to reopen one should know they were deliberate, not defaults.

**This is a second draft, corrected by an independent design review before any code was written.**
Three findings from that pass changed the design materially rather than cosmetically, and are called
out inline rather than silently folded in, so a reader who saw the first draft isn't confused by the
disagreement: the outbox's `DeferredPublish` shape had to become a hybrid, not pure data, because the
in-memory transport's raw-dispatch path erases the CLR object a tripwire test asserts on (§4.1, §4.3);
correlation's API shape changed from a same-named overload to a separate `CorrelateOn` declaration
(§5.1); and `PublishRawAsync` gaining a parameter was replaced with a new `SendRawAsync` method, since
the parameter approach breaks external `IMessageTransport` implementers that packaging (§3) makes
newly plausible (§4.4). One open question from that review is recorded rather than resolved, in §5.4.

---

## 1. Why these five, and the decisions already taken

vSaga is deep on saga *semantics* — five transport adapters, sub-saga composition, mixed
RabbitMQ/REST sagas, chaos middleware, a per-instance service map. The gaps are almost entirely in
three orthogonal dimensions: it cannot be installed, it cannot guarantee delivery, and it cannot be
traced. Plus the correlation model, which forces every participant to echo a GUID it has no business
reason to care about.

1. **Packaging** — `dotnet/Directory.Build.props:12` sets `<IsPackable>false</IsPackable>` for every
   project with no override anywhere; `:11` discards the 143 existing `<summary>` blocks via
   `GenerateDocumentationFile=false`. No LICENSE, zero git tags, and `.github/workflows/ci.yml` has
   no pack or publish step. The library can only be consumed by cloning.
2. **Transactional outbox** — `ctx.PublishAsync` publishes inline mid-step before any persist
   (`SagaContext.cs:128-150`), and `PublishAfterCommitAsync` is an in-memory
   `List<DeferredPublish>` (`SagaContext.cs:52`) lost on a crash between the persist at
   `SagaOrchestrator.cs:475` and the drain at `:477` — which that method's own doc comment at
   `:496-503` concedes.
3. **Business-key correlation** — lookup is `snapshotStore.FindAsync(SagaType, received.CorrelationId)`
   (`SagaOrchestrator.cs:311-312`) and nothing else. `CorrelateBy` is *explicitly documented at
   `EventBuilder.cs:24-28` as not being correlation* — it copies a key onto state for dashboard search.
4. **W3C trace propagation** — `traceparent`/`tracestate` appear nowhere in the repo. There is
   exactly one `StartActivity` in all of `dotnet/src` (`SagaOrchestrator.cs:396`) and
   `VSaga.Observability` references no exporter, so telemetry is collected and discarded.
5. **Docs** — `README.md` is 1967 lines / 156KB of chronological work log whose "Getting started" is
   the last 2% (`:1921-1967`) and describes running the demo, not adding vSaga to your own app.

**Decisions already taken:**

- **Outbox is opt-in, not default-for-all.** It becomes the durable backing for
  `PublishAfterCommitAsync`, plus an `Outbox:Mode=All` switch routing every publish through it.
  Default keeps today's inline semantics, so every existing test and sample is unchanged. This
  deliberately mirrors the same call the user made for `PublishAfterCommitAsync` itself
  (`http-based-sagas.md` §1, decision 4).
- **Correlation goes all the way to per-message correlation expressions**, not initiating-message-only.
- **Packaging publishes for real** to nuget.org and npm, tag-triggered.
- **The README's changelog narrative is preserved verbatim** under `docs/history/`, one file per
  topic. Nothing is deleted.

**Explicitly out of scope**, named so they are not mistaken for oversights: Kafka/Azure Service
Bus/SQS adapters, a SQL Server provider, saga-definition versioning, event-log retention, stuck-saga
alerting, dashboard writes beyond the existing retry, and a serializer abstraction. All real gaps;
none of them these five.

---

## 2. What already exists that this builds on

- **One DI scope per message.** `SagaRuntime.cs:24-28` opens `scopeFactory.CreateAsyncScope()` per
  message/timeout/retry. All EF stores are `AddScoped` and share one `VSagaDbContext` per scope
  (`Persistence.EFCore/ServiceCollectionExtensions.cs:16-27`). **This is the property that makes the
  outbox cheap** — see §4.
- **A single publish chokepoint.** All five context publish paths funnel into
  `SagaContext.PublishInternalAsync` (`:128-150`).
- **A claim-and-dispatch precedent.** `EfCoreSagaTimeoutStore.ClaimDueAsync:46-49` branches on
  provider name; the Postgres path at `:87-99` is `UPDATE ... WHERE Id IN (SELECT ... FOR UPDATE SKIP
  LOCKED) RETURNING`. `SagaTimeoutDispatcherHostedService` is the loop shape to copy.
- **A promoted-column precedent.** `SagaState.ParentSagaType`/`ParentCorrelationId`
  (`SagaState.cs:22-35`) are real columns rather than living only in the `DataJson` blob, for the
  reason `Entities.cs:26-31` states verbatim.
- **Header pass-through.** `MessageEnvelope.From` (`MessageEnvelope.cs:30-45`) is the single outbound
  choke point, with one raw construction at `SagaOrchestrator.cs:70-74` for redelivery.

---

## 3. Packaging and release

**Goal:** `dotnet add package VSaga.Core` and `npm install @vsaga/participant` work.

**Versioning: MinVer.** It derives the version from git tags with no version string committed
anywhere, which suits a repo with zero tags and no version today. Nerdbank.GitVersioning needs a
committed `version.json`; GitVersion needs more configuration than this warrants. **MinVer silently
produces `0.0.0` on a shallow clone**, so it needs `fetch-depth: 0` added to every CI checkout step
(currently shallow, the GitHub Actions default) plus a build-time assertion that fails loudly if the
computed version is ever `0.0.0` — silent version drift into a fake `0.0.0` package is worse than a
missing tag failing the build outright.

**Turning on `GenerateDocumentationFile` and `TreatWarningsAsErrors` together is a landmine — land the
fix in the same commit as the flip.** `GenerateDocumentationFile=true` is necessary (§1: the 143
existing `<summary>` blocks are currently discarded and are effectively this codebase's API
documentation), but it activates `CS1591` ("missing XML comment on publicly visible member") for
every public member that doesn't have one, and `Directory.Build.props:9` already sets
`TreatWarningsAsErrors=true`. Flipping `GenerateDocumentationFile` alone turns every undocumented
public member across 16 projects into a build error in one commit. Add `<NoWarn>$(NoWarn);CS1591</NoWarn>`
in the exact same edit, not a follow-up.

**Ship all 16 libraries in one lockstep version.** The dependency graph is three deep and
`VSaga.Core` depends on nothing but `VSaga.Abstractions`, so there is no reason to hold any back.
Lockstep matches what npm already does with its exact `"@vsaga/protocol": "0.1.0"` pins.
`VSaga.Testing` matters most — a test harness only this repo's own tests can use is the
highest-leverage packaging miss. The four non-libraries opt out explicitly: `VSaga.Dashboard.Api`
(`Microsoft.NET.Sdk.Web`, a deployable app), both `VSaga.Samples.OrderProcessing*`, and
`tools/BackfillStrandedTimeouts`. Tests already opt out via the `.Tests` condition at
`Directory.Build.props:33-36`.

**The three bus SDK pins need version *ranges*, not just central management.**
`VSaga.Transport.MassTransit.csproj:11-13` pins `MassTransit.RabbitMQ` to 8.5.8 with a comment
recording that **MassTransit v9 is commercially licensed**. A package emitting an open `>= 8.5.8`
dependency would let a consumer's restore silently resolve a commercially-licensed v9. Ship bounded
ranges — `[8.5.8,9.0.0)`, and equivalents for WolverineFx 6.30.0 and Paramore.Brighter 10.7.0 — and
move all three out of their per-project `VersionOverride` into `Directory.Packages.props`.

**`CentralPackageTransitivePinningEnabled=true` (`Directory.Packages.props:4`) is a packing hazard.**
Transitively-pinned packages are promoted to direct dependencies in the generated nuspec, so a
package may declare more dependencies than its `.csproj` lists. Inspect the nuspecs before publishing.

npm side: add `publishConfig: { access: public }` to all seven `package.json` files — scoped
`@vsaga/*` names default to restricted and publish fails without it. Add `repository`, `homepage`,
`author`, `keywords`, and a per-package `README.md` (today `"files": ["dist"]` means npm would show
no readme at all). Fix the root `workspaces` glob, whose `"samples/*"` entry matches a directory that
does not exist.

New `.github/workflows/release.yml` on `v*` tags reusing `ci.yml`'s steps, then `dotnet pack -c
Release` and `npm publish`. Add `permissions: contents: read` and a concurrency group, neither of
which `ci.yml` has. Secrets needed: `NUGET_API_KEY`, `NPM_TOKEN`.

> **Status note: this paragraph did not ship, and the rest of §3 did.** `.github/workflows/` still
> contains only `ci.yml` — no `release.yml`, no `dotnet pack` or `npm publish` step anywhere in CI, and
> neither `NUGET_API_KEY` nor `NPM_TOKEN` is referenced by any workflow. The *metadata* half of packaging
> landed in full (§8 items 1–3: MinVer with `MinVerTagPrefix=v`, the two 0.0.0 pack guards,
> `fetch-depth: 0` on all three CI jobs, npm `publishConfig`/metadata), so a local `dotnet pack` against a
> tag works — but the automation and the actual publish do not exist, which
> `dotnet/Directory.Build.props`'s own MinVer comment ("no automated release workflow exists yet, so
> tagging and publishing are manual") and `docs/getting-started.md`'s "vSaga has not yet cut a tagged
> release" note both acknowledge. The deferral reads as deliberate; what hides it is structural.
> `release.yml` lives only in this section's prose and was never assigned a numbered §8 item, so §8's
> "all 19 items in §8 are committed" is literally true while workstream 1 — packaging — is still
> incomplete. Read §1's "**Packaging publishes for real** to nuget.org and npm, tag-triggered" as intent
> that has not yet been carried out, and `dotnet add package VSaga.Core` (this section's stated goal) as
> not yet working.

**Confirm names before the first publish** — the `VSaga.*` NuGet prefix and `@vsaga` npm scope are
claimed publicly and hard to undo.

---

## 4. Transactional outbox

**Goal:** a message queued by a committed step is never lost, without changing when anything is
delivered today.

### 4.1 The central design decision

The naive outbox — write a row, let a background poller dispatch it — **would break the existing test
suite, and for a good reason.** `TimeoutDrainTests.cs:79`
(`TimeoutQueuingALoopback_DrainsAfterItsOwnPersist_NoUnexpectedEvent`) asserts that by the time
`HandleTimeoutAsync` returns, the drained loopback has already re-entered the saga and driven it to
`SagaCompleted`. That holds only because `InMemoryMessageTransport.DispatchAsync` awaits its
subscriber handler inline (`:85`). The entire `VSaga.Http.Tests` suite depends on the same property,
stated at `CallHttpMappingTests.cs:11-14`. Handing the drain to a poller makes all of it
non-deterministic.

**So the outbox row is a crash-recovery backstop, not the dispatch path.** Per committed step:

1. During the step, `PublishAfterCommitAsync` queues in memory exactly as today.
   `StepExecutor.cs:38-39`'s `ClearDeferredPublishes()` on retry keeps working unchanged, because
   nothing has been written yet — **this is why rows must be written at commit time, not at enqueue.**
2. Immediately before `PersistAsync`, the surviving queue is written as outbox rows through the same
   scoped `VSagaDbContext`. Because every EF store shares one context per message (§2), the snapshot
   store's own `SaveChangesAsync` commits the rows and the snapshot in one implicit transaction.
   **No explicit transaction is introduced** — the repo has none today and needs none for this.
3. The existing inline drain runs unchanged right after the persist, marking each row `Dispatched`.
4. A background poller claims rows still `Pending` after a grace period and republishes them.

Every existing test keeps its current timing, and the durability hole closes: a crash between commit
and inline dispatch leaves a `Pending` row the poller picks up.

> **Step 2 is load-bearing, and the first implementation got it wrong.** `ISagaOutboxStore.EnqueueAsync`
> must *stage* onto the shared context and return without saving; the caller's `PersistAsync` is what
> commits it. The original commit had it call `SaveChangesAsync` itself — forced, because it keyed
> `MarkDispatchedAsync` on the EF identity `Id`, which only exists after a save. That committed each row
> in its own transaction *ahead* of the snapshot's, reopening precisely the dual-write window this
> section exists to close, and it stayed invisible until the poller of item 9 existed to claim the
> orphans. Concretely: a timeout whose persist lost its concurrency race had its publishes deliberately
> discarded, but the rows were already durably `Pending`, so the poller published them a grace period
> later — `DiscardDeferredPublishesAsync` was reduced to logging. `TimeoutDrainTests.cs:180` kept passing
> throughout, because it only asserts nothing went out *inline*.
>
> The fix keys `MarkDispatchedAsync` on `MessageId` (a minted GUID, indexed) so no database-generated
> value is needed at enqueue time, and adds `DiscardPendingAsync` for the abandon paths. **That discard
> must run before the discard path's own `LogAsync` calls** — those append through the same context, and
> their `SaveChangesAsync` would otherwise commit the very rows being suppressed.
>
> The general rule, worth stating once because it bit twice: **every EF store here shares one Scoped
> `VSagaDbContext` per message, so any store method that calls `SaveChangesAsync` also commits whatever
> else is staged on that context.** A store's save is never local to that store.

**A second constraint, found by an independent design review and confirmed by reading the code
directly: the inline dispatch cannot go through `PublishRawAsync`.**
`InMemoryMessageTransport.PublishRawAsync` passes `message: null` into `DispatchAsync`
(`InMemoryMessageTransport.cs:55-56`), so `PublishedMessage.Message` is null for anything sent that
way. `TimeoutDrainTests.cs:75` asserts `p.Message is DrainLoopbackAck` on that same record — a
`DeferredPublish` redesigned as pure serialized bytes would force the inline path onto
`PublishRawAsync` to stay type-erased, and that test would silently start failing. §4.3 resolves this
by keeping the inline dispatch strongly typed and letting only the *durability* copy be raw bytes.

### 4.2 New files

- `VSaga.Abstractions/Persistence/ISagaOutboxStore.cs` — modelled on `ISagaTimeoutStore`:
  `EnqueueAsync` (stages only, per the callout in §4.1), `MarkDispatchedAsync(messageId, ct)`,
  `ClaimPendingAsync(olderThan, batchSize, ct)`, and `DiscardPendingAsync(messageIds, ct)` for the
  paths that abandon a batch instead of draining it.
- `VSaga.Persistence.EFCore/EfCoreSagaOutboxStore.cs` — reuse the provider branch and the Postgres
  `FOR UPDATE SKIP LOCKED` claim from `EfCoreSagaTimeoutStore` verbatim.
- `VSaga.Core/Runtime/SagaOutboxDispatcherHostedService.cs` — copy
  `SagaTimeoutDispatcherHostedService`'s loop shape (`BackgroundService`, `TimeProvider`-driven
  `PeriodicTimer`, `do{}while(WaitForNextTickAsync)`, two-level try/catch) but resolve stores via
  `IServiceScopeFactory` per `SagaRuntime.cs:26`. **Do not copy its DI shape** — it injects a Scoped
  `ISagaTimeoutStore` into a singleton, a captive dependency that only works today because the
  in-memory provider registers everything Singleton.
- `VSaga.Core/Runtime/SagaOutboxOptions.cs` — `Mode` (`Deferred` default | `All`), `PollInterval`,
  `BatchSize`, `DispatchGracePeriod`. Separate from `SagaOrchestratorOptions`, whose doc scopes it to
  infrastructure-failure handling.
- A Postgres migration plus a regenerated `VSagaDbContextModelSnapshot.cs`.

### 4.3 Changed files

`SagaContext.cs:21` — `DeferredPublish` becomes a **hybrid**, not pure data: it carries both an
outbox row (message type name, UTF-8 body, destination, envelope headers — for the durability copy
and for the recovery poller, which only ever has bytes) *and* a thin `Func<IMessageTransport,
CancellationToken, Task>` dispatch closure that still calls the strongly-typed
`transport.PublishAsync<TMessage>`/`SendAsync<TMessage>` for the inline path, per the constraint just
above. The envelope and `MessageId` are already minted eagerly at enqueue (`:117`), so both the row
and the closure describe the same message identity — nothing about that changes.
`StepExecutor.cs:38-39`'s `ClearDeferredPublishes()` on retry stays exactly `_deferredPublishes.Clear()`,
since nothing is written to the outbox until commit time (§4.1 step 2) — there is nothing to detach.

`SagaContext.cs:128-150`, `SagaOrchestrator.cs:475-477` and `:235-241` (the two persist/drain
boundaries), and `SagaOrchestrator.cs:586-598` — `PublishChildSagaFinishedAsync` calls
`transport.PublishAsync` directly, bypassing `SagaContext` entirely. **It is a second publish surface**
and must route through the outbox too; it already runs after the persist in both call paths.

> **"Already runs after the persist" is the obstacle here, not the convenience it sounds like.** Since
> `EnqueueAsync` only stages (§4.1), a row written at either of those call sites would only ever be
> committed by `MarkDispatchedAsync`'s own save — inserted already-`Dispatched`, covering no crash
> window at all. So this surface splits in two: `StageChildSagaFinishedAsync` runs *before* the persist
> and returns a `StagedChildSagaFinished` (message + envelope), and the send half consumes it
> afterwards — the same "one row and one dispatch describing one message identity" shape
> `DeferredPublish` has. The timeout race-loss branch discards the staged row as well: a persist that
> lost its race means the saga never reached a terminal status, and announcing one is a lie the parent
> acts on.

Plus `Entities.cs` (copy `SagaTimeoutEntity:77-90`), `VSagaDbContext.cs` (new `DbSet` +
`OnModelCreating` with a claim index on `(Status, CreatedAtUtc)`; the global `DateTimeOffset` UTC
conversion at `:15-18` applies for free), both `ServiceCollectionExtensions.cs`, and
`AddVSagaEngine` (`VSaga.Core/ServiceCollectionExtensions.cs:25`) for the hosted service.

### 4.4 Two sub-decisions

**`PublishRawAsync` has no destination parameter**, so a replayed `ctx.SendAsync` cannot be
reconstructed. **Add a new `SendRawAsync(destination, messageTypeName, body, envelope, ct)` method to
`IMessageTransport` rather than adding a parameter to `PublishRawAsync`.** An added parameter looks
source-compatible when defaulted, but `IMessageTransport` is a public interface and workstream 3
(§3) is what makes an external implementation of it plausible for the first time — anyone who wrote
one loses the ability to compile against the new signature. A new method avoids that entirely, and
can ship as a default interface method (falling back to `PublishRawAsync` with a documented
"destination ignored" note) so even an already-published external adapter keeps compiling. Every
adapter already has an internal `PublishInternalAsync(typeName, body, envelope, destinationQueue,
ct)` that takes a destination — see `RabbitMqTransport.cs:35-52` — so the six adapters' real
implementations are a thin wrapper either way; only the *public* signature choice differs, and the
new-method form is the one that can't break anyone.

**The in-memory provider has no unit of work.** `InMemorySagaStore.Insert`/`Update` mutate
`ConcurrentDictionary` entries immediately (`:40-68`), so there is no `SaveChangesAsync` to piggyback
on. Write the outbox row inside the successful `TryAdd`/`TryUpdate` branch, which is as close to
atomic as the structure allows, and **document the residual gap rather than claiming atomicity**. The
in-memory provider is already dev/test-only.

### 4.5 One correctness fix found along the way

`HandleStepFailureAsync` (`SagaOrchestrator.cs:424-444`) neither drains nor discards the deferred
queue — publishes queued before a throw are silently abandoned with no timeline entry, unlike the
timeout race path which logs `DeliveryExhausted` per dropped publish (`:532-544`). Make it discard
explicitly. Its own commit: it is a visible behaviour change independent of the outbox.

---

## 5. Business-key correlation

**Goal:** `.When<PaymentSettled>().CorrelateBy(m => m.OrderId)` actually finds the saga.

### 5.1 The API decision

**Keep `CorrelateBy`'s existing two-argument signature and behaviour completely unchanged. Add a
separate, new, definition-level method — `CorrelateOn` — that arms correlation.** An earlier draft of
this plan proposed a new one-argument `CorrelateBy` overload that correlates, sitting beside the
unchanged two-argument one that doesn't. An independent design review argued that shape is wrong on
its own terms — two same-named methods where only one actually correlates is exactly the kind of
surface that gets misused — and the alternative below both avoids that and turns out to solve the
harder design problem for free. Replacing it here rather than leaving both on record.

- **`CorrelateOn(Expression<Func<TState, object?>> selector)`** — new, called once at the definition
  level (alongside `InitialState(...)` and `OnUnhandledEvent(...)`, not inside a step), declaring
  *which* state property is this saga type's business key. A saga that never calls it is unaffected
  by everything below.
- **`CorrelateBy(messageKey, stateKey)`** — unchanged in what it does (extract from the message,
  assign to the state property) for every saga that hasn't called `CorrelateOn`. When a saga *has*
  called `CorrelateOn` naming the same property `CorrelateBy` targets, it additionally registers as
  that message type's key extractor for correlation purposes.

**All 39 existing call sites are unaffected, because none of their sagas call `CorrelateOn`.** This
also resolves the model-storage question in §5.2 structurally rather than by validation: there is
exactly one declared business key per saga type, so "the same message type in different states must
resolve to the same key" is true by construction, not by a runtime check. And it makes the existing
doc comment at `EventBuilder.cs:24-28` — "this is not correlation" — become *true precisely when it
should be true* (no `CorrelateOn`) instead of always false as originally drafted.

Worth being conservative about regardless of shape: this repo has shipped an envelope header the
orchestrator never actually read back **five separate times** (see `http-based-sagas.md` §1 and its
sibling notes), and correlation is the highest-blast-radius version of that same mistake.

Five places assert the *opposite* of the new behaviour and must be corrected: `README.md:451`,
`:977`, `:1074`, `sub-saga-composition.md:14`, `mixed-sagas.md:336`.

### 5.2 Shape

- **`SagaState.BusinessKey`** (`string?`) — engine-owned, set once at creation, exactly the precedent
  `SagaState.cs:22-35` establishes for the parent pointer. Promoted to a real column because
  `DataJson` is an opaque `text` blob (`Entities.cs:24`; migration `InitialCreate.cs:49` — **not**
  `jsonb`) and cannot be queried. It joins the field-by-field copy in
  `EfCoreSagaSnapshotStore.UpdateAsync:50-61`, whose comment at `:56-59` states the invariant this
  must not break: the columns are a projection of `DataJson` and the two never disagree. Mirror into
  `InMemorySagaStore.StoredSnapshot` (`:29-30`), which exists for exactly this reason.
- **Index: partial unique on `(SagaType, BusinessKey) WHERE BusinessKey IS NOT NULL`.** Unique, not
  plain, because nothing today prevents a concurrent double-initiate from creating two instances for
  one business key. Portable: partial unique indexes work on both Postgres and SQLite, the two
  providers in use. **Resolve the race by reserving before the step runs, not by catching after it.**
  On the initiate path when a business key is present, insert the blank new instance *before*
  `RunStepAsync`, so the unique index adjudicates before any step side effect executes. The loser's
  insert throws `SagaAlreadyExistsException` (`EfCoreSagaSnapshotStore.cs:28-30`); on that specific
  exception, look the instance up by business key instead of treating it as an infrastructure failure.
  Catching the collision *after* the step ran would mean re-running non-idempotent step actions on the
  retry, which defeats the point. Cost: one extra write per saga start, and only on the business-key
  path — no effect on any saga that hasn't called `CorrelateOn`. Mirror the reservation in
  `InMemorySagaStore` with a second `ConcurrentDictionary<(string SagaType, string BusinessKey), Guid>`
  and a `TryAdd`, so the guarantee holds on both providers, not just the one under test by default.
- **`ISagaDefinition` gains `string? TryGetCorrelationKey(object message)`** — it is the only window
  the orchestrator has onto a definition (`ISagaDefinition.cs`). Null means "this message carries no
  business key" — either the saga never called `CorrelateOn`, or no `CorrelateBy` extractor is
  registered for this message type — and the orchestrator falls back to the transport correlation id,
  which is every saga's behaviour today. Implemented by `OrchestratedSagaDefinition<TState>` and
  `ChoreographedSagaDefinition<TState>`. Returns `string`, not the extractor's own `TKey`, because the
  promoted column (below) must be one type across every saga type, for the same reason
  `ParentSagaType` is a column: `ISagaSummaryReader` queries columns without knowing the saga type.
  Normalize non-string keys via `Convert.ToString(key, CultureInfo.InvariantCulture)` and document
  that `TKey` needs a stable invariant string form.
- **`ISagaSnapshotStore` gains `FindByBusinessKeyAsync(string sagaType, string businessKey, ct)`** —
  two implementations, plus roughly 25 files referencing the interface.
- **Model storage: one shared `SagaCorrelationModel<TState>`, composed into both `SagaDefinitionModel`
  and `ChoreographySagaModel` rather than duplicated into each.** Those two models already share no
  base, and `EventBuilder.CorrelateBy` (`:24-41`) and `ChoreographyEventBuilder.CorrelateBy` (`:39-56`)
  are already byte-identical twins — a third copy of correlation logic guarantees the same drift this
  codebase has already had once. The shared model holds the `CorrelateOn`-declared property plus the
  registered `(messageType → extractor)` map, and is where build-time validation lives: a conflicting
  extractor for one message type, or an extractor targeting a property other than the one
  `CorrelateOn` declared, throws `SagaDefinitionException`, following the precedent at
  `VSaga.Core/ServiceCollectionExtensions.cs:51-56`. Because there is exactly one declared key per
  saga type, resolution tolerates `TimeoutSignal` (`StepDefinition.cs:57-61`, a synthetic message with
  no extractor registered) automatically — it simply returns null, same as any unregistered type.

> **Status note: choreography has no design record of its own, and this section builds on it anyway.**
> `ChoreographedSagaDefinition<TState>`, `ChoreographyEventBuilder` and `ChoreographySagaModel` are
> treated above as first-class things to extend (and were — `CorrelateOn` and `TryGetCorrelationKey` are
> on `dotnet/src/VSaga.Core/Dsl/ChoreographedSagaDefinition.cs`), but no document under `docs/design/`
> ever designed them. The only narrative record is
> [`docs/history/choreographed-saga-support.md`](../history/choreographed-saga-support.md), plus the
> reference material in [`docs/concepts.md`](../concepts.md) and
> [`docs/saga-dsl.md`](../saga-dsl.md). Read that history file first if you are picking this up cold and
> wondering where the choreography half came from; nothing here is a substitute for it, and no
> retroactive design doc for it exists or is planned.

### 5.3 The orchestrator change

In `HandleCoreAsync` (`SagaOrchestrator.cs:300-342`). Deserialization at `:308` already happens
before the lookup at `:312`, so the message body is available as a CLR object exactly where the key
must be extracted — no reordering needed. After the transport-id lookup misses, try the business-key
lookup.

**The invariant that must not break:** on a business-key hit, continue with the *found instance's own*
`CorrelationId`, never `received.CorrelationId`. That local at `:311` flows into `FindAsync`, every
`LogAsync`, `NewInstance`, and via `state.CorrelationId` into every outbound `MessageEnvelope.From`.
Substituting the inbound id would fork the timeline and every downstream envelope.

### 5.4 One hazard to document, not solve

`HttpInboundDispatcher` uses `received.CorrelationId` as the key for its per-correlation
serialization gate (`:87`, `:157`, `:189-198`). If two messages resolve to the same saga via
*different* transport correlation ids, that gate no longer serializes them. The dispatcher sits below
the orchestrator and cannot see the resolution, so this is not fixable at that layer. Document it as
a known limitation of the HTTP transport combined with business-key correlation — the durable guard
in that case is the existing optimistic-concurrency `Version` check on the snapshot store, not the
gate. **This is the least-certain call in the whole plan and needs one thing verified before treating
it as settled:** confirm that a `SagaConcurrencyException` raised inside `RunStepAsync` actually
reaches `HandleInfrastructureFailureAsync` and triggers ordinary redelivery, rather than being
swallowed somewhere on the way out. If it's swallowed, "document and accept" is wrong and this needs
an engine-level gate keyed on the resolved saga instance instead of the transport id — a real but
separable follow-up, not part of this change.

**Verified for item 15: the assumption holds.** Traced in `SagaOrchestrator.cs`:
`HandleStepSuccessAsync`'s final `PersistAsync` call (`:603`) sits *outside* `RunStepAsync`'s own
try/catch (`:508-518`), which wraps only `definition.HandleAsync` itself — so a `SagaConcurrencyException`
thrown there is untouched by that catch and propagates straight through `RunStepAsync`/`HandleCoreAsync`
into `HandleAsync`'s own try/catch (`:44-55`, catch at `:51-54`), which is exactly what routes to
`HandleInfrastructureFailureAsync` and its redelivery publish. Confirmed empirically, not just by
inspection, by two tests exercising the actual race (a controlled-fake interleaving, the same technique
`SagaOrchestratorBusinessKeyRaceTests`/`SagaOrchestratorTimeoutRaceTests` use, since there is no reliable
way to force it through real timing):
- `HttpInboundDispatcherGateHazardTests` (`VSaga.Transport.Http.Tests`) pins the hazard itself: two
  messages carrying different transport correlation ids run fully concurrently through the dispatcher's
  gate, proving it does not serialize them — the mirror image of the existing
  `SyncReply_IsNotDispatchedInlineDuringThePublishingStep`, which proves the gate *does* serialize two
  dispatches sharing the same correlation id.
- `SagaOrchestratorConcurrencyRedeliveryTests` (`VSaga.Core.Tests`) reproduces the resulting version race
  on a shared instance and confirms the exception does reach `HandleInfrastructureFailureAsync`, which
  does publish a redelivery (delivery-attempt header incremented, same MessageId) rather than swallowing
  it or misrouting it into an ordinary business-level step failure (`HandleStepFailureAsync`/`StepFailed`).

`SagaOrchestratorConcurrencyRedeliveryTests` caught two things a read of the code alone would have
gotten wrong, both worth knowing before leaning on this as a "durable guard":
- The redelivery is a **dead end for the loser, not a retry-to-success**. `HandleInfrastructureFailureAsync`
  deliberately reuses the original MessageId (`:79`, "so the dedupe check ... will correctly recognize the
  redelivered copy and skip it"), and `RunStepAsync` already logged a `MessageReceived` entry for that
  MessageId *before* the failing persist (`:492-494`) — so `HandleCoreAsync`'s `IsDuplicateAsync` check
  recognizes the redelivered copy as already-seen and silently skips it. The Version check stops the
  *state* from corrupting; it does not cause the loser's message to eventually apply.
- A step's ordinary (non-deferred) `.Publish(...)` — `EventBuilder.Publish`, `ctx.PublishAsync` — runs
  synchronously inside `definition.HandleAsync`, *before* `PersistAsync` is even called. Both racers'
  publishes go out for real regardless of which one wins the persist. The "durable guard" protects
  stored state from corruption; it does not deduplicate business-visible side effects from an
  unserialized concurrent step. (`ctx.PublishAfterCommitAsync`/outbox-staged publishes are the one kind
  that *is* protected — those are discarded, not sent, when the persist that would have committed them
  never lands, per `HandleStepFailureAsync`'s and `HandleStepSuccessAsync`'s own comments.)

Scope held to what item 15 asked: this only pins and documents the existing behavior. Building an
engine-level gate keyed on the resolved saga instance remains the separable follow-up noted above — not
attempted here.

`typescript/packages/participant/src/participant.ts:24` carries a doc comment that goes stale.

---

## 6. W3C trace propagation

**Goal:** one trace spans a saga across services.

**Use the bare `traceparent`/`tracestate` names, not `x-vsaga-`-prefixed ones.** Interoperability is
the entire point of W3C trace context: an OTel collector, a broker plugin, or a non-vSaga consumer
all expect the standard names. Three .NET allowlists and one TS allowlist must gain them —
`HttpMessageTransport.cs:200`, `VSagaHttpEndpointExtensions.cs:70`, `BrighterTransport.cs:270`, and
`typescript/packages/transport-http/src/transport.ts:336`. These are *allowlists*, so adding two
well-known names is a small intentional addition rather than a loosening; the rationale in Brighter's
comment at `:261-266` (keep its CloudEvents-flavoured Bag noise out of redelivery) is unaffected.
RabbitMQ, MassTransit, Wolverine and InMemory already pass headers through losslessly — RabbitMQ's
`byte[]` quirk is handled at `GetHeaderString:196-207`.

**Hand-roll inject/extract in `VSagaDiagnostics`; do not add a package dependency.**
`VSaga.Abstractions` has zero `PackageReference`s and that is worth preserving. `ActivityContext`
comes from `System.Diagnostics.DiagnosticSource` in the shared framework, and `traceparent` is a
fixed 55-character string — parse and format it directly rather than pulling `OpenTelemetry.Api` into
the dependency-free leaf.

**Spans.** `SagaOrchestrator.cs:396` gains `ActivityKind.Consumer` and a parent context extracted
from `received.Headers`. Today it uses the `StartActivity(string)` overload, which adopts
`Activity.Current` — `null` on broker transports, so every hop roots a new trace; and on HTTP,
ASP.NET Core's *server* activity, so it silently attaches to the wrong trace on the inline dispatch
path but not the pump path. A producer span is new in `SagaContext.PublishInternalAsync`, which is
also where the context gets injected into the envelope. Add
`activity?.SetStatus(ActivityStatusCode.Error, ...)` on the failure path at `:424-444`, which today
records trace ids into the log but never marks the span failed.

**Redelivery keeps the same trace.** `SagaOrchestrator.cs:70-74` already copies all inbound headers
forward, so `traceparent` echoes automatically. A retry of the same logical delivery belongs in the
same trace; add a `delivery.attempt` tag rather than starting a linked span.

**`AddVSagaOpenTelemetry` stays unopinionated about exporters** — a deliberate, documented existing
decision (`Observability/ServiceCollectionExtensions.cs:10-16`), and the dashboard reads the
persisted event log rather than an OTel backend. Document the one-line OTLP wiring in
`docs/observability.md` instead. Do register the W3C propagator as the default.

**Wire `SagaDuration`; delete `RunningSagas` instead of wiring it.** Both
(`VSagaDiagnostics.cs:25-26`) are declared and never recorded, so anyone scraping by name today gets
silently absent dashboards — but they don't call for the same fix. `SagaDuration` is cheap and
correct as a plain `Counter`/`Histogram` recording `(now - state.CreatedAtUtc)` at the two places a
saga already reaches a terminal status (`HandleStepSuccessAsync`, `RecordTimeoutOutcomeAsync`).
`RunningSagas` as an `UpDownCounter` is a trap: it's process-local and non-idempotent, so a restart, a
redelivery, or a second replica desynchronizes it permanently and it drifts to nonsense with no way to
self-correct. The right instrument for "how many sagas are running right now" is an
`ObservableGauge` backed by `COUNT(*) WHERE Status = Running` against the store — which needs a
scoped store reachable from the meter callback, the same captive-dependency shape
`SagaTimeoutDispatcherHostedService` had before §8 item 4 fixes it. Rather than wire
`RunningSagas` wrong, delete it — nothing currently depends on an instrument that's never emitted —
and leave a proper gauge as a named follow-up for `VSaga.Observability`, which already has the
`IServiceScopeFactory` access this would need.

TS mirror: `protocol/src/headers.ts`, `protocol/src/envelope.ts:60-70`,
`transport-http/src/transport.ts:336`, and `participant/src/participant.ts:239`, where the reply
envelope is built from scratch and must thread `traceparent` from `received.headers`. Decide
deliberately that `traceparent` does **not** belong in `ENGINE_OWNED_HEADERS` (`headers.ts:29`, a
strip list) — it must propagate.

---

## 7. Docs restructure

**Goal:** a README someone reads in three minutes, with everything else reachable from it.

The README is structurally hostile to a clean split: 1 H1 + 24 H2 and **zero H3**, so there is no
sub-hierarchy to hoist. Only §4 (Saga Service Map) is pure reference and only §24 (Getting started,
`:1921-1967`) is pure onboarding; §5/§7/§11/§13 are pure changelog. **The other 18 sections are
mixed** — a reference core wrapped in commit narrative — so most must be *cut*, not moved. The
transport-adapter sections (§18-§21, 515 lines) hold the most valuable reference material in the file
and are also the most diluted, at roughly 40-50% run logs and mutation-test transcripts.

```
README.md                    ~200 lines: what it is, install, a first saga, run the demo, doc index
LICENSE                      MIT
CONTRIBUTING.md              build/test/PR conventions
docs/
  README.md                  doc index
  getting-started.md         install, write and run your first saga (NOT the demo)
  concepts.md                orchestrated vs choreographed, correlation, compensation, timeouts
  saga-dsl.md                the DSL reference — this content does not exist yet
  configuration.md           SagaOrchestratorOptions, outbox mode, transport options
  persistence.md             EF Core/Postgres, in-memory, migrations, the volume caveat
  observability.md           traces, metrics, the persisted event log
  dashboard.md               API endpoints, API-key auth, the SPA, the Saga Map
  testing.md                 SagaTestHarness
  chaos.md                   VSaga.Chaos
  transports/
    index.md                 the IMessageTransport contract and how to choose
    rabbitmq.md  wolverine.md  masstransit.md  brighter.md  http.md  in-memory.md
  typescript-participants.md the TS SDK — currently undocumented anywhere
  design/                    the 3 existing design docs, plus this one, moved unchanged
  history/                   the changelog narrative, verbatim, one file per topic
```

**The DSL reference has to be written, not moved.** No section anywhere documents the core DSL — it
appears only as fragments inside §8, §14, §15, §16, §22, §23. `docs/saga-dsl.md` is the one genuinely
new document: the full method inventory for `OrchestratedSagaDefinition`,
`ChoreographedSagaDefinition`, `StateBuilder`, `EventBuilder`, `ChoreographyEventBuilder`,
`TimeoutBuilder`, `RetryPolicy`, `ISagaContext`, and the `.CallHttp` extension.

**The TypeScript SDK is invisible today.** `README.md:41`'s repo-layout tree lists only
`dashboard-web/`; the seven `typescript/packages/*` are never mentioned, despite four commits
building them.

**This resolves six dangling references** to three files that have never existed:
`docs/typescript-participants.md` (`ci.yml:68`, `typescript/eslint.config.mjs:6`,
`typescript/vitest.config.mts:4`), `docs/readme-section-masstransit.md`
(`VSaga.Transport.MassTransit.csproj:12`), `docs/readme-section-brighter.md`
(`BrighterTransport.cs:134`, `BrighterTransportTests.cs:78`). The new tree gives each a real home;
update the six citing comments.

Files under `docs/history/` move verbatim with only a short header naming the commit they describe —
the point is preserving the record, not rewriting it. The new README's install section is the first
`dotnet add package` / `npm install` instruction that has ever existed in this repo.

---

## 8. Sequencing

Three collisions drive the order: the outbox and tracing's producer span both rewrite
`SagaContext.PublishInternalAsync`; correlation rewrites `HandleCoreAsync` while tracing rewrites
`RunStepAsync` (adjacent but separable); packaging touches only build files and collides with
nothing.

**Packaging → outbox → correlation → tracing → docs.** Packaging first because it is independent and
is what makes every later improvement reachable by anyone. Docs last because they document the four
features preceding them. `LICENSE` lands with packaging, not with the restructure.

One commit each, per this repo's habit, and no commit depending on one not yet verified:

**Progress: all 19 items committed, plus two unplanned fixes.** The first
(`Commit outbox rows atomically with the snapshot`, between items 10 and its predecessors) repaired
the §4.1 step 2 violation described in that section's callout — it is not an extra feature, it
restores the behaviour item 8 was supposed to have had. Docker was available from item 12 onward, so
the five Testcontainers-backed suites (RabbitMQ, MassTransit, Wolverine, Brighter, Postgres) ran for
real from that point on, not just compiled — the "run them before trusting" caveat carried forward
from items 7–11 no longer applies to anything after item 11.

Items 13–19 landed via a single Workflow run: implement → adversarial review (two independent lenses —
correctness and concurrency — on the riskier items, 14/15/18) → fix only if a finding reached "high"
severity, which none did. That review process still surfaced two genuine bugs the plan's own prose
didn't anticipate, closed in a second unplanned fix (`Discard staged outbox rows and fix
metric/dead-letter bookkeeping on a lost persist race`, plus a docs-only commit restoring findings the
§7 restructure had dropped): `HandleStepSuccessAsync`'s final persist had no failure handling at all,
so a lost `SagaConcurrencyException` race (exactly the one §5.4 verifies reaches redelivery) left a
staged outbox row durably queued on the in-memory provider — the recovery poller would later dispatch
it for a transition the snapshot never actually recorded — and separately recorded a phantom
`SagaDuration` for the same phantom transition. Both are fixed now (the persist is wrapped in a
try/catch that discards the staged publishes and defers the duration recording until after a
successful commit, then re-throws so redelivery is unaffected), and the identical gap in
`HandleStepFailureAsync`'s own `ChildSagaFinished` staging was found and closed the same way while
fixing the first. Every fix is mutation-tested (the fix temporarily reverted, the new test confirmed
to fail, then restored) — this repo's established discipline, applied to review findings the same way
it's applied to planned work. Full suite as of the fix round: 318/318.

1. `LICENSE` + `Directory.Build.props` packaging metadata + MinVer, with `fetch-depth: 0` added to
   every CI checkout in the same commit — MinVer silently produces `0.0.0` on a shallow clone, and a
   build assertion should fail loudly if the computed version is ever `0.0.0`.
2. Bus SDK version ranges moved into `Directory.Packages.props` — first confirm the per-project
   `VersionOverride` pins aren't there because two projects genuinely need different versions; if
   they're just habit, this is a plain move.
3. npm `publishConfig`/metadata/per-package READMEs + the `workspaces` glob fix.
4. **Fix `SagaTimeoutDispatcherHostedService`'s pre-existing captive-dependency bug** — it injects a
   Scoped `ISagaTimeoutStore` directly into a singleton `BackgroundService`
   (`SagaTimeoutDispatcherHostedService.cs:10`), which only works today because the in-memory
   provider registers everything Singleton; under EF Core it's one `DbContext` for the process
   lifetime driven from a background loop. Switch it to `IServiceScopeFactory` per
   `SagaRuntime.cs:26`, matching what item 8 below will need to get right from the start rather than
   copying a bug into a second hosted service.
5. `HandleStepFailureAsync` discards its abandoned deferred publishes (§4.5, standalone).
6. `ISagaOutboxStore` + EF and in-memory implementations + migration, wired to nothing yet.
7. `SendRawAsync` added to `IMessageTransport` (§4.4) — six adapters, mechanical, independently
   testable ("each adapter's `SendRawAsync` reaches the named destination"), lands before the outbox
   commit that needs it so that commit's diff stays focused on the outbox itself.
8. `DeferredPublish` becomes the §4.3 hybrid (row + typed dispatch closure); rows written pre-persist;
   the existing inline drain marks them dispatched. **The highest-risk commit in the plan** — all 222
   tests must pass with zero behaviour change, and `TimeoutDrainTests.cs:75/:79` plus the whole
   `VSaga.Http.Tests` suite are the canaries that catch a regression here.
9. `SagaOutboxDispatcherHostedService` (the crash-recovery poller), scoped correctly per item 4.
10. `PublishChildSagaFinishedAsync` routes through the outbox.
11. `Outbox:Mode=All`. **Resolved as: `All` makes every `ctx` publish deferred**, joining the same queue
    `PublishAfterCommitAsync` uses. That is forced rather than chosen — `ctx.PublishAsync`/`SendAsync`
    fire mid-step, so by the time any persist commits the message has already left, and a row written
    beside a message that is already gone guarantees nothing. Deferring is the only reading of "routed
    through the outbox" with content. The §1 decision stands: `Deferred` remains the default, so today's
    inline semantics and every existing test are untouched, and an operator choosing `All` is knowingly
    accepting the trade-off `ISagaContext.PublishAfterCommitAsync`'s own doc spells out (a publish that
    fails post-commit has nowhere to go). It also buys something real: under `All` a step that publishes
    and *then* throws no longer leaks the publish, because the failure path discards the queue.

    Two things `Deferred` never exercised become load-bearing here, and both were bugs in the first
    draft: `ctx.SendAsync` queues a **destination** (drop it and the poller broadcasts an addressed
    message), and `StartChildAsync` queues a **fresh correlation id** while `NotifyParentAsync` queues
    the parent's (key the row on the publishing saga and the poller recreates the child under the
    parent's own id). `DeferredPublish` therefore carries the whole `MessageEnvelope` plus the
    destination, and `EnqueueOutboxRowsAsync` reads the row's identity from the envelope, never from the
    saga.
12. `SagaState.BusinessKey` + column + partial unique index + migration + both stores + the
    reservation-insert (§5.2).
13. `CorrelateOn` + the shared `SagaCorrelationModel<TState>` + `ISagaDefinition.TryGetCorrelationKey`
    + `CorrelateBy`'s dual role when a saga has called `CorrelateOn` (§5.1/§5.2). Zero behaviour
    change for all 39 existing call sites — verify that directly, not just by inspection.
14. `HandleCoreAsync` business-key fallback (§5.3). Correct the five docs asserting the opposite.
15. Add a test pinning the `HttpInboundDispatcher` gate hazard (§5.4) — after first verifying whether
    `SagaConcurrencyException` reliably reaches redelivery, per that section's open question.
16. `traceparent`/`tracestate` inject/extract in `VSagaDiagnostics` + `MessageEnvelope.From`.
17. The four allowlists (three .NET, one TS) + the TS envelope/participant mirror. Confirm first
    whether TS `transport-http` mirrors a raw-publish path that also needs the equivalent of item 7.
18. Consumer and producer spans, `SetStatus` on failure, `SagaDuration` wired, `RunningSagas` deleted
    (§6, corrected).
19. Docs restructure, in one commit — a pure move plus new writing; splitting it would leave the
    README half-migrated.

---

## 9. Verification

**Per commit:** `dotnet build dotnet/VSaga.slnx` clean (it is today — 0 warnings under
`TreatWarningsAsErrors`), then `dotnet test dotnet/VSaga.slnx` — 222 tests, Docker required for the
Testcontainers suites. For anything touching the TS packages: `cd typescript && npm run lint && npm
run typecheck && npm run build && npm run test`.

**Mutation testing**, per this repo's established habit — break each change deliberately, confirm
*exactly* the tests written for it fail, restore. The three sharpest cases here:

- Moving the outbox row write to *after* `PersistAsync` must break something. That is the whole
  dual-write point, and a naive "is a row written at all" test would pass the mutation.
- Returning `received.CorrelationId` instead of the found instance's own id on a business-key hit
  must break a timeline-continuity test, not merely a lookup test.
- Making the inline dispatch in commit 8 go through `PublishRawAsync`/`SendRawAsync` instead of the
  hybrid's typed closure must break `TimeoutDrainTests.cs:75` specifically — it's the one test in the
  suite that would catch exactly this regression, since `InMemoryMessageTransport.PublishRawAsync`
  passes `message: null` and that test asserts on `p.Message`, not just on the message type name.

**Tripwire tests that must stay green untouched.** If any of these needs editing, the design is
wrong, not the test:

- `TimeoutDrainTests.cs:75` `..._DrainsItAndTheSagaReachesItsFinalState` (asserts on `p.Message`,
  not just the type name — the one that would catch a regression to raw/type-erased dispatch)
- `TimeoutDrainTests.cs:79` `..._DrainsAfterItsOwnPersist_NoUnexpectedEvent`
- `TimeoutDrainTests.cs:180` `..._DiscardsTheQueuedPublishInsteadOfSendingIt`
- `SagaOrchestratorTests.cs:216` `StepLevelRetryPolicy_QueuedLoopbackPublishesExactlyOnce...`
- the whole `VSaga.Http.Tests` suite
- `CallHttpSagaMapTests.cs:35-42` (publish log entries feed the Saga Map)

**Live verification** with `docker compose up -d --build` plus `docker-compose.chaos.yml`, which is
non-optional for anything touching message flow. Specifically: kill the sample container between a
commit and its drain and confirm the poller republishes — the outbox's whole reason to exist, and
something no unit test can prove; confirm a saga correlates on a business key carried by a message
with a *fresh* transport correlation id; and confirm one `traceId` spans orchestrator and participant
across a hop, the check that catches a header the orchestrator never actually reads back.

Filter live-verification queries by `createdAtUtc` after container start — `docker compose up` does
not reset the named Postgres volume, and stale instances otherwise pollute the counts. Run `docker
compose down -v` for a genuinely clean read.

**Packaging:** `dotnet pack -c Release` locally and inspect the generated nuspecs — confirm the
bounded MassTransit/Wolverine/Brighter ranges landed and that transitive pinning has not promoted
unexpected dependencies. `npm publish --dry-run` for the seven packages. Then a real tag.
