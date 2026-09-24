# Saga DSL reference

The full method inventory for the fluent saga DSL: `OrchestratedSagaDefinition<TState>`,
`ChoreographedSagaDefinition<TState>`, `StateBuilder<TState>`, `EventBuilder<TState, TMessage>`,
`ChoreographyEventBuilder<TState, TMessage>`, `TimeoutBuilder<TState>`, `RetryPolicy`,
`ISagaContext<TState>`, and the `.CallHttp` extension from `VSaga.Http`. This document did not exist
before the production-readiness docs restructure (§8.19) — read it alongside
[`concepts.md`](concepts.md) for *why* each piece exists, not just its signature.

All types below live in `VSaga.Core.Dsl` unless noted. `TState : SagaState, new()` on every generic
except `ISagaContext<out TState>`, which is covariant and constrains only `TState : SagaState` — no
`new()`. Message type parameters (`TMessage`, `TOut`, `TBody`) carry a `: notnull` constraint
throughout; it is stated once here rather than repeated on every row below.

## `OrchestratedSagaDefinition<TState>`

Base class for a state-gated saga. Derive from it, declare states and transitions in the constructor,
and register with `services.AddVSagaEngine(o => o.AddSaga<TDefinition, TState>())`.

| Member | Signature | Notes |
| --- | --- | --- |
| Constructor | `protected OrchestratedSagaDefinition(string? sagaType = null)` | `SagaType` defaults to the derived class's `Type.Name` if not given. |
| `SagaType` | `string` (get) | Identity half of `(SagaType, CorrelationId)`. |
| `Kind` | `SagaKind` (get) | Always `SagaKind.Orchestrated`. |
| `InitialState` | `protected State<TState> InitialState(string name)` | Declares the state a brand-new instance starts in. Call exactly once; a second call throws `SagaDefinitionException`. |
| `State` | `protected static State<TState> State(string name)` | Declares any other named state, for use in `During(...)`/`Compensate(...)`/`WithTimeout(...)`/`TransitionTo(...)`. |
| `During` | `protected StateBuilder<TState> During(params State<TState>[] states)` | Entry point for declaring one or more steps gated on being in any of the given states — pass several states to register the same handler across all of them. |
| `Compensate` | `protected void Compensate(State<TState> forState, Func<ISagaContext<TState>, CancellationToken, Task> action)` | Registers the undo action for a state, invoked by a step's `.Compensate()` call — see [`concepts.md`](concepts.md#compensation). |
| `WithTimeout` | `protected void WithTimeout(State<TState> state, TimeSpan after, Action<TimeoutBuilder<TState>> configure)` | Schedules a timeout for a state; see [`TimeoutBuilder<TState>`](#timeoutbuildertstate) below. |
| `OnUnhandledEvent` | `protected void OnUnhandledEvent(UnhandledEventPolicy policy)` | `LogAndIgnore` (default) records an `UnexpectedEvent` timeline entry and drops the message; `Throw` routes it through the same path a genuine step failure takes — the saga is marked `Failed` and the message is **acked**, not redelivered (see the correction below). |
| `CorrelateOn` | `protected void CorrelateOn(Expression<Func<TState, object?>> selector)` | Declares this saga type's business key — see [`concepts.md`](concepts.md#correlation-transport-id-then-business-key). Must name a simple property. |
| `TryGetCorrelationKey` | `public string? TryGetCorrelationKey(object message)` | `ISagaDefinition` member; extracts the business-key value from a message via whatever `CorrelateBy` calls have registered an extractor for its type. |
| `InitialStateName` | `string` (get) | `ISagaDefinition` member; throws if `InitialState(...)` was never called. |
| `MessageTypes` / `InitiatingMessageTypes` | `IReadOnlyCollection<Type>` (get) | `ISagaDefinition` members, computed once from the registered step table and cached. |
| `CanInitiate` | `bool CanInitiate(Type messageType)` | Whether this saga type can be created by a message of the given type. |
| `GetTimeout` | `public TimeSpan? GetTimeout(string forState)` | `ISagaDefinition` member; the delay registered by `WithTimeout(...)` for a state, or `null`. The orchestrator calls it to decide whether entering a state schedules a timeout row. |
| `HandleAsync` | `public Task<SagaStepOutcome> HandleAsync(ISagaContext<TState> context, object message, CancellationToken cancellationToken)` | `ISagaDefinition` member — the engine's dispatch entry point, public but not meant to be called from saga code. Runs the step registered for `(CurrentState, message type)`, mutates `context.Saga` in place, and returns the outcome; an unhandled message returns `SagaStepOutcome.Unhandled` (or throws, under `UnhandledEventPolicy.Throw`). Throws if the step's actions fail after `Retry(...)` is exhausted — marking the saga `Failed` is the orchestrator's job, not the definition's. |
| `HandleTimeoutAsync` | `public Task<SagaStepOutcome> HandleTimeoutAsync(ISagaContext<TState> context, string forState, CancellationToken cancellationToken)` | `ISagaDefinition` member; the same, for the timeout step registered for `forState`. Returns `SagaStepOutcome.Unhandled` if that state has no `WithTimeout(...)`. |

**`UnhandledEventPolicy.Throw`, corrected.** An earlier design note (and a stale doc comment still on
the enum itself) claimed `Throw` causes the orchestrator to "nack and redeliver forever." Reading
`SagaOrchestrator.RunStepAsync` shows that isn't what happens: the exception `Throw` raises is caught
by `RunStepAsync`'s own catch block and routed to `HandleStepFailureAsync` — the same path an ordinary
step failure takes, which marks the saga `Failed` and **acks** the message. There is no redelivery
loop. The real hazard `Throw` poses to a saga that hasn't opted in for a message it might receive
unexpectedly is a silent, one-shot false `Failed`, not an infinite spin.

## `ChoreographedSagaDefinition<TState>`

Base class for an event-gated (not state-gated) saga. Same registration call as orchestration;
`SagaType`, `Kind` (`SagaKind.Choreographed`), `InitialState`, `State`, `Compensate`, `WithTimeout`,
`OnUnhandledEvent`, `CorrelateOn`, `TryGetCorrelationKey`, `InitialStateName`, `MessageTypes`,
`GetTimeout`, `HandleAsync` and `HandleTimeoutAsync` behave identically to the orchestrated class
above. Two structural differences:

| Member | Signature | Notes |
| --- | --- | --- |
| `On<TMessage>` | `protected ChoreographyEventBuilder<TState, TMessage> On<TMessage>()` | Registers a reaction to one event type, dispatched **regardless of the instance's current state** — there is no `During(...)` gate. Any number of message types may be registered this way, each independently. |
| `InitiatingMessageTypes` | `IReadOnlyCollection<Type>` (get) | The set of event types that called `.StartsNewInstance()`, not (as in orchestration) the message types registered under `InitialStateName` — there is no initial *step* here to derive them from. `CanInitiate` answers from that same set. |

> **Caching differs with it.** `MessageTypes` is computed once and cached, as on the orchestrated class,
> but `InitiatingMessageTypes` is neither: it returns the model's live `HashSet<Type>` directly, so it
> reflects any `.StartsNewInstance()` registered later in the constructor — and hands out a mutable
> reference. Treat it as read-only; the engine does.

`InitialState(name)` still needs exactly one call per saga: the engine seeds every new instance's
`CurrentState` from it before dispatching the initiating event — which event actually *created* the
instance is whichever `.StartsNewInstance()` type was observed, not this label.

## `StateBuilder<TState>`

Returned by `During(...)`. One member:

| Member | Signature | Notes |
| --- | --- | --- |
| `When<TMessage>` | `public EventBuilder<TState, TMessage> When<TMessage>()` | Registers a step for one message type across every state passed to `During(...)`. Returns an `EventBuilder`, which itself inherits `When<T>()` so a chain can move straight on to the next event without repeating `During(...)`. |

## `EventBuilder<TState, TMessage>`

Fluent configuration for one `(state, message type)` step (orchestrated DSL). Every method returns
`this` (or the builder itself) for chaining, except where noted.

| Member | Signature | Notes |
| --- | --- | --- |
| `CorrelateBy` | `CorrelateBy<TKey>(Func<TMessage, TKey> messageKey, Expression<Func<TState, TKey>> stateKey)` | Extracts a value from the message and assigns it onto saga state. Additionally becomes a business-key extractor if `stateKey`'s property matches a `CorrelateOn` declaration — and **throws from the definition's constructor if it names any other property** once `CorrelateOn` has been declared; see the note below and [`concepts.md`](concepts.md#correlation-transport-id-then-business-key). |
| `Then` | `Then(Action<ISagaContext<TState>, TMessage> action)` / `Then(Func<ISagaContext<TState>, TMessage, Task> action)` | The step's own business logic. Sync and async overloads. |
| `Publish<TOut>` | `Publish<TOut>(Func<ISagaContext<TState>, TMessage, TOut> factory)` | Publishes a message built from the context and inbound message, via `ctx.PublishAsync` — fires immediately, mid-step, under the default outbox mode; see the immediacy caveat under [`ISagaContext<TState>`](#isagacontexttstate). |
| `Send<TOut>` | `Send<TOut>(string destination, Func<ISagaContext<TState>, TMessage, TOut> factory)` | Like `Publish`, but sent directly to a named destination via `ctx.SendAsync`, bypassing topic routing. |
| `Retry` | `Retry(RetryPolicy policy)` | Bounded, in-process retry of this step's actions as a whole on failure — see [`RetryPolicy`](#retrypolicy) below. |
| `TransitionTo` | `TransitionTo(State<TState> state)` / `TransitionTo(Func<TState, State<TState>> selector)` | Fixed or computed next state. The computed overload is evaluated *after* the step's own actions have run, so it can see what they just wrote — this is the join half of a parallel fan-out (see [`concepts.md`](concepts.md#fan-out-and-join)): register the same selector on every branch, and return the gathering state to keep waiting or the next state to release it. Returning the gathering state is a self-transition — the orchestrator neither cancels nor reschedules that state's timeout for it. |
| `Finalize` | `Finalize(SagaStatus status)` / `Finalize(Func<TState, SagaStatus?> selector)` | Marks the saga terminal. The computed overload (also evaluated post-actions) returns `null` for "handled, but not terminal yet" — needed for a fan-out join whose completion *is* the saga's ending, where no single branch knows it is last. |
| `Compensate` | `Compensate()` | Runs every registered `Compensate(state, ...)` delegate for this instance's visited states, most-recent first — see [`concepts.md`](concepts.md#compensation). |

**`CorrelateBy` against `CorrelateOn`: three startup failures.** Once a saga has called
`CorrelateOn(...)`, `SagaCorrelationModel` enforces one business key and one extractor per message
type, and each violation throws `SagaDefinitionException` **from the saga definition's constructor** —
so it fails at registration, never at runtime:

- **A `CorrelateBy` whose `stateKey` names any property other than the `CorrelateOn` one.** This is the
  trap, because it reads like a harmless no-op: a saga that declares `CorrelateOn(s => s.OrderId)` and
  then writes an otherwise perfectly reasonable `CorrelateBy(m => m.ShipmentId, s => s.ShipmentId)` on
  some later step does not silently skip extractor registration — it does not construct at all. Arming
  a business key means every `CorrelateBy` in that saga must target that one property; use a plain
  `.Then((ctx, msg) => ctx.Saga.ShipmentId = msg.ShipmentId)` for the other assignments.
- A second `CorrelateBy` for the same message type.
- A second `CorrelateOn`.

A saga that never calls `CorrelateOn` is unaffected: every `CorrelateBy` keeps its assign-onto-state
behaviour and none of these can fire.

## `ChoreographyEventBuilder<TState, TMessage>`

Fluent configuration for one event a `ChoreographedSagaDefinition` reacts to (returned by `On<T>()`).
Deliberately does not chain into a further `.On<T>()` the way `EventBuilder` chains after
`During(...)` — a choreography has no shared "state" context to carry between independently-reacting
events.

| Member | Signature | Notes |
| --- | --- | --- |
| `StartsNewInstance` | `StartsNewInstance()` | Marks this event type as one that can create a brand-new tracked instance when observed with no existing saga for its correlation id. More than one event type may call this — unlike orchestration's single implicit initial step, choreography has no designated first event. |
| `CorrelateBy` | Same signature as `EventBuilder`'s. | Same business-key-extractor behaviour, and the same three constructor-time throws — the correlation model is shared, not duplicated. |
| `Then` | Same two overloads as `EventBuilder`'s. | |
| `Publish<TOut>` / `Send<TOut>` | Same signatures as `EventBuilder`'s. | |
| `Retry` | `Retry(RetryPolicy policy)` | Same semantics. |
| `RecordState` | `RecordState(State<TState> state)` | The choreography analogue of `TransitionTo` — but purely a milestone label for the dashboard/timeline and for keying `Compensate`/`WithTimeout`. Nothing about this DSL's own dispatch depends on it, since dispatch is never state-gated here. |
| `Finalize` | `Finalize(SagaStatus status)` / `Finalize(Func<TState, SagaStatus?> selector)` | Same shape as `EventBuilder`'s. The computed overload is what a fan-out/join choreography needs: register the same selector (e.g. `state => state.A && state.B && state.C ? SagaStatus.Completed : null`) on every branch so whichever arrives last finishes the saga. |
| `Compensate` | `Compensate()` | Same semantics as `EventBuilder`'s. |

## `TimeoutBuilder<TState>`

Configures what happens when a state's timeout fires — a subset of `EventBuilder`'s surface, shared
by both DSLs (constructed internally by `WithTimeout(...)`, never directly).

| Member | Signature | Notes |
| --- | --- | --- |
| `Then` | `Then(Action<ISagaContext<TState>> action)` / `Then(Func<ISagaContext<TState>, Task> action)` | No inbound message parameter — a timeout has none. The async overload exists specifically so a timeout step can `await ctx.CallHttpAsync(...)` directly. |
| `Publish<TOut>` | `Publish<TOut>(Func<ISagaContext<TState>, TOut> factory)` | Same as `EventBuilder.Publish`, minus the message parameter. |
| `TransitionTo` | `TransitionTo(State<TState> state)` | Fixed only — no computed overload on timeouts. |
| `Finalize` | `Finalize(SagaStatus status)` | Fixed only. |
| `Compensate` | `Compensate()` | Same semantics as `EventBuilder.Compensate()`. |

## `RetryPolicy`

Bounded, in-process retry for one step's actions as a whole — for transient technical failures (a
broker connection blip), not saga-level business failures. **Re-running a step re-runs *all* of its
actions from the start**, so actions must tolerate being invoked more than once for the same message.

| Member | Signature | Notes |
| --- | --- | --- |
| `None` | `static readonly RetryPolicy None` | Single attempt, no retry. |
| `Exponential` | `static RetryPolicy Exponential(int maxAttempts, TimeSpan baseDelay)` | `maxAttempts` must be ≥ 1. Delay for attempt *n* is `baseDelay * 2^(n-1)`. |

A step's own deferred-publish queue (see `PublishAfterCommitAsync` below) is cleared on a retry
catch, before the backoff delay — otherwise a retried step containing both a queued loopback publish
and a `.Publish(...)` would queue one loopback copy per attempt.

## `ISagaContext<TState>`

Runtime context (`VSaga.Abstractions.Sagas`) handed to every step action, compensation delegate, and
timeout step. `SagaContext` is the engine's only production implementer.

| Member | Signature | Notes |
| --- | --- | --- |
| `Saga` | `TState` (get) | The mutable saga state. |
| `CorrelationId` | `Guid` (get) | This instance's own correlation id. |
| `VisitedStates` | `IReadOnlyList<string>` (get) | States this instance has successfully transitioned into so far, oldest first — the source `.Compensate()` walks backward through. |
| `Headers` | `IReadOnlyDictionary<string, string>` (get) | Inbound message headers. |
| `Services` | `IServiceProvider` (get) | DI scope for the current unit of work. |
| `CancellationToken` | `CancellationToken` (get) | |
| `PublishAsync<TMessage>` | `Task PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)` | Publishes under this instance's own correlation id — immediately, mid-step, under the default outbox mode (see the caveat below). |
| `SendAsync<TMessage>` | `Task SendAsync<TMessage>(string destination, TMessage message, CancellationToken ct = default)` | Sends directly to a named destination, bypassing topic routing — same immediacy caveat. |
| `StartChildAsync<TMessage>` | `Task StartChildAsync<TMessage>(TMessage message, CancellationToken ct = default)` | Publishes `message` under a **fresh** correlation id, stamped with this instance's identity (`x-vsaga-parent-saga-type`/`x-vsaga-parent-correlation-id`). Whichever saga's `CanInitiate` matches becomes the child. Does not wait — the parent moves on as soon as the publish returns. If no saga initiates on the message type, no child is created and nobody is told; if two do, two children start silently. |
| `NotifyParentAsync<TMessage>` | `Task NotifyParentAsync<TMessage>(TMessage message, CancellationToken ct = default)` | Publishes `message` under `Saga.ParentCorrelationId` — the field the engine stamped when this instance was created by `StartChildAsync`. Throws `InvalidOperationException` immediately (before any I/O) if this saga has no parent. Not a general publish-under-any-id overload: the only id reachable is the one the engine already assigned. Fans out to **every** saga type subscribed to `TMessage` that tracks an instance under the parent's correlation id, not only the one that started this child. |
| `PublishAfterCommitAsync<TMessage>` | `Task PublishAfterCommitAsync<TMessage>(TMessage message, CancellationToken ct = default)` | Default interface method; default body delegates to `PublishAsync`. `SagaContext` overrides it to **queue** the publish until after this step's own persist has committed, draining every queued publish from one step strictly in the order queued. Use this instead of `PublishAsync` whenever the message being published is the mapped result of a synchronous call this step already made (e.g. `.CallHttp`'s message-loopback outcome) — publishing immediately would let the reply re-enter this saga instance before the step's own optimistic-concurrency check has committed. Deliberately opt-in: a deferred publish that fails after commit has nowhere safe to go, so a drain failure is caught, logged, and recorded as a `DeliveryExhausted` timeline entry rather than thrown — the saga is left `Running` for its own state timeout to rescue. |

**"Immediately" means "under `SagaOutboxMode.Deferred`", the default.** `PublishAsync`, `SendAsync`,
`StartChildAsync` and `NotifyParentAsync` all route through `SagaContext.RouteAsync`, which under
`SagaOutboxOptions.Mode = SagaOutboxMode.All` queues them into the very same post-commit queue
`PublishAfterCommitAsync` uses instead of sending inline — that is the only way "routed through the
outbox" can mean anything for a mid-step publish, since a row written after the message is already
gone guarantees nothing. So under `Mode = All` nothing a step publishes leaves the process until the
step's own persist has committed, and a step that loses its optimistic-concurrency race publishes
nothing at all. `PublishAfterCommitAsync` queues unconditionally, in both modes. See
[`configuration.md`](configuration.md#sagaoutboxoptions).

**Engine-published, not through `ISagaContext`:** `ChildSagaFinished(Guid ChildCorrelationId, string
ChildSagaType, SagaStatus Status)` is published directly by `SagaOrchestrator` — not via any
`ISagaContext` method — on the child's behalf, to whichever parent it has, for the two cases a child
cannot report itself because it never reaches a step that could: **an unhandled exception, or a
timeout that goes terminal**. The failure path publishes unconditionally; the timeout path stages it
only when the timeout step resolved a final status, i.e. only when that `WithTimeout(...)` declared
`.Finalize(...)`. A timeout that merely compensates and transitions tells the parent **nothing** — so
a parent waiting on a child must either confirm the child's timeout steps finalize, or (better) carry
its own `WithTimeout(...)` on the state it waits in, which is the only recovery that does not depend
on the child's DSL. A parent opts in simply by declaring a
`.When<ChildSagaFinished>()`/`.On<ChildSagaFinished>()` handler anywhere in its own DSL; a parent that
never declares one is never even subscribed to the message type, so it is never delivered. It
deliberately does **not** fire from the ordinary message-driven success path — a child that finishes
normally reports its own result via `NotifyParentAsync` instead, which carries the actual data
(`ChildSagaFinished` carries only a status).

## `.CallHttp` (from `VSaga.Http`)

An extension method on `EventBuilder<TState, TMessage>`, reached through the DSL's public `Then(...)`
seam — no change to `VSaga.Core` itself, and `VSaga.Core` gains no `HttpClient` dependency. Any
saga, on any transport, gets `.CallHttp` by referencing `VSaga.Http`.

> **Requires `AddVSagaHttpCalls()`.** `.CallHttp`/`ctx.CallHttpAsync` resolve an `IHttpClientFactory`
> from `ISagaContext.Services` at call time — without registering it, the first `.CallHttp` step throws.
> Call it once per host, alongside your other `AddVSaga*` registrations:
>
> ```csharp
> services.AddVSagaHttpCalls();   // required once per host before any saga using .CallHttp runs
> ```
>
> `HttpCallOptions` (its `Timeout`, default 30s) is set the same way: `AddVSagaHttpCalls(o => o.Timeout = TimeSpan.FromSeconds(10))`.

```csharp
.When<OrderShipped>()
    .CallHttp(h => h.Post("https://payments.example/authorize")
        .Body((ctx, msg) => new { ctx.Saga.OrderId, msg.Amount })
        .OnStatus(402).Then(s => s.Declined = true)
        .OnSuccess<PaymentAuthorized>()      // message loopback
        .OnFailure<PaymentAuthFailed>()      // message loopback
        .WithRetry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)))
```

`EventBuilderHttpExtensions.CallHttp<TState, TMessage>(this EventBuilder<TState, TMessage>, Action<HttpCallBuilder<TState, TMessage>> configure)` builds an `HttpCallDefinition` and adds it as a
`Then(...)` action. `HttpCallBuilder<TState, TMessage>` (and the context-only `HttpCallBuilder<TState>`
behind `ctx.CallHttpAsync`, below) expose:

| Member | Signature | Notes |
| --- | --- | --- |
| `Post` | `Post(string url)` | Required — the target URL. |
| `Body<TBody>` | `Body<TBody>(Func<ISagaContext<TState>, TMessage, TBody> factory)` | (Context-only overload: `Body(object value)`, captured eagerly.) |
| `OnSuccess<TOut>()` | Message loopback for any 2xx not covered by a more specific `OnStatus`. Deserializes the response body as `TOut` (case-insensitive, since the far side is an arbitrary REST API) and publishes it via `PublishAfterCommitAsync`. | |
| `OnSuccess(Action<TState> mutate)` | Inline shape for the same case: mutates state synchronously, no loopback, no race. | |
| `OnFailure<TOut>()` / `OnFailure(Action<TState> mutate)` | Same two shapes for anything else: a non-2xx with no more specific `OnStatus` entry, **or** a network-level failure (timeout, no response at all — `TOut` must tolerate deserializing from `{}`). | |
| `OnStatus(int code)` | Returns an `HttpStatusBuilder` with `.As<TOut>()` (loopback) / `.Then(Action<TState> mutate)` (inline) for one exact status code, taking priority over the 2xx/everything-else buckets. | |
| `WithRetry(int maxAttempts, TimeSpan delay)` | This call's **own** bounded retry for a genuine network-level failure only — a definitive HTTP response, even a 5xx, is never retried; it's mapped via `OnStatus`/`OnFailure` instead. Deliberately separate from `EventBuilder.Retry(RetryPolicy)`, which replays the *entire* step's actions from index 0 (re-POSTing this call along with everything else in the step) — `.WithRetry` is scoped to just this one HTTP call. Defaults to a single attempt. | |

**`ctx.CallHttpAsync(...)`** (`SagaContextHttpExtensions`, `VSaga.Http`) is the imperative counterpart,
reachable from a `Compensate(state, ...)` delegate or a `TimeoutBuilder<TState>.Then(...)` step —
neither hands the caller an inbound `TMessage` the way `EventBuilder` does. Same result-shape surface
as above (`HttpCallBuilder<TState>`, no `TMessage` type parameter). Both entry points share one
execution path (`HttpCallExecutor<TState>`) bit-for-bit, so `.CallHttp` and `ctx.CallHttpAsync` behave
identically for retry, status mapping, and loopback vs. inline.

```csharp
Compensate(AwaitingStock, (ctx, ct) =>
    ctx.CallHttpAsync(h => h.Post("https://payments.example/void")
        .Body(new { ctx.Saga.PaymentId })
        .OnSuccess<PaymentVoided>()
        .OnFailure<PaymentVoidFailed>(), ct));
```
