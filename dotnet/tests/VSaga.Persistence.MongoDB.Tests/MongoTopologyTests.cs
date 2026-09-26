using VSaga.Abstractions.Sagas;
using VSaga.Persistence.Conformance;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace VSaga.Persistence.MongoDB.Tests;

/// <summary>
/// The probe against what the provider must refuse: a standalone <c>mongod</c> (its own container, since
/// the shared fixture's is deliberately a replica set) and a connection string that explicitly contradicts
/// the pinned read preference or write concern. Every verdict names the prerequisite.
/// </summary>
public sealed class MongoTopologyTests
{
    /// <summary>Stage 6's gate: on a standalone mongod, DI resolves, the persist that needs a transaction fails, and health reports Unhealthy naming the replica-set prerequisite -- the host never crashes.</summary>
    [Fact]
    public async Task Probe_OnAStandaloneMongod_IsUnhealthyNamingTheReplicaSet_AndAStagedPersistFails()
    {
        await using var container = new MongoDbBuilder(MongoProviderFixture.Image).Build();
        await container.StartAsync();
        var options = new VSagaMongoOptions { ConnectionString = container.GetConnectionString(), DatabaseName = "standalone" };
        using var connection = new MongoConnection(options);
        var collections = new MongoCollections(connection);
        var probe = new MongoServerProbe(connection, collections, options);
        await new MongoPersistenceBootstrapper(collections, probe, options, NullLogger<MongoPersistenceBootstrapper>.Instance).EnsureLayoutAsync(CancellationToken.None);

        var report = await probe.ProbeAsync();

        var failure = Assert.Single(report.Failures);
        Assert.Contains("standalone mongod", failure, StringComparison.Ordinal);
        Assert.Contains("needs a replica set", failure, StringComparison.Ordinal);
        Assert.Equal("standalone", report.Topology);

        // A bare persist works on any topology; one with staged rows needs the transaction and fails
        // loudly -- there is no non-transactional escape hatch.
        var unitOfWork = new MongoSagaUnitOfWork();
        var writes = new MongoPersistWrites(collections);
        var store = new MongoSagaSnapshotStore<ConformanceSagaState>(collections, writes, unitOfWork);
        await store.InsertAsync(new ConformanceSagaState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "Started" });
        await new MongoSagaOutboxStore(collections, unitOfWork).EnqueueAsync("OrderSaga", Guid.NewGuid(), "m1", "Reserved", "{}"u8.ToArray(), null, new Dictionary<string, string>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
        var thrown = await Assert.ThrowsAsync<NotSupportedException>(() => store.InsertAsync(new ConformanceSagaState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "Started" }));
        Assert.Contains("transactions", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(unitOfWork.Staged);
    }

    /// <summary>Q3's decision: the provider overrides what the string says and reports the contradiction, naming the guarantee, rather than silently applying either side.</summary>
    [Theory]
    [InlineData("mongodb://localhost:27017/?readPreference=secondaryPreferred", "readPreference=SecondaryPreferred")]
    [InlineData("mongodb://localhost:27017/?w=1", "w=1")]
    [InlineData("mongodb://localhost:27017/?readConcernLevel=majority", "readConcernLevel=Majority")]
    public void Connection_WithAContradictingConnectionString_ReportsTheContradiction(string connectionString, string named)
    {
        using var connection = new MongoConnection(new VSagaMongoOptions { ConnectionString = connectionString });

        var contradiction = Assert.Single(connection.ConnectionStringContradictions);
        Assert.Contains(named, contradiction, StringComparison.Ordinal);
        Assert.Contains("overrides", contradiction, StringComparison.Ordinal);
        Assert.Equal(ReadPreference.Primary, connection.Database.Settings.ReadPreference);
        Assert.Equal(WriteConcern.WMajority, connection.Database.Settings.WriteConcern);
    }

    [Theory]
    [InlineData("mongodb://localhost:27017/?replicaSet=rs0")]
    [InlineData("mongodb://localhost:27017/?replicaSet=rs0&readPreference=primary&w=majority")]
    public void Connection_WithAnAgreeingConnectionString_ReportsNothing(string connectionString)
    {
        using var connection = new MongoConnection(new VSagaMongoOptions { ConnectionString = connectionString });

        Assert.Empty(connection.ConnectionStringContradictions);
    }

    [Fact]
    public void Connection_TakesTheDatabaseFromTheOptions_ThenTheUrl_ThenTheDefault()
    {
        using var fromOptions = new MongoConnection(new VSagaMongoOptions { ConnectionString = "mongodb://localhost:27017/fromurl", DatabaseName = "fromoptions" });
        using var fromUrl = new MongoConnection(new VSagaMongoOptions { ConnectionString = "mongodb://localhost:27017/fromurl" });
        using var fallback = new MongoConnection(new VSagaMongoOptions { ConnectionString = "mongodb://localhost:27017" });

        Assert.Equal("fromoptions", fromOptions.DatabaseName);
        Assert.Equal("fromurl", fromUrl.DatabaseName);
        Assert.Equal(MongoConnection.DefaultDatabaseName, fallback.DatabaseName);
    }
}
