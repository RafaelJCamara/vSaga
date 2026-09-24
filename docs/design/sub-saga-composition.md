# Design: sub-saga composition

**Status: Slices 1, 2a, and 2b are built and shipped** — see the README's "Sub-saga composition: parent
linkage", "Sub-saga composition: completion notification", and "Sub-saga composition: engine safety net"
sections, which are the authoritative description of what exists. **Slice 3 is closed, deliberately not
built** — see §3.5's status note and §4 for the reasoning and sign-off. This file is now a historical
record of that analysis; there is no further open work behind it.

**One claim in the original sketch was wrong, and it bears directly on the §3.4 decision.** §3.4(a)
said a child publishing its own domain message "works today with no engine change, once the child knows
its parent id." It does not. `SagaContext.PublishAsync` always stamps the *publishing* saga's
correlation id, and (at the time this analysis was written) `SagaOrchestrator.HandleCoreAsync` correlated
strictly on the inbound correlation id — so a child publishing "I'm done" sends it under the child's own
id, where the parent never sees it. `CorrelateBy` was not a fallback for this: it was documented as a
business key for dashboard search, explicitly not used for routing. **Since production-readiness.md
§5.1–§5.3 shipped (§8 items 13/14), that is no longer quite true** — `CorrelateBy`, paired with
`CorrelateOn`, now drives a
business-key lookup when the transport correlation id misses. (Which section is which, since earlier
drafts of this line and of `mixed-sagas.md` cited different pairs: the `CorrelateOn`/`CorrelateBy` API
decision is **§5.1**, the `SagaState.BusinessKey` storage shape and index are **§5.2**, and the
orchestrator's fallback lookup in `HandleCoreAsync` is **§5.3**.) It still doesn't rescue the case this
paragraph is about, though: the lookup is scoped to `(SagaType, BusinessKey)`, and a child gets a fresh
correlation id specifically so it can start its own saga type, so it can only ever resolve to another
instance of the *same* saga type as the one doing the lookup — never a different saga type's instance,
which is what addressing a parent needs.

Following that through changed the shape of the §3.4 decision twice over: **(b), not (a), is the option
needing no new public API** (the orchestrator already holds the transport and can stamp any envelope),
and the two options turn out to be **complementary rather than alternatives** — only (a) can carry the
child's domain result, and only (b) can fire when a child fails, because a failed child never reaches
its own publish step. The original recommendation of "(a) first, (b) later" survived that correction —
Slice 2a built (a), on that revised reasoning. §3.4 is retained below as a historical record of the
analysis; §7.1 carries the current status.

**Slice 2a shipped, and surfaced a real hazard this file didn't anticipate**: a child that addresses its
parent from the very same step that started it can race ahead of the parent's own not-yet-persisted
transition. See the README section for the live/mutation-tested detail and §5 for the failure mode.

Written to be picked up cold in a later session: every claim about the current codebase carries a
`file:line` so it can be re-checked rather than trusted. Line numbers in §2 were accurate at commit
`f00dee3` and have since drifted — re-grep rather than trusting them.

---

## 1. What it is

One saga starts another as a step, waits for it to finish, and resumes with its result — the parent
treats a whole child saga as a single logical step.

Concretely here: `OrderSaga` reaching `AwaitingPayment` could start a separate `PaymentSaga` with its
own retries, gateway-fallback states, and compensation, then wait for it to report back.

The motivations are reuse (one `RefundSaga` invoked by several parents), decomposition (keeping a
20-state flow readable), and independent lifecycles (a child with its own timeout/retry policy that
doesn't pollute the parent's state machine).

Its only trace in the repo today is the roadmap line in `README.md` — "additional transport adapters
(MassTransit/Wolverine) and sub-saga composition" — inherited from the original v1 commit's note.
There is no design or code behind it.

---

## 2. What already exists that this builds on

Three recent passes built most of the machinery without aiming at this. Read these before designing
anything new — the intent is to reuse patterns, not invent parallel ones.

| Capability | Where | Why it matters here |
|---|---|---|
| Composite `(SagaType, CorrelationId)` identity | `dotnet/src/VSaga.Persistence.EFCore/VSagaDbContext.cs:28` | Two sagas can already coexist over one business transaction |
| Join / "wait for all branches" | `dotnet/src/VSaga.Core/Dsl/StepDefinition.cs:31` (`ResolveTargetState`), `EventBuilder.TransitionTo(Func<...>)` | **The parent's wait needs no new engine work** |
| Header threading onto new instances | `dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:127,135,309,336` | Exact pattern the parent link should copy |
| Outbound envelope stamping | `dotnet/src/VSaga.Core/Runtime/SagaContext.cs:53`, `MessageEnvelope.From` | Where `StartChildAsync` hangs off |
| Cross-instance lookup for the dashboard | `ISagaSummaryReader.FindByCorrelationIdAsync` (`ISagaSummaryReader.cs:27`) | Precedent for a `FindChildrenAsync` |
| Related-saga UI strip | `typescript/dashboard-web/src/app/pages/saga-detail/saga-detail.ts:104` | Becomes two relations instead of one |
| Migration precedent | `dotnet/src/VSaga.Persistence.EFCore.Postgres/Migrations/20260825045219_*` | Adding columns + index to `SagaInstances` |

---

## 3. Design

### 3.1 Identity

A child is a **separate instance with its own correlation id**, plus a stored pointer to its parent:

```csharp
// SagaState — dotnet/src/VSaga.Abstractions/Sagas/SagaState.cs
public string? ParentSagaType { get; set; }
public Guid? ParentCorrelationId { get; set; }   // both null => root saga
```

**Rejected: child shares the parent's correlation id.** The primary key is
`(SagaType, CorrelationId)`, so sharing caps a parent at one child *per saga type*, and a
self-recursive saga (a `RefundSaga` starting a `RefundSaga`) collides outright.

### 3.2 Starting a child

The parent must not need the child's `TState` or definition type. It publishes the child's initiating
message with linkage on the envelope:

```csharp
// new on ISagaContext<TState>
Task StartChildAsync<TMessage>(TMessage message, CancellationToken ct = default) where TMessage : notnull;
```

Implementation: publish with a **fresh** correlation id and two new headers on `MessageEnvelope`:

```
x-vsaga-parent-saga-type
x-vsaga-parent-correlation-id
```

`SagaOrchestrator.HandleCoreAsync` reads them when creating an instance and stamps them onto the new
state — the same shape as `GetSourceService`/`GetCausationId` today.

Whichever saga's `CanInitiate` matches the message becomes the child; no compile-time link exists. This
is deliberate and matches how the dashboard's retry redrive already works (publish, let subscribers
pick it up).

