# VSaga.Transport.InMemory

In-memory `IMessageTransport` for vSaga — no broker required, for local development and
`VSaga.Testing`'s `SagaTestHarness`. Takes no options; dispatches synchronously within the process.

A publish fans out to every subscriber declaring the message type; a send delivers only to
subscribers whose `TransportSubscription.QueueNameHint` equals the destination, matching how the
broker adapters resolve a destination to a queue. Like every other adapter, it is wrapped in the
outbound/inbound middleware pipeline, so `VSaga.Chaos`'s fault injection applies here too.

## Install

```bash
dotnet add package VSaga.Transport.InMemory
```

## Usage

```csharp
services.AddVSagaInMemoryTransport();
```

## Docs

[docs/transports/in-memory.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/in-memory.md)
and [docs/transports/index.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/index.md)
for the full transport contract every adapter implements.

## License

MIT
