using VSaga.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MongoDB persistence provider: one shared <see cref="MongoConnection"/>, a Scoped
    /// <see cref="MongoSagaUnitOfWork"/> (the outbox staging buffer, fresh per message/timeout/retry like
    /// EF Core's DbContext), MongoDB-backed implementations of all seven persistence contracts, the
    /// retrying <see cref="MongoPersistenceBootstrapper"/>, and <see cref="MongoPersistenceHealthCheck"/> for
    /// the host to add under <c>AddHealthChecks().AddCheck&lt;MongoPersistenceHealthCheck&gt;("persistence")</c>.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">The provider's own options: connection string, database, thresholds.</param>
    /// <param name="configureClient">
    /// The driver's <see cref="MongoClientSettings"/>, built from the connection string, for what the
    /// client already models (TLS material, pool sizing, timeouts, and the <c>ClusterConfigurator</c> hook
    /// that provider-level tracing needs, since the driver exposes no <c>ActivitySource</c> of its own) --
    /// the same precedent <c>AddVSagaEfCore</c> sets by exposing <c>DbContextOptionsBuilder</c>. Runs after
    /// the provider pins primary reads and majority writes, so either can be overridden there, knowingly.
    /// </param>
    /// <remarks>
    /// Registers with plain <c>AddScoped</c>, like <c>AddVSagaEfCore</c>: two provider registrations in
    /// one container resolve last-one-wins per interface, silently. Register exactly one provider.
    /// </remarks>
    public static IServiceCollection AddVSagaMongoDb(this IServiceCollection services, Action<VSagaMongoOptions> configure, Action<MongoClientSettings>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new VSagaMongoOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton(_ => new MongoConnection(options, configureClient));
        services.AddSingleton<MongoCollections>();
        services.AddSingleton<MongoPersistWrites>();
        services.AddSingleton<MongoServerProbe>();
        services.AddSingleton<MongoPersistenceHealthCheck>();
        services.AddHostedService<MongoPersistenceBootstrapper>();

        services.AddScoped<MongoSagaUnitOfWork>();
        services.AddScoped(typeof(ISagaSnapshotStore<>), typeof(MongoSagaSnapshotStore<>));
        services.AddScoped<MongoSagaSummaryReader>();
        services.AddScoped<ISagaSummaryReader>(sp => sp.GetRequiredService<MongoSagaSummaryReader>());
        services.AddScoped<ISagaAdminStore>(sp => sp.GetRequiredService<MongoSagaSummaryReader>());
        services.AddScoped<ISagaEventLogStore, MongoSagaEventLogStore>();
        services.AddScoped<ISagaTimeoutStore, MongoSagaTimeoutStore>();
        services.AddScoped<ISagaOutboxStore, MongoSagaOutboxStore>();
        services.AddScoped<IServiceTopologyStore, MongoServiceTopologyStore>();
        return services;
    }
}
