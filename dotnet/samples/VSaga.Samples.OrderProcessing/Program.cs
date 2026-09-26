using VSaga.Chaos;
using VSaga.Core;
using VSaga.Http;
using VSaga.Observability;
using VSaga.Persistence.EFCore;
using VSaga.Persistence.Redis;
using VSaga.Samples.OrderProcessing;
using VSaga.Samples.OrderProcessing.Participants;
using VSaga.Transport.RabbitMQ;
using VSaga.Transport.MassTransit;
using VSaga.Transport.Brighter;
using VSaga.Transport.Http;
using VSaga.Transport.Wolverine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Persistence:Provider, the same convention as Transport:Provider below and the same switch
// Dashboard.Api's Program.cs has — Postgres (EF Core) by default, Redis when docker-compose.redis.yml
// says so. Both hosts must agree: the dashboard reads the store this process writes.
var persistenceProvider = builder.Configuration["Persistence:Provider"] ?? "Postgres";
switch (persistenceProvider)
{
    case "Postgres":
        var connectionString = builder.Configuration.GetConnectionString("VSaga")
            ?? "Host=localhost;Port=5432;Database=vsaga;Username=postgres;Password=postgres";
        builder.Services.AddVSagaEfCore(db => db.UseNpgsql(connectionString));
        break;
    case "Redis":
        builder.Services.AddVSagaRedis(o => builder.Configuration.GetSection("Redis").Bind(o));
        break;
    default:
        throw new InvalidOperationException($"Unknown Persistence:Provider '{persistenceProvider}'.");
}

// Opt-in only (default off, see appsettings.json/docker-compose.chaos.yml): registration order
// relative to AddVSagaRabbitMq below doesn't matter — MiddlewarePipelineTransport only resolves
// registered IOutboundMessageMiddleware/IInboundMessageMiddleware lazily, once IMessageTransport is
// first requested, by which point every AddXyz call here has already run.
if (builder.Configuration.GetValue("Chaos:Enabled", defaultValue: false))
    builder.Services.AddVSagaChaos(o => builder.Configuration.GetSection("Chaos").Bind(o));

// Because local subscribers count as routes (docs/design/http-based-sagas.md §3.3a), a single process over
// the HTTP transport would resolve every message locally and perform zero HTTP — the compose run would
// pass while exercising nothing. Role splits the one image into two processes instead: Sagas runs the
// engine (and the order-submitting "front door"), Participants runs the downstream services, and
// docker-compose.http.yml is the only track that ever sets this. Every other track leaves it unset, so
// RabbitMQ/Wolverine/MassTransit/Brighter runs stay bit-for-bit what they are today.
var role = ParseRole(builder.Configuration["Role"]);

