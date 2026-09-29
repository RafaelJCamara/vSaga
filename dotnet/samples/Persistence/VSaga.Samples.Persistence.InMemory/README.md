# Persistence sample: in-memory

`CheckoutSaga` on **`VSaga.Persistence.InMemory`**: no database, no Docker, nothing to start first.
The scenarios and output are described in the [persistence samples README](../README.md).

```bash
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.InMemory
```

## The provider-specific part

`Program.cs` marks every in-memory-specific line with `[InMemory]`. There is only one:

```csharp
builder.Services.AddVSagaInMemoryPersistence();
```

It takes no options. One `InMemorySagaStore` singleton backs all seven store contracts, and there is
no readiness step, because the store is ready as soon as the container is built.

## What to notice in the output

- **The store is empty on every run.** `The store holds 0 CheckoutSaga instance(s) from earlier runs`
  is always 0: the store lives and dies with the process.
- **Timeline sequence numbers are store-wide.** A single counter numbers every saga's entries, so the
  second and third orders' timelines do not start at 1.

## What it does not give you

This provider is for local development and tests; it is also the store behind
[`SagaTestHarness`](../../../../docs/testing.md). Per [`docs/persistence.md`](../../../../docs/persistence.md#in-memory):

- **No durability.** A restart loses every saga, pending timeout and outbox row.
- **Single process only.** Timeout and outbox claims are only safe within one process.
- **No atomic outbox.** `EnqueueAsync` commits immediately instead of staging until the snapshot
  persists, so the outbox's crash-atomicity guarantee does not hold. The durable providers commit the
  outbox rows and the snapshot together.

To move to a durable store, swap that one registration line: compare this `Program.cs` with the
[EF Core/Postgres](../VSaga.Samples.Persistence.EFCore.Postgres/),
[MongoDB](../VSaga.Samples.Persistence.MongoDB/) or [Redis](../VSaga.Samples.Persistence.Redis/)
sample's.
