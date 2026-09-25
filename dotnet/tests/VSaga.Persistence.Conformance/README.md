# VSaga.Persistence.Conformance

An executable check of vSaga's persistence contracts: abstract xUnit test classes, one per store
contract in `VSaga.Abstractions.Persistence`, that a persistence provider derives from to verify its
stores behave the way the engine depends on. The shipped providers (EF Core on SQLite and Postgres,
and in-memory) run exactly this suite; a third-party provider verifies itself the same way.

## Requirements

An xUnit **v2** test project on `xunit` 2.9.3. xunit.v3 is not supported: its assemblies define the
same `Xunit.*` types as the v2 packages this one depends on.

## Install

```bash
dotnet add package VSaga.Persistence.Conformance
```

## Usage

In your provider's test project, implement `IProviderFixture` — a way to create a fresh, isolated
backing store, a unit-of-work hook, and three declarations about the provider — then derive one
concrete class per contract suite:

```csharp
public sealed class MyProviderFixture : IProviderFixture
{
    public bool SupportsAtomicUnitOfWork => true;
    public bool SupportsConcurrentClaim => true;
    public TimeSpan TimestampResolution => TimeSpan.FromTicks(1);

    public Task<IProviderStores> CreateStoresAsync(CancellationToken cancellationToken = default) => ...;
}

public sealed class MySnapshotStoreConformanceTests(MyProviderFixture fixture)
    : SnapshotStoreConformanceTests(fixture), IClassFixture<MyProviderFixture>;
// ...and likewise for EventLog, Timeout, Outbox, SummaryReader, AdminStore and ServiceTopology.
```

Derive `ConcurrentClaimConformanceTests` if you declare `SupportsConcurrentClaim`, and
`AtomicUnitOfWorkConformanceTests` if you declare `SupportsAtomicUnitOfWork` — and only then. Then pin
your declarations in a test of your own, so a flag can only change in a reviewed diff, and call
`ConformanceCoverage.AssertEverySuiteIsDerived(fixture, typeof(YourTest).Assembly)` from it so that no
suite your declarations call for can go missing.

## What a green run does not cover, by design

A null or corrupt state blob must make `FindAsync` throw rather than return null
(`ISagaSnapshotStore.FindAsync`). The suite writes only through the contracts, so it has no way to
plant such a blob; verify that case against your own storage.

## Docs

The contract is the XML documentation on the interfaces in `VSaga.Abstractions.Persistence`.
[docs/design/persistence-contracts.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/design/persistence-contracts.md)
records how each clause was arrived at and why it exists.

## License

MIT
