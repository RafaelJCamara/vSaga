using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Transport;
using VSaga.Dashboard.Identity;
using VSaga.Persistence.EFCore;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using VSaga.Transport.RabbitMQ;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Boots the real Program.cs but swaps EF Core/Postgres and RabbitMQ for the in-memory persistence
/// and transport providers, so the endpoint tests exercise the actual HTTP pipeline, routing, and
/// SagaEndpoints logic (including the retry redrive/fallback logic) without needing Docker or a live
/// database. <see cref="HealthEndpointTests"/> separately verifies the real (untouched) Program.cs
/// composition root resolves correctly. The identity store is the real SQLite one, on a file of this
/// factory's own that is deleted with it. Test classes that own one dispose it through IAsyncLifetime:
/// xunit 2 never calls a test class's IAsyncDisposable, which left every such host running until the
/// process ended.
/// </summary>
public sealed class DashboardApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Configured below so authenticated endpoint tests have a key to send; auth fails closed otherwise.</summary>
    public const string TestApiKey = "test-api-key";

    private readonly TestIdentityDatabase _identity = new();

    public InMemoryMessageTransport Transport => Services.GetRequiredService<InMemoryMessageTransport>();

    /// <summary>
    /// This factory's identity database file, shared by every host derived from it with
    /// <c>WithWebHostBuilder</c> (each one migrates it idempotently, as a restarted API would).
    /// </summary>
    public string IdentityDatabasePath => _identity.FilePath;

    public override async ValueTask DisposeAsync()
    {
        // The base stops every host, derived ones included, before the file is deleted.
        // ConfigureAwait(false): xunit disposes a class fixture through the blocking Dispose(), which runs this.
        await base.DisposeAsync().ConfigureAwait(false);
        _identity.Delete();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting, not ConfigureAppConfiguration: Program.cs reads the identity path while composing.
        builder.UseSetting(DashboardIdentitySettings.SqlitePathKey, _identity.FilePath);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Dashboard:ApiKey"] = TestApiKey,
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<VSagaDbContext>>();
            services.RemoveAll<VSagaDbContext>();
            services.RemoveAll(typeof(ISagaSnapshotStore<>));
            services.RemoveAll<ISagaSummaryReader>();
            services.RemoveAll<ISagaEventLogStore>();
            services.RemoveAll<ISagaTimeoutStore>();
            services.RemoveAll<ISagaOutboxStore>();
            services.RemoveAll<ISagaAdminStore>();
            services.RemoveAll<IServiceTopologyStore>();
            services.RemoveAll<EfCoreSagaSummaryReader>();

            services.RemoveAll<RabbitMqOptions>();
            services.RemoveAll<RabbitMqConnectionManager>();
            services.RemoveAll<RabbitMqTransport>();
            services.RemoveAll<IRoutingKeyConvention>();
            services.RemoveAll<IMessageTransport>();

            services.AddVSagaInMemoryPersistence();
            services.AddVSagaInMemoryTransport();
        });
    }
}