### 3.3 Waiting — no new engine work

This is the join primitive from `f00dee3`:

```csharp
During(AwaitingChildren)
    .When<PaymentSettled>()
        .Then((ctx, _) => ctx.Saga.PaymentDone = true)
        .TransitionTo(s => s.AllChildrenDone ? Ready : AwaitingChildren);
```

Self-transition semantics are already right: one timeout covers the whole wait, and an arriving child
does not silently extend it (a self-transition neither cancels nor reschedules).

**Still true, but only for the parent's half.** The parent can park and be released by a message. What
has no machinery is *the child getting a message to the parent in the first place* — see the
correction in §3.4. So "no new engine work" describes the waiting, not the notifying, and the section
title oversold it.

### 3.4 Notifying the parent — **OPEN DECISION**

> **Corrected after building Slice 1.** Option (a) does *not* work with no engine change — see the
> status note at the top of this file. A child cannot address its parent at all today, so (a) needs a
> way to publish under the parent's correlation id before it is even expressible.

**(a) Child publishes its own domain message.** ~~Works today with no engine change~~ — needs a new
method on `ISagaContext` that publishes under `Saga.ParentCorrelationId` (that field does now exist,
and is populated). Typed; the parent matches `When<PaymentSettled>()`. Every child must remember to do
it, and a child that forgets leaves the parent hanging until its own timeout.

**(b) Engine auto-publishes `ChildSagaFinished(childCorrelationId, childSagaType, status)`** to the
parent when a child with a parent reaches a terminal status. Uniform; works for children unaware they
are children. But it is *one CLR type*, so a parent awaiting two different child types must branch on a
string field inside `.Then(...)` rather than on message type — against the grain of this DSL.

