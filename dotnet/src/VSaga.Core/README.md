# VSaga.Core

The vSaga orchestration engine: the fluent saga DSL (states, steps, compensation, timeouts, fan-out/join)
and the runtime that drives orchestrated and choreographed sagas.

## Install

```bash
dotnet add package VSaga.Core
dotnet add package VSaga.Persistence.InMemory   # or VSaga.Persistence.MongoDB / .Redis / .EFCore + .EFCore.Postgres
dotnet add package VSaga.Transport.InMemory     # or VSaga.Transport.RabbitMQ / .Wolverine / .MassTransit / .Brighter / .Http
```

## Usage

```csharp
public sealed class OrderApprovalSaga : OrchestratedSagaDefinition<OrderApprovalState>
{
    public State<OrderApprovalState> AwaitingApproval { get; }
    public State<OrderApprovalState> Approved { get; }

    public OrderApprovalSaga()
    {
        AwaitingApproval = InitialState(nameof(AwaitingApproval));
        Approved = State(nameof(Approved));

        During(AwaitingApproval)
            .When<SubmitOrder>()
                .Then((ctx, msg) => ctx.Saga.Amount = msg.Amount)
                .Publish((ctx, msg) => new OrderApproved(msg.OrderId))
                .TransitionTo(Approved)
                .Finalize(SagaStatus.Completed);
    }
}
```

```csharp
builder.Services.AddVSagaInMemoryPersistence();
builder.Services.AddVSagaInMemoryTransport();
builder.Services.AddVSagaEngine(o => o.AddSaga<OrderApprovalSaga, OrderApprovalState>());
```

## Docs

Full, runnable walkthrough: [docs/getting-started.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/getting-started.md).
Complete DSL reference: [docs/saga-dsl.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/saga-dsl.md).
Every option, including `SagaEngineBuilder.ConfigureOrchestrator`/`ConfigureOutbox`:
[docs/configuration.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/configuration.md).

## License

MIT
