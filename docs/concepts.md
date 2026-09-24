# Concepts

The mental model behind vSaga: what a saga is, the two DSLs for writing one, how an instance gets
found again when a reply arrives, how compensation unwinds a partially-completed saga, and how
timeouts recover from a reply that never arrives.

## Saga instances and identity

A saga instance is a row of persisted state (`SagaState` and its subclasses) tracked through a
sequence of inbound messages. Every instance is identified by the pair **`(SagaType, CorrelationId)`**,
not by correlation id alone — that is what lets two different saga types (an orchestrated one and a
choreographed one, or a parent and a child) track the same business transaction, or two unrelated
transactions that happen to reuse a correlation id, without colliding. Every store method that reads
or mutates one instance takes both parts of the key (`ISagaSnapshotStore<TState>.FindAsync(sagaType,
correlationId, ...)`, `ISagaEventLogStore.GetTimelineAsync(sagaType, correlationId, ...)`, and so on).

`GET /api/correlations/{correlationId}` is the one place a bare correlation id is still meaningful: it
returns every saga instance — of any type — currently tracking that id, which is how a caller holding
only an id (a log line, a support ticket) resolves it to a concrete instance.

## Orchestrated vs. choreographed

vSaga has two DSL base classes, both implementing the same `ISagaDefinition<TState>` contract the
runtime, persistence, timeout dispatcher, and dashboard are written against. Neither the engine nor
the dashboard needs to know which kind a saga is to run it — registration is the same
`services.AddVSagaEngine(o => o.AddSaga<TDefinition, TState>())` call either way. See
[`saga-dsl.md`](saga-dsl.md) for the full method reference of both.

**`OrchestratedSagaDefinition<TState>`** is a central conductor: steps are declared
`During(state).When<TMessage>()`, gated on the instance's *current recorded state*. Only the message
types registered for the state a saga is presently in are dispatched to it; anything else is an
unhandled event. `.TransitionTo(...)` moves the saga to its next state once a step's actions have run.
This is the natural shape for a linear (or fan-out/join) process with a well-defined "what happens
next" at every point.

**`ChoreographedSagaDefinition<TState>`** has no conductor: reactions are registered per event type
only, `On<TMessage>()`, and are dispatched *regardless* of the instance's current state, because
independent participants — not this definition — decide what to publish and when. There is no
`During(...)` gate. `.RecordState(...)` replaces `.TransitionTo(...)` as a milestone label for the
dashboard/timeline and for keying `Compensate`/`WithTimeout` — it never gates dispatch. More than one
event type can call `.StartsNewInstance()`, since a choreography has no single designated first step:
whichever participant happens to publish first is the one that starts tracking.

