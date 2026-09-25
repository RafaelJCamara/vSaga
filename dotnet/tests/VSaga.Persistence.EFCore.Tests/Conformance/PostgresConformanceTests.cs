using VSaga.Persistence.Conformance;

namespace VSaga.Persistence.EFCore.Tests.Conformance;

// The shared conformance suite (VSaga.Persistence.Conformance), run against the EF Core provider on a real
// Postgres. One collection, so every class shares the fixture's single container.

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresSnapshotStoreConformanceTests(PostgresProviderFixture fixture)
    : SnapshotStoreConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresEventLogStoreConformanceTests(PostgresProviderFixture fixture)
    : EventLogStoreConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresTimeoutStoreConformanceTests(PostgresProviderFixture fixture)
    : TimeoutStoreConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresOutboxStoreConformanceTests(PostgresProviderFixture fixture)
    : OutboxStoreConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresSummaryReaderConformanceTests(PostgresProviderFixture fixture)
    : SummaryReaderConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresAdminStoreConformanceTests(PostgresProviderFixture fixture)
    : AdminStoreConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresServiceTopologyStoreConformanceTests(PostgresProviderFixture fixture)
    : ServiceTopologyStoreConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresAtomicUnitOfWorkConformanceTests(PostgresProviderFixture fixture)
    : AtomicUnitOfWorkConformanceTests(fixture);

[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresConcurrentClaimConformanceTests(PostgresProviderFixture fixture)
    : ConcurrentClaimConformanceTests(fixture);

/// <summary>Pins what <see cref="PostgresProviderFixture"/> declares, so a flag the suite tailors its assertions by only ever changes in a reviewed diff.</summary>
[Collection(PostgresConformanceGroup.Name)]
public sealed class PostgresProviderCapabilityTests(PostgresProviderFixture fixture)
{
    [Fact]
    public void DeclaresTheReviewedCapabilities()
    {
        Assert.True(fixture.SupportsAtomicUnitOfWork);
        Assert.True(fixture.SupportsConcurrentClaim);
        Assert.Equal(TimeSpan.FromMicroseconds(1), fixture.TimestampResolution);
    }

    [Fact]
    public void DerivesEverySuiteItsCapabilitiesCallFor() =>
        ConformanceCoverage.AssertEverySuiteIsDerived(fixture, typeof(PostgresProviderCapabilityTests).Assembly);
}
