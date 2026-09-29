# Persistence sample: EF Core / Postgres

`CheckoutSaga` on **`VSaga.Persistence.EFCore`** with **`VSaga.Persistence.EFCore.Postgres`**, the
reference provider. The scenarios and output are described in the
[persistence samples README](../README.md).

## Run it

From the repository root:

```bash
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.Postgres/docker-compose.yml up -d --wait
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.Postgres
```

| Setting | Value | Where |
| --- | --- | --- |
| Postgres | `localhost:5434`, database `vsaga`, `postgres`/`postgres` | `docker-compose.yml` |
| Connection string | `ConnectionStrings:VSaga` | `appsettings.json` |

Port `5434` avoids both a local Postgres (`5432`) and the reference stack's (`5433`). The named volume
keeps the sagas across `down`/`up`; `docker compose ... down -v` starts clean.

## The provider-specific part

`Program.cs` marks every Postgres-specific block with `[Postgres]`. There are two.

**1. Registration.** `AddVSagaEfCore` is provider-agnostic: it takes EF Core's own
`DbContextOptionsBuilder`, and the database hookup is yours to make.

```csharp
builder.Services.AddVSagaEfCore(db => db.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")));
```

`MigrationsAssembly` is required. The migrations live in `VSaga.Persistence.EFCore.Postgres`, not in
the `DbContext`'s own assembly. Without it, `MigrateAsync` reports "no migrations were applied" and
creates no tables.

**2. Migrations, before the host starts.** The engine's timeout and outbox dispatchers query their
tables as soon as they start, so the schema has to exist first:

```csharp
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VSagaDbContext>();
    await db.Database.MigrateAsync();
}
```

Use `MigrateAsync`, never `EnsureCreatedAsync`. `EnsureCreatedAsync` bypasses `__EFMigrationsHistory`,
and the next migration then fails against the tables it created.

**On the first run against a fresh database**, EF Core logs one
`fail: Microsoft.EntityFrameworkCore.Database.Command` for a `SELECT` from `__EFMigrationsHistory`.
That is EF probing for the history table before it creates it. The next line reports the eight
migrations applied, and later runs don't log it.

## What to notice in the output

- **The count survives restarts.** Run the sample twice: the second run starts from the first run's
  three sagas.
- **Timeline sequence numbers are store-wide.** They come from the `SagaEventLog.Id` identity column,
  so each order's timeline continues from the previous one's numbers.

## Look at the data

The five tables `VSagaDbContext` maps are `SagaInstances`, `SagaEventLog`, `SagaTimeouts`,
`SagaOutboxMessages` and `SagaConsumerRegistrations`. The last one stays empty here, because the
samples don't enable topology recording. Status columns hold the enum's integer value. The commands
below are for Bash and run from this sample's directory, where `docker compose` finds its
`docker-compose.yml`:

```bash
cd dotnet/samples/Persistence/VSaga.Samples.Persistence.EFCore.Postgres
```

The snapshots, where `Status` is `SagaStatus` (1 Completed, 2 Failed, 5 TimedOut) and `DataJson` holds
the whole serialized `CheckoutState`:

```bash
docker compose exec postgres psql -U postgres -d vsaga -c 'SELECT "BusinessKey", "CurrentState", "Status", "Version", "DataJson"::jsonb ->> '"'"'AuthorizationCode'"'"' AS auth FROM "SagaInstances" ORDER BY "CreatedAtUtc" DESC LIMIT 3;'
```

The business-key constraint, which is a partial unique index:

```bash
docker compose exec postgres psql -U postgres -d vsaga -c "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_SagaInstances_SagaType_BusinessKey';"
```

The timeouts (0 Pending, 1 Fired, 2 Cancelled) and the outbox rows (0 Pending, 1 Dispatched):

```bash
docker compose exec postgres psql -U postgres -d vsaga -c 'SELECT "ForState", "Status", "DueAtUtc" FROM "SagaTimeouts" ORDER BY "Id" DESC LIMIT 3;' -c 'SELECT "MessageTypeName", "Status" FROM "SagaOutboxMessages" ORDER BY "Id" DESC LIMIT 4;'
```

## Before you run it in production

[`docs/persistence.md`](../../../../docs/persistence.md#ef-core--postgres) covers the details. Two
points matter most:

- **The concurrency-safe claim is Postgres-only.** Timeouts and outbox rows are claimed with
  `FOR UPDATE SKIP LOCKED`, so several replicas can poll safely. Every other EF Core provider falls
  back to a load-then-update that is correct for exactly one dispatcher instance.
- **`DateTimeOffset` columns are stored as UTC `DateTime`**, truncated to microseconds. `DataJson`
  keeps full precision.
