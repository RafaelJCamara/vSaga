# Chaos engineering: `VSaga.Chaos`

`VSaga.Chaos` plugs three fault types into the `IOutboundMessageMiddleware`/`IInboundMessageMiddleware`
seam that `MiddlewarePipelineTransport` already wraps every transport in, so it works identically
across all six adapters with no adapter-specific code. It is opt-in and never registered by default.

**Including the in-memory transport** — which is the local-dev and `SagaTestHarness` path most people
reach for first, and which is the one adapter this did *not* cover until recently: it registered its
bare transport directly rather than through the pipeline, so `AddVSagaChaos` appeared to take effect
and then silently injected nothing. `AddVSagaInMemoryTransport()` now applies the same unconditional
wrap every broker adapter does (see [`transports/index.md`](transports/index.md#the-two-decorators-every-adapter-is-wrapped-in)),
so faults configured against it genuinely fire. Note what that does and does not buy you: an
in-memory drop or duplicate is real, but the in-memory transport dispatches synchronously on the
publisher's own call stack, so an injected `Delay` blocks the publisher rather than modelling a slow
broker.

**Inside `SagaTestHarness`, reach for `Drop` and `Duplicate` — not `Delay`.** Passing `AddVSagaChaos`
through the harness's `configureServices` works now that the in-memory transport is wrapped. But the
harness registers its `FakeTimeProvider` as the ambient `TimeProvider` *before* `configureServices`
runs, so `AddVSagaChaos`'s own `TryAddSingleton(TimeProvider.System)` is a no-op and the delay
middlewares await the fake clock. The only thing that advances that clock is `AdvanceTimeByAsync`,
which the test cannot reach while it is still awaiting the publish that triggered the delay — so an
enabled `Delay` hangs the test rather than slowing it. `Drop` and `Duplicate` touch no clock and behave
there exactly as they do against a broker. See [`testing.md`](testing.md).

**.NET-only, today.** There is no TypeScript equivalent of `VSaga.Chaos` — fault injection is a .NET
runtime-only capability; a Node participant in a mixed-runtime saga cannot delay, drop, or duplicate its
own messages this way.

```csharp
services.AddVSagaChaos(o =>
{
    o.Delay.Enabled = true;
    o.Drop.Enabled = true;
    o.Duplicate.Enabled = true;
});
```

See [`configuration.md`](configuration.md#chaosoptions-vsagachaos) for the full options shape and
defaults.

## Fault types

- **Delay** — waits a random `[MinDelay, MaxDelay]` before the publish/delivery continues through the
  rest of the pipeline. Driven by an injected `TimeProvider` (not `Task.Delay` directly), so a unit test
  that drives the middleware directly uses `FakeTimeProvider` instead of actually waiting — but see the
  `SagaTestHarness` caveat above, where nothing is in a position to advance that clock.
- **Drop** — outbound sets `OutboundMessageContext.Suppressed` (the publish call returns normally;
  nothing ever arrives, simulating an unroutable or lost publish). Inbound sets
  `InboundMessageContext.Suppressed` **and acks the delivery itself** before returning without calling
  the next middleware — since suppressing skips the terminal handler that normally owns the ack, a
  dropped delivery would otherwise sit unacknowledged until the consumer's prefetch window is
  exhausted, rather than behaving like a message silently lost after delivery.
- **Duplicate** — re-invokes the next middleware `ExtraDeliveries` extra times after the real one,
  simulating a broker's at-least-once guarantee. Inbound extra invocations wrap the message with a
  no-op ack context, so a genuine second delivery of the *same* physical message is never acked twice —
  the real delivery's ack/nack decision is made exactly once, by the real invocation, regardless of how
  many synthetic duplicates run.

Each fault independently gates outbound vs. inbound application (`ApplyToOutbound`/`ApplyToInbound`)
and its own trigger probability. A disabled fault is never registered into the pipeline at all — no
runtime check, no per-message cost.

## Scope: what chaos can and can't reach

All three faults operate purely at the transport middleware layer, which sits **outside**
`SagaOrchestrator.HandleAsync`'s own try/catch. That means chaos faults cannot reach — and
deliberately don't try to fake — `HandleInfrastructureFailureAsync`'s bounded-redelivery/
`DeliveryExhausted` dead-letter path, which is reserved for genuine infrastructure failures inside the
orchestrator itself (a deserialize error, a persistence-store exception); that method's own redelivery
publish also bypasses the middleware pipeline entirely, so chaos can't intercept it even indirectly.

What chaos *does* exercise, end to end against a real broker: publisher-confirm behaviour continuing to
work correctly under injected latency and re-publishes; a state's `WithTimeout` firing and compensating
when a drop/delay makes a reply never arrive in time (including the race where a delayed reply finally
lands after the timeout already fired — see [`concepts.md`](concepts.md#timeouts)); and
`ISagaEventLogStore.IsDuplicateAsync` absorbing a chaos-duplicated message so it isn't processed twice.
A state with **no configured timeout** has no such safety net — vSaga's own sample found this the hard
way (see [`history/timeout-coverage-every-awaiting-state.md`](history/timeout-coverage-every-awaiting-state.md)):
every state a saga can be dropped-into-permanently from needs a `WithTimeout`.

## Running it against the sample

```bash
docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build
```

`docker-compose.chaos.yml` is an overlay that turns all three faults on with sample-tuned
probabilities against the `OrderProcessing` sample (`Chaos:Enabled` config; the base compose file
leaves it `false`).

**A caution on tuning `Delay` against a single-consumer subscription.** `RabbitMqTransport` gives each
`SubscribeAsync` call one channel with a single sequential consumer — an inbound delay doesn't just
slow the one delayed message, it blocks that whole consumer from dispatching anything else while it
waits. A `Delay.MaxDelay` longer than your actual message cadence produces an unbounded backlog, not
steady-state throughput with occasional slow messages. Keep `MaxDelay` comfortably below your
expected inter-message interval.

## Testing

`VSaga.Chaos.Tests` covers each fault type in isolation — trigger vs. no-trigger, both directions, the
no-double-ack property of duplicate-inbound, the probability-roll/delay helpers' edge cases, and the
registration gating — using the same hand-written-fake xUnit style as the rest of the repo (no mocking
framework; `FakeTimeProvider` for the delay tests instead of real waits).
