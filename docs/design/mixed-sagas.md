# Design: mixed sagas (RabbitMQ messages and outbound REST calls in one saga)

**Status: built and live-verified**, as five commits in the order §4-§7 lay out: `ctx.CallHttpAsync`
(§4), the §3.2 retry-dedupe fix and the §3.1 timeout-drain fix as two separate engine commits (each
landed and live-verified alone before anything depended on them — this repo's habit, see `937243a`), the
Saga Map fix (§6, its own commit per the note below), then `MixedFulfilmentSaga` (§7). See the README's
"Mixed sagas: RabbitMQ messages and REST calls in one saga" section for the shipped shape and live-
verification evidence, including the chaos-overlay run that caught `AwaitingStock`'s own timeout firing
directly (not via a `StockUnavailable` message) and resolving cleanly — the direct live proof that §5's
drain fix closes the gap it was built for.

Every §9 mutation test passed as specified: removing a fix (or, for the timeout drain, moving it to the
wrong place instead of deleting it) made exactly the tests written for that fix fail, and reverting
restored a fully green suite each time. Two implementation notes worth recording for a later reader:

- **§3.2's dedupe fix and §3.1's timeout-drain fix landed as two separate commits**, not one "§5 engine
  change" commit — each is independently mutation-tested in `docs/mixed-sagas.md` §9's own list (four
  distinct mutations across the two), and splitting them let each be a small, focused, easily-reverted
  diff, consistent with `937243a`'s own "landed and verified alone" precedent.
