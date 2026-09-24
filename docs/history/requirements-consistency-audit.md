# History: the requirements-vs-implementation consistency audit

> Written fresh, not preserved from `README.md`. Describes a docs-vs-code audit run on 2026-09-24
> across every reference doc, every design record, and both runtimes, plus the fix pass that followed
> it the same day.

---

## What was checked, and what came back

Eight parallel audits, one per documentation area, extracted every verifiable claim from `docs/`,
`docs/design/`, `README.md` and `CONTRIBUTING.md` and compared it against source with file-and-line
evidence: the DSL and concepts, configuration, all six transports, the TypeScript SDK, the dashboard,
persistence/observability/testing/chaos, the top-level docs and runnable stack, and the design records.

The headline is worth stating plainly, because the fix list below is long enough to imply otherwise:
**no documented user-facing feature was missing.** Every DSL method matched its documented signature,
every options class matched its documented defaults character-for-character, all seven `@vsaga/*`
packages matched down to individual wire-header constants, and all 19 numbered items in
`design/production-readiness.md` §8 were genuinely implemented. Roughly 46 findings came out of it,
and the large majority were documentation drift rather than broken code.

Two were not.

## The two gaps that were real

**The in-memory transport was never in the middleware pipeline.** `docs/transports/index.md` and
`docs/chaos.md` both claimed every adapter is wrapped in `MiddlewarePipelineTransport`, which is "why
chaos works identically across all six adapters with zero adapter-specific code."
`VSaga.Transport.InMemory` registered its transport directly and did not even reference
`VSaga.Transport.Common`. So `AddVSagaChaos` registered its middlewares and they were never invoked —
no error, no warning, nothing to notice. This bit exactly where a developer meets chaos first: local
dev and `SagaTestHarness`.

Fixing it surfaced why it had survived. An unconditional wrap broke 33 call sites across 18 files
(including `SagaTestHarness` itself) that did `(InMemoryMessageTransport)GetRequiredService<IMessageTransport>()`.
The first attempt elided the wrapper when no middleware was registered, which compiled and passed but
left a worse trap than the original bug: adding chaos to a harness test would then throw
`InvalidCastException` from library code the user doesn't own, in precisely the scenario the fix was
meant to enable. The call sites were changed to resolve the concrete type directly — the pipeline
wraps that same singleton, so object identity and every `GetPublished()` assertion are unchanged — and
the wrap is now unconditional, byte-identical in shape to the other five adapters.

The same pass fixed the in-memory adapter's addressed send, which recorded the destination and then
broadcast by message type anyway. It now matches `destination` against `QueueNameHint`. One divergence
from a broker is deliberate and documented: an addressed send to a queue nobody subscribed is a silent
no-op rather than a `MessageTransportPublishException`, because making it throw would break a test that
legitimately sends to an unsubscribed queue.

**The HTTP adapter's ack model was never implemented.** `design/http-based-sagas.md` §4.4 specifies
ack → drop, `NackAsync(requeue: true)` → re-enqueue, `NackAsync(requeue: false)` → error log and drop.
Every `ReceivedMessage` the adapter constructed got a no-op ack context whose two methods were bare
`Task.CompletedTask`, so requeue requests vanished silently and the error log never happened. The
adjacent §4.4 decision (no `IHttpDeadLetterSink`) *had* shipped, which is how half a section went
unimplemented without anyone noticing.

The implementation is organised around a question the design doc doesn't ask but should have: **can a
redelivery reproduce the original delivery exactly?** Yes for a local dispatch and for a `200`
synchronous reply — both arrive through the same in-process channel with no ambient reply collector
and no HTTP response riding on the outcome, so a re-enqueued copy differs from the original in nothing
but time. No for a genuine inbound HTTP request, which is dispatched inline under the reply collector
that decides the peer's status and body; a re-enqueued copy would run later with no collector and throw
unroutable on its reply — a different, reply-less delivery wearing the original's name. That path logs
at error and drops on both nack forms, and the peer owns the retry.

Requeue chains needed their own bound, and the reason is subtle enough to be worth recording:
`SagaOrchestrator` never calls `NackAsync(requeue: true)` at all — it republishes through
`PublishRawAsync` with an incremented `x-vsaga-delivery-attempt` and dead-letters at
`MaxDeliveryAttempts`. A requeue therefore increments nothing the orchestrator reads, so a
handler that always requeues would spin the channel forever on a counter nobody owns. The cap rides on
the ack context rather than in a header, which is what keeps a redelivered copy byte-identical; the two
bounds compose rather than cancel.

`@vsaga/transport-http` turned out to have the same three dispatch paths and the same no-op ack, so it
got the same model with the same split and the same cap. The only divergence is the log sink: .NET uses
`ILogger`, TypeScript uses `console.error` behind a `[vsaga]` prefix, because `@vsaga/transport-http`
takes no logger dependency and `@vsaga/participant` owns the `Logger` interface.

## A fix that outgrew its finding

The dashboard's change poller read only the first page of sagas (`PageSize = 100`) while still
advancing its watermark, so more than 100 updates inside one one-second tick silently lost the
remainder forever. The fix was to drain in ascending `UpdatedAtUtc` order with a page budget, under an
invariant worth writing down: the returned watermark is always the timestamp of a row this tick
actually pushed, so anything unreached is strictly newer and stays inside the next tick's window.
Stopping early can only cost latency, never an update. The old code violated exactly this — it read the
*newest* page but advanced to the newest row, so the unread remainder was *older* than the new
watermark and became unreachable.

