# VSaga.Persistence.Redis

Redis persistence for vSaga: saga snapshot storage, the event log, timeouts and the transactional
outbox on core Redis data types plus server-side Lua. No module dependency, so anything that speaks
core Redis 7.0+ works, **Valkey included**. Built on `StackExchange.Redis` (MIT).

**Read the positioning before choosing it.** Redis is a durable single-node provider with a stated
loss window, not a Postgres peer: at its default configuration (`appendfsync everysec`) a hard kill
loses up to one second of acknowledged writes, which for vSaga means a redelivered message can be
reprocessed rather than deduped. It reaches production tier only under the Tier B configuration
(`appendfsync always` plus `min-replicas-to-write 1`). Redis Cluster is unsupported, and a shared or
evicting instance is refused by the provider's health check.

## Install

```bash
dotnet add package VSaga.Persistence.Redis
```

## Usage

```csharp
services.AddVSagaRedis(o =>
{
    o.ConnectionString = "redis:6379";
    o.Namespace = "orders";
});

services.AddHealthChecks().AddCheck<RedisPersistenceHealthCheck>("persistence");
```

The server must run `appendonly yes` and `maxmemory-policy noeviction`; the health check verifies both
on every call and reports Unhealthy, naming the guarantee, when either is off or cannot be checked.

## Docs

[docs/persistence.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/persistence.md) — the
durability tiers, the supported-servers table, the key space, the capacity model and the search bound.
[docs/adr/0002-redis-persistence-provider.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/adr/0002-redis-persistence-provider.md)
records why it is shaped this way.

## License

MIT. The package depends only on MIT `StackExchange.Redis`; the server's licence (RSALv2/SSPLv1/AGPLv3
for Redis, BSD-3 for Valkey) is the operator's choice, not this package's.
