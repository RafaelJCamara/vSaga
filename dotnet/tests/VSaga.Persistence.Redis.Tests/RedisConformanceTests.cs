using VSaga.Persistence.Conformance;

namespace VSaga.Persistence.Redis.Tests;

// The shared conformance suite (VSaga.Persistence.Conformance), run against the Redis provider on a real
// Redis 7. One collection, so every class shares the fixture's single container.

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisSnapshotStoreConformanceTests(RedisProviderFixture fixture)
    : SnapshotStoreConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisEventLogStoreConformanceTests(RedisProviderFixture fixture)
    : EventLogStoreConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisTimeoutStoreConformanceTests(RedisProviderFixture fixture)
    : TimeoutStoreConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisOutboxStoreConformanceTests(RedisProviderFixture fixture)
    : OutboxStoreConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisSummaryReaderConformanceTests(RedisProviderFixture fixture)
    : SummaryReaderConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisAdminStoreConformanceTests(RedisProviderFixture fixture)
    : AdminStoreConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisServiceTopologyStoreConformanceTests(RedisProviderFixture fixture)
    : ServiceTopologyStoreConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisAtomicUnitOfWorkConformanceTests(RedisProviderFixture fixture)
    : AtomicUnitOfWorkConformanceTests(fixture);

[Collection(RedisConformanceGroup.Name)]
public sealed class RedisConcurrentClaimConformanceTests(RedisProviderFixture fixture)
    : ConcurrentClaimConformanceTests(fixture);

/// <summary>Pins what <see cref="RedisProviderFixture"/> declares, so a flag the suite tailors its assertions by only ever changes in a reviewed diff.</summary>
[Collection(RedisConformanceGroup.Name)]
public sealed class RedisProviderCapabilityTests(RedisProviderFixture fixture)
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
        ConformanceCoverage.AssertEverySuiteIsDerived(fixture, typeof(RedisProviderCapabilityTests).Assembly);
}
