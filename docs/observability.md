# Observability

vSaga emits both OpenTelemetry traces/metrics and a fully persisted event log. They are independent as
*wiring* — either works without the other — but they are not equivalent in kind.

> **The event log is a correctness input, not an observability output.**
> `SagaOrchestrator.GetVisitedStatesAsync` derives the entire compensation set from it, and
> `IsDuplicateAsync` is the redelivery dedupe check (`ISagaEventLogStore.cs:4-11`, `:28-44`). It
> therefore cannot be sampled, truncated, or retention-pruned without changing which compensations
> run and re-admitting messages that were already processed. Traces and metrics can be sampled freely;
> the event log cannot.

## The persisted event log

Every saga instance's history is recorded as an append-only sequence of `SagaLogEntry` rows
(`ISagaEventLogStore`), viewable via the dashboard's Timeline tab or `GET
/api/sagas/{sagaType}/{correlationId}/timeline`. This is the backing data for the dashboard's Saga
Map too (see [`dashboard.md`](dashboard.md#saga-map)) — the dashboard reads this log directly rather
than an OTel backend, so **none of the OTel wiring below is required for the dashboard to work.**
The full `SagaEntryType` set is `SagaStarted`, `StateEntered`, `MessageReceived`,
`MessagePublished`/`MessageSent`, `UnexpectedEvent`, `StepStarted`/`StepSucceeded`/`StepFailed`,
`CompensationStarted`/`CompensationStepSucceeded`/`CompensationStepFailed`,
`TimeoutScheduled`/`TimeoutFired`/`TimeoutCancelled`, `ManualRetryRequested`,
`ChildSagaStarted`/`ChildSagaFinished`, `DeliveryExhausted`, `SagaCompleted`, `SagaCancelled`, and
`StatePersisted` (see [State snapshots](#state-snapshots) below).

**Message bodies.** `SagaStarted`, `MessageReceived` and `StepFailed` carry the inbound message's body
in `PayloadJson`: the engine serialises the message it received, on every step, the first included.
That body is what lets the dashboard re-run a failed step (see
[`dashboard.md`](dashboard.md#manual-retry)); a saga recorded before `MessageReceived` carried it can
be retried only from a `StepFailed` entry or its first step. On MongoDB a body above
`MaxPayloadJsonBytes` is stored as a size marker instead (see
[`configuration.md`](configuration.md#vsagamongooptions-vsagapersistencemongodb)). These bodies, the state
snapshots and the exception text in `ErrorMessage` are business data: the dashboard API serves them only to a
caller who holds `sagas.data` for the saga's type, and nulls them, entry by entry, for everyone else (see
[What a scoped caller sees](dashboard.md#what-a-scoped-caller-sees)). The log itself is the same for every
reader.

### State snapshots

A saga's stored state is overwritten at every persist, so on its own the log says which states a saga
went through but not what its data was after each step. `StatePersisted` entries fill that gap: right
after a persist commits, the engine appends one whose `PayloadJson` is the state exactly as the
snapshot store wrote it (the same `JsonSerializer.Serialize(state)` call on the same object, so the text
equals the stored blob). The decision and its alternatives are in
[ADR 0007](adr/0007-state-snapshots-in-the-event-log.md).

- **When one is written.** After a step's persist commits (`PersistAndFinalizeStepSuccessAsync`), after
  a failed step's persist marks the saga `Failed` (`HandleStepFailureAsync`), after a timeout's final
  persist (`CommitAndDispatchTimeoutAsync`), and after a redelivery-exhausted message's persist
  (`RecordDeliveryExhaustedAsync`). Each one goes after the commit and before the step's deferred
  publishes are drained, so a snapshot never describes a state that was not stored and always precedes
  anything the step's own publishes cause. The dashboard API also appends one after a
  [manual retry](dashboard.md#manual-retry) resets the saga, but only for a saga that already has
  snapshots.
- **When none is written.** For the timeout claim, the business-key reservation insert, an
  `UnexpectedEvent`, a duplicate delivery, an unhandled timeout, and any transition that lost its
  concurrency race; nor when a process dies between the commit and the append. A step can therefore
  have no snapshot, and the dashboard says so instead of guessing.
- **Identity.** `MessageType`/`MessageId` name the inbound message whose step the snapshot follows (both
  null after a timeout or a dashboard reset). `FromState` and `ToState` are always null, because the
  compensation set is read from `ToState` and a snapshot must not add to it.
- **Two markers instead of the state.** A state larger than `MaxStateSnapshotBytes` (UTF-8 bytes of its
  JSON, 256 KiB by default) is recorded as `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}`. A
  snapshot of a successful step or a timeout that would take the saga's recorded snapshots past
  `MaxStateSnapshotBytesPerSaga` (1 MiB by default) is recorded as
  `{"$vsagaStateOmitted":true,"bytes":N,"budget":B}`; the snapshots after a step failure or a delivery exhaustion are still recorded in full (up to the
  per-snapshot cap), because those are the ones an investigation needs. The budget exists because the
  engine reads every snapshot back before every step (below).
- **Best effort, with its own deadline.** A failed append, cancellation and timeouts included, is logged
  as a Warning ("Could not record the state snapshot for saga …") and the step carries on: the only
  effect is a step with no snapshot. The append runs under `StateSnapshotTimeout` (5 s by default), so a
  stalled event-log write cannot hold back the step's deferred publishes and acknowledgement.
- **Not a correctness input.** `StatePersisted` is the one entry type no correctness decision depends
  on: neither the compensation set nor the duplicate check looks at it (the engine reads only its
  payload length, for the per-saga budget), and the Saga Map skips it.
  It is still part of the log, so it shares the log's no-retention rule above: snapshots, business data
  included, stay as long as the saga's history does.
- **Cost.** One more serialisation and one more append per committed transition, and the storage of
  roughly steps × state size per saga (measured per provider in
  [`persistence.md`](persistence.md#capacity-model) and
  [`persistence.md`](persistence.md#mongodb)). `GetVisitedStatesAsync` reads the whole timeline,
  snapshots included, before every message and timeout, so the read volume grows with the square of the
  step count; the per-saga budget bounds it.
- **Upgrade order.** A dashboard API older than this entry type returns it as the number `21` and serves
  its payload like any other, unredacted; deploy the dashboard before the engine hosts (compose builds
  both together). See [ADR 0007](adr/0007-state-snapshots-in-the-event-log.md).

The options are listed in [`configuration.md`](configuration.md#sagaorchestratoroptions);
`RecordStateSnapshots = false` turns the entry off.

## Traces

`SagaOrchestrator` starts an `ActivityKind.Consumer` span for every inbound message handled, with the
parent context extracted from the message's own `traceparent`/`tracestate` headers when present.
`SagaContext.PublishInternalAsync` starts a producer span and injects the current activity context
into the outbound envelope's headers — so one trace can span an orchestrator and every participant it
talks to, across any transport, as long as the transport passes headers through losslessly (all six
adapters do; see [`transports/index.md`](transports/index.md)).

**W3C Trace Context, not a custom header.** vSaga uses the bare `traceparent`/`tracestate` header
names (not `x-vsaga-`-prefixed) specifically for interoperability — an OTel collector, a broker
plugin, or a non-vSaga consumer all expect the standard names. `VSagaDiagnostics.Inject`/
`TryExtractActivityContext` hand-roll the W3C format directly (`traceparent` is a fixed 55-character
string) rather than pulling in `OpenTelemetry.Api`, so `VSaga.Abstractions` takes no runtime package
dependency at all (the only `PackageReference`s it gets are the build-time MinVer and analyzer ones
`Directory.Build.props` adds to every project, all `PrivateAssets="all"`).

**A retried delivery keeps the same trace.** `SagaOrchestrator` already copies every inbound header
forward on redelivery, so `traceparent` echoes automatically — a retry of the same logical delivery
gets a `delivery.attempt` tag on its span rather than a fresh linked span, since it's the same logical
operation, not a new one.

**A failed step marks its span failed — but the stack is not on the span.** The failure path calls
`activity?.SetStatus(ActivityStatusCode.Error, ex.Message)`, so a trace backend can distinguish a
successful hop from one that threw without cross-referencing the event log. That is all it does: the
message becomes the status *description*, and there is no `Activity.AddException`/OTel `exception`
event, so the exception type and stack trace are **not** on the span. They are not on the `StepFailed`
log entry either: its only error detail is that same `ex.Message` (as `ErrorMessage`), next to the
`FromState`, the failed message's type, id and payload, and the `traceId`/`spanId` for
cross-referencing back. Nothing vSaga records for a failed message-driven step carries the exception
type or stack trace. A timeout step that throws is different: it gets no `saga.step` span and no
`StepFailed` entry, but the exception propagates out of `HandleTimeoutAsync` to
`SagaTimeoutDispatcherHostedService`, which logs it in full (type and stack included) at Error level.

Source and span names, for writing queries: the `ActivitySource` is named `VSaga.Saga`
(`VSagaDiagnostics.ActivitySourceName`, same string as the meter). The consumer span is named
`saga.step {SagaType}.{fromState}`; the producer span is named `saga.publish {MessageTypeName}`.

Tag names (`VSagaDiagnostics`): `saga.type`, `saga.kind`, `saga.correlation_id`, `saga.from_state`,
`saga.to_state`, `delivery.attempt`.

**Persistence-provider spans are the provider's client's, not vSaga's.** Npgsql ships an
`ActivitySource` you enable on your own OpenTelemetry pipeline. StackExchange.Redis has none of its
own; its spans come from the OpenTelemetry contrib `OpenTelemetry.Instrumentation.StackExchangeRedis`
package added to that same pipeline, which by default instruments the `IConnectionMultiplexer` it
resolves from DI. vSaga registers none, so pass the multiplexer `RedisConnection.GetMultiplexerAsync`
returns to that package's `AddConnection` instead. The MongoDB .NET driver 3.6, which this repo
resolves, exposes no `ActivitySource` of its own either (3.7.0 and later, still inside the package's
`[3.6.0,4.0.0)` range, add built-in tracing): instrument it through `AddVSagaMongoDb`'s
`configureClient` callback, whose `MongoClientSettings.ClusterConfigurator` is where a command-event
subscriber (such as the community `MongoDB.Driver.Core.Extensions.DiagnosticSources` package) attaches. The step's persist transaction
and the event-log appends made while the step runs then appear as child spans of the `saga.step` span.
The store calls that precede the step (the instance lookup and any business-key reservation, the
`SagaStarted`/`MessageReceived` appends, the duplicate check, the visited-state read) run before that
span starts, so they do not.

## Metrics

Meter name `VSaga.Saga` (`VSagaDiagnostics.Meter`):

| Instrument | Kind | Meaning |
| --- | --- | --- |
| `vsaga.saga.started` | `Counter<long>` | Incremented after a new saga's **first step succeeds** and its persist commits — not when the instance is created. See the caveat below. |
| `vsaga.saga.completed` | `Counter<long>` | Incremented when a saga reaches `Completed`. |
| `vsaga.saga.failed` | `Counter<long>` | Incremented when a saga reaches `Failed`. |
| `vsaga.saga.step.retries` | `Counter<long>` | Incremented per step-level retry attempt. |
| `vsaga.saga.step.duration` | `Histogram<double>` (ms) | Duration of one step's execution. |
| `vsaga.saga.duration` | `Histogram<double>` (ms) | Total saga duration as `now - state.CreatedAtUtc`, recorded on every path that reaches a terminal status — step success, step failure, timeout outcome, and redelivery-exhausted dead-lettering alike. |
| `vsaga.saga.stuck` | `ObservableGauge<long>` | Sagas idle past their threshold at the last [stuck-saga sweep](#stuck-sagas), by saga type and cause. Only when the sweep is enabled. |

Every instrument carries the tag `saga.type`; `vsaga.saga.stuck` also carries `saga.stuck_cause`. `completed` and `failed` count only their own
status: a saga finalized as `TimedOut`, `Compensated` or `Cancelled` increments neither, though
`vsaga.saga.duration` still records it.

**`started` counts first-step successes, not instance creations — so don't subtract to get in-flight.**
`SagasStarted` is incremented only under `if (isNew)` in `PersistAndFinalizeStepSuccessAsync`, i.e.
after the first step has succeeded *and* its persist has committed. The failure path
(`HandleStepFailureAsync`) increments `SagasFailed` but never `SagasStarted`. A brand-new saga whose
very first step throws is therefore counted in `vsaga.saga.failed` without ever having been counted in
`vsaga.saga.started`, and `started - (completed + failed)` is **not** a valid in-flight estimate —
it can even go negative. Use a store-backed `COUNT(*) WHERE Status = Running` for that number (see the
next note). (`needsInsert`, the flag selecting `PersistAsync`'s insert-vs-update branch, is unrelated
to newness for metrics purposes; `isNew` is tracked separately.)

**Every terminal path records `vsaga.saga.duration`.** The rule is the terminal status, not any one
method: `RecordDeliveryExhaustedAsync`, `RecordTimeoutOutcomeAsync`, `HandleStepFailureAsync`, and
`PersistAndFinalizeStepSuccessAsync` (the tail of `HandleStepSuccessAsync`) each record it once their
own persist has committed — recording before the commit would emit a phantom duration for a transition
a lost concurrency race then discarded. Treat that list as the current sites rather than an exhaustive
contract; the rule is what holds.

**No `vsaga.saga.running` gauge — deliberately.** An `UpDownCounter` for "how many sagas are running
right now" was considered and rejected: it's process-local and non-idempotent, so a restart, a
redelivery, or a second replica desynchronizes it permanently with no way to self-correct. The correct
instrument is an `ObservableGauge` backed by `COUNT(*) WHERE Status = Running` against the store, which
needs scoped-store access from a meter callback that `VSaga.Observability` doesn't have yet — a named
follow-up, not built here (the [stuck-saga gauge](#stuck-sagas) is the one store-backed gauge that exists: it reads the store from a sweep and caches the reading for the callback). Wiring the wrong instrument (and shipping a permanently-wrong dashboard
number) was judged worse than shipping none.

## Stuck sagas

Timeouts rescue a saga whose reply never arrives. They do not rescue one whose timeout itself was lost: a due
timeout is claimed (marked fired, with a version-checked persist) *before* the timeout step runs, so a step that
throws, or a process that dies mid-step, leaves the saga `Running` in the same state with no timeout left. The same
holds for a state that declares no timeout, a deferred publish that failed after its commit, a `Compensating` saga
whose process died, and a saga type that no running process hosts. Nothing flags any of these on its own.

The stuck-saga sweep does. It is opt-in ([`SagaStuckOptions`](configuration.md#sagastuckoptions)) and detects only:
it never changes a saga, and recovery stays with the operator, through the dashboard's retry or the saga's own
timeout once it is back.

**What counts as stuck.** A saga that is `Running` or `Compensating` and has been idle (no persist) for strictly
longer than its threshold. Every persist moves `UpdatedAtUtc`, a firing timeout's claim included, so a `Running`
saga idle past its state's timeout plus the grace had that timeout lost, whatever the reason.

| Cause | The saga | Stuck when idle longer than |
| --- | --- | --- |
| `TimeoutOverdue` | `Running`, in a state that declares a timeout | that timeout plus `TimeoutGrace` |
| `IdleWithoutTimeout` | `Running`, in a state that declares none | the idle threshold |
| `CompensationIdle` | `Compensating` | the idle threshold |

A saga in a terminal status (`Completed`, `Failed`, `Compensated`, `TimedOut`, `Cancelled`) is never stuck. Not
covered: a saga that is busy but never finishes, and a stuck [outbox](configuration.md#sagaoutboxoptions) row, which
is a different table with its own age.

**What a process reports.** Only the saga types it hosts, as the timeout dispatcher claims only its own: it can
classify those and no others. A process that hosts no saga types does nothing, and neither does the dashboard
process, which does not register saga definitions (its [idle filter](dashboard-guide.md#filtering-searching-sorting-and-paging)
shows the same sagas without the cause).

- **The `vsaga.saga.stuck` gauge** (`ObservableGauge<long>`, unit `{saga}`), tagged `saga.type` and `saga.stuck_cause`
  (`TimeoutOverdue`, `IdleWithoutTimeout`, `CompensationIdle`): how many sagas were stuck at the last sweep. Every
  saga type and cause is reported, zeros included, so an alert rule sees a recovery as a value. A reading of the store
  the sweep took, cached for the meter callback (which cannot await a scoped store), so a restart loses only the
  previous reading, unlike an `UpDownCounter`, which a restart or a second replica would desynchronise for good.
  **Aggregate across replicas with `max`, not `sum`:** each replica of a service reports the same population.
  After three failed sweeps in a row the reading is withdrawn, so a dead sweep does not look like a quiet store.
- **A log line.** A `Warning` the first time a saga is seen stuck (type, correlation id, status, state, idle time,
  threshold, cause, last update), and an `Information` when it leaves the set. At most `MaxReportedPerSweep` per saga
  type are listed in one sweep, with the rest counted in one further line. A restart forgets what was reported, so a
  saga still stuck is reported again.
- **`ISagaStuckNotifier`** (`VSaga.Abstractions.Notifications`), for a webhook, a page or a chat message. vSaga
  ships a no-op; register your own to replace it:

  ```csharp
  public sealed class PagerNotifier : ISagaStuckNotifier
  {
      public Task SagaStuckAsync(StuckSaga saga, CancellationToken cancellationToken = default) { /* page someone */ }
      public Task SagaRecoveredAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) { /* resolve it */ }
  }

  services.AddSingleton<ISagaStuckNotifier, PagerNotifier>(); // after AddVSagaEngine
  ```

  `StuckSaga` carries the type, correlation id, state, status, cause, how long it has been idle, the threshold it
  crossed, its last update and its `Version`. **Delivery is at-least-once:** a saga counts as announced only after the
  call returns, so a call that threw is offered again at the next sweep (the first throw is logged and ends that
  sweep's notifying), and `SagaRecoveredAsync` is only called for a saga whose announcement got through. Every newly
  stuck saga is offered, not only the ones the log lists, so a notifier should batch. Each replica notifies for the
  same saga, so a receiver de-duplicates on `(SagaType, CorrelationId, Version)`.

**Cost.** One paged query per hosted saga type per sweep, oldest first, over instances idle past the type's floor (the
smaller of its idle threshold and its shortest timeout plus the grace). EF Core and MongoDB serve it from their
existing indexes. On Redis, with a saga type in the filter, it is a server-side intersection of each status bucket
with the type's index, which costs the smaller of the two sets and not only the idle rows; measure it on a large
`Running` bucket before sweeping often. A definition that does not report `ShortestTimeout` gets the idle threshold as its
floor and misses a timeout-overdue saga idle for longer than its timeout but shorter than that threshold; both DSL
base classes report it.

## Wiring it up: `AddVSagaOpenTelemetry`

```csharp
services.AddVSagaOpenTelemetry(
    configureTracing: t => t.AddOtlpExporter(),
    configureMetrics: m => m.AddOtlpExporter());
