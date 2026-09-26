using VSaga.Abstractions.Persistence;
using VSaga.Persistence.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB.Tests;

/// <summary>The registration resolves every contract from a scope without touching a server: the client connects on first use, not at resolution.</summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddVSagaMongoDb_RegistersEveryContract_ScopedOverOneUnitOfWork()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        MongoClientSettings? configured = null;

        services.AddVSagaMongoDb(o =>
        {
            o.ConnectionString = "mongodb://localhost:1/?replicaSet=rs0";
            o.DatabaseName = "di-test";
        }, settings => configured = settings);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<ConformanceSagaState>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaEventLogStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaTimeoutStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISagaOutboxStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IServiceTopologyStore>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<ISagaSummaryReader>(), scope.ServiceProvider.GetRequiredService<ISagaAdminStore>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<MongoSagaUnitOfWork>(), scope.ServiceProvider.GetRequiredService<MongoSagaUnitOfWork>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is MongoPersistenceBootstrapper);
        Assert.Equal("di-test", provider.GetRequiredService<MongoConnection>().DatabaseName);

        // The callback saw the provider's pinned settings on the client settings built from the connection
        // string, and nothing above opened a connection to the unreachable port -- the stores only hold handles.
        Assert.NotNull(configured);
        Assert.Equal(ReadPreference.Primary, configured.ReadPreference);
        Assert.Equal(WriteConcern.WMajority, configured.WriteConcern);
        Assert.Equal("localhost", configured.Servers.Single().Host);
    }
}
