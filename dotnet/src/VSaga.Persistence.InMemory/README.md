# VSaga.Persistence.InMemory

In-memory saga persistence for vSaga — snapshot storage and event log with no database, for local
development and tests. Backs every store contract with a single shared `InMemorySagaStore` singleton.
**Not for production use**: state does not survive a process restart.

## Install

```bash
dotnet add package VSaga.Persistence.InMemory
```

## Usage

```csharp
services.AddVSagaInMemoryPersistence();
```

## Docs

[docs/persistence.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/persistence.md) covers this
alongside the EF Core/Postgres, MongoDB and Redis providers and when to reach for each.

## License

MIT
