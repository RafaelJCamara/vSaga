# VSaga.Persistence.Conformance

An executable check of vSaga's persistence contracts: abstract xUnit test classes, one per store
contract in `VSaga.Abstractions.Persistence`, that a persistence provider derives from to verify its
stores behave the way the engine depends on. The shipped providers (EF Core on SQLite and Postgres,
MongoDB, Redis and in-memory) run exactly this suite; a third-party provider verifies itself the same
way.

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

## What a green run covers that a provider written earlier may not

`SagaListFilter.SagaTypes` (the rows of any of several saga types, compared exactly; empty matches nothing,
null puts no limit on the type) is a filter every `ISagaSummaryReader` must honour, and the summary-reader
suite holds it to that: the set composes with every other filter, counts the filtered set, and keeps the
order the unfiltered list gives the same rows for every sort arm. A provider that ignores it returns rows of
other types, and the dashboard's scoped lists would then show a page of mostly foreign rows.

There is no capability flag and no fallback for it, on purpose: the dashboard makes one `ListAsync` call
carrying the set instead of one call per type, and no vSaga package has been published yet (no tagged
release exists), so a reader written against an earlier snapshot of the repository has to add the filter,
not declare that it lacks it. A reader that ignores it still never shows a caller a row outside their scope (the
API drops those), but gives a scoped caller short pages and a total over the wrong set. The same rule is stated on
`ISagaSummaryReader.ListAsync` and `SagaListFilter.SagaTypes`.

## What a green run covers on reads, nulls and updates

The summary-reader cases hold every item `ListAsync` and `FindChildrenAsync` return to the state that was
written, field by field — kind, current state, status, version (through `ListAsync`, an instance updated once or
twice lists with that version, not a constant), parent link and both timestamps (at the `TimestampResolution` you declare) —
whichever filter selected it, not only to row ids, counts and page numbers; a root saga's parent link comes
back null. An outbox row enqueued without a destination is claimed
with a null `Destination`, and an event-log entry written without a source service, destination service or
causation id reads back with all three null — never empty text. After `UpdateAsync` the projected status and
current state are the new ones (`GetAsync` and a status-filtered `ListAsync` say so), only the updated
`(sagaType, correlationId)` instance changed when two saga types share a correlation id, and a non-null
business key the update leaves alone still resolves to the instance.

A write that fails does not poison the unit of work (clause 4): after an update that loses its race at the
commit, an update onto a business key another instance holds, or an insert whose business key is taken, a
later log entry and commit in the same unit of work still succeed, and what was staged before the failure
becomes durable with them (a provider that declares `SupportsAtomicUnitOfWork = false` writes an outbox row
immediately, so for it that half is met trivially). The failed write itself leaves no trace — the live object is back at its expected
version, the loser is not stored and the holder keeps the key. This is the case a provider with a change
tracker (EF Core) fails when it leaves the rejected row in the tracker for the next commit to re-send.

## What a green run does not cover, by design

A corrupt state blob must make `FindAsync` and `FindByBusinessKeyAsync` throw rather than return null
or a state (`ISagaSnapshotStore.FindAsync`). The suite writes only through the contracts and
`System.Text.Json`, so it plants the corrupt blob with a state type whose own converter writes the bad
text: JSON `null`, and a document cut off mid-way (`Utf8JsonWriter.WriteRawValue` with input validation
off). Both are checked, through both lookups. A corrupt blob that does not come from the serializer — a row
damaged in storage, or text your storage would normalise or reject on write (a `jsonb` column, a BSON
document) — is outside what the suite can write, so verify that against your own storage.

## Docs

The contract is the XML documentation on the interfaces in `VSaga.Abstractions.Persistence`.
[docs/design/persistence-contracts.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/design/persistence-contracts.md)
records how each clause was arrived at and why it exists.

## License

MIT