#### The cost comparison runs the opposite way to the original framing

**(b) needs no new public API at all.** `SagaOrchestrator` already holds the transport and can stamp
any envelope it likes, and it already has the child's `ParentCorrelationId` on the state — so (b) is a
terminal-status hook, a contract type, and an append-only `SagaEntryType`. It is **(a)** that adds
public surface to `ISagaContext`. The original sketch had this backwards, and so did the first attempt
at correcting it.

#### …but the decision does not turn on cost, it turns on payload

**`ChildSagaFinished` carries no domain data.** A parent that started a `PaymentSaga` wants to know
*what was charged*, not merely that it finished. Under (b) alone its only recourse is reading the
child's state, which it cannot do generically: `ISagaSummaryReader` is saga-type-agnostic and hands
back untyped `DataJson`. So (b) alone is insufficient for most real waits however cheap it is.

**And a failed child never reaches its "publish my result" step**, by construction — so (a) cannot
cover failure or timeout, however typed it is.

**They are complementary, not alternatives**, which is what the original either/or framing missed:

| | carries the domain result | fires when the child fails or times out |
|---|---|---|
| (a) child publishes | yes | no — the child never gets there |
| (b) engine publishes | no | yes |

**Recommendation: (a) as the primary path, (b) afterwards as the failure net.** Same conclusion the
original sketch reached, reached for a sound reason this time — not "(a) is free" (it is not), but
"only (a) can carry the answer back."

Two things to settle before building (a):

1. **Narrow the API.** Not `PublishAsync(message, correlationId)` — that lets any saga publish under
   any id and mint orphan instances, dissolving the invariant that a saga's outbound messages carry its
   own correlation id. Prefer `ctx.NotifyParentAsync(message)`, which reads `ParentCorrelationId`
   itself and fails loudly on a root saga. Identical engine work; the only foreign id a saga can
   publish under is one the engine already put on its state.
2. **The notification fans out.** Published under the parent's correlation id, it reaches *every* saga
   type tracking that id — in the sample, `OrderSaga` as well as `PostShipmentChoreography`. That is
   the same fan-out any published message has, but it needs documenting rather than discovering.

**A hazard specific to (b), corrected after building it.** The original claim here was that
`UnhandledEventPolicy.Throw` makes an unhandled message nack and redeliver. Reading
`SagaOrchestrator.RunStepAsync` closely while building Slice 2b showed that is not what actually
happens: the exception `HandleAsync` throws on an unhandled event is caught by `RunStepAsync`'s own
catch block and routed to `HandleStepFailureAsync` — the same path a genuine step failure takes — which
marks the saga `Failed` and **acks** the message. There is no redelivery loop; the real hazard is a
silent, one-shot false `Failed` on a parent that never asked for the message. That doesn't change the
conclusion — a parent using `Throw` and never expecting `ChildSagaFinished` still should not be
corrupted by it — so (b) still has to be opt-in per parent, not a global engine behaviour. It changed the
mechanism: see the Slice 2b status note below for how the opt-in ended up implemented with no new DSL
call at all.

**Status: (a) built and shipped as Slice 2a — `ctx.NotifyParentAsync`.** Built exactly as specified
above: narrowed to read `Saga.ParentCorrelationId` itself rather than a general publish-under-any-id
overload, fails loudly (before any I/O) on a root saga, and does not cover a child's timeout — see §5
for what that leaves uncovered and the new race it surfaced.

**(b) built and shipped as Slice 2b — the engine-published `ChildSagaFinished` safety net.** The opt-in
mechanism turned out simpler than either option this file originally considered (a new
`OnUnhandledEvent`-style DSL call, or a manual per-parent flag): `SagaRuntime<TState>.Subscription` is
already built from `ISagaDefinition.MessageTypes` — the union of every message type a saga has *any*
declared handler for. A parent that never declares `.When<ChildSagaFinished>()` (or
`.On<ChildSagaFinished>()`) anywhere in its own DSL never subscribes to the message type at all, so the
transport never delivers it and `UnhandledEventPolicy` never enters the picture. Declaring the handler
*is* the opt-in; no separate switch exists. See the README's "Sub-saga composition: engine safety net"
section for the full shipped shape, live verification, and mutation results.

