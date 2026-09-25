using VSaga.Persistence.Conformance;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

// The shared conformance suite (VSaga.Persistence.Conformance), run against the EF Core provider on SQLite.

public sealed class SqliteSnapshotStoreConformanceTests(SqliteProviderFixture fixture)
    : SnapshotStoreConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

public sealed class SqliteEventLogStoreConformanceTests(SqliteProviderFixture fixture)
    : EventLogStoreConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

public sealed class SqliteTimeoutStoreConformanceTests(SqliteProviderFixture fixture)
    : TimeoutStoreConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

public sealed class SqliteOutboxStoreConformanceTests(SqliteProviderFixture fixture)
    : OutboxStoreConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

public sealed class SqliteSummaryReaderConformanceTests(SqliteProviderFixture fixture)
    : SummaryReaderConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

public sealed class SqliteAdminStoreConformanceTests(SqliteProviderFixture fixture)
    : AdminStoreConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

public sealed class SqliteServiceTopologyStoreConformanceTests(SqliteProviderFixture fixture)
    : ServiceTopologyStoreConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

public sealed class SqliteAtomicUnitOfWorkConformanceTests(SqliteProviderFixture fixture)
    : AtomicUnitOfWorkConformanceTests(fixture), IClassFixture<SqliteProviderFixture>;

// No ConcurrentClaimConformanceTests: SQLite takes the single-dispatcher claim fallback (ADR 0004).

/// <summary>Pins what <see cref="SqliteProviderFixture"/> declares, so a flag the suite tailors its assertions by only ever changes in a reviewed diff.</summary>
public sealed class SqliteProviderCapabilityTests(SqliteProviderFixture fixture) : IClassFixture<SqliteProviderFixture>
{
    [Fact]
    public void DeclaresTheReviewedCapabilities()
    {
        Assert.True(fixture.SupportsAtomicUnitOfWork);
        Assert.False(fixture.SupportsConcurrentClaim);
        Assert.Equal(TimeSpan.FromTicks(1), fixture.TimestampResolution);
    }

    [Fact]
    public void DerivesEverySuiteItsCapabilitiesCallFor() =>
        ConformanceCoverage.AssertEverySuiteIsDerived(fixture, typeof(SqliteProviderCapabilityTests).Assembly);
}
