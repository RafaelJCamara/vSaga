# VSaga.Persistence.EFCore.SqlServer

SQL Server (and Azure SQL) provider wiring for `VSaga.Persistence.EFCore` — the migrations for running
vSaga sagas against SQL Server, plus the `Microsoft.EntityFrameworkCore.SqlServer` dependency that supplies
`UseSqlServer`. The connection setup itself is yours, as in Usage below. Kept as a separate package from
`VSaga.Persistence.EFCore` specifically so that package stays provider-agnostic.

Timeouts and outbox rows are claimed atomically (`READPAST`), so several replicas of a service can share one
database. Requires SQL Server 2016 or later.

## Install

```bash
dotnet add package VSaga.Persistence.EFCore.SqlServer
```

## Usage

```csharp
services.AddVSagaEfCore(db => db.UseSqlServer(connectionString,
    sql => sql.MigrationsAssembly("VSaga.Persistence.EFCore.SqlServer")));

// at startup:
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<VSagaDbContext>();
await db.Database.MigrateAsync();
```

## Docs

[docs/persistence.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/persistence.md#ef-core--sql-server)
has the `MigrationsAssembly` requirement, the claim, the collation note and the key-length limit.
[ADR 0009](https://github.com/RafaelJCamara/vSaga/blob/main/docs/adr/0009-sql-server-persistence-provider.md)
records why it is built this way.

## License

MIT