### 3.5 Compensation cascade — **CLOSED, not built**

Does a parent's `.Compensate()` cascade into completed children?

**Recommendation: no, not automatically.** An engine that publishes compensating side effects on your
behalf, for children it merely *believes* completed, is the same failure class as commit `c24928d`
("Fix saga timeout/message race that could ship and refund the same order"). Ordering across a tree is
ambiguous (depth-first? completion order?), and a child may already have self-compensated on its own
failure — so automatic cascade is a double-refund generator. The parent explicitly publishing its own
compensating command is boring and correct.

If it is ever wanted, it should be explicit opt-in (`.CompensateChildren()`) plus a child-side hook to
receive the request — that is Slice 3, and it is the one I would push back on.

#### Three further arguments, from having built Slice 1

1. **The parent genuinely does not know its children.** `StartChildAsync` returns `Task`, not the
   child's id, and there is no compile-time link. A cascade would therefore mean the engine issuing a
   *database query* mid-compensation to discover which side effects to send — and that query returns
   children still `Running`, children that already self-compensated on their own failure, and (where
   two saga types initiate on one child message) children the parent never intended to start.
2. **Unbounded depth, no cycle detection.** Slice 1 deliberately gives each child a fresh correlation
   id precisely so a saga can start its own type; a cascade walks that tree with nothing bounding it.
3. **Slice 1 shipped with child failure not touching the parent** — verified live across all 43
   parent/child pairs, every parent `Completed` regardless of whether its child completed, bounced, or
   timed out. A cascade adds implicit coupling in the reverse direction, and with both directions
   implicit nobody can predict what a single `.Compensate()` sends.
4. **Confirmed directly in code while closing this out, not just inferred from the design.**
   `CompensationRunner.RunAsync` (`dotnet/src/VSaga.Core/Dsl/CompensationRunner.cs:16`) and every delegate it
   invokes only ever receive `ISagaContext<TState>` (`dotnet/src/VSaga.Abstractions/Sagas/ISagaContext.cs:7`),
   which has no children-lookup method. Only `ISagaSummaryReader.FindChildrenAsync` has one, and that is
   a read-model query compensation code has no route to today. Argument 1 above is not a hypothetical
   engine change to imagine — it is the actual shape of the type `.Compensate()` delegates run against.

**Status: closed 2026-08-25 — recommendation confirmed, not built.** Reviewed against the current code
(argument 4) rather than re-approved from the sketch alone. No `.CompensateChildren()`, no child-side
compensation hook. A parent that needs a child compensated publishes its own compensating command
explicitly, the same way it would address any other collaborator. Documented as shipped (non-)behaviour
in the README's "Sub-saga composition: parent linkage" section. If a concrete use case surfaces later
that changes this calculus, treat it as a fresh design question — the arguments above assume no such
case exists yet.

---

## 4. Slices

### Slice 1 — parent linkage — **DONE**

- [x] `SagaState`: add `ParentSagaType` / `ParentCorrelationId`
- [x] `MessageEnvelope`: the two header name constants
- [x] `ISagaContext.StartChildAsync` + implementation in `SagaContext`
- [x] `SagaOrchestrator`: read the headers and stamp the new instance (in `NewInstance`, extracted from
      `HandleCoreAsync` to stay under the 60-line analyzer cap)
- [x] `SagaInstanceEntity` + `VSagaDbContext`: nullable columns, index on the pair
- [x] Migration `20260825121440_AddSagaParentLinkage` — purely additive, upgrades an existing volume in place
- [x] `ISagaSummaryReader.FindChildrenAsync` + both providers
- [x] `GET /api/sagas/{sagaType}/{correlationId}/children`
- [x] Dashboard: "started by" / "started" strips alongside the existing correlation-id one
- [x] Tests: `SubSagaCompositionTests` (real publish/receive path), children query in both providers,
      endpoint tests, Angular tests. Mutation-verified from both ends of the wire.
- [x] Sample: `PostShipmentChoreography` starts an `InvoiceDeliverySaga`. Answers §7.3/§7.4 narrowly —
      a demonstration was needed for the §6 live verification to be possible at all, and it was added
      without touching `OrderSaga`, leaving the "should `OrderSaga` itself be restructured" question open.

