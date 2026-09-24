# Configuration

This page covers the **.NET engine's** options classes specifically. None of them participate in the
framework's options-binding pipeline: there is no `services.Configure<T>(...)` step anywhere in this
library, and calling one yourself is a silent no-op (it registers an `IOptions<T>` nobody reads — every
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
once at startup — there is no reload-on-change, and the section name is whatever that call site passes
(the sample binds `BrighterOptions` and `WolverineTransportOptions` from the same `"RabbitMq"` section
`RabbitMqOptions` uses, and binds `HttpTransportOptions` from `"HttpSagas"`/`"HttpParticipants"`
depending on its `Role`). Config keys nest with `:` (or `__` in environment variables), so
`RabbitMq__ConnectionString` in a compose file reaches `RabbitMqOptions.ConnectionString`.

`SagaOrchestratorOptions`/`SagaOutboxOptions` are the two exceptions to the delegate convention —
`AddVSagaEngine` takes no options delegate of its own — configured instead via
`SagaEngineBuilder.ConfigureOrchestrator`/`ConfigureOutbox`, shown below. Defaults are shown as written
in source.

## TypeScript SDK options

The TypeScript SDK (`@vsaga/transport-rabbitmq`, `@vsaga/transport-http`, `@vsaga/participant`, ...) has
its own options interfaces, each documented in that package's own README rather than duplicated here —
see the package table in [`typescript-participants.md`](typescript-participants.md#packages). They mirror
the .NET shapes below closely but are not identical; the one most worth knowing about before you cross
runtimes:

| | .NET | TypeScript |
| --- | --- | --- |
| HTTP request timeout | `HttpTransportOptions.RequestTimeout` — a `TimeSpan`, default `30s` | `HttpTransportOptions.requestTimeoutMs` — a plain `number` of milliseconds, default `30_000` |

`@vsaga/transport-rabbitmq`'s options also add a `prefetchCount` knob (default `32`, matching the fixed
`BasicQosAsync` call `RabbitMqTransport` already makes) that `RabbitMqOptions` on the .NET side doesn't
expose as a setting at all.

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
which publishes get an outbox row in the first place.

| Property | Default | Meaning |
| --- | --- | --- |
| `Mode` | `SagaOutboxMode.Deferred` | `Deferred`: only `ctx.PublishAfterCommitAsync` calls get an outbox row (the crash-recovery backstop for the deferred-publish queue). `All`: additionally covers `ctx.PublishAsync`/`SendAsync`'s immediate publishes, by routing them through the same deferred queue `PublishAfterCommitAsync` uses — see the trade-off note below. |
| `PollInterval` | `5s` | How often the poller checks for `Pending` outbox rows. |
| `BatchSize` | `50` | Max rows claimed per poll. |
| `DispatchGracePeriod` | `30s` | A row younger than this is still within the window where the inline drain that wrote it is expected to mark it `Dispatched` itself; only a row older than this is treated as evidence of a crash between commit and drain, worth the poller republishing. |

**`Deferred` (the default) preserves today's inline publish semantics for every existing call site and
test** — `ctx.PublishAsync`/`SendAsync` still fire mid-step, immediately, exactly as before. `All` is a
deliberate trade-off, not a strict improvement: because `ctx.PublishAsync`/`SendAsync` fire mid-step
with no queuing under `Deferred`, the only way to route them through the outbox at all is to defer
them too — a row written beside a message that's already gone over the wire guarantees nothing. Under
`All`, a step that publishes and then throws no longer leaks that publish (the failure path discards
the deferred queue), but an operator choosing `All` is knowingly accepting
`ISagaContext.PublishAfterCommitAsync`'s own documented trade-off: a deferred publish that fails
post-commit has nowhere safe to go, and is caught, logged, and recorded as a `DeliveryExhausted`
timeline entry rather than retried or thrown.

## Transport options

Every `IMessageTransport` adapter has its own options class, registered by its own
`AddVSaga<Adapter>(...)` extension. `ConnectionString`/`ExchangeName` default identically across the
RabbitMQ-family adapters so switching providers is close to a drop-in config change.

### `Transport:Provider` — picking the adapter

Nothing in the library reads this key: it is a **host-level** convention, a `switch` in each host's own
`Program.cs` over `Configuration["Transport:Provider"] ?? "RabbitMq"` deciding which single
`AddVSaga<Adapter>` call runs. That's what each `docker-compose` overlay sets (`Transport__Provider:
"Wolverine"`, `"Brighter"`, `"MassTransit"`, `"Http"`). Your own host is free to use a different key,
or none.

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

The asymmetry is intentional, not an oversight. The dashboard only needs a working transport for
`/retry`'s type-erased `PublishRawAsync` redrive, and every RabbitMQ-family adapter (Wolverine,
MassTransit, Brighter) speaks the same broker over the same exchange as `RabbitMq` does — so running
the dashboard as `RabbitMq` against a saga host on any of those three is already the right pairing.
`Http` is the only track where the dashboard genuinely has to match, because there is no broker in the
middle. So `docker-compose.wolverine.yml`/`.brighter.yml`/`.masstransit.yml` set `Transport__Provider`
on `order-processing` only, while `docker-compose.http.yml` sets it on `dashboard-api` too.

### `RabbitMqOptions` (`VSaga.Transport.RabbitMQ`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ClientProvidedName` | `VSaga` |
| `ExchangeName` | `vsaga.saga.events` |
| `DeadLetterExchangeName` | `vsaga.dlx` |

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

`InboundPath` is symmetric, so change it on every process or none. A host that sets it only on its own
side still POSTs to the *default* path on its peers (or, if only the peer changed it, POSTs to a path
the peer no longer serves) — the result is 404s on delivery, not a startup error. `Endpoints` values
are therefore bare base URLs (`http://payments:8080`), never a full message-endpoint URL.

The in-memory transport (`VSaga.Transport.InMemory`, `AddVSagaInMemoryTransport()`) takes no options —
it's a single-process, dev/test-only provider with nothing to configure.

## Persistence

The persistence providers break the `Action<TOptions>` convention above: there is no `VSagaEfCoreOptions`
class, because EF Core already has one.
`AddVSagaEfCore(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDbContext)`
(`VSaga.Persistence.EFCore`) hands you EF Core's own `DbContextOptionsBuilder` instead, so the provider
hookup (`UseNpgsql`/`UseSqlServer`/`UseSqlite`/...) is yours to make — the package itself references
only `Microsoft.EntityFrameworkCore`, no specific provider. `AddVSagaInMemoryPersistence()`
(`VSaga.Persistence.InMemory`) takes no arguments at all. See [`persistence.md`](persistence.md) for
what each registers, and for the `MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")` requirement
— `UseNpgsql` alone silently applies no migrations — which is documented there rather than duplicated
here.

### `ConnectionStrings:VSaga`

Also not a library key: both shipped hosts read it themselves with the standard
`Configuration.GetConnectionString("VSaga")`, with an identical **hardcoded fallback** when it is
unset:

```
Host=localhost;Port=5432;Database=vsaga;Username=postgres;Password=postgres
```

The fallback is there so `dotnet run` against a local Postgres needs no configuration at all; it is a
development default, not a safe production one, and nothing warns when it is used. In containers it is
supplied as the environment form `ConnectionStrings__VSaga` — `docker-compose.yml` sets it on both
`dashboard-api` and `order-processing`, and `docker-compose.http.yml` sets it again on the extra
`order-processing-participants` service the HTTP track adds.

One tool reads the environment variable directly rather than through `IConfiguration`:
`dotnet/tools/BackfillStrandedTimeouts` (`Environment.GetEnvironmentVariable("ConnectionStrings__VSaga")`),
and its own fallback is deliberately **`Port=5433`**, not `5432` — it runs on the host against the
compose stack's published port mapping, not inside the compose network.

## `HttpCallOptions` (`VSaga.Http`)

Registered by `AddVSagaHttpCalls(...)` — required once per host before any saga's `.CallHttp`/
`ctx.CallHttpAsync` step runs (see [`saga-dsl.md`](saga-dsl.md#callhttp-from-vsagahttp)). Unrelated to
`VSaga.Transport.Http` above: this is the transport-agnostic outbound REST-call step, available on any
saga regardless of which `IMessageTransport` it uses.

| Property | Default | Meaning |
| --- | --- | --- |
| `Timeout` | `30s` | Per-call timeout for the outbound HTTP request, before `.WithRetry`'s own bounded retry (if configured) kicks in. |

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
app's OpenTelemetry pipeline, and registers the W3C trace-context propagator as the SDK default (the
wire format vSaga's own `traceparent`/`tracestate` handling uses, so a host that set a different
default propagator first — e.g. B3 — would otherwise silently disagree with what actually goes out on
the wire).

It never assumes an OTel collector is present — the dashboard reads the persisted event log instead of
an OTel backend, so nothing here is required for the dashboard to work. See
[`observability.md`](observability.md) for the one-line OTLP exporter wiring and the full span/metric
inventory.