```

This is the complete OTLP wiring — add the `OpenTelemetry.Exporter.OpenTelemetryProtocol` package and
pass `configureTracing`/`configureMetrics` delegates as shown. `AddVSagaOpenTelemetry` itself stays
unopinionated about exporters (no dependency on any specific one, and it never assumes a collector is
present) — the two delegates are exactly where an app plugs in whatever exporter(s) it wants (OTLP,
Jaeger, Prometheus, console, ...). The method also calls `Sdk.SetDefaultTextMapPropagator(...)` with a
`CompositeTextMapPropagator` of `TraceContextPropagator` **and** `BaggagePropagator`. The trace-context
half matches the wire format described above — set explicitly so a host process (or another library)
calling `SetDefaultTextMapPropagator` first with something else (e.g. B3) can't silently disagree with
what vSaga actually puts on the wire.

**Note the side effect:** `SetDefaultTextMapPropagator` is process-wide, so calling
`AddVSagaOpenTelemetry` enables OTel **baggage** propagation for the whole host — not just for vSaga's
own spans — and overrides any propagator the host had already configured. vSaga itself neither reads
nor writes baggage; the `BaggagePropagator` is there so composing with other instrumentation doesn't
silently drop it.

```csharp
services.AddVSagaOpenTelemetry();   // sources registered, propagator set — no exporter without the delegates above
```

## Dashboard log events

`VSaga.Dashboard.Api` logs its sign-in, access and live-connection events with stable event ids (source
generated `LoggerMessage`s, so the id and name are fixed and a filter or alert can key on them). The ids are
in the 7000s; the range says what produced them.

| Event ids | Produced by | What |
| --- | --- | --- |
| 7100–7102, 7110–7115 | the **audit log**, category `VSaga.Dashboard.Audit` | Sign-ins and sign-outs, failed sign-ins, lockouts, rate-limited attempts, and every committed or refused change to users, teams and roles. Each event carries the actor, the action, the target, the outcome and the client address; never passwords, hashes or setup codes. The event ids, levels and what each means are listed under [Audit log](dashboard.md#audit-log). |
| 7200–7204 | the identity store's start-up | `7200` the resolved database path (Information), `7201` ready (Information), `7202` not ready, with the reason, at Error, `7203` still not ready (Debug), `7204` the file's mode could not be restricted to its owner (Warning). |
| 7210–7215 | the first administrator | `7210` first-run setup is open, **with the one-time setup code in the message** (Warning), `7211` setup is open with the code from `Dashboard:Setup:Code` (Warning), `7212` the administrator was seeded (Information), `7213` the seed could not be applied (Error), `7214` `ResetOnStart` reset the account (Warning; it repeats at every start while the setting is `true`), `7215` a seed was ignored because users exist (Debug). |
| 7300–7303 | sessions and the API key | `7300` a session was rejected, and why (Debug; the response never says), `7301` the API key is shorter than 24 characters, `7302` `Dashboard:ApiKeyRole` names no role, `7303` that role holds `access.manage`, which the key never gets (each Warning, at start). |
| 7310–7312 | antiforgery, the hub's origin check and `RequireHttps` | `7310` a request failed the antiforgery check (Debug), `7311` a hub request from a foreign origin was refused, with the received and the expected origin (Warning), `7312` a plain-HTTP request was refused with `400` `https_required` because `Dashboard:Session:RequireHttps` is true (Warning). Behind a proxy a burst of 7311 means the proxy rewrites the host or the scheme, and a burst of 7312 that the proxy is not in `Dashboard:TrustedProxies`. |
| 7320–7322 | live hub connections | `7320` N connections were closed because access changed (Information), `7321` closing one threw, so it was aborted instead (Warning; a connection that offers no close-for-reconnect feature is aborted without a line of its own), `7322` it could not even be aborted and may stay open on access it lost (Error). |

The audit events are the ones to ship: route the `VSaga.Dashboard.Audit` category to wherever you keep
security logs (a `Logging:LogLevel:VSaga.Dashboard.Audit` entry sets its level; any logging provider's own
filter can select it). Event `7210` is the exception that needs the opposite care: while it is the only way to
claim the first administrator, whoever can read the API's log can read the code, so treat the log of a fresh
install with no users as sensitive until the first administrator exists (or preset the code with
`Dashboard:Setup:Code` and do not log it).

### The `identity` health check

`GET /health` on the dashboard API lists a third check, `identity`, next to `persistence` and `rabbitmq`: the
dashboard's own SQLite identity store (see [`dashboard.md`](dashboard.md#the-identity-store)), separate from
the saga store the `persistence` check watches. It is registered to fail as `Degraded`, never `Unhealthy`:
without the store nobody can sign in, but the saga views, the API key with a built-in role and the saga host
do not depend on it, so `/health` stays `200` and compose's `service_healthy` gate on `dashboard-api` still
opens for `order-processing`. A probe also gives a store that failed to initialise its chance to retry (at most
once every 10 s) and waits for that at most 2 s; the description carries the reason, naming the setting to fix
but not the file's path (the log has the path, event `7200`). The check is `Degraded` too while a configured
first administrator could not be created, with the setting named. CI asserts that it is `healthy` on a fresh
compose stack.

Each retry made through the dashboard is also in the saga's own event log rather than here: the
`ManualRetryRequested` entry's `sourceService` is `dashboard:<username>` or `dashboard:api-key`.
