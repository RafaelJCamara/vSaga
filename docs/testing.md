# Testing: `SagaTestHarness`

`VSaga.Testing.SagaTestHarness<TDefinition, TState>` is a given/when/then-style wrapper around one
saga definition, running the **real** `SagaOrchestrator<TState>` against the in-memory persistence and
transport providers — a test exercises the exact same engine code path as production, minus any real
broker or database. It includes a `FakeTimeProvider` so timeout behaviour is testable without real
waiting.

```csharp
var correlationId = Guid.NewGuid();
var orderId = "ORD-1";

await using var harness = new SagaTestHarness<OrderSaga, OrderSagaState>();

await harness
    .Given(correlationId)
    .WhenAsync(new OrderSubmitted(orderId, "CUST-1", Amount: 42m));

await harness.AssertStateAsync(harness.Saga.Gathering);
harness.AssertPublished<ChargePayment>(m => m.Amount == 42m);
```

## API

| Member | Signature | Notes |
| --- | --- | --- |
| Constructor | `SagaTestHarness(Action<IServiceCollection>? configureServices = null)` | Builds a fresh DI container: in-memory persistence + transport, the real engine, your saga registered. Use `configureServices` to add anything the saga needs beyond the engine itself. Starts every registered `IHostedService` *except* the timeout/outbox background pollers (see Notes below). |
| `Saga` | `TDefinition` (get) | The registered saga definition instance. |
| `TimeProvider` | `FakeTimeProvider` (get) | Drives `AdvanceTimeByAsync` below; also injected as the ambient `TimeProvider` for anything the saga/engine reads time from. |
| `CorrelationId` | `Guid` (get) | Defaults to a fresh random id; set via `Given`. |
| `Services` | `IServiceProvider` (get) | The harness's own container, for resolving anything else you need directly. |
| `Given` | `SagaTestHarness<TDefinition, TState> Given(Guid correlationId)` | Sets the correlation id subsequent `When`/assert calls act on. Fluent — returns the harness. |
| `WhenAsync<TMessage>` | `Task<SagaTestHarness<TDefinition, TState>> WhenAsync<TMessage>(TMessage message, CancellationToken ct = default) where TMessage : notnull` | Publishes under the current correlation id and waits for full processing (the in-memory transport dispatches synchronously). Fluent. |
| `AdvanceTimeByAsync` | `Task<SagaTestHarness<TDefinition, TState>> AdvanceTimeByAsync(TimeSpan duration, CancellationToken ct = default)` | Advances the fake clock and processes any timeouts now due for this saga type — the deterministic alternative to a real wait. Fluent. |
| `RetryAsync` | `Task<SagaTestHarness<TDefinition, TState>> RetryAsync(CancellationToken ct = default)` | Fluent. Redrives the last recorded technical failure (a `StepFailed` entry) in process, against the saga's current, unchanged state. Only valid while the saga is `Failed` **with** a `StepFailed` entry; throws otherwise. The dashboard's Retry button covers more (see [`dashboard.md`](dashboard.md#manual-retry)): it also re-runs the step that ended a saga in a business failure, a dead-letter or a timeout, after resetting the saga's state and status to what they were before that step, and it does so by republishing the step's message to the broker. This method does none of that, so it can't stand in for a test of those paths. |
| `FindStateAsync` | `Task<TState?> FindStateAsync(CancellationToken ct = default)` | Raw snapshot lookup. |
| `GetTimelineAsync` | `Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(CancellationToken ct = default)` | Raw timeline lookup. |
| `GetPublished` | `IReadOnlyList<object> GetPublished()` | Every message published so far, across every correlation id this harness has touched (publish and send both). |
| `AssertStateAsync` | `Task<TState> AssertStateAsync(State<TState> expected, CancellationToken ct = default)` | Throws `SagaAssertionException` on mismatch or if no instance exists. |
| `AssertStatusAsync` | `Task<TState> AssertStatusAsync(SagaStatus expected, CancellationToken ct = default)` | Same, for `SagaState.Status`. |
| `AssertPublished<TMessage>` | `void AssertPublished<TMessage>(Func<TMessage, bool>? predicate = null)` | Throws if no matching message of the given type was published. |
| `AssertNotPublished<TMessage>` | `void AssertNotPublished<TMessage>(Func<TMessage, bool>? predicate = null)` | Throws if a matching message *was* published. |
| `AssertNoSagaCreatedAsync` | `Task AssertNoSagaCreatedAsync(CancellationToken ct = default)` | Throws if an instance exists for the current correlation id — useful for asserting a message that shouldn't initiate anything didn't. |
| `DisposeAsync` | `ValueTask DisposeAsync()` | Stops every hosted service and disposes the container — always `await using` the harness. |

## Notes

- The harness records [state snapshots](observability.md#state-snapshots) by default, like any engine
  host, so `GetTimelineAsync` returns a `StatePersisted` entry after every committed step, its
  `PayloadJson` the state as stored. A test that counts or orders entries, or that should not see them,
  opts out:
  `new SagaTestHarness<TDefinition, TState>(s => s.AddSingleton(new SagaOrchestratorOptions { RecordStateSnapshots = false }))`.
  The registration replaces the engine's default `SagaOrchestratorOptions`, so set any other option you
  rely on in the same object. A snapshot's `UpdatedAtUtc` follows the `FakeTimeProvider`, while every
  entry's `OccurredAtUtc` stays wall-clock.
- `Given`/`WhenAsync`/`AdvanceTimeByAsync`/`RetryAsync` all return the harness (the async three as a
  `Task<...>`), which is what lets the example above chain `.Given(...).WhenAsync(...)` in one
  expression. The assertion and lookup members do not — they return what they assert or read.
- `WhenAsync` relies on the in-memory transport's synchronous dispatch, so there is no need to poll or
  wait after publishing — by the time `WhenAsync` returns, the saga has fully processed the message
  (including any chain of self-published messages the in-memory transport dispatches recursively).
- The harness registers the real `AddVSagaEngine`, so anything true of production dispatch (retry
  policies, compensation ordering, the deferred-publish drain) is exercised exactly as written — this
  is not a hand-rolled test double of the orchestrator.
- Because the harness uses `VSaga.Persistence.InMemory` and `VSaga.Transport.InMemory` under the hood,
  it inherits their single-process, non-durable characteristics — this is a unit-testing tool, not a
  substitute for the live `docker compose` verification this repo's own history
  (see [`history/`](history/)) leans on for anything envelope/header/timing-sensitive.
- Chaos fault injection reaches the harness — `new SagaTestHarness<TDefinition, TState>(s => s.AddVSagaChaos(...))`
  — now that `VSaga.Transport.InMemory` is wrapped in the middleware pipeline like every other adapter.
  Two caveats. Enabling `Delay` **hangs the test**: the delay middlewares await this same
  `FakeTimeProvider`, and the only thing that advances it is `AdvanceTimeByAsync`, which the test cannot
  reach while it is awaiting the publish that triggered the delay. `Drop` and `Duplicate` are safe.
  Separately, `WhenAsync` publishes through the bare transport, so an outbound fault never disturbs the
  test's own stimulus — inbound faults and the saga's own publishes do go through the full pipeline.
  See [`chaos.md`](chaos.md).
- `SagaTimeoutDispatcherHostedService` and `SagaOutboxDispatcherHostedService` — the production
  crash-recovery pollers — are deliberately never started here. Both are driven by a `PeriodicTimer`
  built against this same `FakeTimeProvider`, so `AdvanceTimeByAsync` would otherwise wake their
  background polling loops too, and the timeout poller's loop would race the harness's own explicit
  claim-and-handle call for the exact timeout `AdvanceTimeByAsync` is trying to fire deterministically.
  `AdvanceTimeByAsync` already does everything the timeout poller would for a saga under test. It does
  not replay outbox rows, though: a message queued with `ctx.PublishAfterCommitAsync` whose post-commit
  send throws stays `Pending` in the outbox, and nothing in the harness redrives it the way the outbox
  poller would after `DispatchGracePeriod`. Leaving the pollers running would only reintroduce the
  non-determinism the harness exists to remove.
