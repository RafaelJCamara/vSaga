# Persistence sample: EF Core / SQL Server

`CheckoutSaga` on **`VSaga.Persistence.EFCore`** with **`VSaga.Persistence.EFCore.SqlServer`**. The
scenarios and output are described in the [persistence samples README](../README.md).

## Run it

From the repository root:

```bash
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.SqlServer/docker-compose.yml up -d --wait
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.SqlServer
```

| Setting | Value | Where |
| --- | --- | --- |
| SQL Server | `localhost:1436`, database `vsaga`, `sa` / `Vsaga_dev_only_1` | `docker-compose.yml` |
| Connection string | `ConnectionStrings:VSaga` | `appsettings.json` |

Port `1436` avoids a local SQL Server (`1433`) and the repository root's SQL Server overlay (`1435`).
The image needs about 2 GB of memory. The named volume keeps the sagas across `down`/`up`;
`docker compose ... down -v` starts clean.

## The provider-specific part

`Program.cs` marks every SQL Server-specific block with `[SqlServer]`. There are two, the same two as
the [Postgres sample](../VSaga.Samples.Persistence.EFCore.Postgres/README.md):

**1. Registration.** `AddVSagaEfCore` takes EF Core's own `DbContextOptionsBuilder`:

```csharp
builder.Services.AddVSagaEfCore(db => db.UseSqlServer(connectionString,
    sql => sql.MigrationsAssembly("VSaga.Persistence.EFCore.SqlServer")));
```

`MigrationsAssembly` is required: the migrations live in `VSaga.Persistence.EFCore.SqlServer`, not in
the `DbContext`'s own assembly.

**2. Migrations, before the host starts.** `MigrateAsync` creates the `vsaga` database if it is missing
and applies the migrations. Use it, never `EnsureCreatedAsync`, which bypasses `__EFMigrationsHistory`.

## Look at the data

The tables are `SagaInstances`, `SagaEventLog`, `SagaTimeouts`, `SagaOutboxMessages` and
`SagaConsumerRegistrations` (empty here: the samples don't record topology). Status columns hold the
enum's integer value. From this sample's directory:

```bash
cd dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.SqlServer
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P Vsaga_dev_only_1 -d vsaga -Q "SELECT TOP 3 BusinessKey, CurrentState, Status, Version FROM SagaInstances ORDER BY CreatedAtUtc DESC"
```

The business-key constraint is a filtered unique index:

```bash
docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P Vsaga_dev_only_1 -d vsaga -Q "SELECT name, filter_definition FROM sys.indexes WHERE name = 'IX_SagaInstances_SagaType_BusinessKey'"
```

## Before you run it in production

[`docs/persistence.md`](../../../../docs/persistence.md#ef-core--sql-server) covers the details. The
points that matter most:

- **The claim is concurrency-safe.** Timeouts and outbox rows are claimed with `READPAST`, so several
  replicas can poll one database.
- **String comparison follows the database collation.** The default is case-insensitive.
- **Change the `sa` password** and use a least-privilege login; this one is for the sample.
