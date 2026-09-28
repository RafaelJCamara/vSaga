# VSaga.Transport.Http

Brokerless `IMessageTransport` for vSaga — run sagas and participants over plain HTTP instead of a
message broker. No RabbitMQ, no broker infrastructure: publish/send POST directly to a configured
endpoint, and a `200` response is itself the reply.

## Install

```bash
dotnet add package VSaga.Transport.Http
```

## Usage

```csharp
services.AddVSagaHttp(o =>
{
    o.Endpoints["payments"] = "http://payments:8080";
    o.Routes["ChargeCard"] = ["payments"];
});
app.MapVSagaHttp();
```

## Docs

[docs/transports/http.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/http.md) and
[docs/design/http-based-sagas.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/design/http-based-sagas.md)
for the full design.

## License

MIT
