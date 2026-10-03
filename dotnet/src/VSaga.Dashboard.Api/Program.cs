using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Notifications;
using VSaga.Dashboard.Api;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Api.HealthChecks;
using VSaga.Dashboard.Api.Hosting;
using VSaga.Dashboard.Api.Hubs;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Services;
using VSaga.Observability;
using VSaga.Persistence.EFCore;
using VSaga.Persistence.MongoDB;
using VSaga.Persistence.Redis;
using VSaga.Transport.Http;
using VSaga.Transport.RabbitMQ;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Serialize enums (SagaKind/SagaStatus/SagaEntryType) as their string names, not raw ints — both over
// plain HTTP JSON and over the SignalR hub's payloads, so the dashboard doesn't need its own
// int<->name mapping table for every enum.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The edge (CORS and forwarded headers) is read and validated once, here, by DashboardEdge, the only
// reader of its keys. Both are opt-in: the bundled UI is same-origin, so with Dashboard:WebOrigin empty
// there is no policy, and with Dashboard:TrustedProxies empty no peer's forwarded headers are honoured.
var edge = DashboardEdge.Read(builder.Configuration);
builder.Services.AddDashboardEdge(edge);

// Dashboard.Api deliberately never calls AddVSagaEngine/AddSaga<>() — it stays generic across any
// number of saga types by reading persisted data (ISagaSummaryReader/ISagaEventLogStore) rather than
// requiring its own copy of every saga definition. Retry works the same way: it redrives via a raw
// transport republish (see SagaEndpoints), not an in-process orchestrator call.
//
// Persistence:Provider, the same convention as Transport:Provider below — Postgres (EF Core) by default,
// matching every prior compose run, Redis or MongoDb when that provider's compose overlay says so. A
// greenfield choice, not a migration: flipping it on a running system points every store at an empty
// key space or database. The health check registered further down follows the same switch, under the
// provider-neutral name "persistence".
var persistenceProvider = builder.Configuration["Persistence:Provider"] ?? "Postgres";
switch (persistenceProvider)
{
    case "Postgres":
        var connectionString = builder.Configuration.GetConnectionString("VSaga")
            ?? "Host=localhost;Port=5432;Database=vsaga;Username=postgres;Password=postgres";
        // Migrations live in VSaga.Persistence.EFCore.Postgres (not VSaga.Persistence.EFCore) so the latter
        // can stay free of any Npgsql-specific reference/generated code — it only depends on
        // Microsoft.EntityFrameworkCore, not any specific provider. MigrationsAssembly points EF Core at the
        // Postgres project's assembly instead of the DbContext's own.
        builder.Services.AddVSagaEfCore(db => db.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")));
        break;
    case "Redis":
        // Redis binds its own options section; its connection string is StackExchange.Redis's own format
        // (host:port,...), not an ADO.NET one, so it does not share ConnectionStrings:VSaga with Postgres.
        builder.Services.AddVSagaRedis(o => builder.Configuration.GetSection("Redis").Bind(o));
        break;
    case "MongoDb":
        // Its own section too: a mongodb:// URI cannot share ConnectionStrings:VSaga's ADO.NET format, and
        // handing the wrong string to the wrong driver would fail at connect time rather than at config time.
        builder.Services.AddVSagaMongoDb(o => builder.Configuration.GetSection("MongoDb").Bind(o));
        break;
    default:
        throw new InvalidOperationException($"Unknown Persistence:Provider '{persistenceProvider}'.");
}

// The dashboard's own users, teams, roles and Data Protection key ring, in a database of their own whatever
// Persistence:Provider says: SQLite, the only provider so far, behind IDashboardIdentityStore so another
// can be added. An unknown name fails composition like the switch above; an unusable database does not (see
// IdentityStartup below).
var identityProvider = builder.Configuration[DashboardIdentitySettings.ProviderKey] ?? DashboardIdentitySettings.SqliteProvider;
switch (identityProvider)
{
    case DashboardIdentitySettings.SqliteProvider:
        builder.Services.AddSqliteDashboardIdentity(DashboardIdentitySettings.ReadSqlite(builder.Configuration));
        break;
    default:
        throw new InvalidOperationException($"Unknown {DashboardIdentitySettings.ProviderKey} '{identityProvider}'.");
}

// Transport:Provider, same convention as the OrderProcessing sample's own switch
// (dotnet/samples/VSaga.Samples.OrderProcessing/Program.cs) — RabbitMq by default (matching every prior
// compose run), Http when a docker-compose overlay says so (docker-compose.http.yml). This only ever needs to
// match whichever adapter the saga host itself is running, so it doesn't grow a case per adapter the
// way the sample does: /retry's PublishRawAsync just needs a working IMessageTransport pointed at that
// host, not feature parity with every track. RabbitMqHealthCheck already degrades to "no message broker
// configured" when RabbitMqConnectionManager isn't registered (see its own null check), so making this
// conditional is what makes AddHealthChecks below stop being unconditionally RabbitMQ, with no change
// to the health check registration itself needed.
switch (builder.Configuration["Transport:Provider"] ?? "RabbitMq")
{
    case "RabbitMq":
        builder.Services.AddVSagaRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
        break;
    case "Http":
        // A single wildcard route ("*" in Routes, see ConfigHttpRouteTable) rather than one entry per
        // message type: this process only ever originates PublishRawAsync's raw, type-erased redrive
        // (SagaEndpoints.RetrySagaAsync), never a typed publish, so every possible message type resolves
        // to the same one place — the saga host — and there is no fixed universe of types to enumerate
        // the way the sample's own per-command routing has.
        builder.Services.AddVSagaHttp(o => builder.Configuration.GetSection("Http").Bind(o));
        break;
    default:
        throw new InvalidOperationException($"Unknown Transport:Provider '{builder.Configuration["Transport:Provider"]}'.");
}
builder.Services.AddVSagaOpenTelemetry();

// Dashboard:StateSnapshots:MaxBytes caps the StatePersisted entry a retry reset records; read and validated
// once, here, so a bad value fails composition rather than the first retry.
builder.Services.AddSingleton(DashboardStateSnapshotOptions.Read(builder.Configuration));
builder.Services.AddScoped<SagaResetSnapshotRecorder>();

// The saga list for callers scoped to named saga types: one stream per type, merged within bounds. The type
// names it may need are read at most every few seconds, shared across requests.
builder.Services.AddSingleton<SagaTypeNameCache>();
builder.Services.AddScoped<ScopedSagaLister>();

// The live hub connections, and the observer sign-out, password changes and access administration tell:
// it aborts the connections an access change affects, so they reconnect under the current access.
builder.Services.AddSingleton<HubConnectionRegistry>();
builder.Services.AddSingleton<IAccessChangeObserver>(provider => provider.GetRequiredService<HubConnectionRegistry>());

builder.Services.AddSingleton<ISagaChangeNotifier, SignalRSagaChangeNotifier>();
builder.Services.AddHostedService<SagaChangePollingService>();

// "persistence" rather than the provider's name, so a compose health check, a dashboard or an alert
// reads the same key whichever store is behind it. The Redis and MongoDB checks are the providers' own:
// each re-probes the server on every call and goes Unhealthy on any guarantee it cannot verify.
var healthChecks = builder.Services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq");
switch (persistenceProvider)
{
    case "Redis":
        healthChecks.AddCheck<RedisPersistenceHealthCheck>("persistence");
        break;
    case "MongoDb":
        healthChecks.AddCheck<MongoPersistenceHealthCheck>("persistence");
        break;
    default:
        healthChecks.AddCheck<PostgresHealthCheck>("persistence");
        break;
}

// Degraded, never Unhealthy: sign-in needs the identity store, the saga views and the saga host do not.
healthChecks.AddCheck<IdentityHealthCheck>("identity", failureStatus: HealthStatus.Degraded);

// A session cookie or the API key, through one policy scheme, and every endpoint protected unless it opts
// out; the security settings (sessions, the API key's role) are read and validated here, once.
builder.Services.AddDashboardAuth(builder.Configuration, edge);

var app = builder.Build();

// Apply versioned EF Core migrations at startup. Non-fatal if Postgres isn't reachable yet — e.g.
// under WebApplicationFactory in tests, or if this container wins the startup race against the DB —
// the app still starts; DB-backed endpoints simply fail until the schema is migrated by this or
// another process. Skipped entirely when no DbContext is registered (Persistence:Provider=Redis or
// MongoDb, which need no migration; each provider's bootstrapper writes its own schema marker instead).
using (var scope = app.Services.CreateScope())
{
    try
    {
        if (scope.ServiceProvider.GetService<VSagaDbContext>() is { } db)
            await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not apply VSaga migrations at startup; will retry lazily on first request.");
    }
}

// Create, migrate and fill the identity database. Never throws and gives up after 30 s: an unusable
// database (an unset path in a container, a read-only volume, a stale migration lock) leaves sign-in
// unavailable and the "identity" health check Degraded with the reason, and the check retries it.
await app.Services.GetRequiredService<IdentityStartup>().EnsureReadyAsync(app.Lifetime.ApplicationStopping);
await app.WarnAboutApiKeyAsync();

if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

app.UseDashboardEdge();
app.UseApiResponseHeaders();
app.UseHubOriginGuard();
app.UseDashboardAuth();

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapSagaEndpoints();
// The only endpoints exempt from antiforgery enforcement: a WebSocket upgrade cannot carry the header, so
// HubOriginGuard checks the Origin instead. A socket outlives the cookie check that opened it: it is closed
// when its ticket expires, and HubConnectionRegistry aborts it when the user's access changes.
app.MapHub<SagaHub>("/hubs/saga", options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization()
    .WithMetadata(AntiforgeryExemption.Hub);
// Left unauthenticated: infra probes (docker-compose healthcheck, orchestrators) hit this without a key.
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthResponseAsync }).AllowAnonymous();

await app.RunAsync();

// Preserves the endpoint's original { "status": "healthy" } response shape (extended with a per-check
// breakdown) instead of the health-checks middleware's default plain-text body.
static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";

    var payload = new
    {
        status = ToStatusString(report.Status),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = ToStatusString(e.Value.Status),
            description = e.Value.Description,
        }),
    };

    return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

static string ToStatusString(HealthStatus status) => status switch
{
    HealthStatus.Healthy => "healthy",
    HealthStatus.Degraded => "degraded",
    _ => "unhealthy",
};

/// <summary>Exposed so VSaga.Dashboard.Api.Tests can boot the app via WebApplicationFactory.</summary>
#pragma warning disable S1118 // required marker type for WebApplicationFactory<Program>, not a utility class
public partial class Program;
#pragma warning restore S1118
