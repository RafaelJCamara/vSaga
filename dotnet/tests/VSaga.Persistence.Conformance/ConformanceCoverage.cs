using System.Reflection;

namespace VSaga.Persistence.Conformance;

/// <summary>
/// Checks that a provider's test project runs the whole suite its declarations call for — nothing more,
/// nothing silently left out. Call it from the test that pins the provider's declared capabilities.
/// </summary>
public static class ConformanceCoverage
{
    private static readonly Type[] UngatedSuites =
    [
        typeof(SnapshotStoreConformanceTests),
        typeof(EventLogStoreConformanceTests),
        typeof(TimeoutStoreConformanceTests),
        typeof(OutboxStoreConformanceTests),
        typeof(SummaryReaderConformanceTests),
        typeof(AdminStoreConformanceTests),
        typeof(ServiceTopologyStoreConformanceTests),
    ];

    /// <summary>
    /// Asserts <paramref name="testAssembly"/> derives, for <paramref name="fixture"/>'s type, every
    /// contract suite, plus each capability-gated suite exactly when the fixture declares that capability.
    /// A gated suite skipped by a provider that declares the capability would otherwise just go missing
    /// from the run, with nothing failing.
    /// </summary>
    /// <remarks>
    /// A class counts when it is public — the only kind xUnit v2 discovers — concrete, and has a
    /// constructor taking the fixture (alongside anything else it injects).
    /// </remarks>
    public static void AssertEverySuiteIsDerived(IProviderFixture fixture, Assembly testAssembly)
    {
        var fixtureType = fixture.GetType();
        var derived = testAssembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType.IsAssignableFrom(fixtureType))))
            .ToList();

        foreach (var suite in UngatedSuites)
            Assert.True(derived.Exists(suite.IsAssignableFrom), $"No concrete {suite.Name} for {fixtureType.Name}.");

        AssertGatedSuite(derived, typeof(ConcurrentClaimConformanceTests), fixture.SupportsConcurrentClaim, nameof(IProviderFixture.SupportsConcurrentClaim), fixtureType);
        AssertGatedSuite(derived, typeof(AtomicUnitOfWorkConformanceTests), fixture.SupportsAtomicUnitOfWork, nameof(IProviderFixture.SupportsAtomicUnitOfWork), fixtureType);
    }

    private static void AssertGatedSuite(List<Type> derived, Type suite, bool declared, string capability, Type fixtureType) =>
        Assert.True(derived.Exists(suite.IsAssignableFrom) == declared,
            declared
                ? $"{fixtureType.Name} declares {capability} but derives no {suite.Name}."
                : $"{fixtureType.Name} does not declare {capability} but derives {suite.Name}.");
}
