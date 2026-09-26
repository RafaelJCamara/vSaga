# History: the Redis persistence provider

> Describes the commit sequence that built `VSaga.Persistence.Redis` on 2026-09-26, following
> [`../adr/0002-redis-persistence-provider.md`](../adr/0002-redis-persistence-provider.md) and the plan in
> [`../design/redis-persistence.md`](../design/redis-persistence.md). See [`../persistence.md`](../persistence.md#redis)
> for the current reference documentation.

---

## What was built

A third persistence provider on core Redis data types plus server-side Lua, through `StackExchange.Redis`
3.0 — no module, so Valkey is a first-class target. One Lua script writes a snapshot, its staged outbox
rows and every summary index atomically; the timeout and outbox claims are one script and one round trip
each; the event log is one `RPUSH` whose reply is the sequence number; the business-key reservation is
`SET NX`. Configuration is verified rather than documented: a retrying bootstrapper and a health check
probe `appendonly`, `maxmemory-policy`, cluster mode, the role, memory pressure, the torn-write sentinel
and the schema marker on every tick, and report Unhealthy naming the broken guarantee — including "cannot
be verified" when `CONFIG GET` is disabled.

Both hosts gained a `Persistence:Provider` switch (`Postgres` default, `Redis`), the dashboard's health
check moved to the provider-neutral name `persistence`, and `docker-compose.redis.yml` runs the reference
stack against a Tier A Redis.

## How it was verified

**Conformance.** The whole `VSaga.Persistence.Conformance` suite — all nine abstract classes, including
the atomic-unit-of-work and racing-dispatcher suites, since the provider declares both capabilities —
runs against Redis 7.4 in a Testcontainer, one key-space namespace per case. The first run failed the
Status-sort arms in one direction: the sort's `Descending` flag had been derived for the `UpdatedAt` arms
(where the default descends whatever the flag says) and applied to the Status walk too, so
`SortBy = Status` ascended in reverse. The suite's `List_SortsByEachColumnInEitherDirection` caught it
before anything else did.

**Redis-specific cases**, in `VSaga.Persistence.Redis.Tests`: every write path with `SCRIPT FLUSH`
issued immediately before it (zero failures — the `EVALSHA` → `NOSCRIPT` → `EVAL` retry is the
provider's, not the client's); a persist aborted between the snapshot write and the index writes through
an internal hook, asserting the torn-write sentinel is set, the health check names the instance and the
live object keeps the version it expected; the memory gate refusing a persist before any write with the
server's `maxmemory` set live; three misconfigured containers (`allkeys-lru`, `appendonly no`, `CONFIG`
renamed away) each Unhealthy naming the guarantee or its unverifiability; a search over more members
than the bound throwing, and a narrowing filter bringing it back; and the change poller's shape over
2000 rows sharing one instant — each row exactly once across 20 pages, and the watermark the drain ends
on excluding them all on the next tick. 107 tests; the whole solution is 745 tests across 14 test projects,
all green.

**Live, under `docker-compose.redis.yml`.** The sample submitted orders continuously against Redis 7.4.11
for several minutes: 391 sagas across all seven of the sample's types by the end, sub-sagas completing
and their parents finishing on the child's report, `search=invoice` matching 36, timeouts firing
(9 `TimedOut`), no torn writes, no stranded outbox rows, and the dashboard's `/health` reporting
`redis 7.4.11, durability tier A (appendfsync everysec, min-replicas-to-write 0, 0 replica(s)), 1.8 % of
maxmemory used`.

Then the Stage 9 gate the plan would not let anyone assert: `docker kill -s KILL` on the Redis container
with 256 sagas stored, a 94-second outage, `docker start`. It booted unaided — `DB loaded from append only
file: 0.028 seconds`, no `redis-check-aof` — the health check went back to Healthy on its next probe, and
323 sagas were listed seconds later. During the outage the engine's infrastructure path redelivered
(`attempt n/5`) and 18 messages exhausted their five attempts inside the 94 seconds and were
dead-lettered, each with `Failed to record delivery-exhausted state … message is still being
dead-lettered` because the store that would have recorded the entry was the one that was down. That is
the engine's behaviour under any store outage longer than its redelivery budget, and it is now written
down beside the restart result rather than left for the next person to discover.

**Measured capacity**, by `MEMORY USAGE` over the live key space: a completed `OrderSaga` is a 1 556-byte
snapshot hash, a 7 288-byte 15-entry timeline and a 232-byte dedupe set; every provider key averaged over
206 sagas comes to about 5.5 KB per saga — roughly a quarter of the plan's 20–25 KB estimate, because the
sample's payloads are small. `persistence.md` publishes it as 5–10 KB per saga and 100 000–180 000 sagas
per gigabyte.

## Where the build left the plan

Recorded in the plan's §8.1: timestamps are stored once as Unix microseconds (not milliseconds plus exact
ticks), because one representation is what keeps the index order and the `UpdatedSince` predicate
consistent for the change poller's watermark; the claim and cancel scripts append a row id to an `ARGV`
prefix, the one place a script builds a key; the persist script has an outbox-only mode for the
conformance fixture; and the health check is registered by the host under `"persistence"` rather than
from inside `AddVSagaRedis`. The fault-injection tier landed as ordinary CI-run xUnit cases for
everything but `SIGKILL` and failover, which stay a documented manual gate.
