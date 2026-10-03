# Configuration

This page covers vSaga's options classes. None of them participate in .NET's options-binding pipeline:
there is no `services.Configure<T>(...)` step for any of them anywhere in this library, and calling one yourself is a silent no-op (it registers an `IOptions<T>` nobody reads — every
options class below is resolved as a plain `T` singleton). The one exception is the dashboard host's
use of ASP.NET Core's own framework-owned options (cookie authentication, antiforgery, forwarded headers,
key management and the API-key scheme's options): see
[Framework-owned options](#framework-owned-options-the-one-exception) under [Dashboard](#dashboard). Every adapter's own options
(`RabbitMqOptions`, `HttpTransportOptions`, ...) are set the same way: pass an `Action<TOptions>` to
that adapter's `AddVSaga*` extension, e.g. `AddVSagaRabbitMq(o => o.ConnectionString = "...")`.

**That is not the same as "these can't come from configuration."** Binding *inside* that delegate is
the intended and shipped pattern — `HttpTransportOptions`' own XML doc prescribes it, and it is how
both shipped hosts (`VSaga.Dashboard.Api`, the `OrderProcessing` sample) and every `docker-compose`
track configure themselves:

```csharp
services.AddVSagaRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
services.AddVSagaHttp(o => builder.Configuration.GetSection("Http").Bind(o));
```

Because the binding is an ordinary `Bind` call and not a registered `IOptions<T>` source, it happens
once at startup — there is no reload-on-change, and the section name is whatever that call site passes.
The sample binds `BrighterOptions` and `WolverineTransportOptions` from the same `"RabbitMq"` section
`RabbitMqOptions` uses, but `MassTransitOptions` from its own `"MassTransit"` section, so the
MassTransit track needs `MassTransit__ConnectionString` (which `docker-compose.masstransit.yml` sets) —
`RabbitMq__ConnectionString` alone leaves it on its `localhost` default. The sample binds
`HttpTransportOptions` from `"HttpSagas"`/`"HttpParticipants"` depending on its
[`Role`](#role--splitting-the-sample-in-two); the dashboard binds it from `"Http"`. Config keys nest
with `:` (or `__` in environment variables), so `RabbitMq__ConnectionString` in a compose file reaches
`RabbitMqOptions.ConnectionString`.

`SagaOrchestratorOptions`/`SagaOutboxOptions` are the two exceptions to the delegate convention —
`AddVSagaEngine` takes no options delegate of its own — configured instead via
`SagaEngineBuilder.ConfigureOrchestrator`/`ConfigureOutbox`, shown below. Defaults are shown as written
in source.

## `SagaOrchestratorOptions`

Registered by `AddVSagaEngine(...)` with library defaults; override with `ConfigureOrchestrator` inside
the same builder delegate:

```csharp
using VSaga.Core.Runtime; // SagaOrchestratorOptions

services.AddVSagaEngine(o => o
    .ConfigureOrchestrator(opt => opt.MaxDeliveryAttempts = 10)
    .AddSaga<OrderSaga, OrderSagaState>());
```

Five tunables. None is validated:

| Property | Default | Meaning |
| --- | --- | --- |
| `MaxDeliveryAttempts` | `5` | How many times an **infrastructure-level** failure (a deserialize error, a persistence-store exception — distinct from a saga step's own thrown exception, which `HandleStepFailureAsync` already handles by marking the saga `Failed`) redelivers the same message, with an incremented `x-vsaga-delivery-attempt` header, before it is routed to the dead-letter queue instead of requeued forever. |
| `RecordStateSnapshots` | `true` | Whether the engine appends a `StatePersisted` entry, carrying the state exactly as it was stored, after each committed step, step failure, timeout and delivery exhaustion (see [`observability.md`](observability.md#state-snapshots)). `false` records none, and the dashboard then shows no per-step data. |
| `MaxStateSnapshotBytes` | `262144` (256 KiB) | The largest state, in UTF-8 bytes of its JSON, recorded in full. A larger one is recorded as `{"$vsagaStateOmitted":true,"bytes":N,"limit":L}`. `0` records size-only markers, which keeps state out of the log while still showing where each step committed. |
| `MaxStateSnapshotBytesPerSaga` | `1048576` (1 MiB) | The per-instance budget for the snapshots one saga's timeline holds, since the engine reads them all back before every step. A snapshot after a successful step or a timeout that would take the recorded total past it becomes `{"$vsagaStateOmitted":true,"bytes":N,"budget":B}`; those after a step failure or a delivery exhaustion are still recorded in full (up to `MaxStateSnapshotBytes`). `0` or less means unlimited. |
| `StateSnapshotTimeout` | `00:00:05` | How long one snapshot append may take before it is abandoned and logged as a Warning, like any other failed append. The append sits between the commit and the step's deferred publishes and acknowledgement, so a stalled event-log write must not hold them back; 5 s sits well under the outbox's 30 s `DispatchGracePeriod`. |

The snapshot options interact with one persistence option: on MongoDB a `PayloadJson` above
[`MaxPayloadJsonBytes`](#vsagamongooptions-vsagapersistencemongodb) (12 MiB) is replaced by
`{"$vsagaPayloadOmitted":true,…}` whatever `MaxStateSnapshotBytes` says, so raising the snapshot cap
past it records that marker instead.

**The sample binds them from configuration.** The `OrderProcessing` sample is the one host that passes
an `Orchestrator` configuration section to `ConfigureOrchestrator`, so a compose file or the
environment can set any of the five:

```csharp
builder.Services.AddVSagaEngine(o => o
    .ConfigureOrchestrator(opt => builder.Configuration.GetSection("Orchestrator").Bind(opt))
    .AddSaga<OrderSaga, OrderSagaState>());
```

```
Orchestrator__RecordStateSnapshots=false
Orchestrator__MaxStateSnapshotBytes=64
Orchestrator__StateSnapshotTimeout=00:00:02
```

None of the compose files sets them, so the stacks run on the defaults.

## `SagaOutboxOptions`

Registered by `AddVSagaEngine(...)` with library defaults; override with `ConfigureOutbox` the same way:

```csharp
using VSaga.Core.Runtime; // SagaOutboxOptions, SagaOutboxMode

services.AddVSagaEngine(o => o
    .ConfigureOutbox(opt =>
    {
        opt.Mode = SagaOutboxMode.All;
        opt.PollInterval = TimeSpan.FromSeconds(2);
    })
    .AddSaga<OrderSaga, OrderSagaState>());
```

Governs the transactional outbox's crash-recovery poller (`SagaOutboxDispatcherHostedService`) and
which publishes get an outbox row in the first place. The timeout dispatcher's equivalent poll (5s, 50
rows) is fixed, with no options class — see [`concepts.md`](concepts.md#timeouts).

| Property | Default | Meaning |
| --- | --- | --- |
| `Mode` | `SagaOutboxMode.Deferred` | `Deferred`: of the `ctx` calls, only `ctx.PublishAfterCommitAsync` gets an outbox row (the crash-recovery backstop for the deferred-publish queue). `All`: additionally covers the immediate publishes of `ctx.PublishAsync`, `SendAsync`, `StartChildAsync` and `NotifyParentAsync`, by routing them through the same deferred queue `PublishAfterCommitAsync` uses — see the trade-off note below. In either mode, the engine's own `ChildSagaFinished` safety-net publish (sent on a child's behalf when a step throws or a timeout ends it) gets a row too. |
| `PollInterval` | `5s` | How often the poller checks for `Pending` outbox rows. |
| `BatchSize` | `50` | Max rows claimed per poll. |
| `DispatchGracePeriod` | `30s` | A row younger than this is still within the window where the inline drain that wrote it is expected to mark it `Dispatched` itself; only a row older than this is treated as evidence of a crash between commit and drain, worth the poller republishing. |

**`Deferred` (the default) preserves today's inline publish semantics for every existing call site and
test** — `ctx.PublishAsync`, `SendAsync`, `StartChildAsync` and `NotifyParentAsync` still fire
mid-step, immediately, exactly as before. `All` is a deliberate trade-off, not a strict improvement:
because those calls fire mid-step with no queuing under `Deferred`, the only way to route them through
the outbox at all is to defer them too — a row written beside a message that's already gone over the
wire guarantees nothing. Under `All`, a step that publishes and then throws no longer leaks that
publish (the failure path discards the deferred queue), but an operator choosing `All` is knowingly
accepting `ISagaContext.PublishAfterCommitAsync`'s own trade-off: a deferred publish that fails
post-commit cannot fail the step, which has already committed. It is caught, logged, and recorded as a
`DeliveryExhausted` timeline entry rather than thrown. Its outbox row stays `Pending`, so the recovery
poller republishes it once `DispatchGracePeriod` has passed — a single, at-most-once attempt, since the
claim marks the row `Dispatched` before it sends.

## Transport options

Every broker-backed or HTTP `IMessageTransport` adapter has its own options class, registered by its
own `AddVSaga<Adapter>(...)` extension; the in-memory transport, at the end of this section, has none.
`ConnectionString`/`ExchangeName` default identically across the RabbitMQ-family adapters so switching
providers is close to a drop-in config change.

### `Transport:Provider` — picking the adapter

Nothing in the library reads this key: it is a **host-level** convention, a `switch` in each host's own
`Program.cs` over `Configuration["Transport:Provider"] ?? "RabbitMq"` deciding which single
`AddVSaga<Adapter>` call runs. That's what each transport `docker-compose` overlay sets
(`Transport__Provider: "Wolverine"`, `"Brighter"`, `"MassTransit"`, `"Http"`). Your own host is free to
use a different key, or none.

The two shipped hosts deliberately accept **different** sets of values, and both `throw
InvalidOperationException` at startup on anything else — an unknown provider fails loudly rather than
silently falling back to RabbitMQ:

| Value | `VSaga.Samples.OrderProcessing` | `VSaga.Dashboard.Api` |
| --- | --- | --- |
| `RabbitMq` (default when unset) | yes | yes |
| `Http` | yes | yes |
| `Wolverine` | yes | **throws** |
| `MassTransit` | yes | **throws** |
| `Brighter` | yes | **throws** |

The asymmetry leaves a known gap. The dashboard only needs a working transport for `/retry`'s
type-erased `PublishRawAsync` redrive, but that redrive only lands if it matches the saga host's wire
format, and the RabbitMQ-family adapters do not all share one (see
[`transports/index.md`](transports/index.md#choosing-an-adapter)). Brighter binds the same
lower-kebab-case routing keys `RabbitMq` publishes, so a `RabbitMq` dashboard's redrive routes to a
Brighter saga host's queues. Wolverine and MassTransit bind the raw PascalCase type name and wrap each
message in their own envelope, so on those two tracks the redrive is unroutable and `/retry` answers
`502` — after it has already recorded a `ManualRetryRequested` timeline entry and reset the saga to the
step's from-state; the API then puts the state and status back as it found them (best effort, see
[Manual retry](dashboard.md#manual-retry)) and the 502 says whether it did (`restored`). `Http` has no broker in the middle at all, so there the
dashboard has to run it too: `docker-compose.http.yml` sets `Transport__Provider` on `dashboard-api` as
well, while `docker-compose.wolverine.yml`/`.brighter.yml`/`.masstransit.yml` set it on
`order-processing` only.

### `Role` — splitting the sample in two

A sample-only key: `VSaga.Samples.OrderProcessing` reads it, and neither the library nor the dashboard
does. Unset, empty or `All` runs the whole sample in one process. `Sagas` runs only the saga engine,
the `OrderSubmitter` front door and the sample's own `/loyalty/lookup` and `/payments/*` endpoints;
`Participants` runs only the participant services. Any other value throws `InvalidOperationException`
at startup. Only `docker-compose.http.yml` sets it: over the HTTP transport a single process resolves
every message to its own local subscribers and sends no message over HTTP at all, so that track runs the one
image as two containers, one per role. Under `Transport:Provider=Http` the role also picks the section
`HttpTransportOptions` binds from — `HttpParticipants` for `Participants`, `HttpSagas` otherwise.

### `RabbitMqOptions` (`VSaga.Transport.RabbitMQ`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ClientProvidedName` | `VSaga` |
| `ExchangeName` | `vsaga.saga.events` |
| `DeadLetterExchangeName` | `vsaga.dlx` |

The consumer prefetch is not a setting: `RabbitMqTransport` always calls
`BasicQosAsync(prefetchCount: 32)` (see [`transports/rabbitmq.md`](transports/rabbitmq.md)).

### `WolverineTransportOptions` (`VSaga.Transport.Wolverine`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ExchangeName` | `vsaga.saga.events` |

### `MassTransitOptions` (`VSaga.Transport.MassTransit`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ExchangeName` | `vsaga.saga.events` |

### `BrighterOptions` (`VSaga.Transport.Brighter`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ClientProvidedName` | `VSaga` |
| `ExchangeName` | `vsaga.saga.events` |

### `HttpTransportOptions` (`VSaga.Transport.Http`)

No broker at all — see [`transports/http.md`](transports/http.md) for the full model.

| Property | Default | Meaning |
| --- | --- | --- |
| `ServiceName` | `vsaga-http` | This process's own identity, for logging only — never stamped onto envelopes (that's `MessageEnvelope.From`'s job). |
| `Endpoints` | `{}` | Endpoint name → base URL, e.g. `{"payments": "http://payments:8080"}`. |
| `Routes` | `{}` | Message type name → endpoint names to POST to on publish. A `"*"` key is a wildcard fallback for any type with no explicit entry. |
| `RequestTimeout` | `30s` | Per-request timeout for the outbound HTTP call, including the participant's own processing time. |
| `InboundPath` | `/vsaga/messages` | Both halves of the wire convention: the path this service's own receive endpoint is mapped to by `MapVSagaHttp()`, **and** the path appended to every base URL in `Endpoints` when publishing outbound (`HttpMessageTransport.BuildRequestUri`). |

`InboundPath` is symmetric, so change it on every process or none. A host that changes it only on its
own side POSTs that new path to peers that still serve the default, and those peers keep POSTing the
default path the host no longer serves — the result is 404s on delivery in both directions, not a
startup error. `Endpoints` values are therefore bare base URLs (`http://payments:8080`), never a full
message-endpoint URL.

The in-memory transport (`VSaga.Transport.InMemory`, `AddVSagaInMemoryTransport()`) takes no options —
it's a single-process, dev/test-only provider with nothing to configure.

## Persistence

The EF Core provider breaks the `Action<TOptions>` convention above: there is no `VSagaEfCoreOptions`
class, because EF Core already has one.
`AddVSagaEfCore(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDbContext)`
(`VSaga.Persistence.EFCore`) hands you EF Core's own `DbContextOptionsBuilder` instead, so the provider
hookup (`UseNpgsql`/`UseSqlServer`/`UseSqlite`/...) is yours to make — the package's only EF Core
references are `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational`, no
specific provider (the `.Relational` reference rules out non-relational EF Core providers).
`AddVSagaInMemoryPersistence()` (`VSaga.Persistence.InMemory`) takes no arguments at all. See
[`persistence.md`](persistence.md) for what each registers, and for the
`MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")` requirement — `UseNpgsql` alone silently
applies no migrations — which is documented there rather than duplicated here.

### `Persistence:Provider` — picking the store

Not a library key: like `Transport:Provider`, both shipped hosts (`VSaga.Dashboard.Api` and the
OrderProcessing sample) read it themselves and switch on it. `Postgres` (the default, EF Core with
`UseNpgsql`), `Redis` or `MongoDb`. Both hosts must agree — the dashboard reads the store the saga host
writes — and `docker-compose.redis.yml` and `docker-compose.mongo.yml` are the two overlays that set it.
It is a greenfield choice, not a migration: flipping it on a running system points every store at an
empty key space or database, so in-flight sagas vanish, pending timeouts never fire and pending outbox
rows are never drained. The dashboard's `/health` reports the chosen store under the provider-neutral
name `persistence`.

### `VSagaRedisOptions` (`VSaga.Persistence.Redis`)

`AddVSagaRedis(Action<VSagaRedisOptions> configure, Action<ConfigurationOptions>? configureConnection = null)`.
Bound from the `Redis` section by both hosts. The second parameter follows the EF precedent above: it
hands you StackExchange.Redis's own `ConfigurationOptions`, parsed from `ConnectionString`, for
everything the client already models — TLS, `AbortOnConnectFail`, `ConnectRetry`, multiplexer sizing.
The provider sets `AbortOnConnectFail = false` (the host starts without Redis, as it does without
Postgres) and `AllowAdmin = true` (its configuration probe needs `INFO` and `CONFIG GET`) before your
callback runs, so either can be overridden there.

| Property | Default | Meaning |
| --- | --- | --- |
| `ConnectionString` | `localhost:6379` | StackExchange.Redis's own format (`host:port,password=...`), not an ADO.NET one — so it is a `Redis` section key, never `ConnectionStrings:VSaga`. |
| `Namespace` | `default` | Every key is prefixed `{vsaga:<Namespace>}:`. Braces are a Redis hash tag. A prefix is a naming convention, **not a security boundary**: a neighbour's `FLUSHALL` reaches every namespace, so a dedicated instance is the documented prerequisite. Must not contain braces. |
| `WriteMemoryThreshold` | `0.90` | The `used_memory`/`maxmemory` ratio above which persists are refused before any write (`RedisMemoryPressureException`, an infrastructure failure the engine redelivers). A Lua script has no rollback, so refusing loudly beats tearing a write set on `OOM`. Ignored when the server has no `maxmemory`. Read on the probe interval, not per write. |
| `MaxSearchScanMembers` | `100000` | The most index members a dashboard `Search` may scan before `ListAsync` throws `RedisSearchScanLimitExceededException` (HTTP 400) rather than silently truncating a page — see [`persistence.md`](persistence.md#redis). |
| `ProbeInterval` | `10s` | How often the bootstrapper re-probes `appendonly`, `maxmemory-policy`, cluster mode, memory pressure and the torn-write sentinel. Re-probed, not checked once, because a live `CONFIG SET` silently changes the guarantees. |

### `VSagaMongoOptions` (`VSaga.Persistence.MongoDB`)

`AddVSagaMongoDb(Action<VSagaMongoOptions> configure, Action<MongoClientSettings>? configureClient = null)`.
Bound from the `MongoDb` section by both hosts. The second parameter follows the same precedent: it
hands you the driver's own `MongoClientSettings`, built from `ConnectionString`, for everything the
client already models — TLS material, pool sizing, timeouts, and the `ClusterConfigurator` hook that
provider-level tracing needs (driver 3.6, which this repo resolves, exposes no `ActivitySource` of its
own; 3.7.0 and later, still inside the package's `[3.6.0,4.0.0)` range, add built-in tracing). The
provider sets `ReadPreference.Primary`, `ReadConcern.Local` and `WriteConcern.WMajority` on these
settings before your callback runs, and pins all three again on the database handle every collection is
opened from and on the persist transaction's own options — so changing them in the callback has no
effect on any read or write the provider makes. [`persistence.md`](persistence.md#mongodb) says what
each one protects.

| Property | Default | Meaning |
| --- | --- | --- |
| `ConnectionString` | `mongodb://localhost:27017/?replicaSet=rs0` | A MongoDB URI — a `MongoDb` section key, never `ConnectionStrings:VSaga`. A `readPreference`, `w` or `readConcernLevel` it states explicitly that differs from the pinned values is reported by the health check as Unhealthy, naming the guarantee it would have broken. From the host against a single-node Docker replica set, add `directConnection=true`. |
| `DatabaseName` | `null` | The database every collection lives in: the URI's own database path when set, else `vsaga`. One database per service — two services sharing one would share an outbox and a timeout schedule. |
| `MaxPayloadJsonBytes` | `12 MiB` | A `SagaLogEntry.PayloadJson` above this is replaced by a small JSON marker (`{"$vsagaPayloadOmitted":true,…}`) and logged as a warning. The engine records the full inbound body before the step runs and MongoDB caps a document at 16 MB; without the guard an oversized message would make its saga permanently unstartable. |
| `ProbeInterval` | `10s` | How often the bootstrapper re-runs index creation (idempotent) and the topology probe. |
| `StrandedOutboxThreshold` | `5m` | A Pending outbox row older than this counts as stranded in the health check's `strandedOutboxRows` data — reported, not failed on. |

### `ConnectionStrings:VSaga`

Also not a library key: both shipped hosts read it themselves with the standard
`Configuration.GetConnectionString("VSaga")`, with an identical **hardcoded fallback** when it is
unset:

```
Host=localhost;Port=5432;Database=vsaga;Username=postgres;Password=postgres
```

Both hosts' `appsettings.json` also set `ConnectionStrings:VSaga` to this same string, so `dotnet run`
against a local Postgres needs no configuration at all; the hardcoded fallback applies only if that entry
is removed. Either way it is a development default, not a safe production one, and nothing warns when it
is used. In containers it is supplied as the environment form `ConnectionStrings__VSaga` —
`docker-compose.yml` sets it on both `dashboard-api` and `order-processing`, and
`docker-compose.http.yml` sets it again on the extra `order-processing-participants` service the HTTP
track adds.

One tool reads the environment variable directly rather than through `IConfiguration`:
`dotnet/tools/BackfillStrandedTimeouts` (`Environment.GetEnvironmentVariable("ConnectionStrings__VSaga")`),
a one-off, Postgres-only maintenance app for the sample, not a library feature. It schedules an
immediately due timeout for every `OrderSaga` left `Running` in `AwaitingInventory`/`AwaitingShipment`
without one, and leaves the running engine's timeout dispatcher to unwind them (why:
[`history/backfill-stranded-sagas.md`](history/backfill-stranded-sagas.md)). Its own fallback is
deliberately **`Port=5433`**, not `5432` — it runs on the host against the compose stack's published
port mapping, not inside the compose network.

## `HttpCallOptions` (`VSaga.Http`)

Registered by `AddVSagaHttpCalls(...)` — required once per host before any saga's `.CallHttp`/
`ctx.CallHttpAsync` step runs (see [`saga-dsl.md`](saga-dsl.md#callhttp-from-vsagahttp)). Unrelated to
`VSaga.Transport.Http` above: this is the transport-agnostic outbound REST-call step, available on any
saga regardless of which `IMessageTransport` it uses.

| Property | Default | Meaning |
| --- | --- | --- |
| `Timeout` | `30s` | Per-call timeout for the outbound HTTP request, before `.WithRetry`'s own bounded retry (if configured) kicks in. |

The sample's two `.CallHttp` sagas take their targets from three sample-only keys, `Loyalty:LookupUrl`,
`Payments:AuthorizeUrl` and `Payments:VoidUrl`, which default to that same process's own endpoints at
`http://localhost:8080/loyalty/lookup`, `/payments/authorize` and `/payments/void`. The compose files
make that default true by setting `ASPNETCORE_URLS=http://+:8080`. A bare `dotnet run` of the sample
sets no URL (its `launchSettings.json` has none), so it listens on Kestrel's default
`http://localhost:5000` and those REST hops fail unless `ASPNETCORE_URLS` or the three keys are set to
match.

## `ChaosOptions` (`VSaga.Chaos`)

Registered by `AddVSagaChaos(...)`. See [`chaos.md`](chaos.md) for the full fault model; the shape:

```
ChaosOptions
  Delay:     Enabled, ApplyToOutbound, ApplyToInbound, Probability, MinDelay, MaxDelay
  Drop:      Enabled, ApplyToOutbound, ApplyToInbound, Probability
  Duplicate: Enabled, ApplyToOutbound, ApplyToInbound, Probability, ExtraDeliveries
```

| Fault | Defaults |
| --- | --- |
| `Delay` | `Enabled=false`, `ApplyToOutbound=true`, `ApplyToInbound=true`, `Probability=0.1`, `MinDelay=200ms`, `MaxDelay=2s` |
| `Drop` | `Enabled=false`, `ApplyToOutbound=true`, `ApplyToInbound=true`, `Probability=0.05` |
| `Duplicate` | `Enabled=false`, `ApplyToOutbound=true`, `ApplyToInbound=true`, `Probability=0.05`, `ExtraDeliveries=1` |

Each fault is independently gated by its own `Enabled` flag; a disabled fault is never registered into
the middleware pipeline at all — no runtime check, no cost.

### The `Chaos:Enabled` master switch is not a `ChaosOptions` member

Note what the shape above does **not** have: a top-level `Enabled`. `ChaosOptions` has exactly three
properties — `Delay`, `Drop`, `Duplicate`. The master switch is a **host-level** key, read separately
and before the options are bound at all:

```csharp
if (builder.Configuration.GetValue("Chaos:Enabled", defaultValue: false))
    builder.Services.AddVSagaChaos(o => builder.Configuration.GetSection("Chaos").Bind(o));
```

The gate is the `if`, not a flag inside the options — whole-package opt-in (nothing registered, so not
even the per-fault checks exist) sitting above the per-fault opt-ins. `Chaos:Enabled` lives beside the
fault settings in the same `"Chaos"` config section purely for operator convenience; the `Bind(o)` call
right next to it **silently ignores** that key, since `ChaosOptions` has no matching property. So
`docker-compose.chaos.yml`'s `Chaos__Enabled: "true"` works only because the sample's `Program.cs`
reads the key itself first. A host that copies the `AddVSagaChaos` line without the surrounding `if`
gets no chaos at all no matter what `Chaos__Enabled` says, and no error either.

## Dashboard

Every `Dashboard:*` key that `VSaga.Dashboard.Api` reads is in this table, with its default. They are plain
configuration keys (as an environment variable `:` is written `__`, so `Dashboard:Session:CookieName` is
`Dashboard__Session__CookieName`), not an options class bound from a section. The API reads each one once,
while it composes itself, and validates it there: a value outside the range given below stops the API from
starting with an `InvalidOperationException` that names the key and the value, instead of surfacing at the
first sign-in or the first retry, and a change needs a restart. The exceptions are `Dashboard:ApiKey`, which
the API-key handler reads from `IConfiguration` on each request, and two settings whose problems do not stop
the API: `Dashboard:Identity:Sqlite:Path` and the seed administrator (`Dashboard:Admin:Username` and
`Dashboard:Admin:Password`). A path or a seed that cannot be used leaves `identity` Degraded with the reason
and the rest of the API running (see their rows). `docker-compose.yml` sets `Dashboard__ApiKey`,
`Dashboard__ApiKeyRole`, `Dashboard__TrustedProxies`, the identity path, the seeded administrator and the
session cookie name; it leaves `Dashboard:WebOrigin` empty, because the bundled UI is served on the API's own
origin (see [`dashboard.md`](dashboard.md#the-spa)).

| Group | Key | Default | Meaning |
| --- | --- | --- | --- |
| Identity store | `Dashboard:Identity:Provider` | `Sqlite` | Which identity store the API composes. `Sqlite` is the only value; anything else stops the API at start. See [The identity store](dashboard.md#the-identity-store). |
| Identity store | `Dashboard:Identity:Sqlite:Path` | Outside a container `{LocalApplicationData}/vSaga/dashboard/identity.db`; **none in a container** | The identity database file, made absolute. When `DOTNET_RUNNING_IN_CONTAINER` is `true` (the official images set it) there is deliberately no default: an unset path does not stop the API but is reported by the `identity` health check (`Degraded`, sign-in unavailable), because a file in the container layer would lose every user and the key ring when the container is recreated. The dashboard API image and compose set `/var/lib/vsaga-dashboard/identity.db`, on a named volume. |
| First administrator | `Dashboard:Admin:Username` | unset | With `Dashboard:Admin:Password`: the administrator created at the first start against an empty identity store (never touched afterwards). While either is set, first-run setup is never offered. A seed that cannot be applied does not stop the API: `identity` is `Degraded` with the reason and setup stays closed. Compose seeds `admin`. See [The first administrator](dashboard.md#the-first-administrator). |
| First administrator | `Dashboard:Admin:Password` | unset | The seed administrator's password, which must meet the password policy; never logged. A password the policy rejects does not stop the API either: `identity` is `Degraded` naming this key (never its value) and setup stays closed. Compose seeds the public `dev-local-only-change-me`. |
| First administrator | `Dashboard:Admin:ResetOnStart` | `false` | `true` or `false`. When `true`, every start resets the seed user's password, re-enables and unlocks the account, ends its sessions and restores an Administrator grant for all saga types. Needs at least one of the two seed keys, or the API refuses to start. Set it back to `false` once you can sign in. |
| First administrator | `Dashboard:Setup:Code` | unset: a code is generated and logged at start when there is no user and no seed key | The one-time setup code, presetting the generated one: 16 to 128 characters not counting spaces and hyphens, never logged. |
| Sessions | `Dashboard:Session:CookieName` | `vsaga.session` | The session cookie's name: 1 to 128 letters, digits, dots, hyphens or underscores, not starting with `__`. Compose sets `vsaga.session.<compose project>`, so stacks side by side on `localhost` do not share a sign-in. |
| Sessions | `Dashboard:Session:IdleTimeoutMinutes` | `480` | The sliding idle window: a request made once more than half of it has passed since the session was issued renews the session for the whole window (so one idle for less than half never ends, and one idle for all of it always does). 1 to 10080. |
| Sessions | `Dashboard:Session:AbsoluteTimeoutHours` | `24` | A session ends this long after sign-in, however active it is; an open hub socket is closed by then too. 1 to 720. |
| Sessions | `Dashboard:Session:RequireHttps` | `false` | `true` or `false`. `true` makes the session and antiforgery cookies `Secure` always, adds the `__Host-` name prefix and sends HSTS. The API refuses to start with it unless `Dashboard:TrustedProxies` is set. |
| Passwords | `Dashboard:Password:MinLength` | `12` | The shortest password accepted, 8 to 128 (the longest password is always 128). |
| Passwords | `Dashboard:Lockout:MaxFailedAttempts` | `5` | Consecutive wrong passwords that lock an account: the count restarts when a lock begins, and on a successful sign-in or an unlock. `0` never locks; at most 100. |
| Passwords | `Dashboard:Lockout:Minutes` | `15` | How long a locked account refuses sign-in, 1 to 1440. |
| Rate limits | `Dashboard:RateLimit:AuthPerMinute` | `20` | Sign-in and password-change attempts per one-minute window for one client address and username; first-run setup gets the same number per address. 1 to 1000. |
| API key | `Dashboard:ApiKey` | *(empty in `appsettings.json`)* | The machine credential `ApiKeyAuthenticationHandler` checks. Empty fails closed: every request that presents a key is refused. See [`Dashboard:ApiKey`](#dashboardapikey). |
| API key | `Dashboard:ApiKeyRole` | `Viewer` | The built-in or custom role the key acts as, for every saga type, never with `access.manage`. At most 64 characters; a name that matches no role makes every request with the key `401` and logs a Warning at start. |
| Browser origin and proxies | `Dashboard:WebOrigin` | *(empty: CORS off)* | Optional. The one browser origin, other than the API's own, that a credentialed CORS policy admits, and that the hub's origin check accepts. Validated at startup. See [`Dashboard:WebOrigin`](#dashboardweborigin). |
| Browser origin and proxies | `Dashboard:TrustedProxies` | *(empty: forwarded headers ignored)* | Optional. Comma-separated addresses or CIDR networks whose `X-Forwarded-For`/`X-Forwarded-Proto` the API honours. Validated at startup. See [`Dashboard:TrustedProxies`](#dashboardtrustedproxies). |
| State snapshots | `Dashboard:StateSnapshots:MaxBytes` | `262144` (256 KiB) | Caps, in UTF-8 bytes, the `StatePersisted` entry the API records after a [manual retry](dashboard.md#manual-retry) resets a saga; a larger state is recorded as the size marker, `0` records size-only markers. The cap actually applied is the smaller of this and the `limit` of the saga's latest `$vsagaStateOmitted` marker, so a host that set [`MaxStateSnapshotBytes`](#sagaorchestratoroptions) lower is not overridden. Must be a non-negative whole number; anything else stops the API at startup. |

The engine side of state snapshots is not a `Dashboard:*` key: the host that runs the sagas records them,
configured by the `Orchestrator:*` keys the `OrderProcessing` sample binds (`RecordStateSnapshots`,
`MaxStateSnapshotBytes`, `MaxStateSnapshotBytesPerSaga`, `StateSnapshotTimeout`; see
[`SagaOrchestratorOptions`](#sagaorchestratoroptions)). The dashboard API runs no engine.

`Dashboard:WebOrigin` and `Dashboard:TrustedProxies` are read by `Hosting/DashboardEdge.cs`, the only code
that reads them, and the security and sign-in keys by `DashboardSecuritySettings`,
`FirstAdministratorSettings` and `DashboardIdentitySettings` in `VSaga.Dashboard.Identity`. How each setting
behaves is described in [`dashboard.md`](dashboard.md#authentication).

### Framework-owned options: the one exception

The dashboard host configures ASP.NET Core's **own** options classes in code. They are the only options on
this page that go through the framework's options pipeline, and they are not vSaga's. The ones the settings
reach: `CookieAuthenticationOptions` (the session cookie: its name, HttpOnly, `SameSite=Strict`, the `Secure`
policy, the idle timeout and sliding renewal), `AntiforgeryOptions` (the `X-XSRF-TOKEN` header, and the cookie
token's name, the session cookie's name plus `.af`), `ForwardedHeadersOptions` (only when
`Dashboard:TrustedProxies` is not empty), `CorsOptions` (the one credentialed policy, only when
`Dashboard:WebOrigin` is set), `HstsOptions` (only when `Dashboard:Session:RequireHttps` is `true`) and
`KeyManagementOptions` (the Data Protection key ring's repository, which is the identity store). The wiring
that no `Dashboard:*` key touches, for example `AuthenticationOptions` and `PolicySchemeOptions` (the policy
scheme that picks the cookie or the API key), `AuthorizationOptions` (every endpoint needs an authenticated
caller; the permission policies) and `ApiKeyAuthenticationSchemeOptions`, a marker type that declares no
members, required because `AddScheme<TOptions, THandler>` wants a distinct `AuthenticationSchemeOptions`
subtype. None is bound from a configuration section and no `Dashboard:*` key names
one of their properties: the code fills them from the values it read once, so the rule that vSaga's own settings
are plain singletons read once and validated at composition still holds.

### `Dashboard:ApiKey`

See [`dashboard.md`](dashboard.md#api-key) for the full model: where the key is accepted (`X-Api-Key`,
`Authorization: Bearer`, and `?access_token=` on the hub endpoints only), why the dashboard fails closed on an
unconfigured key, the role it acts as (`Dashboard:ApiKeyRole`, `Viewer` by default, never `access.manage`) and
why a retry made with it needs a role that holds `sagas.retry`. The key is for scripts and probes; the SPA signs
in with a username and password and has no key in its bundle.

Unlike the other `Dashboard:*` keys the handler reads the key straight off `IConfiguration`, on each request,
not once at composition, so there is nothing to validate at start; the API only logs a Warning at start when a
configured key is shorter than 24 characters. Its options type is one of the
[framework-owned options](#framework-owned-options-the-one-exception).

### `Dashboard:WebOrigin`

Not needed by the bundled UI. Both ways of serving it, the `dashboard-web` container and `ng serve`,
put the SPA and a proxy to the API on one origin, so the browser never makes a cross-origin call. See
[`dashboard.md`](dashboard.md#the-spa).

Set it only for a browser app served from a different origin that must call the API directly. The API
then registers a single-origin CORS policy, `WithOrigins(origin).AllowAnyHeader().AllowAnyMethod()
.AllowCredentials()`, and applies it before authentication. Empty (the default) registers no policy at
all, so a cross-origin browser call gets no CORS headers and the browser refuses the response. The hub's
origin check accepts the same origin, in addition to the API's own.

Treat it as **read-only cross-origin access**. The antiforgery check on every unsafe request assumes a
same-origin page that can read the `XSRF-TOKEN` cookie and echo it in `X-XSRF-TOKEN` (Angular's own XSRF
handling, which the bundled SPA relies on, skips cross-origin requests, and a page on another host cannot read
the cookie), so a request from that page that changes something is refused with `400` `antiforgery`. See
[Deploying beyond localhost](dashboard.md#deploying-beyond-localhost).

The value must be an origin: an absolute `http` or `https` URI with no user info, path, query or
fragment, such as `https://ops.example.com` or `http://localhost:3000`. A trailing slash is dropped,
and scheme and host are lower-cased and a default port removed, giving the form a browser sends in its
`Origin` header. Anything else (`localhost:4200`, `http://localhost:4200/app`, `ftp://x`) stops the API
at startup. There is no wildcard: `AllowCredentials()` and `AllowAnyOrigin()` are mutually exclusive in
ASP.NET Core.

The default is empty on purpose, not `http://localhost:4200` as it once was. Browsers scope cookies by
host, not port, and every compose stack's UI runs on its own `localhost` port, so a credentialed policy
for a fixed localhost port would admit whatever happens to run there (another stack's UI, any dev
server), not only this stack's UI.

### `Dashboard:TrustedProxies`

The peers whose forwarded headers the API believes. Behind a reverse proxy every browser request
arrives from the proxy's address, over the proxy's scheme; listing the proxy here makes the API take
the client's address from `X-Forwarded-For` and the scheme from `X-Forwarded-Proto`, so anything that
reads the request's remote address or scheme sees the client's. `Host` is not taken from a header: the
bundled proxies pass the browser's `Host` through unchanged.

- **Format.** A comma-separated list of IPv4 or IPv6 addresses (`10.0.0.5`) or CIDR networks
  (`10.0.0.0/8`); whitespace around entries is ignored. IPv4 must be written in canonical dotted-decimal
  form, a network must have no host bits set (`10.0.0.1/8` is refused rather than guessed), and scoped
  IPv6 addresses are refused. A malformed entry stops the API at startup and is named in the message.
- **Off by default, never "everyone".** Empty means forwarded headers are ignored from every peer, and
  there is no value that trusts all peers.
- **Only the last hop.** The API reads one entry, the right-most, which the trusted proxy itself
  appended; anything to its left was written by the client and could be forged. Behind two proxies in a
  row the address the API sees is therefore the outer proxy's.
- **Untrusted peers are reported.** When a request carries `X-Forwarded-For` or `X-Forwarded-Proto`
  from a peer that is not listed (including when the list is empty), the API ignores the headers and
  logs a Warning naming the peer and this key, at most once every 5 minutes per peer and at most 20
  times in 5 minutes overall, then one line saying further warnings are suppressed until the window
  ends. A proxy you forgot to list shows up there rather than failing silently.
- **How it is applied.** The parsed list feeds ASP.NET Core's own `ForwardedHeadersOptions`
  (`XForwardedFor | XForwardedProto`, `ForwardLimit = 1`, the default loopback entries cleared, the
  networks added to `KnownIPNetworks`) through `services.Configure` — one of the
  [framework-owned options](#framework-owned-options-the-one-exception), not a vSaga class — and only when
  the list is non-empty.

**Do not trust more than your proxy.** A listed peer can assert any client address and scheme, so a
client that can reach the API directly from inside a trusted range can spoof both. `docker-compose.yml`
sets `Dashboard__TrustedProxies` to the private ranges `10.0.0.0/8,172.16.0.0/12,192.168.0.0/16`,
because the `dashboard-web` container's address is assigned dynamically inside the compose network.
That is acceptable for the demo only because the dashboard ports are bound to `127.0.0.1`: on Docker
Desktop even a request from the host to `localhost:5080` arrives from the compose network's gateway,
which lies inside `172.16.0.0/12`, so any local process can set those headers. Narrow the list to your
proxy's address or network in a real deployment.

### The dashboard UI's container and dev server

Two environment variables configure the `dashboard-web` image; the image's entrypoint substitutes them
(and only them, plus the resolver it reads from the container) into the nginx configuration at
start. A third configures the dev server.

| Variable | Default | Where | Meaning |
| --- | --- | --- | --- |
| `DASHBOARD_API_UPSTREAM` | `dashboard-api:8080` | `dashboard-web` image | `host:port` that nginx proxies `/api/` and `/hubs/` to, resolved per request. The default is the compose service name, which each compose project resolves to its own API. |
| `DASHBOARD_OUTER_PROXY` | `false` | `dashboard-web` image | `true` when a TLS terminator in front of the container sets `X-Forwarded-Proto`: nginx then passes its `http` or `https` value to the API. Otherwise nginx sends its own scheme and a client cannot claim `https`. |
| `VSAGA_API_URL` | `http://localhost:5080` | `ng serve` (`dashboard-web/proxy.conf.mjs`) | The API the dev server proxies `/api` and `/hubs` to: an overlay's API port (5180 to 5680) or `http://localhost:5275` for `dotnet run`. |

## OpenTelemetry: `AddVSagaOpenTelemetry`

`VSaga.Observability.ServiceCollectionExtensions.AddVSagaOpenTelemetry(this IServiceCollection,
Action<TracerProviderBuilder>? configureTracing = null, Action<MeterProviderBuilder>?
configureMetrics = null)` wires vSaga's shared `ActivitySource`/`Meter` (`VSaga.Saga`, defined once in
`VSaga.Abstractions` so Core, Persistence, and Transport all emit against the same names) into the
app's OpenTelemetry pipeline, and sets the SDK's process-wide default propagator to a composite of the
W3C trace-context and baggage propagators (trace-context being the wire format vSaga's own
`traceparent`/`tracestate` handling uses, so a host that set a different default propagator first —
e.g. B3 — would otherwise silently disagree with what actually goes out on the wire).

It never assumes an OTel collector is present — the dashboard reads the persisted event log instead of
an OTel backend, so nothing here is required for the dashboard to work. See
[`observability.md`](observability.md) for the one-line OTLP exporter wiring, the baggage propagator's
process-wide side effect, and the full span/metric inventory.