**Columns, not just `DataJson`:** `ISagaSummaryReader` is saga-type-agnostic and queries columns, so a
tree view needs real columns. The `DataJson` blob picks the new fields up for free (additive), but that
alone is not queryable.

**One thing worth knowing before Slice 2:** `SagaSummary` gained the two fields as *required* positional
parameters rather than defaulted ones, deliberately, so every projection site had to decide what to put
there instead of silently defaulting to null. That is a source-breaking change to a public record.

### Slice 2 — completion notification

Split in two after the §3.4 analysis, because the two halves cover different cases and 2a is the one
that makes a wait usable at all. Built 2a first, so the sample demonstrates a real wait before the
engine starts injecting messages on anyone's behalf.

**Slice 2a — the child reports its own result (the primary path) — DONE**

- [x] `ISagaContext.NotifyParentAsync(message)` — publishes under `Saga.ParentCorrelationId`, fails
      loudly on a root saga. Deliberately *not* a general publish-under-any-id overload; see §3.4
- [x] A parent in the sample that actually waits: `InvoiceFollowUpSaga`, via the existing join
      (`During(state).When<T>().TransitionTo(...)`) plus a timeout on the waiting state. Not
      `PostShipmentChoreography` — see the README section for why that would have contradicted its own
      documented "must not hold the leg open" invariant, and why archival (`InvoiceArchivalSaga`)
      rather than a second `InvoiceDeliverySaga` avoids sending two customer emails per invoice
- [x] Tests through the real publish/receive path (`NotifyParentAsyncTests`), mutation-verified from
      both ends the same way Slice 1 was. The fan-out note from §3.4 turned out narrower than written
      there once built and live-verified: it reaches every saga type that both tracks the parent's
      correlation id **and has declared a handler for that exact message type** — since subscription is
      per declared message type, `OrderSaga`/`PostShipmentChoreography` sharing the same correlation id
      as `InvoiceFollowUpSaga` never even receive `InvoiceArchivalFinished`, confirmed live (empty
      timeline for that message type on both)
- [x] **New, not anticipated by this doc**: a child that calls `NotifyParentAsync` from the very same
      step `StartChildAsync` started it in can race ahead of the parent's own not-yet-persisted
      transition and be silently dropped as `UnexpectedEvent` — see §5. Pinned by
      `NotifyParentAsync_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`;
      not observed live under normal load, since every real child in the sample has a genuine I/O
      round-trip between the two calls

**Slice 2b — the engine reports failures the child could not (the safety net) — DONE**

- [x] `ChildSagaFinished` contract — `VSaga.Abstractions.Sagas.ChildSagaFinished(Guid
      ChildCorrelationId, string ChildSagaType, SagaStatus Status)`
- [x] Orchestrator publishes it when a child with a parent goes terminal — narrower than the checklist
      line above literally reads: only from `HandleStepFailureAsync`'s exception path (always terminal)
      and `HandleTimeoutAsync`'s timeout path when it goes terminal, **never** from the ordinary
      message-driven success path (`HandleStepSuccessAsync`), even though that path can also finalize a
      saga — that's `NotifyParentAsync`'s territory, and firing there too would be a redundant,
      data-free duplicate for any child that already calls it. **Opt-in per parent** turned out to need
      no new mechanism at all: see the corrected §3.4 hazard note above — declaring a handler for
      `ChildSagaFinished` anywhere in a parent's own DSL is what subscribes it, via the same
      `ISagaDefinition.MessageTypes` → `SagaRuntime.Subscription` path every other message type already
      uses. Not routed through `SagaContext`/`ISagaContext`, unlike `NotifyParentAsync`: `SagaOrchestrator`
      publishes directly via its own `transport` field, since this is engine-initiated, not
      saga-code-initiated.
- [x] `SagaEntryType`: `ChildSagaStarted`, `ChildSagaFinished` — appended after `MessageSent`, per the
      append-only rule. `ChildSagaStarted` retags `StartChildAsync`'s own publish (previously an ordinary
      `MessagePublished`, per Slice 1's deferred note); `ChildSagaFinished` tags the engine's own publish.