That correct fix was also O(table size) per tick, because `SagaListFilter` could sort on a timestamp
but not filter on one, so the ascending drain had to fetch and discard every row at or below the
watermark first. So `UpdatedSince` was added to the filter and honoured by both readers — and then the
index, because `(Status, UpdatedAtUtc)` cannot serve a bare `UpdatedAtUtc > @p` predicate with `Status`
leading, leaving Postgres a sequential scan on a query that runs every second. A single-column
`IX_SagaInstances_UpdatedAtUtc` serves both the poller and the list endpoint's default
`UpdatedAt DESC` sort. Reordering the existing composite to `(UpdatedAtUtc, Status)` was rejected: it
would demote the status-filtered diagnostic query, and the statuses operators actually filter on
(`Failed`, `Compensating`) are the rare ones where a `Status`-leading index wins most.

The client-side `> since` comparison was kept rather than deleted as redundant. `UpdatedSince` is an
optional property on a public filter type, so a third-party `ISagaSummaryReader` compiled against the
older contract still compiles and silently ignores it; keeping the check makes such a reader degrade to
the previous scan-everything behaviour instead of re-pushing every saga in the store on every tick.

## Where the audit itself was wrong

Three of the audit's own findings did not survive contact with the code, and recording them matters as
much as the fixes:

- **`docs/transports/index.md`'s "production-readiness §8.17" is not a broken citation.** `§8.<n>` is
  established notation in this repo for "§8's numbered item *n*" — `saga-dsl.md` and
  `project-origins-and-hardening-pass.md` both use `§8.19` — and item 17 is exactly the allowlist work
  that sentence describes. Treating it as a typo would have destroyed information. It was widened to
  name both the design section and the shipped item.
- **`SagaTestHarness` does not resolve the concrete transport type**, which the in-memory fix was
  briefed as a constraint to preserve. It casts from `IMessageTransport`, along with 32 other sites —
  the opposite of the stated premise, and the reason the first fix attempt had to compromise.
- **The TypeScript HTTP transport is not structurally inline-only.** It has a real deferred path,
  equivalent to .NET's channel-and-pump, used in the same two places. Had the "inline-only" hypothesis
  been accepted, the TS side would have been *under*-implemented to match a shape that doesn't apply.

## A CI blocker found on the way

`dotnet restore` failed repo-wide on `NU1902`: `Microsoft.SourceLink.GitHub` 8.0.0 pulls
`Microsoft.Build.Tasks.Git` 8.0.0, which now carries a moderate advisory, and `TreatWarningsAsErrors`
turns the audit warning into a restore error on every project. Pre-existing and unrelated to this
audit, but it would have broken CI on the next push. The SDK has shipped Source Link in-box since
.NET 8, so the standalone package reference was removed outright rather than suppressed;
`PublishRepositoryUrl`/`EmbedUntrackedSources` still do their job.

## Verification

`dotnet build` clean with zero warnings, and **382 tests passing across all 12 projects**, including
every Testcontainers-backed suite (RabbitMQ, MassTransit, Wolverine, Brighter, Postgres). The
TypeScript workspace typechecks and lints clean with **133 tests passing**.

Test coverage grew where the audit found claims nobody had pinned. `docs/transports/index.md` claimed
every adapter had a header round-trip test; RabbitMQ — the reference adapter — had none at all, and
MassTransit and Wolverine had no `traceparent`/`tracestate` coverage. All three now do.
`UnhandledEventPolicy` had zero test references anywhere in the repo, which is how the stale comment on
`Throw` (claiming it nacks and redelivers, when it marks the saga `Failed` and acks) survived being
called out as stale in `saga-dsl.md` without ever being fixed. Both policies are now pinned, with the
ack asserted directly through a recording ack context, since the in-memory transport's no-op ack would
otherwise hide it.

## What this does not do

- **No release automation.** `design/production-readiness.md` §3 specifies a tag-triggered
  `release.yml` publishing to nuget.org and npm. It still does not exist, and tagging and publishing
  remain manual. The gap was invisible from the design doc because `release.yml` lived only in §3's
  prose and was never given a numbered §8 item — so §8's accurate "all 19 items committed" was true
  while the workstream was materially incomplete. Both status lines now say so.
- **Offset paging is still racy.** A row updated mid-scan moves to the end of the ascending order and
  can be missed. `UpdatedSince` gets most of the way toward keyset paging but is not a drop-in: a
  proper fix needs a `(UpdatedAtUtc, SagaType, CorrelationId)` continuation token and a matching
  composite sort in both readers.
- **`Delay` chaos still cannot be used inside `SagaTestHarness`.** The delay middlewares await the
  harness's `FakeTimeProvider`, and the only thing that advances it is `AdvanceTimeByAsync`, which the
  test cannot reach while awaiting the publish that triggered the delay — so it hangs rather than
  slowing. `Drop` and `Duplicate` work. Documented in `testing.md` and `chaos.md` rather than fixed.
- **The in-memory transport has no header round-trip test**, so the adapter-wide claim is worded to
  name the five wire adapters rather than all six.
