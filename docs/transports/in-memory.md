# Transport adapter: in-memory

`VSaga.Transport.InMemory` (`AddVSagaInMemoryTransport()`) is a single-process `IMessageTransport` with
no broker and no network — a publish dispatches synchronously and recursively to every matching
in-process subscriber. It underlies `SagaTestHarness` (see [`../testing.md`](../testing.md)) and is
convenient for local development without standing up Postgres/RabbitMQ, but it is **not for production
use**: state does not survive a process restart, and on its own it exhibits none of the failure modes
(lost/duplicated/delayed delivery, partition) a real transport has to handle. `AddVSagaChaos` can now
inject the first three here deliberately — see [`../chaos.md`](../chaos.md) — but nothing does so
unless you ask.

**It does serialize.** Every publish and send JSON-round-trips the message
(`JsonSerializer.SerializeToUtf8Bytes`, then a subscriber deserializing the body back out) exactly
like the RabbitMQ adapter, so saga definitions and the orchestrator behave identically regardless of
transport. That is deliberate and worth knowing when you decide what a green harness test proves: a
payload bug that is really a *serialization* bug — a missing converter, a non-round-trippable
property, a polymorphic field that loses its subtype — does reproduce here. What does not reproduce is
anything about the wire *headers*: the publisher's own `MessageEnvelope.Headers` dictionary is handed
to the subscriber by reference, with no encoding step to lose or mangle it (see
[`index.md`](index.md#what-every-adapter-guarantees)).

**Addressed sends stay addressed.** `SendAsync`/`SendRawAsync` do not broadcast by message type: a
subscription receives the message only if it declares the type *and* its
`TransportSubscription.QueueNameHint` equals the `destination` string exactly (ordinal comparison).
That mirrors what every broker adapter does with a destination — RabbitMQ publishes to the default
exchange with it as the routing key, Brighter binds each queue to its own name as an extra routing
key — so a `SendAsync`-isolation test that passes here also passes against a real broker.

One divergence remains, and it matters because this transport backs `SagaTestHarness`: **a send
addressed to a queue nobody subscribed is a silent no-op.** Nothing matches, nothing is dispatched,
and no exception is raised — there is no broker here to return the message as unroutable the way
`RabbitMqTransport` does with `MessageTransportPublishException.IsUnroutable`. A typo in a destination
name is therefore a passing test here and a thrown exception in production. The message is still
recorded in `GetPublished()` (with its `Destination`), so assert on that rather than on the absence of
a throw.

Because dispatch is synchronous and recursive, a chain of self-published messages resolves entirely
within the original `PublishAsync`/`WhenAsync` call — this is what lets `SagaTestHarness.WhenAsync`
return only once a saga has fully processed a message, with no polling. It is also the reason a small
number of race conditions in this repo's sub-saga composition were only reproducible under this
transport's synchronous dispatch (a child notifying its parent from the very step that started it can
race ahead of the parent's own not-yet-persisted transition) — see
[`../concepts.md`](../concepts.md#sub-saga-composition) and
[`../history/sub-saga-completion-notification.md`](../history/sub-saga-completion-notification.md).
Every real transport decouples a subscriber's dispatch from the publisher's own call stack, so this
narrow hazard is specific to the in-memory adapter's synchronous model, not a property of the engine
generally.

```csharp
services.AddVSagaInMemoryTransport();
services.AddVSagaInMemoryPersistence();
```

No options to configure.

`AddVSagaInMemoryTransport()` registers `IMessageTransport` as a factory producing a
`MiddlewarePipelineTransport` over the in-memory transport — unconditionally, exactly like every
broker adapter, which is what makes `AddVSagaChaos` actually inject faults here (see
[`../chaos.md`](../chaos.md)) and what lets `AddVSagaTopologyRecording()` re-wrap the registration at
all. Two consequences worth knowing: middleware can be registered before *or* after this call (the
factory resolves it lazily), and resolving `IMessageTransport` hands you the pipeline wrapper, not the
concrete transport — test code wanting `GetPublished()`/`Reset()` resolves `InMemoryMessageTransport`
by its own type, which is the same singleton instance the wrapper is built over.
