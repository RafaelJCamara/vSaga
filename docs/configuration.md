# Configuration

This page covers vSaga's options classes. None of them participate in .NET's options-binding pipeline:
there is no `services.Configure<T>(...)` step anywhere in this library, and calling one yourself is a silent no-op (it registers an `IOptions<T>` nobody reads — every
options class below is resolved as a plain `T` singleton, with the one `IOptions<T>` exception noted
under [`Dashboard:ApiKey`](#dashboardapikey)). Every adapter's own options
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

One tunable:

| Property | Default | Meaning |
| --- | --- | --- |
| `MaxDeliveryAttempts` | `5` | How many times an **infrastructure-level** failure (a deserialize error, a persistence-store exception — distinct from a saga step's own thrown exception, which `HandleStepFailureAsync` already handles by marking the saga `Failed`) redelivers the same message, with an incremented `x-vsaga-delivery-attempt` header, before it is routed to the dead-letter queue instead of requeued forever. |

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
`502` — after it has already recorded a `ManualRetryRequested` timeline entry and, when the retry resets
state, moved the saga back to `Running`. `Http` has no broker in the middle at all, so there the
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

Two plain configuration keys, both read by `VSaga.Dashboard.Api` directly — neither is an options
class. Both are set as `Dashboard__ApiKey`/`Dashboard__WebOrigin` in `docker-compose.yml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Dashboard:ApiKey` | *(empty in `appsettings.json`)* | The one shared secret `ApiKeyAuthenticationHandler` checks. |
| `Dashboard:WebOrigin` | `http://localhost:4200` | The single browser origin the CORS policy admits. |

### `Dashboard:ApiKey`

See [`dashboard.md`](dashboard.md#authentication) for the full three-places-it-can-arrive model and why
the dashboard fails closed on an unconfigured key.

This key is also the one place in the codebase where an options class is resolved as `IOptions<T>`
rather than a plain singleton — but it isn't a vSaga options class. `AddScheme<TOptions, THandler>`
requires a distinct `AuthenticationSchemeOptions` subtype, so
`ApiKeyAuthenticationSchemeOptions` exists solely to satisfy that signature (it declares no members)
and is injected into the handler as `IOptionsMonitor<ApiKeyAuthenticationSchemeOptions>` by ASP.NET
Core's own machinery. It carries no vSaga settings: the handler reads `Dashboard:ApiKey` straight off
`IConfiguration`, per request.

### `Dashboard:WebOrigin`

The Angular SPA's origin, fed to a single-origin CORS policy (`WithOrigins(allowedOrigin)
.AllowAnyHeader().AllowAnyMethod().AllowCredentials()`). It is deliberately one exact origin, not a
wildcard: `AllowCredentials()` and `AllowAnyOrigin()` are mutually exclusive in ASP.NET Core, and the
dashboard needs credentials for the SignalR hub connection.

A wrong value is the most silent misconfiguration on this page. Nothing fails at startup, `/health`
stays green, and the API answers `curl` normally — only the browser refuses the responses, so the
dashboard shows as an empty or perpetually-loading page with CORS errors visible solely in the devtools
console. Match it exactly, scheme and port included (`http://localhost:4200`, not
`localhost:4200` or a trailing slash), to wherever the SPA is actually served from — `ng serve`'s own
port if you changed it. See [`dashboard.md`](dashboard.md#the-spa).

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
