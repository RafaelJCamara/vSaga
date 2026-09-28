# VSaga.Persistence.MongoDB

MongoDB persistence for vSaga: saga snapshot storage, the event log, timeouts and the transactional
outbox on the native `MongoDB.Driver` (Apache-2.0). A snapshot and the outbox rows staged with it
commit together in one short multi-document transaction (a snapshot with nothing staged, the common
case, is a bare single-document write); the event log's appends commit on their own, at majority write
concern, because the engine's redelivery dedupe rests on them.

**A replica set is a hard prerequisite** — a single-member set is enough — and there is no
non-transactional escape hatch: an outbox row that commits without its snapshot would be republished
after a crash for a transition that never happened. The provider's health check reports Unhealthy,
naming the prerequisite, on a standalone `mongod` or a `mongos`, until its indexes exist, and when the
connection string explicitly asks for a read preference, read concern or write concern the provider
overrides.

## Install

```bash
dotnet add package VSaga.Persistence.MongoDB
```

## Usage

```csharp
services.AddVSagaMongoDb(o =>
{
    o.ConnectionString = "mongodb://mongo:27017/?replicaSet=rs0";
    o.DatabaseName = "orders";   // one database per service
});

services.AddHealthChecks().AddCheck<MongoPersistenceHealthCheck>("persistence");
```

The second parameter of `AddVSagaMongoDb` hands you the driver's own `MongoClientSettings` for TLS,
pool sizing, timeouts and the `ClusterConfigurator` hook.

## Docs

[docs/persistence.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/persistence.md) — the
replica-set prerequisite, the write-concern table, the collections and indexes, the supported-servers
table and the `sagaSequences` lifecycle rule.
[docs/adr/0001-mongodb-persistence-provider.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/adr/0001-mongodb-persistence-provider.md)
records why it is shaped this way.

## License

MIT. The package depends only on the Apache-2.0 `MongoDB.Driver`; the server's licence (SSPL for
MongoDB Community, commercial for Enterprise and Atlas) is the operator's choice, not this package's.