Use orchestration when there is a clear step order to express as states. Use choreography when several
independent participants react to the same upstream event on their own initiative and you need to
observe the fan-out (and know when it's finished) without commanding any of them — see the
`.Finalize(Func<TState, SagaStatus?>)` overload below for how a choreography expresses "the last of
several independent branches to arrive finishes the saga."

Both DSLs share their step-execution and compensation-walk internals (`StepExecutor`,
`CompensationRunner`) so the two kinds cannot silently drift in retry/backoff or compensation-ordering
behaviour.

## Correlation: transport id, then business key

The default correlation mechanism is the transport-stamped correlation id (`MessageEnvelope`'s
`CorrelationId`, propagated by every `IMessageTransport` adapter) — an inbound message is routed to
the saga instance whose `(SagaType, CorrelationId)` matches. `CorrelateBy` on an `EventBuilder`/
`ChoreographyEventBuilder` step additionally *assigns* a value extracted from the message onto saga
state (e.g. stamping `ctx.Saga.OrderId` from an inbound message's own `OrderId` field) — by itself,
that's just a stored value for dashboard search/traceability with no effect on message routing.

**Business-key correlation** (`CorrelateOn`) arms a second, fallback lookup. A saga definition that
calls `CorrelateOn(s => s.OrderId)` in its constructor declares `OrderId` as its business key; any
`CorrelateBy` on that same property additionally registers as that message type's business-key
extractor — and a `CorrelateBy` on any *other* property is a startup failure, not a no-op (see below).
When an inbound message's transport correlation id doesn't match an existing instance, the
orchestrator falls back to looking up `(SagaType, BusinessKey)` before concluding the message starts a
new instance. This matters for messages that legitimately arrive under a *different* transport
correlation id than the saga's own — the canonical case is a child saga's own domain events, or any
integration where an upstream system mints its own message ids but carries a shared business
identifier (an order number, an external reference) that both sides agree on. A saga that never calls
`CorrelateOn` is unaffected: every `CorrelateBy` call site keeps its original behaviour (assign onto
state, nothing else), and the fallback lookup never runs.

**Arming a business key makes `CorrelateOn`'s property exclusive — and the enforcement is a hard
startup failure.** Once `CorrelateOn` has been declared, a `CorrelateBy` targeting any *other* state
property throws `SagaDefinitionException` from the saga definition's own constructor, so the saga
never registers and the host never starts. The trap is that the offending call looks entirely
reasonable: a saga with `CorrelateOn(s => s.OrderId)` that also writes
`CorrelateBy(m => m.ShipmentId, s => s.ShipmentId)` on a later step does not quietly skip registering
that second extractor — it does not construct at all. Assign non-key fields with a plain
`.Then((ctx, msg) => ctx.Saga.ShipmentId = msg.ShipmentId)` instead. Two sibling throws come from the
same guard: a second `CorrelateBy` for the same message type, and a second `CorrelateOn`. All three
are constructor-time, which is the point — a mis-declared business key is caught at registration
rather than surfacing as a mysterious correlation miss in production.

The business key is persisted as `SagaState.BusinessKey`, with a **partial unique index** scoped to
`(SagaType, BusinessKey) WHERE BusinessKey IS NOT NULL` — partial so sagas that never set a business
key (the common case, unaffected by this feature) impose no uniqueness constraint on `NULL`, and scoped
by saga type so two different saga types may legitimately reuse the same business-key value (an order
number meaningful to `OrderSaga` says nothing about a `PostShipmentChoreography` instance that happens
to reuse the string).

**A correlation-id-carrying message still resolves by transport id first.** The business-key fallback
only runs when the transport correlation id misses. This means `CorrelateOn` does not change behaviour
for the common case where every message in a saga's flow carries the same transport correlation id
throughout — it only helps when that assumption breaks.

**A parent/child pair is a different mechanism entirely.** A child started via
`ISagaContext.StartChildAsync` gets a **fresh** correlation id — deliberately not the parent's, since
the snapshot primary key would then cap a parent at one child per saga type, and a self-recursive saga
would collide with itself. The parent-child relationship rides on two dedicated envelope headers
(`x-vsaga-parent-saga-type`, `x-vsaga-parent-correlation-id`), not on business-key correlation. See
[`saga-dsl.md`](saga-dsl.md)'s `ISagaContext` section and
[`design/sub-saga-composition.md`](design/sub-saga-composition.md) for the full sub-saga design.

## Compensation

`.Compensate()` on a step (or a timeout) runs every registered `Compensate(state, ...)` delegate for
the states this saga instance has actually visited, **most-recent first**. "Visited" is derived from
the persisted event log (`ISagaContext.VisitedStates`), not from a static list of every declared
state — a state the instance never reached has no compensation delegate invoked for it. Compensation
delegates are ordinary `Func<ISagaContext<TState>, CancellationToken, Task>` callbacks registered via
`Compensate(State<TState> forState, ...)` in the definition's constructor; they typically publish an
undo command (`ReleaseInventory`, `RefundPayment`) for whatever that state's forward step did.

**Compensation must be defensive.** A timeout firing means "no reply arrived in time", never "the
participant declined" (a decline is an ordinary reply and takes its own explicit failure branch
instead) — so a compensating message has to be safe to receive for work that may never actually have
happened. The same defensiveness applies to a fan-out/join's compensation: when several branches ran
concurrently, a failure on one branch cannot assume the others didn't also succeed, so a gathering
state's compensation unconditionally undoes every branch rather than checking which one landed.

**Compensation does not cascade into children.** A parent's `.Compensate()` only ever runs its own
registered delegates; it never automatically walks `FindChildrenAsync` or touches a child saga. This
is a closed design decision (see
[`design/sub-saga-composition.md`](design/sub-saga-composition.md) §3.5), not a gap — a parent that
needs a child compensated publishes its own compensating command explicitly, the same way it would
address any other collaborator.

**Publishes inside a multi-message compensation delegate must run sequentially, not concurrently.**
`ctx.PublishAsync` shares the saga's own context (and, transitively, one persistence unit of work)
across every action in a step; awaiting two publishes via `Task.WhenAll` inside a hand-written
compensation delegate is unsafe for the same reason a `.Publish(...).Publish(...)` chain in the DSL
itself is sequential by construction.

## Timeouts

`WithTimeout(state, after, configure)` schedules a timeout row for a state; if a message doesn't move
the saga out of that state within the delay, `SagaOrchestrator.HandleTimeoutAsync` fires the
configured recovery step (typically `.Compensate().TransitionTo(...).Finalize(...)`). A timeout is
only ever scheduled on a **real transition into** a state (`ToState != FromState`) — a self-transition
(the gathering-state pattern below) neither cancels nor reschedules the pending timeout. That rule is
the whole rule: there is no special case for a saga's *initial* state, so a step that transitions back
into it from some other state is a real transition and does schedule its timeout. What the rule does
rule out is the common initiating shape — the opening event is dispatched while the instance is
already *in* the initial state, so a step that "stays" there is a self-transition and hangs no timeout
off itself. If a milestone needs a timeout and is reached via a self-transition from the initiating
event, give it its own distinct state so the transition is real.

**One timeout covers a whole gather.** In a fan-out/join (see below), returning the gathering state
from `TransitionTo(Func<TState, State<TState>>)` is a self-transition, so an arriving branch does not
extend the deadline — the timeout represents "the whole gather took too long", not "this branch was
slow."

**A due timeout is claimed with a version-checked persist before running any compensation/publish side
effects.** This closes a race where a message and a timeout read the same snapshot version
concurrently: the timeout claims first (or loses the claim and is abandoned, publishing nothing) rather
than running its side effects and only discovering the lost race on its own final write, after
messages had already gone out over the transport.

## Fan-out and join

A single step can dispatch several messages at once — `.Publish(...).Publish(...)` chains, run
sequentially. The *join* half — waiting for all of several replies that can arrive in any order — is
expressed with the computed overloads of `TransitionTo`/`Finalize` (orchestrated) or `Finalize`
(choreographed): register the same selector on every branch, and whichever reply lands last is the one
whose selector result actually releases the join or finalizes the saga, without any branch assuming it
is last. See [`saga-dsl.md`](saga-dsl.md) for the exact signatures.

## Sub-saga composition

A saga can start another saga as a step (`ISagaContext.StartChildAsync`), and a child can notify its
own parent (`ISagaContext.NotifyParentAsync`) or have the engine notify on its behalf when it fails,
or when it times out *and the timeout goes terminal* (`ChildSagaFinished`, published by the engine
itself, not through `ISagaContext`).

**`ChildSagaFinished` is not a general "the child stopped waiting" signal.** The failure path publishes
it unconditionally, but the timeout path publishes only when the timeout step resolved a final status —
that is, only when the child's `WithTimeout(...)` declared `.Finalize(...)`. A child whose timeout
merely compensates and transitions tells its parent nothing at all. A parent must therefore not treat
`ChildSagaFinished` as its recovery mechanism: give the state it waits in its own `WithTimeout(...)`,
which is the only recovery that does not depend on how the child's DSL happens to be written.

See [`saga-dsl.md`](saga-dsl.md) for the method reference and
[`design/sub-saga-composition.md`](design/sub-saga-composition.md) for the full design history,
including two races found in production traffic (a child that reports back from the very step that
started it can race ahead of the parent's own not-yet-persisted transition) that are pinned by tests
but not fixed, since a real fix means reordering the engine's "run step actions, then persist"
sequence throughout — out of scope for the sub-saga feature itself.
