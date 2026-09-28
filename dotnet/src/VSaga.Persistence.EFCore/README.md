# VSaga.Persistence.EFCore

EF Core persistence for vSaga: saga snapshot storage, the event log, timeouts and the transactional outbox,
backed by any relational EF Core database provider. Provider-agnostic on purpose — its only EF Core
dependencies are `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational`, not any
specific database. Pass the actual provider hookup (`UseNpgsql`, `UseSqlServer`, `UseSqlite`, ...)
yourself; see `VSaga.Persistence.EFCore.Postgres` for the Postgres-specific migrations package.

## Install

```bash
dotnet add package VSaga.Persistence.EFCore
```

## Usage

```csharp
services.AddVSagaEfCore(db => db.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")));
```

## Docs

[docs/persistence.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/persistence.md) — why
`MigrationsAssembly` matters, `MigrateAsync()` vs. `EnsureCreatedAsync()`, and the Postgres volume
caveat for `docker compose up`.

## License

MIT
