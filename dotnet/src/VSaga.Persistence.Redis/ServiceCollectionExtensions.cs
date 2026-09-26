using VSaga.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis persistence provider: one shared <see cref="RedisConnection"/>, a Scoped
    /// <see cref="RedisSagaUnitOfWork"/> (the outbox staging buffer, fresh per message/timeout/retry like
    /// EF Core's DbContext), Redis-backed implementations of all seven persistence contracts, the
    /// retrying <see cref="RedisPersistenceBootstrapper"/>, and <see cref="RedisPersistenceHealthCheck"/> for
    /// the host to add under <c>AddHealthChecks().AddCheck&lt;RedisPersistenceHealthCheck&gt;("persistence")</c>.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">The provider's own options: connection string, namespace, thresholds.</param>
    /// <param name="configureConnection">
    /// StackExchange.Redis's <see cref="ConfigurationOptions"/>, parsed from the connection string, for
    /// what the client already models (TLS, <c>AbortOnConnectFail</c>, retry counts, multiplexer sizing)
    /// -- the same precedent <c>AddVSagaEfCore</c> sets by exposing <c>DbContextOptionsBuilder</c>.
    /// </param>
    /// <remarks>
    /// Registers with plain <c>AddScoped</c>, like <c>AddVSagaEfCore</c>: two provider registrations in
    /// one container resolve last-one-wins per interface, silently. Register exactly one provider.
    /// </remarks>
    public static IServiceCollection AddVSagaRedis(this IServiceCollection services, Action<VSagaRedisOptions> configure, Action<ConfigurationOptions>? configureConnection = null)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new VSagaRedisOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton(new RedisKeySpace(options.Namespace));
        services.AddSingleton(_ => new RedisConnection(options, configureConnection));
        services.AddSingleton<RedisServerProbe>();
        services.AddSingleton<RedisPersistScripts>();
        services.AddSingleton<RedisPersistenceHealthCheck>();
        services.AddHostedService<RedisPersistenceBootstrapper>();

        services.AddScoped<RedisSagaUnitOfWork>();
        services.AddScoped(typeof(ISagaSnapshotStore<>), typeof(RedisSagaSnapshotStore<>));
        services.AddScoped<RedisSagaSummaryReader>();
        services.AddScoped<ISagaSummaryReader>(sp => sp.GetRequiredService<RedisSagaSummaryReader>());
        services.AddScoped<ISagaAdminStore>(sp => sp.GetRequiredService<RedisSagaSummaryReader>());
        services.AddScoped<ISagaEventLogStore, RedisSagaEventLogStore>();
        services.AddScoped<ISagaTimeoutStore, RedisSagaTimeoutStore>();
        services.AddScoped<ISagaOutboxStore, RedisSagaOutboxStore>();
        services.AddScoped<IServiceTopologyStore, RedisServiceTopologyStore>();
        return services;
    }
}
