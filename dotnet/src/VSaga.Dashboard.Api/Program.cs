using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Notifications;
using VSaga.Dashboard.Api;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Api.HealthChecks;
using VSaga.Dashboard.Api.Hubs;
using VSaga.Observability;
using VSaga.Persistence.EFCore;
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

const string CorsPolicy = "Dashboard";
var allowedOrigin = builder.Configuration["Dashboard:WebOrigin"] ?? "http://localhost:4200";
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Dashboard.Api deliberately never calls AddVSagaEngine/AddSaga<>() — it stays generic across any
// number of saga types by reading persisted data (ISagaSummaryReader/ISagaEventLogStore) rather than
// requiring its own copy of every saga definition. Retry works the same way: it redrives via a raw
// transport republish (see SagaEndpoints), not an in-process orchestrator call.
//
// Persistence:Provider, the same convention as Transport:Provider below — Postgres (EF Core) by default,
// matching every prior compose run, Redis when docker-compose.redis.yml says so. A greenfield choice, not
// a migration: flipping it on a running system points every store at an empty key space. The health
// check registered further down follows the same switch, under the provider-neutral name "persistence".
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
    default:
        throw new InvalidOperationException($"Unknown Persistence:Provider '{persistenceProvider}'.");
}

// Transport:Provider, same convention as the OrderProcessing sample's own switch
// (samples/VSaga.Samples.OrderProcessing/Program.cs) — RabbitMq by default (matching every prior compose
// run), Http when a docker-compose overlay says so (docker-compose.http.yml). This only ever needs to
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

builder.Services.AddSingleton<ISagaChangeNotifier, SignalRSagaChangeNotifier>();
builder.Services.AddHostedService<SagaChangePollingService>();

// "persistence" rather than the provider's name, so a compose health check, a dashboard or an alert
// reads the same key whichever store is behind it. The Redis check is the provider's own: it re-probes the
// server's configuration on every call and goes Unhealthy on any guarantee it cannot verify.
var healthChecks = builder.Services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq");
if (string.Equals(persistenceProvider, "Redis", StringComparison.Ordinal))
    healthChecks.AddCheck<RedisPersistenceHealthCheck>("persistence");
else
    healthChecks.AddCheck<PostgresHealthCheck>("persistence");

builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.SchemeName)
    .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.SchemeName, configureOptions: null);
builder.Services.AddAuthorization();

var app = builder.Build();

// Apply versioned EF Core migrations at startup. Non-fatal if Postgres isn't reachable yet — e.g.
// under WebApplicationFactory in tests, or if this container wins the startup race against the DB —
// the app still starts; DB-backed endpoints simply fail until the schema is migrated by this or
// another process. Skipped entirely when no DbContext is registered (Persistence:Provider=Redis, whose
// key space needs no migration; its bootstrapper writes a schema marker instead).
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

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors(CorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapSagaEndpoints();
app.MapHub<SagaHub>("/hubs/saga").RequireAuthorization();
// Left unauthenticated: infra probes (docker-compose healthcheck, orchestrators) hit this without a key.
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthResponseAsync });

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
