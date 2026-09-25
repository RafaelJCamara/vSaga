using VSaga.Persistence.Conformance;

namespace VSaga.Persistence.InMemory.Tests;

// The shared conformance suite (VSaga.Persistence.Conformance), run against the in-memory provider.

public sealed class InMemorySnapshotStoreConformanceTests(InMemoryProviderFixture fixture)
    : SnapshotStoreConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

public sealed class InMemoryEventLogStoreConformanceTests(InMemoryProviderFixture fixture)
    : EventLogStoreConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

public sealed class InMemoryTimeoutStoreConformanceTests(InMemoryProviderFixture fixture)
    : TimeoutStoreConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

public sealed class InMemoryOutboxStoreConformanceTests(InMemoryProviderFixture fixture)
    : OutboxStoreConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

public sealed class InMemorySummaryReaderConformanceTests(InMemoryProviderFixture fixture)
    : SummaryReaderConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

public sealed class InMemoryAdminStoreConformanceTests(InMemoryProviderFixture fixture)
    : AdminStoreConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

public sealed class InMemoryServiceTopologyStoreConformanceTests(InMemoryProviderFixture fixture)
    : ServiceTopologyStoreConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

public sealed class InMemoryConcurrentClaimConformanceTests(InMemoryProviderFixture fixture)
    : ConcurrentClaimConformanceTests(fixture), IClassFixture<InMemoryProviderFixture>;

// No AtomicUnitOfWorkConformanceTests: EnqueueAsync writes immediately, so there is no unit of work to abandon.

/// <summary>Pins what <see cref="InMemoryProviderFixture"/> declares, so a flag the suite tailors its assertions by only ever changes in a reviewed diff.</summary>
public sealed class InMemoryProviderCapabilityTests(InMemoryProviderFixture fixture) : IClassFixture<InMemoryProviderFixture>
{
    [Fact]
    public void DeclaresTheReviewedCapabilities()
    {
        Assert.False(fixture.SupportsAtomicUnitOfWork);
        Assert.True(fixture.SupportsConcurrentClaim);
        Assert.Equal(TimeSpan.FromTicks(1), fixture.TimestampResolution);
    }

    [Fact]
    public void DerivesEverySuiteItsCapabilitiesCallFor() =>
        ConformanceCoverage.AssertEverySuiteIsDerived(fixture, typeof(InMemoryProviderCapabilityTests).Assembly);
}