- [x] Dashboard timeline + saga map rendering for the child hop: both new entry types were added to
      `SagaMapBuilder.ProcessEntry`'s outbound-edge-stitching switch arm (alongside `MessagePublished`/
      `MessageSent`), so they edge-stitch to a resolved destination via the same topology-registry path
      `NotifyParentAsync`'s publishes already use — no bespoke map logic needed. The Angular timeline/map
      views render `entryType` as plain text with no per-type icon switch, so the only frontend change
      needed was adding the two literals to `SagaEntryType` in `saga.model.ts`.
- [x] **New, not anticipated by this doc**: the same race Slice 2a found has a StepFailed-path analogue.
      A child that fails via exception in its own initiating step (the one `StartChildAsync` triggered)
      publishes `ChildSagaFinished` while still nested inside the parent's own `StartChildAsync` call
      under `InMemoryMessageTransport`'s synchronous/recursive dispatch — before the parent has persisted
      its own transition. Same outcome as Slice 2a's race: `UnexpectedEvent`, silently dropped, no
      redelivery. Only the StepFailed path can race this way; the timeout path is dispatched
      independently by `SagaTimeoutDispatcherHostedService` and cannot nest inside a `StartChildAsync`
      call. Pinned by
      `ChildSagaFinishedTests.ChildSagaFinished_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`.
      Not fixed, same reasoning as Slice 2a's race: a fix means reordering "run step actions, then
      persist" throughout the engine, beyond this slice's scope.
