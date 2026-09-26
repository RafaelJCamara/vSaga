using VSaga.Persistence.Conformance;

namespace VSaga.Persistence.MongoDB.Tests;

// The shared conformance suite (VSaga.Persistence.Conformance), run against the MongoDB provider on a real
// MongoDB 8 replica set. One collection, so every class shares the fixture's single container.

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoSnapshotStoreConformanceTests(MongoProviderFixture fixture)
    : SnapshotStoreConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoEventLogStoreConformanceTests(MongoProviderFixture fixture)
    : EventLogStoreConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoTimeoutStoreConformanceTests(MongoProviderFixture fixture)
    : TimeoutStoreConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoOutboxStoreConformanceTests(MongoProviderFixture fixture)
    : OutboxStoreConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoSummaryReaderConformanceTests(MongoProviderFixture fixture)
    : SummaryReaderConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoAdminStoreConformanceTests(MongoProviderFixture fixture)
    : AdminStoreConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoServiceTopologyStoreConformanceTests(MongoProviderFixture fixture)
    : ServiceTopologyStoreConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoAtomicUnitOfWorkConformanceTests(MongoProviderFixture fixture)
    : AtomicUnitOfWorkConformanceTests(fixture);

[Collection(MongoConformanceGroup.Name)]
public sealed class MongoConcurrentClaimConformanceTests(MongoProviderFixture fixture)
    : ConcurrentClaimConformanceTests(fixture);

/// <summary>Pins what <see cref="MongoProviderFixture"/> declares, so a flag the suite tailors its assertions by only ever changes in a reviewed diff.</summary>
[Collection(MongoConformanceGroup.Name)]
public sealed class MongoProviderCapabilityTests(MongoProviderFixture fixture)
{
    [Fact]
    public void DeclaresTheReviewedCapabilities()
    {
        Assert.True(fixture.SupportsAtomicUnitOfWork);
        Assert.True(fixture.SupportsConcurrentClaim);
        Assert.Equal(TimeSpan.FromTicks(1), fixture.TimestampResolution);
    }

    [Fact]
    public void DerivesEverySuiteItsCapabilitiesCallFor() =>
        ConformanceCoverage.AssertEverySuiteIsDerived(fixture, typeof(MongoProviderCapabilityTests).Assembly);
}
