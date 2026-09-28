# VSaga.Transport.RabbitMQ

`RabbitMQ.Client`-based `IMessageTransport` for vSaga — the reference broker adapter, built directly on
`RabbitMQ.Client` with no MassTransit/Wolverine/Brighter dependency. One durable topic exchange, publisher
confirms + `mandatory: true` for unroutable-publish detection, automatic connection recovery, and a
shared dead-letter exchange (`vsaga.dlx` by default) with a `.poison` dead-letter queue per consumer.

## Install

```bash
dotnet add package VSaga.Transport.RabbitMQ
```

## Usage

```csharp
services.AddVSagaRabbitMq(o => o.ConnectionString = "amqp://guest:guest@localhost:5672/");
```

> Publishing a message type nothing has called `SubscribeAsync` for yet throws an unroutable-publish
> exception rather than vanishing silently — the most common way to hit this is moving a saga from the
> in-memory transport to a real broker for the first time. See the callout in
> [docs/getting-started.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/getting-started.md#run-it).

## Docs

[docs/transports/rabbitmq.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/rabbitmq.md)
and [docs/configuration.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/configuration.md#rabbitmqoptions-vsagatransportrabbitmq)
for every `RabbitMqOptions` property.

## License

MIT