- **The retried-`ctx.CallHttpAsync` test can't observe its `.Body(...)` value's identity** the way a
  message-aware `.CallHttp` body factory can (a counter closed over by a real factory function) — `.Body`
  takes an already-evaluated value, and `SagaTestHarness`'s in-memory store round-trips saga state through
  JSON on every read/write (deliberately, for real snapshot-isolation semantics), which silently resets
  any counter stored on saga state between the write that captured it and the read that would inspect it.
  The test instead reads the raw bytes the stub `HttpMessageHandler` actually received on each attempt,
  from a payload whose serialized value increments on every serialization
  (`dotnet/tests/VSaga.Http.Tests/CallHttpAsyncFixtures.cs`'s `CountingPayload`) — proving the shared executor's
  `body()` call happens once per attempt without relying on saga-state persistence at all.
- **Forcing the timeout's final-persist race to lose needed a way to bump the saga's `Version` without
  cancelling its pending timeout.** A self-transition (`During(Waiting).When<NudgeVersion>().TransitionTo(Waiting)`,
  `dotnet/tests/VSaga.Core.Tests/TimeoutDrainTests.cs`) does both: `HandleStepSuccessAsync` persists
  unconditionally regardless of whether the state actually changed, but a self-transition is "no
  transition" to the timeout-scheduling logic, so the pending timeout survives untouched — the same
  behaviour `EventBuilder.TransitionTo(Func<TState,State<TState>>)`'s own remarks document for a
  parallel-fan-out join.

Written to be picked up cold in a later session: every claim about the current codebase carries a
`file:line` so it can be re-checked rather than trusted. Line numbers were accurate at commit
`d95fb5b` and will drift — re-grep rather than trusting them.

---

## 1. What it is, and why it doesn't work today

`docs/http-based-sagas.md` §1 splits vSaga's HTTP surface into two things that "share the word HTTP and
nothing else":

1. `dotnet/src/VSaga.Transport.Http` — an `IMessageTransport` adapter so two vSaga services can talk without a
   broker. Not involved here.
2. `dotnet/src/VSaga.Http` — the `.CallHttp(...)` DSL, a saga step that calls an ordinary REST API that knows
   nothing about vSaga. Transport-agnostic; depends only on `VSaga.Core`. This is the one that matters.

The second shipped in Phase 2 (`ee8e108`, `e6b7704`, `d95fb5b`): `LoyaltyLookupSaga` reacts to a RabbitMQ
message and makes a real REST round trip. But it only *receives* over the broker — it never publishes a
broker command of its own, and it never compensates.

**A mixed saga** is one that drives RabbitMQ participants and REST participants side by side, and whose
compensation has to unwind both kinds of hop. That is not expressible today. There is no guard, no
`NotSupportedException`, no "HTTP-only saga" base type anywhere in the repo — it fails by omission, in
three separate places:

1. **`.CallHttp` is reachable only from `EventBuilder`** — `dotnet/src/VSaga.Http/EventBuilderHttpExtensions.cs:
   15-26` is the only entry point, and it delegates to `EventBuilder.Then(Func<…,Task>)`
   (`dotnet/src/VSaga.Core/Dsl/EventBuilder.cs:54-58`). Not from a `Compensate(state, ...)` delegate
   (`dotnet/src/VSaga.Core/Dsl/OrchestratedSagaDefinition.cs:61-62` takes a raw
   `Func<ISagaContext<TState>, CancellationToken, Task>`), and not from a `TimeoutBuilder` step
   (`dotnet/src/VSaga.Core/Dsl/TimeoutBuilder.cs:17-26` has only a synchronous `Then`). A compensating REST call
   must be hand-written against a raw `HttpClient`, which writes **no timeline entries** and therefore
   **never appears on the Saga Map** — the exact defect class §5.3 of `http-based-sagas.md` was written
   to fix.
2. **`SagaOrchestrator.HandleTimeoutAsync` never drains deferred publishes.**
   `HandleStepSuccessAsync` persists at `dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:471` and drains at
   `:473`. The timeout path (`:187-239`) does the analogous two-phase persist (`:205`, `:235`) but never
   drains. Anything queued via `ISagaContext.PublishAfterCommitAsync` on that path — which is exactly how
   `.CallHttp`'s loopback outcome publishes (`dotnet/src/VSaga.Http/HttpOutcomeAction.cs:45`) — is silently
   dropped. `docs/http-based-sagas.md:412` already named this ("`HandleTimeoutAsync` would also need its
   own parallel drain after `:235`") when it designed the engine change and it was never built.
3. **`SagaMapBuilder.ProcessInboundEntry` hardcodes `IsCompensation: false`**
   (`dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs:136`), while outbound `MakeEdge` honours the `_compensating`
   flag (`:175`). Invisible today because no compensation currently produces an inbound timeline entry —
   broker participants never reply to a compensating command in this repo's sample. A compensating REST
   call produces the first one.

**Outcome:** a `MixedFulfilmentSaga` in the OrderProcessing sample that authorizes payment over REST, then
reserves stock over RabbitMQ, and — on stock failure and on timeout — releases the stock over the broker
*and* voids the authorization over REST, waiting for the void to confirm before it calls itself `Failed`.
Both hops visible on the Saga Map, both proven against a live `docker compose` stack.

### Decisions already taken

Asked and answered before this was written, so treat them as settled rather than reopening them:

- **Mixed means broker messaging + outbound REST in one saga.** The other reading — per-message-type
  transport routing via a composite `IMessageTransport`, so some message types ride RabbitMQ and others
  ride `VSaga.Transport.Http` — was offered and explicitly **not** chosen. Out of scope for this design.
- **Demo vehicle: a new saga inside `dotnet/samples/VSaga.Samples.OrderProcessing`**, alongside `OrderSaga` and
  `LoyaltyLookupSaga`, matching this repo's established "one sample, converted" pattern
  (`docs/http-based-sagas.md` §1's decision 3). Not a new sample project; `OrderSaga` is not modified.

---

## 2. What already exists that this builds on

| Capability | Where | Why it matters here |
|---|---|---|
| `.CallHttp`'s execution body | `dotnet/src/VSaga.Http/HttpCallDefinition.cs:30-100` | The logic §4 shares between the declarative and imperative forms |
| Loopback vs. inline outcomes | `dotnet/src/VSaga.Http/HttpOutcomeAction.cs` | Unchanged by this design; both forms reuse it as-is |
| `PublishAfterCommitAsync` | `dotnet/src/VSaga.Core/Runtime/SagaContext.cs:103-108` | The mechanism §3's timeout drain is missing for |
| The deferred-publish queue | `dotnet/src/VSaga.Core/Runtime/SagaContext.cs:22-25, 40` | What §3.2's retry fix and §3.1's drain both touch |
| Compensation ordering | `dotnet/src/VSaga.Core/Dsl/CompensationRunner.cs` | Unchanged; a mixed saga's compensation is an ordinary `Compensate(state, ...)` delegate |
| The Saga Map's compensation flag | `dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs:106-107, 175` | What §5 extends to inbound entries |
| `InternalsVisibleTo` already granted | `dotnet/src/VSaga.Core/VSaga.Core.csproj`, `dotnet/src/VSaga.Http/VSaga.Http.csproj` | §4's `ctx.CallHttpAsync` needs none beyond what already exists |

---

## 3. Two constraints found by tracing the engine

Per this repo's habit (`docs/http-based-sagas.md` §3, `docs/sub-saga-composition.md` §5): read this before
building anything.

### 3.1 The timeout path drops any deferred publish, and this design makes that reachable for the first time

Traced in §1 item 2 above. `LoyaltyLookupSaga`'s `.CallHttp` never queues anything on the timeout path
because it has no timeout at all. A mixed saga's compensation runs from **both** a message-triggered step
(`.When<StockUnavailable>().Compensate()`) and a timeout (`WithTimeout(..., t => t.Compensate())`) — the
same `Compensate(state, ...)` delegate, reached two different ways. If that delegate's REST call uses a
loopback outcome, the message path drains fine (it always has) and the timeout path silently drops it.
This is the reason the engine change (§4) has to land before the sample can exist, not alongside it.

### 3.2 A retried step queues its loopback more than once, and the dedupe check cannot catch it

Independent of the timeout path, and the sharper of the two:

- `SagaOrchestrator.RunStepAsync` builds the `SagaContext` **once**, at `SagaOrchestrator.cs:390`, before
  calling `definition.HandleAsync`.
- `StepExecutor.RunAsync` (`dotnet/src/VSaga.Core/Dsl/StepExecutor.cs:18-34`) replays **every action from index
  0** on retry, reusing that same context.
- `SagaContext._deferredPublishes` (`SagaContext.cs:40`) is a plain list that is appended to and **never
  cleared** between replays.
- `PublishAfterCommitAsync` (`SagaContext.cs:103-108`) builds its envelope eagerly via
  `MessageEnvelope.From(...)`, minting a **fresh `MessageId`** every call.

So a step containing `.CallHttp(...).OnSuccess<TOut>()` followed by any action that can throw, under a
`.Retry(policy)`, queues the loopback N times with N *different* `MessageId`s.
`ISagaEventLogStore.IsDuplicateAsync` keys on `MessageId`, so it catches none of them, and the drain
publishes all N after commit.

This cannot happen today: `LoyaltyLookupSaga`'s `.CallHttp` is the last action in its step and carries no
`.Retry`. A mixed saga is precisely the shape that breaks that — a `.CallHttp` **and** a `.Publish` in the
same step, with `.Retry` a natural reach for the broker hop. It must be fixed before the sample exists.

---

## 4. `ctx.CallHttpAsync(...)` — the imperative form

One new primitive covers **both** compensation delegates and timeout steps, so no `.CallHttp` extension on
`TimeoutBuilder` is needed and no new `InternalsVisibleTo` is needed —
`VSaga.Core.csproj` already grants `VSaga.Http` access to `ISagaContextLogSink`, which is all this uses.

```csharp
public static Task CallHttpAsync<TState>(
    this ISagaContext<TState> context,
    Action<HttpCallBuilder<TState>> configure,
    CancellationToken cancellationToken = default)
    where TState : SagaState, new();
```

A new context-only `HttpCallBuilder<TState>` mirrors the existing message-aware
`HttpCallBuilder<TState,TMessage>`'s surface minus `TMessage`: `.Post(url)`, `.Body(object)`,
`.OnSuccess<TOut>()`, `.OnSuccess(Action<TState>)`, `.OnFailure<TOut>()`, `.OnFailure(Action<TState>)`,
`.OnStatus(int)` → `.As<TOut>()`/`.Then(Action<TState>)`, `.WithRetry(maxAttempts, delay)`.

**`.Body` takes its value eagerly at the call site, but the shared executor must still invoke it lazily,
once per retry attempt.** `SendWithRetryAsync` today re-invokes the message-aware builder's body factory
on every attempt (`HttpCallDefinition.cs:58-66`); nothing mutates saga state between attempts (the only
thing that happens between them is `Task.Delay`), so eager-vs-lazy is semantically a no-op either way —
but the shared executor should take `Func<object?>`, not a pre-computed `object?`, so `.CallHttp`'s retry
behaviour is preserved *literally*, not merely "unobservably so." `ctx.CallHttpAsync` passes
`() => eagerlyCapturedBody`, which is correct because this form executes inside an already-running
delegate: the caller's own lambda has already closed over `ctx` by the time `.Body(new {
ctx.Saga.AuthorizationId })` is written, so there is nothing to defer.

**Refactor `dotnet/src/VSaga.Http/HttpCallDefinition.cs`** so execution is shared, not duplicated: extract the
body (log outbound `MessagePublished` with `messageId: callId` → `SendWithRetryAsync` → log inbound
`MessageReceived` with `causationId: callId, sourceService: _host` → `ResolveAction(status).ApplyAsync`)
into an internal executor taking `Func<object?> body`. The existing declarative form supplies
`() => bodyFactory(ctx, msg)`; `ctx.CallHttpAsync` supplies its eager value the same way.
`HttpOutcomeAction.cs` is unchanged and shared by both.

**The existing `.CallHttp` public API and wire behaviour must stay bit-for-bit unchanged** — same headers,
same `PropertyNameCaseInsensitive` deserialization, same empty-body-`{}` handling, same `callId` stitch,
same per-attempt body-factory call count. `dotnet/tests/VSaga.Http.Tests`'s six existing tests are the regression
proof and must not be edited.

---

## 5. Engine change: draining the timeout path, and a discard path for the race it can't avoid

**`dotnet/src/VSaga.Core/Runtime/SagaContext.cs`** — widen the internal deferred-publish queue so both the drain
and a new discard path can name what they're handling, not just count it:

```csharp
internal readonly record struct DeferredPublish(string MessageType, Func<Task> SendAsync);

internal interface ISagaContextDeferredPublisher
{
    IReadOnlyList<DeferredPublish> DeferredPublishes { get; }
    void ClearDeferredPublishes();   // §3.2's fix — see StepExecutor below
}
```

**Status note: `ISagaContextDeferredPublisher` survived verbatim; the `DeferredPublish` shape above did
not, and was deliberately superseded.** `production-readiness.md` §4.3's transactional outbox needed the
same queue to carry a durable copy of each message, so `DeferredPublish` became a **hybrid** — an outbox
row (message type name, the full `MessageEnvelope`, the UTF-8 body, the `SendAsync` destination) *plus*
the thin dispatch closure this section specified. The current shape is at
`dotnet/src/VSaga.Core/Runtime/SagaContext.cs` (constructed in `QueueAsync`). §4.3 argues why the closure
had to stay rather than the row replacing it — a pure-bytes `DeferredPublish` would force the inline
dispatch onto `PublishRawAsync`, which type-erases the message and silently breaks
`TimeoutDrainTests.cs`'s assertion on `p.Message`. Read that section before assuming the two-field record
above is still the thing to change. Everything this section says about *when* the queue is drained,
discarded and cleared is unaffected — only the struct's payload grew.

**`dotnet/src/VSaga.Core/Dsl/StepExecutor.cs`** — clear in the catch, before the backoff delay, so a step being
replayed from index 0 also discards the side effects it queued but never committed:

```csharp
catch when (attempt < step.RetryPolicy.MaxAttempts)
{
    if (context is ISagaContextDeferredPublisher deferred)
        deferred.ClearDeferredPublishes();
    VSagaDiagnostics.StepRetries.Add(...);
    await Task.Delay(step.RetryPolicy.DelayForAttempt(attempt), cancellationToken);
}
```

Pattern-match (`is`), not a hard cast: `ISagaContext<TState>` is public and an external implementation
will not implement the internal interface.

**`dotnet/src/VSaga.Core/Dsl/TimeoutBuilder.cs`** — an additive async overload mirroring `EventBuilder`'s
existing `Then(Action<...>)` / `Then(Func<..., Task>)` pair (`EventBuilder.cs:43-58`):

```csharp
public TimeoutBuilder<TState> Then(Func<ISagaContext<TState>, Task> action)
{
    Step.Actions.Add((ctx, _) => action(ctx));
    return this;
}
```

`TimeoutBuilder` today has only a synchronous `Then`. The sample (§7) reaches its REST call through
`.Compensate()` rather than this overload, so it needs its own test; it is still needed for the general
case of a timeout step calling REST directly. (`ChoreographyEventBuilder` already has the async `Then` it
would need — `ChoreographyEventBuilder.cs:69-73` — so this gap exists only on `TimeoutBuilder`.)

**`dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs`** — drain on the timeout path. `HandleTimeoutAsync` already
builds a concrete `SagaContext<TState>` at `:211`. Place the drain **after** the second,
side-effects-ran persist at `:235` — that persist is the actual commit, matching
`HandleStepSuccessAsync`'s persist-then-drain ordering at `:471-473` — and **before**
`RecordTimeoutOutcomeAsync` at `:238`. That ordering matters: `RecordTimeoutOutcomeAsync` does I/O
(`notifier.SagaUpdatedAsync`, and for a terminal timeout `PublishChildSagaFinishedAsync`) that can itself
throw, and if it ran first and threw, the drain would never run and the deferred publish would vanish
silently — reintroducing the exact bug being fixed.

```csharp
if (!await TryPersistOrLogRaceLossAsync(state, timeout, sideEffectsAlreadyRan: true, cancellationToken))
{
    await DiscardDeferredPublishesAsync(timeout.CorrelationId, context, timeout.ForState, cancellationToken);
    return;
}

await DrainDeferredPublishesAsync(timeout.CorrelationId, context, cancellationToken);
await RecordTimeoutOutcomeAsync(state, outcome, cancellationToken);
```

The race-loss branch matters and must not be a silent `return`. The persist lost, so the transition was
never written; publishing a loopback that announces it would be wrong. But silently discarding it is the
failure class this repo has been bitten by three times (`docs/http-based-sagas.md` §3, §6).
`DiscardDeferredPublishesAsync` logs one `DeliveryExhausted` entry per dropped publish, naming the
`MessageType` from the new `DeferredPublish` struct and the state whose race was lost, reusing
`DrainDeferredPublishesAsync`'s existing "leave the saga for its own timeout to rescue it" policy
(`:491-518`) rather than inventing a second one.

---

## 6. The Saga Map: compensating replies should render as compensation edges

**`dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs`** — thread `_compensating` into `ProcessInboundEntry`'s edge
(`:136`) so a compensating call's reply leg renders as a compensation edge like its request leg already
does:

```csharp
IsCompensation: _compensating, Failed: failed, Unanswered: false, entry.OccurredAtUtc
```

This should land as its **own commit**, separate from §4's DSL addition — unlike Phase 2's `ee8e108`,
which merged its DSL addition with its own Map fix. This change affects edge rendering for every saga in
the repo, not only HTTP ones, and deserves its own green-suite-plus-mutation gate rather than riding
along.

No Angular change is needed: `typescript/dashboard-web/src/app/components/saga-map/saga-map.ts:44` already carries
`isCompensation` through, `saga-map.html:16` already binds `.edge--compensation`, and `saga-map.scss:41`
already styles it as a dotted edge with a legend entry — the API-side fix alone lights it up.

Existing tests are unaffected — `dotnet/tests/VSaga.Dashboard.Api.Tests/SagaEndpointsTests.cs`'s only existing
`IsCompensation` assertion is on an *outbound* edge, and its inbound fixture entry is logged before
`CompensationStarted`, so `_compensating` is still `false` when that edge is built. That is structural,
not lucky: `MessageReceived` is always logged before the step that might start compensating runs.

---

## 7. The sample: `MixedFulfilmentSaga`

New contracts in `dotnet/samples/VSaga.Samples.OrderProcessing.Contracts/Messages.cs`. Deliberately **new broker
message types**, not `ReserveInventory`/`InventoryReserved`: RabbitMQ's topic exchange fans a published
message out to *every* subscriber of that type, so reusing them would deliver copies to `OrderSaga` under
a correlation id it has no instance for, logging `UnexpectedEvent` noise on every run.

```csharp
public sealed record FulfilmentRequested(string OrderId, decimal Amount);
public sealed record ReserveStock(Guid CorrelationId, string OrderId);
public sealed record StockReserved(Guid CorrelationId, string OrderId);
public sealed record StockUnavailable(Guid CorrelationId, string OrderId, string Reason);
public sealed record ReleaseStock(Guid CorrelationId, string OrderId);
```

A new `dotnet/samples/.../Participants/StockParticipant.cs`, a sibling of `InventoryParticipant.cs` rather than
new handlers bolted onto it. `ParticipantService` stamps its own `consumerName` as `sourceService` on
every reply and derives its subscription from one `Handlers` dictionary and one `QueueName`; folding stock
into `InventoryService` would make the mixed saga's own Map claim `InventoryService` did the work, which
defeats the point of a live-verification step whose whole purpose is "look at *this* saga's map in
isolation." A separate `"StockService"` / `vsaga.participant.stock` keeps queue, topology entry, and map
node disjoint. Give it a small `StockUnavailable` branch and a small never-reply branch (mirroring
`PaymentParticipant.cs`'s own failure-simulation shape) so both compensation paths fire live.

`OrderSubmitter.cs` publishes `FulfilmentRequested` under its own fresh correlation id
(`Guid.NewGuid()`, never the order's), so the mixed saga never shares a correlation id with
`OrderSaga`/`PostShipmentChoreography` and gets a clean Saga Map of its own.

**The flow is deliberately sequential, not a fan-out**: a declined authorization must not be able to
strand a stock reservation that nothing will release. Money is authorized first, stock is reserved only
once authorization succeeded, and the saga does not declare itself `Failed` until the reversal is actually
confirmed — applying the rule from §8 below about compensating loopbacks and terminal states.

This saga never calls `CorrelateOn`, so its `CorrelateBy` below keeps the behaviour it always had:
`OrderId` is copied onto state for dashboard search/traceability only, with no effect on how later
messages are matched to this instance — the `.Then(...)` right after it sets `OrderId` again (alongside
`Amount`) because nothing here should be assumed to depend on `CorrelateBy` for that. Since
production-readiness.md §5.1–§5.3 (§8 items 13/14), a saga that *does* call `CorrelateOn` naming the same
property gets a different `CorrelateBy`: it also registers as this message type's business-key
extractor, and the orchestrator falls back to a business-key lookup when the transport correlation id
misses. (The dual-role `CorrelateBy` is §5.1's API decision and the fallback lookup is §5.3's
orchestrator change; §5.2 is the `SagaState.BusinessKey` storage shape in between. Earlier drafts of this
line and of the matching line in `sub-saga-composition.md` cited two different, incomplete pairs.)

```csharp
During(Start)
    .When<FulfilmentRequested>()
        .CorrelateBy(m => m.OrderId, s => s.OrderId)
        .Then((ctx, m) => { ctx.Saga.OrderId = m.OrderId; ctx.Saga.Amount = m.Amount; })
        .CallHttp(h => h                                    // REST participant
            .Post(authorizeUrl)
            .Body((ctx, m) => new { m.OrderId, m.Amount })
            .OnSuccess<PaymentAuthorized>()                 // loopback
            .OnStatus(402).Then(s => s.Declined = true)     // inline
            .OnFailure(s => s.Declined = true))
        .TransitionTo(s => s.Declined ? Failed : AwaitingAuthorization)
        .Finalize(s => s.Declined ? SagaStatus.Failed : (SagaStatus?)null);
        // A decline needs no unwind: nothing has been reserved and there is no authorization to void.

During(AwaitingAuthorization)
    .When<PaymentAuthorized>()                              // loopback reply
        .Then((ctx, m) => ctx.Saga.AuthorizationId = m.AuthorizationId)
        .Publish((ctx, _) => new ReserveStock(ctx.CorrelationId, ctx.Saga.OrderId!))   // broker hop
        .TransitionTo(AwaitingStock);

During(AwaitingStock)
    .When<StockReserved>()
        .Then((ctx, _) => ctx.Saga.StockReserved = true)
        .TransitionTo(Fulfilled).Finalize(SagaStatus.Completed)
    .When<StockUnavailable>()
        .Compensate()
        .TransitionTo(Voiding);        // deliberately NOT terminal yet — see §8

During(Voiding)
    .When<PaymentVoided>()                                  // loopback from the compensating REST call
        .Then((ctx, m) => ctx.Saga.VoidedAtUtc = m.VoidedAt)
        .TransitionTo(Failed).Finalize(SagaStatus.Failed);
```

The compensation is the point of the feature — it unwinds both kinds of hop:

```csharp
Compensate(AwaitingStock, async (ctx, ct) =>
{
    // Sequential awaits, never Task.WhenAll: ctx.PublishAsync and ctx.CallHttpAsync share this saga's
    // one SagaContext and the single DbContext behind its event log, which is only safe one operation
    // at a time. A Task.WhenAll version of OrderSaga's own compensation failed 13 of 20 attempts under
    // live chaos load with "A second operation was started on this context instance".
    await ctx.PublishAsync(new ReleaseStock(ctx.CorrelationId, ctx.Saga.OrderId!), ct);   // broker

    await ctx.CallHttpAsync(h => h                                                        // REST
        .Post(voidUrl)
        .Body(new { ctx.Saga.AuthorizationId })
        .OnSuccess<PaymentVoided>()            // loopback — the saga waits for confirmation
        .OnFailure(s => s.VoidFailed = true), ct);
});

WithTimeout(AwaitingStock, ReplyTimeout, t => t.Compensate().TransitionTo(Voiding));

// Backstop: if the void itself never confirms (OnFailure fired, so no loopback), don't hang forever.
WithTimeout(Voiding, ReplyTimeout, t => t.TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
```

`PaymentAuthorized` and `PaymentVoided` are pure loopback replies and must be registered under
`AwaitingAuthorization`/`Voiding`, never `Start` — a message type registered under the saga's
`InitialState` also counts as capable of *initiating* a fresh instance, because `CanInitiate` reads
`StepsByState[InitialStateName].Keys` (`OrchestratedSagaDefinition.cs:40-43`). `LoyaltyLookupSaga.cs:
62-66` comments the same hazard. Both types live in the saga's own file, not in the cross-service
`Contracts` project, matching `LoyaltyTierResolved`'s precedent.

Two new Minimal API endpoints in `Program.cs`, in the same style as the existing `/loyalty/lookup`
(`Program.cs:140-154`):

- `POST /payments/authorize` → `200 { authorizationId }`, a simulated 402 decline rate, a simulated 503
  rate
- `POST /payments/void` → `200 { voidedAt }` (the body the `PaymentVoided` loopback deserializes), with an
  occasional 500 so the `.OnFailure` arm and the `Voiding` backstop timeout both fire for real

Both must return **camelCase** JSON — `.CallHttp` deserializes with `PropertyNameCaseInsensitive = true`
precisely because a real REST API does, and that setting was itself a bug caught on the first test run of
Phase 2 (`e6b7704`). URLs come from config with a same-process fallback, exactly as
`LoyaltyLookupSaga.cs:51-56` does.

**No new docker-compose overlay**, but **`docker-compose.http.yml` needs editing**. The mixed saga runs on
the default RabbitMQ track and calls REST over HTTP, so plain `docker compose up -d --build` exercises it
— exactly as Phase 2's `.CallHttp` verification did against the existing stack with no new infrastructure.
`docker-compose.chaos.yml` (existing) drives the timeout/compensation arm. But `docker-compose.http.yml`
(the Phase-1 transport track) enumerates saga→participant routes explicitly (`:54-60`) and has no entry
for the new message types — add:

```yaml
      HttpSagas__Routes__ReserveStock__0: "participants"
      HttpSagas__Routes__ReleaseStock__0: "participants"
```

or that track's HTTP transport throws unroutable on the mixed saga's first step. No return-direction
entries are needed: `StockReserved`/`StockUnavailable` ride back as the synchronous response body, exactly
like `InventoryReserved` already does with no entry of its own.

---

## 8. Further constraints, found by an adversarial second pass over this design

Per this repo's habit — an adversarial second pass over a transport/envelope design has paid for itself
before (see the `NotifyParentAsync`/`ChildSagaFinished` scars this repo has recorded twice already) — a
second pass over this exact design found four more things worth stating here rather than discovering live:

- **A compensating loopback can resurrect a terminated saga.** `RunStepAsync`
  (`SagaOrchestrator.cs:382-383`) unconditionally flips `Status` from `Failed` back to `Running` on any
  redelivery. A compensating `.CallHttpAsync(...).OnSuccess<TOut>()` that transitions straight into a
  `.Finalize(...)` step queues a loopback that, when drained, re-enters an already-terminal saga and
  resurrects it. This is invisible today only because `HandleAsync` returns `Unhandled` before anything is
  persisted, for every saga in the repo that compensates today. **Rule:** a compensating call may use a
  loopback outcome only if the state it transitions into handles that reply and is *not* itself terminal.
  §7's `Voiding` state exists because of this rule — it waits for `PaymentVoided` before finalizing, rather
  than finalizing at the point of compensation.
- **`docker-compose.http.yml` needs two new route entries**, not a new overlay — covered in §7 above,
  repeated here because it is exactly the kind of thing an implementer skims past.
- **`SagaMapBuilder`'s `_compensating` flag is sticky and never resets.** Fine for every saga today (none
  continues after compensating); worth naming explicitly once a saga's compensation is followed by more
  message traffic, so a later reader doesn't mistake it for a bug. A `CompensationFinished` entry type
  that resets it is the principled fix — see §9.
- **`ctx.CallHttpAsync`'s URL is validated at call time, not at saga-construction time**, unlike
  `.CallHttp`'s eager `new Uri(url)` in its constructor path. A malformed compensation URL fails at
  runtime, possibly on a path nobody exercises in tests. Documented here rather than guarded against.

---

## 9. Tests, mutation tests, and live verification

**Tests** (`dotnet/tests/VSaga.Core.Tests`, `dotnet/tests/VSaga.Http.Tests`, `dotnet/tests/VSaga.Dashboard.Api.Tests`; xUnit v2,
raw `Assert.*`, hand-written fakes — no Moq/FluentAssertions). Reuse the existing harnesses:
`SagaTestHarness<TDefinition,TState>`, `CallHttpTestHarness`/`StubHttpMessageHandler`, and
`SagaMapBuilder.Build(...)` called directly the way `CallHttpSagaMapTests` already does.

Core (§5): a fixture timeout step that queues a loopback and transitions onward (fails today — the saga
sits in the intermediate state forever); the same fixture asserting drain-after-persist ordering and no
`UnexpectedEvent`; a timeout that loses its final persist race, asserting the queued message was never
published and exactly one `DeliveryExhausted` entry names it; a no-op regression pin for existing
timeout shapes; the new async `TimeoutBuilder.Then` overload exercised directly; a `.CallHttp` step
followed by a throwing action under `.Retry(2)`, asserting the loopback publishes exactly once.

`VSaga.Http` (§4): the 2xx/explicit-status/5xx/network-failure/timeout mapping table mirrored for
`ctx.CallHttpAsync`; a compensation delegate performing a broker publish and a `ctx.CallHttpAsync` in
order, asserting both happened; a pin that a retried `ctx.CallHttpAsync` call invokes its body exactly
once per attempt.

`VSaga.Dashboard.Api` (§6): a compensating reply logged after `CompensationStarted` renders
`IsCompensation: true` on both legs; a reply logged before `CompensationStarted` still renders `false`.

The six existing `dotnet/tests/VSaga.Http.Tests` tests must stay unedited and green throughout — the refactor's
regression proof.

**Mutation tests** (this repo has no Stryker; the discipline is manual — break one line, confirm *exactly*
the intended tests fail and nothing else, revert, and write the result into the commit message and
README):

- Remove the `HandleTimeoutAsync` drain entirely → only the timeout-drain test fails.
- **Move the drain to before the final persist instead of after** (not just deleting it) → the same test
  must still fail. This is the mutation that actually matters: a drain in the *wrong place* would pass a
  naive "is it called at all" test.
- Remove `ClearDeferredPublishes` from `StepExecutor`'s catch → only the retry-clear test fails.
- Revert `ProcessInboundEntry` to `IsCompensation: false` → only the new map test fails; the existing
  business-failure map test must stay green (the no-over-marking proof).
- In the shared HTTP executor, hoist the body-factory call out of the retry loop → only the retry-count
  pin fails.

**Live verification — not optional in this repo.** It has shipped envelope-header threading the
orchestrator never actually read three separate times (`docs/http-based-sagas.md` §3, §6), each caught
only by a live run and never by tests that hand-built the objects under test.

```
dotnet build dotnet/VSaga.slnx
dotnet test dotnet/VSaga.slnx
docker compose up -d --build
docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build   # timeout/compensation arm
```

Against the running stack (Postgres on host **5433** — 5432 is an unrelated stack on this machine;
dashboard API on **5080**; header `X-Api-Key: dev-local-only-change-me`):

1. `GET /api/sagas?sagaType=MixedFulfilmentSaga` — instances reach `Completed`, some reach `Failed` via
   the 402 decline arm, and under chaos some reach `Failed` via `StockUnavailable`/timeout → `Voiding`.
   **No instance should be left sitting in `Voiding`** — one that is means the timeout drain did not
   land.
2. `GET /api/sagas/MixedFulfilmentSaga/{correlationId}/map` on a **compensated** instance — the real test.
   It must show the REST host as a `Participant` node with a stitched, non-`unanswered` request/reply pair
   for **both** `/payments/authorize` and `/payments/void`, `StockService` as its own node, and the void +
   `ReleaseStock` edges marked `isCompensation` (dotted in the SPA). A raw-`HttpClient` compensation would
   produce **no void edge at all** — that absence is what this check exists to rule out.
3. Timeline on a compensated instance: `CompensationStarted` → `MessagePublished ReleaseStock` →
   `MessagePublished POST …/void` / `MessageReceived PaymentVoided` → `CompensationStepSucceeded`, then
   `MessageReceived PaymentVoided` again as the drained loopback re-enters and drives `Voiding → Failed`.
   On a timed-out instance, `TimeoutFired` precedes all of it — that instance is the direct live proof of
   §5's drain.
4. Zero unexplained `DeliveryExhausted` entries, and zero new `UnexpectedEvent` entries on `OrderSaga`
   (proves the new message types did not cross-talk over the topic exchange).
5. **Filter every count by `createdAtUtc` after the containers' start time** — the Postgres volume is
   reused across `docker compose up`, and stale sagas otherwise pollute the numbers.
6. After §7's route fix, one confirmation run on the HTTP transport track (`docker-compose.http.yml`)
   that it did not regress — `OrderSaga` completions unchanged is the bar.

---

## 10. Explicitly deferred, and named as such rather than built

- **Idempotency keys on `.CallHttp`/`ctx.CallHttpAsync` requests.** The internal `callId` never leaves the
  process; the far side gets no way to dedupe a retried POST. Combined with the documented `.Retry()`
  trap, that is a genuine double-charge hazard — but a stable key across retries needs a step-identity
  concept the DSL does not have.
- **`.CallHttp`/`.CallHttpAsync` verbs other than POST.**
- **A `CompensationFinished` entry type that resets the Map's `_compensating` flag.** `SagaEntryType` is
  persisted as a plain integer — a schema-shaped change deserving its own pass, not a rider on this one.
- **A DSL guard against a terminal-state loopback.** The engine cannot know at registration time whether
  the target state handles a given reply type; §8 documents the rule instead of enforcing it.
- **A composite `IMessageTransport`** (per-message-type transport routing). The interpretation not chosen
  for this design — recorded here so a later session knows it was considered.
- **Async webhook delivery.** Still reserved as `docs/http-based-sagas.md` §7 item 3's "natural third
  phase" — a different axis from mixed sagas, not superseded by this doc.
