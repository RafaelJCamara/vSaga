# VSaga.Transport.Common

Shared building blocks for vSaga's transport adapters (RabbitMQ, MassTransit, Wolverine, Brighter, HTTP,
in-memory) — `MiddlewarePipelineTransport`, the outbound/inbound middleware decorator every adapter
registers itself behind and the seam `VSaga.Chaos`'s fault injection plugs into. An internal dependency
of those adapters; not typically referenced directly by application code.

## Install

```bash
dotnet add package VSaga.Transport.Common
```

## Docs

[docs/transports/index.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/index.md)
for the `IMessageTransport` contract every adapter implements.

## License

MIT
