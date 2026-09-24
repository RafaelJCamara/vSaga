# Observability

vSaga emits both OpenTelemetry traces/metrics and a fully persisted event log — the two are
independent and either works without the other.

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
`ChildSagaStarted`/`ChildSagaFinished`, `DeliveryExhausted`, `SagaCompleted`, and `SagaCancelled`.

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
string) rather than pulling in `OpenTelemetry.Api`, so `VSaga.Abstractions` stays free of any
`PackageReference` at all.

**A retried delivery keeps the same trace.** `SagaOrchestrator` already copies every inbound header
forward on redelivery, so `traceparent` echoes automatically — a retry of the same logical delivery
gets a `delivery.attempt` tag on its span rather than a fresh linked span, since it's the same logical
operation, not a new one.

**A failed step marks its span failed — but the stack is not on the span.** The failure path calls
`activity?.SetStatus(ActivityStatusCode.Error, ex.Message)`, so a trace backend can distinguish a
successful hop from one that threw without cross-referencing the event log. That is all it does: the
message becomes the status *description*, and there is no `Activity.AddException`/OTel `exception`
event, so the exception type and stack trace are **not** on the span. They are on the `StepFailed`
log entry instead (which also records the `traceId`/`spanId` for cross-referencing back) — that entry,
not the span, is where to look for a stack.

Source and span names, for writing queries: the `ActivitySource` is named `VSaga.Saga`
(`VSagaDiagnostics.ActivitySourceName`, same string as the meter). The consumer span is named
`saga.step {SagaType}.{fromState}`; the producer span is named `saga.publish {MessageTypeName}`.

Tag names (`VSagaDiagnostics`): `saga.type`, `saga.kind`, `saga.correlation_id`, `saga.from_state`,
`saga.to_state`, `delivery.attempt`.

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
follow-up, not built here. Wiring the wrong instrument (and shipping a permanently-wrong dashboard
number) was judged worse than shipping none.

**Node participants: propagation only, no metrics.** The TypeScript SDK's participant (`vsaga/participant`)
threads the inbound `traceparent`/`tracestate` headers onto every reply it publishes, so a Node
participant correctly continues a trace started by a .NET saga rather than rooting a new one. It does
not start its own spans and emits no OpenTelemetry metrics of its own — there is no TypeScript
equivalent of `VSagaDiagnostics`/`AddVSagaOpenTelemetry` today, so a Node hop shows up in a trace as
propagated context only, with none of the counters/histograms above.

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
