# Persistence sample: Redis

`CheckoutSaga` on **`VSaga.Persistence.Redis`**, built on core Redis data types and server-side Lua,
with no modules, so Valkey works too. The scenarios and output are described in the
[persistence samples README](../README.md).

## Run it

From the repository root:

```bash
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis/docker-compose.yml up -d --wait
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis
```

| Setting | Value | Where |
| --- | --- | --- |
| Redis | `localhost:6380`, Redis 7.4 at durability Tier A | `docker-compose.yml` |
| `Redis:ConnectionString` | `localhost:6380` (StackExchange.Redis format, not ADO.NET) | `appsettings.json` |
| `Redis:Namespace` | `samples`, so every key starts with `{vsaga:samples}:` | `appsettings.json` |

Port `6380` avoids both a local Redis (`6379`) and the reference stack's Redis overlay (`6479`). The
append-only file lives on a named volume, so the sagas survive `down`/`up`;
`docker compose ... down -v` starts clean.

## The provider-specific part

`Program.cs` marks every Redis-specific block with `[Redis]`. There are two.

**1. Registration**, bound from the `Redis` configuration section:

```csharp
builder.Services.AddVSagaRedis(o => builder.Configuration.GetSection("Redis").Bind(o));
```

The optional second parameter hands you StackExchange.Redis's `ConfigurationOptions` for TLS and
retries. The namespace is a key prefix, not a security boundary: a neighbour's `FLUSHALL` reaches
every namespace. The server must be dedicated to vSaga, never a shared cache.

**2. Waiting until the provider is ready.** `RedisPersistenceBootstrapper` runs in the background,
retrying until Redis is reachable. It verifies the server's configuration and writes the schema
marker. The sample waits for the provider's own health check before submitting anything:

```csharp
await PersistenceReadiness.WaitUntilHealthyAsync(
    host.Services.GetRequiredService<RedisPersistenceHealthCheck>(), TimeSpan.FromSeconds(60), CancellationToken.None);
```

It prints the check's description, which names the durability tier:

```
Persistence healthy: redis 7.4.11, durability tier A (appendfsync everysec, min-replicas-to-write 0, 0 replica(s)), 0.5 % of maxmemory used.
```

In a web host, register the check with `AddHealthChecks().AddCheck<RedisPersistenceHealthCheck>("persistence")`
and gate traffic on `/health` instead.

## The server configuration is checked, not assumed

`docker-compose.yml` starts Redis with the settings the provider requires, and the health check
verifies them on every probe:

| Setting | Value here | Why |
| --- | --- | --- |
| `appendonly` | `yes` | required; without the append-only file, a crash loses every write since the last RDB snapshot |
| `maxmemory-policy` | `noeviction` | required; any evicting policy silently deletes snapshots, timeouts and outbox rows |
| `maxmemory` | `256mb` | gives the provider's pre-write memory gate (`WriteMemoryThreshold`, 90 %) a ceiling to measure against |
| `appendfsync` | `everysec` | makes this **Tier A**: a hard kill can lose up to one second of acknowledged writes |

To see the check refuse a bad configuration, change one live and wait for the next probe (10 s by
default). The health check turns Unhealthy and names the setting; the next sample run times out
waiting for it with that same message:

```bash
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis/docker-compose.yml exec redis redis-cli CONFIG SET maxmemory-policy allkeys-lru
# ...and back:
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis/docker-compose.yml exec redis redis-cli CONFIG SET maxmemory-policy noeviction
```

Tier A suits development, CI and single-node deployments. **Tier B** (`appendfsync always`, plus at
least one replica with `min-replicas-to-write 1`) is the only configuration
[`docs/persistence.md`](../../../../docs/persistence.md#durability-policy) calls production-tier. Read
that section before choosing this provider: at Tier A, a redelivered message after a crash can run a
step twice.

## What to notice in the output

- **The count survives restarts.** Run the sample twice: the second run starts from the first run's
  three sagas.
- **Each saga's timeline starts at 1.** A timeline is a Redis list, and the sequence number is the
  position `RPUSH` returns.

## Look at the data

The commands below are for Bash and run from this sample's directory, where `docker compose` finds its
`docker-compose.yml`:

```bash
cd dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis
```

Every key the provider owns, grouped by kind (`saga:` snapshots, `log:` timelines, `bk:` business-key
reservations, `to:` timeouts, `ob:` outbox rows, `ix:` the summary indexes):

```bash
docker compose exec redis redis-cli --scan --pattern '{vsaga:samples}:*' | sort
```

One snapshot. It is a hash: the projected fields plus `dataJson`, the whole serialized `CheckoutState`.
Timestamps are integer Unix microseconds, and `status` is `SagaStatus` stored numerically:

```bash
KEY=$(docker compose exec -T redis redis-cli --scan --pattern '{vsaga:samples}:saga:*' | head -1 | tr -d '\r')
docker compose exec redis redis-cli HGETALL "$KEY"
```

That saga's timeline, one JSON element per entry:

```bash
docker compose exec redis redis-cli LRANGE "${KEY/:saga:/:log:}" 0 -1
```

## Before you run it in production

[`docs/persistence.md`](../../../../docs/persistence.md#redis) covers the capacity model. The dataset
is RAM-bound, at roughly 5–10 KB per completed saga, and nothing expires it. The same section covers
supported servers (Redis Cluster is reported Unhealthy) and the torn-write sentinel the health check
watches.