// Transport:Provider picks which IMessageTransport adapter this sample runs against — RabbitMQ by
// default (matching every prior compose run), or one of the other adapters via a docker-compose
// overlay (e.g. docker-compose.wolverine.yml sets Transport__Provider=Wolverine) for that adapter's
// own live-verification pass. Each adapter's own ServiceCollectionExtensions wraps its transport in
// the same MiddlewarePipelineTransport as RabbitMQ, so chaos/topology-recording work unchanged.
switch (builder.Configuration["Transport:Provider"] ?? "RabbitMq")
{
    case "RabbitMq":
        builder.Services.AddVSagaRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
        break;
    case "Wolverine":
        builder.Services.AddVSagaWolverine(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
        break;
    case "MassTransit":
        builder.Services.AddVSagaMassTransit(o => builder.Configuration.GetSection("MassTransit").Bind(o));
        break;
    case "Brighter":
        // Binds from the same "RabbitMq" section RabbitMqOptions uses — BrighterOptions mirrors
        // RabbitMqOptions' shape closely enough that this is the least-surprising way to keep the two
        // adapters config-swappable; docker-compose.brighter.yml only overrides Transport:Provider.
        builder.Services.AddVSagaBrighter(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
        break;
    case "Http":
        // No shared section to bind from the way the RabbitMQ-backed adapters share "RabbitMq" —
        // HttpTransportOptions' Endpoints/Routes are inherently role-specific (each role talks to the
        // other), so docker-compose.http.yml populates "HttpSagas"/"HttpParticipants" as two distinct
        // config sections (via environment variables — there's no meaningful standalone default for
        // either, so neither lives in appsettings.json).
        builder.Services.AddVSagaHttp(o => builder.Configuration.GetSection(
            role == ServiceRole.Participants ? "HttpParticipants" : "HttpSagas").Bind(o));
        break;
    default:
        throw new InvalidOperationException($"Unknown Transport:Provider '{builder.Configuration["Transport:Provider"]}'.");
}
// Wraps the transport so every SubscribeAsync call (the saga engine's and the participants' alike)
// records which service consumes which message type — the only way the saga map can name a
// destination that never actually replied (e.g. the simulated hung payment gateway). The participants
// role needs this exactly as much as Sagas does — over HTTP, skipping it here means every participant
// node in the map degrades to "unresolved:{MessageType}".
builder.Services.AddVSagaTopologyRecording();
builder.Services.AddVSagaOpenTelemetry();

// The HttpClient LoyaltyLookupSaga's .CallHttp(...) call resolves via ISagaContext.Services -- unrelated
// to whichever IMessageTransport is active above (docs/design/http-based-sagas.md §1: the two HTTP halves share
// nothing but the name), so this is registered unconditionally rather than gated on Transport:Provider.
builder.Services.AddVSagaHttpCalls();

if (role != ServiceRole.Participants)
{
    // Both saga kinds in one engine. They deliberately share a correlation id per order — OrderSaga
    // drives the order to shipment, PostShipmentChoreography tracks the independent fan-out that follows
    // — which is only expressible because a saga instance is keyed by (SagaType, CorrelationId). Both
    // receive their own copy of OrderShipped: the RabbitMQ transport binds one queue per subscription to
    // a topic exchange, so a published message reaches every subscriber of that type rather than one.
    //
    // InvoiceDeliverySaga and InvoiceArchivalSaga are the odd ones out: they do NOT share that correlation
    // id. Each is a sub-saga started with ctx.StartChildAsync — by PostShipmentChoreography and
    // InvoiceFollowUpSaga respectively — so each gets a fresh id of its own plus a stored pointer back to
    // the instance that started it. Registering them here is all the wiring there is — the parent publishes
    // DeliverInvoice/ArchiveInvoice and the child's CanInitiate matches it; neither type references the
    // other. InvoiceFollowUpSaga itself DOES share the order's correlation id, same as
    // PostShipmentChoreography: both react to InvoiceIssued, so both open under whatever correlation id
    // that message already carries.
    builder.Services.AddVSagaEngine(o => o
        .AddSaga<OrderSaga, OrderSagaState>()
        .AddSaga<PostShipmentChoreography, PostShipmentState>()
        .AddSaga<InvoiceDeliverySaga, InvoiceDeliveryState>()
        .AddSaga<InvoiceFollowUpSaga, InvoiceFollowUpState>()
        .AddSaga<InvoiceArchivalSaga, InvoiceArchivalState>()
        // Live-verification vehicle for .CallHttp (docs/design/http-based-sagas.md §5) -- see
        // LoyaltyLookupSaga's own doc comment. A second, independent subscriber of LoyaltyPointsAwarded,
        // same ordinary fan-out PostShipmentChoreography's own subscription already relies on.
        .AddSaga<LoyaltyLookupSaga, LoyaltyLookupSagaState>()
        // Live-verification vehicle for mixed sagas (docs/design/mixed-sagas.md) -- drives a broker participant
        // (StockService) and a REST participant (this process's own /payments endpoints) side by side,
        // under OrderSubmitter's own fresh correlation id, so it never shares an instance with OrderSaga.
        .AddSaga<MixedFulfilmentSaga, MixedFulfilmentSagaState>());

    builder.Services.AddHostedService<OrderSubmitter>();
}

if (role != ServiceRole.Sagas)
{
    builder.Services.AddHostedService<InventoryParticipant>();
    builder.Services.AddHostedService<PaymentParticipant>();
    builder.Services.AddHostedService<ShippingParticipant>();
    builder.Services.AddHostedService<StockParticipant>();

    // The choreographed leg's participants: no conductor commands these, they each react to OrderShipped.
    //
    // Notification is the one that can be handed to another runtime. docker-compose.node.yml sets this
    // to false and starts typescript/samples/notification-participant in its place — same queue, same
    // message types, same replies, different language. Leaving this one registered alongside the Node
    // service would not fail loudly: both would bind the same queue and the broker would round-robin
    // deliveries between them, so roughly half the notifications would still be handled in .NET and the
    // cross-runtime run would prove much less than it appears to.
    if (builder.Configuration.GetValue("Participants:NotificationInProcess", defaultValue: true))
        builder.Services.AddHostedService<NotificationParticipant>();

    builder.Services.AddHostedService<LoyaltyParticipant>();
    builder.Services.AddHostedService<InvoicingParticipant>();
}

var app = builder.Build();

// Only meaningful for the HTTP transport, and only registered when it's the active provider — mapping
// it unconditionally would be harmless (nothing would ever call it) but would misstate what this
// process actually speaks on every other track.
if (string.Equals(builder.Configuration["Transport:Provider"], "Http", StringComparison.Ordinal))
    app.MapVSagaHttp();

// An ordinary REST API with no vSaga awareness at all -- the live-verification target for .CallHttp
// (docs/design/http-based-sagas.md §5), called by LoyaltyLookupSaga over a real HTTP round trip regardless of
// which IMessageTransport is active. Mapped only where that saga's engine actually runs (never
// Participants), matching where its own .CallHttp call executes.
if (role != ServiceRole.Participants)
{
    app.MapPost("/loyalty/lookup", (LoyaltyLookupRequest request) =>
    {
        // Simulated flaky gateway, same spirit as the RabbitMQ participants' own occasional declines/
        // failures, so a live pass exercises both of LoyaltyLookupSaga's result-shape branches for real.
        if (Random.Shared.NextDouble() < 0.15)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var tier = request.Points switch
        {
            >= 200 => "Gold",
            >= 100 => "Silver",
            _ => "Bronze",
        };
        return Results.Ok(new { tier });
    });

    // The live-verification target for MixedFulfilmentSaga's compensating REST hop (docs/design/mixed-sagas.md
    // §7) -- an ordinary REST API with no vSaga awareness, same spirit as /loyalty/lookup above.
    app.MapPost("/payments/authorize", (AuthorizePaymentRequest request, ILogger<Program> paymentsLogger) =>
    {
        // A simulated decline rate distinct from the simulated-failure rate, so both of the saga's
        // 402/.OnFailure branches fire live -- same spirit as PaymentParticipant's own decline/hang split.
        if (Random.Shared.NextDouble() < 0.15)
            return Results.StatusCode(StatusCodes.Status402PaymentRequired);
        if (Random.Shared.NextDouble() < 0.1)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        paymentsLogger.LogInformation("Order {OrderId}: payment authorized for {Amount:C}", request.OrderId, request.Amount);
        var authorizationId = Guid.NewGuid().ToString("N");
        return Results.Ok(new { authorizationId });
    });

    app.MapPost("/payments/void", (VoidPaymentRequest request, ILogger<Program> paymentsLogger) =>
    {
        // An occasional failure so both the .OnFailure arm and the Voiding backstop timeout fire live,
        // not only the happy-path confirmation.
        if (Random.Shared.NextDouble() < 0.15)
            return Results.StatusCode(StatusCodes.Status500InternalServerError);

        paymentsLogger.LogInformation("Authorization {AuthorizationId}: voided", request.AuthorizationId);
        var voidedAt = DateTimeOffset.UtcNow;
        return Results.Ok(new { voidedAt });
    });
}

// Schema creation is dashboard-api's job (versioned `dotnet ef` migrations against
// VSaga.Persistence.EFCore.Postgres) — docker-compose.yml gates this service on dashboard-api's
// health check so its migration always runs first. This service used to also call
// EnsureCreatedAsync() as a standalone-convenience fallback, but that bypasses
// __EFMigrationsHistory: run against a database it bootstrapped, a later `dotnet ef migrations add`
// migration fails with "relation already exists". Removed rather than kept as a landmine.
await app.RunAsync();

static ServiceRole ParseRole(string? value) => value switch
{
    null or "" or "All" => ServiceRole.All,
    "Sagas" => ServiceRole.Sagas,
    "Participants" => ServiceRole.Participants,
    _ => throw new InvalidOperationException($"Unknown Role '{value}'."),
};

namespace VSaga.Samples.OrderProcessing
{
    /// <summary>All (default) runs the whole sample in one process, exactly as every non-HTTP track does. Sagas/Participants split it in two — see the Role comment above.</summary>
    internal enum ServiceRole
    {
        All,
        Sagas,
        Participants,
    }

    /// <summary>Request body for the plain /loyalty/lookup REST endpoint — see its own mapping comment in this file.</summary>
    internal sealed record LoyaltyLookupRequest(string OrderId, int Points);

    /// <summary>Request body for the plain /payments/authorize REST endpoint — see its own mapping comment in this file.</summary>
    internal sealed record AuthorizePaymentRequest(string OrderId, decimal Amount);

    /// <summary>Request body for the plain /payments/void REST endpoint — see its own mapping comment in this file.</summary>
    internal sealed record VoidPaymentRequest(string AuthorizationId);
}
