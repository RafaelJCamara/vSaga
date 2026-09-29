# Persistence sample: MongoDB

`CheckoutSaga` on **`VSaga.Persistence.MongoDB`**, built on the native `MongoDB.Driver`. The scenarios
and output are described in the [persistence samples README](../README.md).

## Run it

From the repository root:

```bash
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB/docker-compose.yml up -d --wait
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB
```

| Setting | Value | Where |
| --- | --- | --- |
| MongoDB | `localhost:27019`, single-member replica set `rs0` | `docker-compose.yml` |
| `MongoDb:ConnectionString` | `mongodb://localhost:27019/?directConnection=true` | `appsettings.json` |
| `MongoDb:DatabaseName` | `vsaga` | `appsettings.json` |

Port `27019` avoids both a local `mongod` (`27017`) and the reference stack's MongoDB overlay (`27018`).
The named volume keeps the sagas across `down`/`up`; `docker compose ... down -v` starts clean.

**Why `directConnection=true`:** the replica-set member advertises itself as `mongo:27017`, a name only
the compose network resolves. Connecting directly skips replica-set discovery, so the driver never
tries that name. Against a real replica set whose members' names resolve, use the usual
`?replicaSet=...` connection string.

## The provider-specific part

`Program.cs` marks every MongoDB-specific block with `[MongoDB]`. There are two.

**1. Registration**, bound from the `MongoDb` configuration section:

```csharp
builder.Services.AddVSagaMongoDb(o => builder.Configuration.GetSection("MongoDb").Bind(o));
```

The optional second parameter hands you the driver's `MongoClientSettings` for TLS, pool size and
timeouts. The provider pins primary reads, `local` read concern and `majority` writes itself. A
connection string that says otherwise is overridden, and the health check reports it as Unhealthy.

**2. Waiting until the provider is ready.** `MongoPersistenceBootstrapper` runs in the background. It
creates the provider's named indexes and writes the schema marker, retrying until the server is
reachable. The sample waits for the provider's own health check before submitting anything:

```csharp
await PersistenceReadiness.WaitUntilHealthyAsync(
    host.Services.GetRequiredService<MongoPersistenceHealthCheck>(), TimeSpan.FromSeconds(60), CancellationToken.None);
```

It prints the check's description:

```
Persistence healthy: MongoDB 8.0.32, replica set rs0 (primary), indexes in place, 0 stranded outbox row(s).
```

In a web host, register the check with `AddHealthChecks().AddCheck<MongoPersistenceHealthCheck>("persistence")`
and gate traffic on `/health` instead.

## Why the replica set is not optional

The saga engine runs with `SagaOutboxMode.All`, so every step that publishes stages outbox rows. The
snapshot persist then commits those rows and the snapshot together, in one **multi-document
transaction**, and MongoDB supports those only on a replica set. A single member is enough. On a
standalone `mongod`, the health check stays Unhealthy, naming the reason, and the first persist with
staged rows fails with `Standalone servers do not support transactions`. There is deliberately no
non-transactional fallback. See
[`docs/persistence.md`](../../../../docs/persistence.md#the-replica-set-is-a-hard-prerequisite).

## What to notice in the output

- **The count survives restarts.** Run the sample twice: the second run starts from the first run's
  three sagas.
- **Each saga's timeline starts at 1.** `seq` comes from a per-instance counter document in
  `sagaSequences`, not a global identity column.

## Look at the data

The commands below are for Bash and run from this sample's directory, where `docker compose` finds its
`docker-compose.yml`:

```bash
cd dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB
```

The collections, and the three newest snapshots. `status` is `SagaStatus` stored numerically (1 Completed,
2 Failed, 5 TimedOut); `dataJson`, left out here, holds the whole serialized `CheckoutState`:

```bash
docker compose exec mongo mongosh vsaga --quiet --eval 'printjson(db.getCollectionNames().sort()); printjson(db.sagaInstances.find({}, { _id: 0, businessKey: 1, currentState: 1, status: 1, version: 1 }).sort({ createdAtTicks: -1 }).limit(3).toArray())'
```

The indexes the health check requires, including `ux_sagaType_businessKey`, the unique index that
enforces one saga per business key:

```bash
docker compose exec mongo mongosh vsaga --quiet --eval 'printjson(db.sagaInstances.getIndexes().map(i => i.name))'
```

The outbox rows, all `status: 1` (Dispatched) once the run ends:

```bash
docker compose exec mongo mongosh vsaga --quiet --eval 'printjson(db.sagaOutboxMessages.find({}, { _id: 0, messageTypeName: 1, status: 1 }).toArray())'
```

## Before you run it in production

[`docs/persistence.md`](../../../../docs/persistence.md#mongodb) lists which servers are supported.
MongoDB 6.0 or later on a replica set is supported. Sharded clusters, primary-secondary-arbiter
topologies, Amazon DocumentDB and Azure Cosmos DB are not. The same page covers schema evolution,
since this provider has no migrations. Use one database per service: two services sharing a database
would share one outbox and one timeout schedule.