- [x] Sample: `InvoiceFollowUpSaga` opts in with a `.When<ChildSagaFinished>()` branch on
      `AwaitingArchival`, demonstrating the engine safety net rescuing the parent in ~15s (when
      `InvoiceArchivalSaga`'s own timeout fires) instead of the full 30s `ArchivalWaitTimeout` would
      otherwise take. `InvoiceArchivalSaga`'s timeout branch itself is unchanged — it still never calls
      `NotifyParentAsync`, which is exactly the gap this closes.

### Slice 3 — compensation cascade — **CLOSED, not built**

Considered, not built. §3.5's recommendation — no automatic cascade — was checked against the shipped
Slice 1/2a/2b code before closing (see §3.5's argument 4) and confirmed rather than re-approved from the
original sketch alone. Nothing here shipped:

- [x] ~~`.CompensateChildren()` on the parent's failure step~~ — not building
- [x] ~~Child-side hook to receive a compensation request~~ — not building
- [x] ~~Ordering and double-compensation semantics~~ — moot; there is nothing to order or deduplicate

A parent that needs a child compensated publishes its own compensating command explicitly. See the
README's "Sub-saga composition: parent linkage" section for where this is documented as shipped
(non-)behaviour, and §3.5 above for the full reasoning. If a concrete use case ever changes the
calculus, that is a new design question, not a resumption of this checklist.

---

## 5. Failure modes — document, don't discover

- **Child never starts** (nothing's `CanInitiate` matched). Parent hangs until its timeout.
  Unpreventable at publish time; must be documented. **Now pinned by a test**
  (`AChildMessageNobodyInitiatesOn_StartsNothingAndTellsNobody`) and described in the README, so it is
  documented behaviour rather than a surprise.
- **Two saga types initiate on the child message** → two children, parent counts one. Wants a guard or
  a loud note. Compare the `AddSaga` duplicate-`TState` guard added in `f00dee3`
  (`dotnet/src/VSaga.Core/ServiceCollectionExtensions.cs`), which turned a similar silent misbehaviour into a
  startup error. **Still unguarded** after Slice 1 — noted in the README, not solved.
- **Parent times out while the child still runs** → orphaned child, still holding whatever it reserved.
- **Parent retried from the dashboard** → children are *not* re-run; the reset replays the parent's
  own message only.
- **Adding a timeout to an existing state does not rescue in-flight instances** — timeouts are scheduled
  on entry to a state. Same trap as the 60 stranded sagas in the "Timeout coverage for every awaiting
  state" README section.
- **A child that calls `NotifyParentAsync` from the same step `StartChildAsync` started it in can race
  ahead of the parent's own not-yet-persisted transition.** Found building Slice 2a, not anticipated by
  this doc. `InMemoryMessageTransport.DispatchAsync` invokes every subscriber synchronously and
  recursively, so a zero-I/O child's notification is still nested inside the parent's own
  `StartChildAsync` call when it arrives — before the parent has persisted its own state, or (for a
  brand-new parent) inserted a row at all. `SagaOrchestrator.HandleCoreAsync` finds no existing
  instance, the message isn't among the parent's initiating types, so it logs `UnexpectedEvent` and
  drops it — no exception, so no redelivery either. Pinned by
  `NotifyParentAsync_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`
  (`dotnet/tests/VSaga.Core.Tests/NotifyParentAsyncTests.cs`). Real transports decouple a child's dispatch
  from the publisher's call stack, so this is not expected to reproduce deterministically the way it
  does in-memory — every real child in this repo has genuine I/O between `StartChildAsync` and
  `NotifyParentAsync` (a participant round-trip), which is what makes the parent's own persist reliably
  win the race in practice, confirmed live: zero dropped notifications across 21 real archival children.
  Not fixed — a fix would mean reordering this engine's "run step actions, then persist" sequence
  throughout, well beyond Slice 2a's scope. A child that reports back with no intervening work at all
  remains a real, narrow hazard.
- **The same race has a StepFailed-path analogue, found building Slice 2b.** A child that throws in the
  very same step `StartChildAsync` started it in publishes the engine's `ChildSagaFinished` while still
  nested inside the parent's own `StartChildAsync` call, for the identical reason: no persisted parent
  row exists yet for `SagaOrchestrator.HandleCoreAsync` to find. Same outcome — `UnexpectedEvent`, silent
  drop, no redelivery — and the same real-transport caveat: nothing in this repo's real sample children
  throws synchronously from their own initiating step, so this is a lab-conditions hazard, not one
  observed live. Unlike the `NotifyParentAsync` race, this one is StepFailed-path-only — a timeout can
  never fire nested inside `StartChildAsync`'s call stack, since `SagaTimeoutDispatcherHostedService`
  dispatches independently on its own poll loop. Pinned by
  `ChildSagaFinishedTests.ChildSagaFinished_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`
  (`dotnet/tests/VSaga.Core.Tests/ChildSagaFinishedTests.cs`). Not fixed, same reasoning as above.

---

## 6. Verification plan

**Unit/integration tests are not sufficient for the linkage.** This repo has a specific scar here: a
previous pass threaded `SourceService`/`CausationId` onto envelope headers, and tests that hand-built
`SagaLogEntry` objects with the field already populated passed while the orchestrator never actually
read the header. Any test that constructs the parent link by hand proves nothing.

Required, and **all three were done for Slice 1** — see the README section for the results:

1. Tests that drive the **real** publish → receive → create-instance path and assert
   `ParentSagaType`/`ParentCorrelationId` on the persisted snapshot.
2. Live `docker compose up` run with a real parent/child pair, then
   `curl .../children` and inspect the actual rows.
3. Mutation check, per this repo's habit: make the orchestrator ignore the parent headers and confirm
   the linkage tests fail — and *only* those.

**Worth keeping for Slice 2, because the mutation run confirmed the scar is still live.** Under both
mutations the EF Core provider tests and the dashboard endpoint tests stayed green, because they build
the parent link by hand. That is correct for their subject — the store and the route — but it means
they cannot catch a linkage that is never set, which is exactly how the `CausationId` tests passed
against a header nobody read. Any Slice 2 test for `NotifyParentAsync` has the same trap available to
it: a test that hand-publishes under the parent's correlation id proves nothing about whether
`NotifyParentAsync` reads `ParentCorrelationId`.

**All three done for Slice 2a too, same discipline** — see the README section for the full results:

1. `NotifyParentAsyncTests` drives the real `StartChildAsync` → transport → orchestrator →
   `NotifyParentAsync` → transport → orchestrator path; nothing hand-sets `ParentCorrelationId` or the
   released parent's state.
2. Live under `docker compose` (chaos overlay on, for all three endings): 21 archival children, 23
   follow-up parents, zero dangling or half-linked pointers, zero `InvoiceArchivalFinished` noise on
   `OrderSaga`/`PostShipmentChoreography` despite sharing the correlation id.
3. Mutated both ends: publishing under the child's own id instead of `parentCorrelationId`, and
   treating the read of `Saga.ParentCorrelationId` as always absent. Each failed exactly the 3 tests
   that depend on a real notification reaching its parent, and nothing else.

**All three done for Slice 2b too** — see the README's "Sub-saga composition: engine safety net"
section for the full results:

1. `ChildSagaFinishedTests`/`ChildSagaFinishedOptInTests` drive the real
   `StartChildAsync`/exception-or-timeout → engine publish → transport → orchestrator path; nothing
   hand-sets `ParentCorrelationId`, hand-builds a `ChildSagaFinished` message, or stamps a header.
2. Live under `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d` — see the README
   section for the run's numbers.
3. Mutated four ways: published under the child's own id instead of the parent's (failed exactly the 4
   tests that depend on real delivery to the parent); removed the root-saga guard, falling back to the
   child's own id (failed exactly the one root-saga test); made the ordinary success path also publish
   `ChildSagaFinished` (failed exactly the scope-boundary test, and confirmed clean across the rest of
   the solution's test suite, not just this file); dropped `StartChildAsync`'s `ChildSagaStarted`
   entry-type override back to the old `MessagePublished` (failed exactly the one Slice-1-era test
   updated for this slice).

---

## 7. Open questions

1. **§3.4 — both (a) and (b) built, as Slice 2a and Slice 2b.** The two options turned out to be
   complementary rather than alternatives: only (a) can carry the child's domain result, and only (b) can
   fire when a child fails or times out (since a failed child never reaches its own publish step). Built
   **(a) as the primary path** via the narrowed `ctx.NotifyParentAsync(message)`, and **(b) as the
   failure net** via the engine-published `ChildSagaFinished` — opt-in turned out to fall out of the
   existing `ISagaDefinition.MessageTypes` → transport-subscription mechanism for free, with no new DSL
   call, once the `UnhandledEventPolicy.Throw` hazard this file originally flagged was traced to its
   actual behaviour (a silent one-shot false-`Failed`, not redelivery — see the corrected note in §3.4).
   Together they cover the three ways (a) alone leaves a parent unanswered: a child that never reaches
   its own report-back step (unhandled exception, or timeout), and — still open, not something either
   slice addresses — the race in §5 where a same-step notification can race ahead of the parent's own
   unpersisted transition (StepFailed has an analogous race now too, also documented in §5).
2. **§3.5 — closed 2026-08-25.** Recommendation confirmed: no automatic cascade, and Slice 3 will not be
   built. Building Slice 1 added three arguments for it rather than against — the parent does not know
   its children without a mid-compensation database query, the tree is unbounded in depth with
   self-recursion deliberately enabled, and Slice 1 shipped with child failure not touching the parent,
   verified live — and closing this out added a fourth, confirmed directly in code: compensation
   delegates run against `ISagaContext<TState>`, which has no children-lookup method at all. See §3.5.
3. ~~Is Slice 1 alone worth shipping without a sample demonstrating it?~~ Settled by necessity: §6's
   live verification requires a real parent/child pair in the running stack, so the sample wiring could
   not be split off the way the choreographed DSL's was.
4. **Which sample?** Further answered, still additively. `InvoiceDeliverySaga` (Slice 1) demonstrates
   linkage without waiting; `InvoiceFollowUpSaga`/`InvoiceArchivalSaga` (Slice 2a) demonstrate a parent
   that actually waits, kept as a *separate* pair rather than retrofitted onto
   `PostShipmentChoreography` — see the README section for why. The original question — whether
   `OrderSaga` itself should be restructured around sub-sagas — is still open and still a product
   decision about what the sample is *for*, the same one the parallel fan-out work left open.
5. **Resolved by not needing it.** Should the parent learn its child's correlation id?
   `StartChildAsync` still returns `Task`, not `Task<Guid>`. Slice 2a didn't need it: `NotifyParentAsync`
   has the child address the parent via `Saga.ParentCorrelationId`, not the reverse, and
   `InvoiceFollowUpSaga` never needs its child's id for anything. Still a one-line signature change if a
   future slice does need it.
