# VSaga.Http

The `.CallHttp` step and transport-agnostic REST integration for vSaga sagas — call plain HTTP APIs
from a saga step without a message broker. Any orchestrated saga, on any `IMessageTransport`, gets
`.CallHttp` by referencing this package (a choreographed step calls `ctx.CallHttpAsync` from an async
`.Then(...)` instead); unrelated to `VSaga.Transport.Http` (the brokerless message transport).

## Install

```bash
dotnet add package VSaga.Http
```

## Usage

```csharp
services.AddVSagaHttpCalls();   // required once per host before any saga using .CallHttp runs
```

```csharp
.When<OrderShipped>()
    .CallHttp(h => h.Post("https://payments.example/authorize")
        .Body((ctx, msg) => new { ctx.Saga.OrderId, msg.Amount })
        .OnSuccess<PaymentAuthorized>()
        .OnFailure<PaymentAuthFailed>()
        .WithRetry(maxAttempts: 3, delay: TimeSpan.FromSeconds(1)))
```

## Docs

[docs/saga-dsl.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/saga-dsl.md#callhttp-from-vsagahttp)
for the complete `.CallHttp`/`ctx.CallHttpAsync` reference.

## License

MIT
