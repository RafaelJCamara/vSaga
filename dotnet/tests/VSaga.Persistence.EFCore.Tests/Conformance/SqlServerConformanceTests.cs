using Microsoft.Data.SqlClient;
using VSaga.Persistence.Conformance;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

// The shared conformance suite (VSaga.Persistence.Conformance), run against the EF Core provider on a real
// SQL Server. One collection, so every class shares the fixture's single container.

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerSnapshotStoreConformanceTests(SqlServerProviderFixture fixture)
    : SnapshotStoreConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerEventLogStoreConformanceTests(SqlServerProviderFixture fixture)
    : EventLogStoreConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerTimeoutStoreConformanceTests(SqlServerProviderFixture fixture)
    : TimeoutStoreConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerOutboxStoreConformanceTests(SqlServerProviderFixture fixture)
    : OutboxStoreConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerSummaryReaderConformanceTests(SqlServerProviderFixture fixture)
    : SummaryReaderConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerAdminStoreConformanceTests(SqlServerProviderFixture fixture)
    : AdminStoreConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerServiceTopologyStoreConformanceTests(SqlServerProviderFixture fixture)
    : ServiceTopologyStoreConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerAtomicUnitOfWorkConformanceTests(SqlServerProviderFixture fixture)
    : AtomicUnitOfWorkConformanceTests(fixture);

[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerConcurrentClaimConformanceTests(SqlServerProviderFixture fixture)
    : ConcurrentClaimConformanceTests(fixture);

/// <summary>Pins what <see cref="SqlServerProviderFixture"/> declares, so a flag the suite tailors its assertions by only ever changes in a reviewed diff.</summary>
[Collection(SqlServerConformanceGroup.Name)]
public sealed class SqlServerProviderCapabilityTests(SqlServerProviderFixture fixture)
{
    [Fact]
    public void DeclaresTheReviewedCapabilities()
    {
        Assert.True(fixture.SupportsAtomicUnitOfWork);
        Assert.True(fixture.SupportsConcurrentClaim);
        Assert.Equal(TimeSpan.FromTicks(1), fixture.TimestampResolution);
    }

    /// <summary>
    /// A run under <c>VSAGA_SQLSERVER_COLLATION</c> is only evidence if the databases really carry that
    /// collation; this fails a run whose variable was ignored. Without the variable it passes on whatever the server defaults to.
    /// </summary>
    [Fact]
    public async Task CreatesItsDatabasesUnderTheRequestedCollation()
    {
        var requested = Environment.GetEnvironmentVariable(SqlServerProviderFixture.CollationVariable);
        var database = await fixture.CreateMigratedDatabaseAsync();
        try
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))", connection);
            var actual = (string?)await command.ExecuteScalarAsync();

            Assert.False(string.IsNullOrEmpty(actual));
            if (!string.IsNullOrWhiteSpace(requested))
                Assert.Equal(requested, actual, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await database.Release();
        }
    }

    [Fact]
    public void DerivesEverySuiteItsCapabilitiesCallFor() =>
        ConformanceCoverage.AssertEverySuiteIsDerived(fixture, typeof(SqlServerProviderCapabilityTests).Assembly);
}
