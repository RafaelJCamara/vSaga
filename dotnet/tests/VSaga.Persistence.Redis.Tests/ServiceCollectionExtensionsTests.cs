using VSaga.Abstractions.Persistence;
using VSaga.Persistence.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis.Tests;

/// <summary>The registration resolves every contract from a scope without touching a server: the connection is opened on first use, not at resolution.</summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddVSagaRedis_RegistersEveryContract_ScopedOverOneUnitOfWork()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        ConfigurationOptions? configured = null;

        services.AddVSagaRedis(o =>
        {
            o.ConnectionString = "localhost:1";
            o.Namespace = "di-test";
        }, connection => configured = connection);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<ConformanceSagaState>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaEventLogStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaTimeoutStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaOutboxStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IServiceTopologyStore>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<ISagaSummaryReader>(), scope.ServiceProvider.GetRequiredService<ISagaAdminStore>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<RedisSagaUnitOfWork>(), scope.ServiceProvider.GetRequiredService<RedisSagaUnitOfWork>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is RedisPersistenceBootstrapper);
        Assert.Equal("{vsaga:di-test}:", provider.GetRequiredService<RedisKeySpace>().Prefix);

        // The callback saw the provider's defaults on the options parsed from the connection string, and
        // nothing above opened a connection to the unreachable port -- the stores only hold the connection.
        Assert.NotNull(configured);
        Assert.False(configured.AbortOnConnectFail);
        Assert.True(configured.AllowAdmin);
        Assert.Equal("localhost", ((System.Net.DnsEndPoint)configured.EndPoints[0]).Host);
    }

    [Fact]
    public void AddVSagaRedis_RejectsANamespaceWithBraces()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddVSagaRedis(o => o.Namespace = "{bad}"));
    }
}
