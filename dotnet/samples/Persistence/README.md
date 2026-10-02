# Persistence samples

One small sample per persistence provider. All four run the **same saga** with the **same scenarios**
and print the **same report**. The only thing that changes between them is the provider each one
registers, so comparing two `Program.cs` files shows exactly what switching providers costs.

| Provider package | Sample | Needs | Host port |
| --- | --- | --- | --- |
| `VSaga.Persistence.InMemory` | [`VSaga.Samples.Persistence.InMemory`](VSaga.Samples.Persistence.InMemory/) | nothing | — |
| `VSaga.Persistence.EFCore` + `VSaga.Persistence.EFCore.Postgres` | [`VSaga.Samples.Persistence.EFCore.Postgres`](VSaga.Samples.Persistence.EFCore.Postgres/) | Docker (Postgres 16) | `5434` |
| `VSaga.Persistence.MongoDB` | [`VSaga.Samples.Persistence.MongoDB`](VSaga.Samples.Persistence.MongoDB/) | Docker (MongoDB 8, single-member replica set) | `27019` |
| `VSaga.Persistence.Redis` | [`VSaga.Samples.Persistence.Redis`](VSaga.Samples.Persistence.Redis/) | Docker (Redis 7.4, Tier A) | `6380` |

[`VSaga.Samples.Persistence.Common`](VSaga.Samples.Persistence.Common/) holds the provider-neutral
code: the saga, its messages, a fake payment gateway and the scenario runner. It references no
persistence package, so it compiles the same against every store.

The host ports avoid both a locally installed server's default port and the reference stack's ports
(`5433`, `27018`, `6479`; see the [root README](../../../README.md#run-the-demo)), so a sample's
database can run alongside either.

## Running one

Every command below runs from the repository root. The in-memory sample needs nothing first:

```bash
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.InMemory
```

Each durable sample ships a `docker-compose.yml` that starts its database and nothing else. The
transport is in-memory in every sample, so no broker is needed:

```bash
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis/docker-compose.yml up -d --wait
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis
```

Replace `Redis` with `EFCore.Postgres` or `MongoDB` in both lines for the other two. Each sample's own
README covers what is specific to its provider and how to inspect the data it wrote.

## What every sample does

`CheckoutSaga` (in `Common`) submits an order, asks the payment gateway to charge the card, and
finishes. It is small, but each part lands in a different store contract:

| Saga feature | Store contract | What you see |
| --- | --- | --- |
| The saga state, versioned on every persist | `ISagaSnapshotStore<TState>` | `snapshot: state …, status …, version …` |
| Every step, publish, timeout and unexpected message | `ISagaEventLogStore` | the numbered `timeline` |
| The state each committed step saved | `ISagaEventLogStore` (a `StatePersisted` entry) | `StatePersisted  state saved, N bytes` after each step |
| `CorrelateOn(s => s.OrderNumber)`, the business key | the snapshot store's unique key per `(SagaType, BusinessKey)` | the resubmitted order finds the existing saga |
| `WithTimeout(AwaitingPayment, 5 s)` | `ISagaTimeoutStore` | `TimeoutScheduled`, then `TimeoutFired` for the unanswered order |
| `SagaOutboxMode.All` (set in `AddCheckoutSample`) | `ISagaOutboxStore` | outbox rows committed with the snapshot (inspect them per provider) |

The runner submits three orders, each with a payment token that fixes its outcome:

| Order | Payment token | Ends in | Status |
| --- | --- | --- | --- |
| `ORD-<run>-1` | `tok_approved` | `Paid` | `Completed` |
| `ORD-<run>-2` | `tok_declined` | `PaymentDeclined` | `Failed` |
| `ORD-<run>-3` | `tok_no_response` | `PaymentTimedOut` | `TimedOut`, 5–10 s later (the timeout dispatcher polls every 5 s) |

It then resubmits `ORD-<run>-1` under a brand-new correlation id. The transport id matches no saga,
but the business key does, so the message reaches the existing, already-paid saga as an
`UnexpectedEvent` and no second saga is created. Finally it prints each saga's snapshot and timeline,
read back through the store contracts, and the number of `CheckoutSaga` instances in the store before
and after the run. `<run>` is the time of day (`HHmmss`), which keeps order numbers unique across runs
of a durable store.

## Where the providers differ in the output

- **Before/after count.** In-memory always starts at 0. The three durable providers keep counting up
  across runs until you remove the compose volume (`docker compose ... down -v`).
- **Timeline sequence numbers.** The in-memory store and EF Core number entries from one store-wide
  counter (a single in-process counter and an identity column, respectively), so the second saga's
  timeline starts where the first left off. MongoDB and Redis number each saga's timeline from 1 (a
  per-instance counter document, and the position in a per-instance list). The contract only requires
  the order within one saga; see [`docs/persistence.md`](../../../docs/persistence.md).
- **Readiness.** MongoDB and Redis bootstrap in a background service and expose a health check. Their
  samples wait for it to report Healthy, and print the description it gives. EF Core has no
  bootstrapper; its sample applies migrations before the host starts instead.

The provider-specific trade-offs (durability tiers, the replica-set requirement, the Postgres-only
atomic claim, and so on) are in [`docs/persistence.md`](../../../docs/persistence.md). The samples
point at the relevant sections rather than repeat them.
