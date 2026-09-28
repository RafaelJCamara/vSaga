# VSaga.Transport.Wolverine

Wolverine adapter for vSaga's `IMessageTransport` — run vSaga sagas over an existing
WolverineFx.RabbitMQ-based messaging setup instead of the reference `VSaga.Transport.RabbitMQ` adapter.
Shares the other RabbitMQ-family adapters' default topic exchange (`vsaga.saga.events`) but is **not**
wire-compatible with them: routing keys are the raw PascalCase type name (`OrderApproved`, not the
RabbitMQ and Brighter adapters' `order-approved`), and every message travels inside a `WireEnvelope`
JSON payload. Give each adapter its own `ExchangeName` if they share a broker.

## Install

```bash
dotnet add package VSaga.Transport.Wolverine
```

## Usage

```csharp
services.AddVSagaWolverine(o => o.ConnectionString = "amqp://guest:guest@localhost:5672/");
```

## Docs

[docs/transports/wolverine.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/wolverine.md)
covers what's different from the reference RabbitMQ adapter — notably, no unroutable-publish detection
(Wolverine's underlying gateway has no equivalent as of the pinned version).

## License

MIT
