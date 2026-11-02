# ADR 0011: The sample submits a seed batch of orders, then only on demand

**Status:** **Proposed**
**Date:** 2026-11-02
**Relates to:** [`0010-dashboard-design-tokens-and-light-theme.md`](0010-dashboard-design-tokens-and-light-theme.md)
(the dashboard list the sample feeds); the sample's saga design in
[`../design/mixed-sagas.md`](../design/mixed-sagas.md).

---

## Context

`OrderSubmitter` (`dotnet/samples/VSaga.Samples.OrderProcessing/OrderSubmitter.cs`) was a
`BackgroundService` that submitted an order every 8 seconds for as long as the process ran. Each order
starts several saga instances: `OrderSaga`, `PostShipmentChoreography`, `LoyaltyLookupSaga`,
`MixedFulfilmentSaga`, and the `InvoiceDeliverySaga`, `InvoiceFollowUpSaga` and `InvoiceArchivalSaga`
that follow, depending on how the order goes. At one order per 8 seconds that is 10,800 orders a day,
each starting up to seven saga instances.

The stack's data lives on a named volume (`vsaga-postgres-data`), so it also survives `docker compose
down`. Anyone who leaves the demo running, or stops and starts it over several days, ends up with a
dashboard list of thousands of near-identical rows, a growing event log, and a broker that is never idle.
The sample was meant to give the dashboard something to show, not to be a load generator.

The maintainer asked on 2026-11-02 for the demo to start with about 10 orders and to create more
only when someone calls an endpoint.

---

## Decision

**The sample submits a seed batch of orders at startup and then nothing on its own. `POST /orders`
submits more.**

- `OrderSubmitter` binds an `OrderSubmitter` configuration section. `SeedOrders` (default `10`) is how
  many orders it submits once at startup, one second apart so the list shows sagas at different stages.
  `0` starts with none. `MaxOrdersPerRequest` (default `50`) caps one `POST /orders` call.
- `POST /orders` submits one order; `POST /orders?count=N` submits `N`, from 1 up to
  `MaxOrdersPerRequest`. It answers `202` with the order ids, and `400` for a count outside the range. It
  is mapped wherever the saga engine runs (never `Participants`), next to the sample's other plain REST
  endpoints, and carries no authentication, like them.
- The base compose file publishes the `order-processing` container's port 8080 on the loopback host port
  `5081`, and every overlay remaps it to its dashboard API's port plus one (`5181` for Wolverine through
  `5781` for SQL Server), so stacks still run side by side.
- The 8-second timer is removed. Long soak runs that want continuous load script `POST /orders?count=50`
  in a loop instead.

---

## Options considered

| Option | Verdict |
| --- | --- |
| Keep the timer and add a cap on the total number of orders | Rejected. It still produces a burst of arbitrary size that stops at an arbitrary point, and gives no way to make more once the cap is reached other than a restart. |
| Keep the timer with a configurable interval, default off | Rejected. Two code paths for one behaviour nobody asked to keep; the on-demand call covers continuous load. |
| Seed on first start only, remembered in the store | Rejected. The sample would need a store of its own for a convenience, and a fresh `down -v` stack already starts from an empty store. |
| Seed at every start, then on demand | **Chosen.** A restarted stack adds another ten, which is a bounded, predictable amount. |
| Put the trigger in the dashboard | Rejected. The dashboard is definition-agnostic (ADR 0005) and does not know about the sample. |

---

## Consequences

### Positive

- A stack left running stays at the seed size until someone asks for more.
- A demo can create sagas at a chosen moment, which is what a walkthrough of the list, the map and a
  retry needs.
- The broker and the store are idle after the seed settles.

### Negative

- **The dashboard list no longer fills on its own.** Documentation that said it does is changed.
  Anything that relied on continuous traffic, such as a live check that watches for new sagas arriving,
  has to call `POST /orders` first.
- **Order ids restart at `0001` on every start**, as they did before, so a restart on the same day
  repeats the earlier ids. The sample's saga instances are keyed by correlation id, not by order id, so
  nothing collides, but the ids are not unique across restarts.
- **A new host port per stack**, bound to loopback, to document and keep unique.
- **`POST /orders` is unauthenticated.** Acceptable for the loopback-bound demo; the endpoint is a sample
  convenience, not a pattern to copy.

### Neutral

- The seed orders still pass through the same sagas, participants and simulated failures as before, so
  every outcome the sample demonstrated still appears within the first minutes.

---

## What would invalidate this decision later

1. **A need for sustained load in CI or soak runs.** If those become routine, a dedicated load
   generator, outside the sample, is a better home than a script around this endpoint.
2. **A sample with more than one front door.** Then the trigger belongs in a shared
   place, not in this process.
