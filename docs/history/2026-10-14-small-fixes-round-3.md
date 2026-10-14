# History: three small fixes the second live run left open (2026-10-14)

> Written fresh. The three items
> [`2026-10-14-live-verification-round-2.md`](2026-10-14-live-verification-round-2.md) listed under "Seen and not
> changed" or "Left as designed" and section 13 of
> [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md) carried as still open,
> starting from `6891963`. Each was done on its own branch from the same base, then merged. Every value below was
> observed in the run it is listed under; the commits' messages carry the detail.

---

## What was fixed

| Commit | Defect | Spec and mutation |
| --- | --- | --- |
| `ab8883d` | A `DeliveryExhausted` entry for a transient database error read "A '...' saga instance with correlation id '...' already exists." | 16 specs; the helper's `innermost.Message` to `exception.Message`: 6 of 12 Core specs and 3 EF Core specs red |
| `914d13b` | A saga's first snapshot landing inside the pending window was not followed up, so the detail page said "No snapshot was recorded for this step" until the next push | 2 specs; re-adding `\|\| history.snapshotCount === 0` (not one token): 2 red, 236 passed in the file |
| `ebe9e28` | No provider-neutral case for a persist that loses its race at the commit (B3, Q2): only EF-specific specs existed | 1 case on 5 provider configurations; deleting the statement `Forget(entity);` (not one token): 2 red on EF Core |

## 1. The cause a `DeliveryExhausted` entry names (`ab8883d`)

`EfCoreSagaSnapshotStore.UpdateAsync` and `InsertAsync` map every `DbUpdateException` to
`SagaAlreadyExistsException`, whose message is the "already exists" text, and the orchestrator recorded the outer
message. **Decision: record the innermost message too and leave the stores' mapping alone.** It changes no
persistence contract and every provider and every entry type benefits. Mapping only a genuine unique-index
violation (Q3 / clause D10) stays open: the EF project cannot name Npgsql or SQLite types, so it needs a
provider-neutral way to recognise the violation and a conformance clause.

`SagaErrorText.Describe(Exception)` (new, public, in `VSaga.Abstractions.Persistence`) returns the message, then
" Cause: " and the innermost exception's message when the chain has one and the outer message does not already
contain it; the appended cause is cut at 1000 characters (my choice; nothing bounded the text before). An
exception without an inner one yields exactly its `Message`. It is used by the `DeliveryExhausted` entries (after
redelivery is exhausted and for a failed deferred publish), `StepFailed`, `CompensationStepFailed` and the reply
entry of a failed `.CallHttp` (the `HttpClient` wrapper "An error occurred while sending the request" hid the
socket error). The span status and the dashboard's retry-redrive cause are not timeline entries and keep their
text. The HTTP shape and the golden fixtures are unchanged: the `errorMessage` field carries more text.

- **Specs (16).** Twelve in `VSaga.Core.Tests` (the helper: no inner, innermost not middle, no repeat, blank inner,
  the bound, a surrogate pair at the bound, an inner exception whose `Message` is null; `DeliveryExhausted` through the orchestrator for a transient database error, for a real collision
  that still reads "already exists" and carries the unique-constraint text, and for a plain exception unchanged;
  `StepFailed`; `CompensationStepFailed`), three SQLite specs in `EfCoreStoreTests` (a `RAISE(ABORT)` trigger on
  update and on insert names the database's own error through the real EF exception chain; a business-key
  collision on update still reads as one), one in `VSaga.Http.Tests`. The failed deferred publish has no spec of
  its own (it needs a transport that fails after commit); it is the same call.
- **Mutation.** One token, `innermost.Message` to `exception.Message` in the helper, rebuilt between mutation and
  run: 6 of 10 Core specs (of 12 now) and 3 of 56 `EfCoreStoreTests` red. Not one token: the five call sites mutated back to the
  outer message turned the two `DeliveryExhausted` specs, the `StepFailed`, `CompensationStepFailed` and Http specs red.
- **Live** (base stack `vsaga-r3a`, an exh3-style counting trigger on `SagaEventLog` and a `BEFORE UPDATE`
  trigger on `SagaInstances`, for `OrderSaga`'s `InventoryReserved`; the `errorMessage` of the `DeliveryExhausted`
  entry from `GET /api/sagas/OrderSaga/{id}/timeline`). Before, at `6891963` (entry 1988, 04:58Z): "A 'OrderSaga'
  saga instance with correlation id '7aef16c3-...' already exists." After (entry 2371, 04:59:49Z): "A 'OrderSaga'
  saga instance with correlation id 'af6a0d5c-...' already exists. Cause: P0001: r3a injected transient failure
  (instance update)". The triggers were dropped again and the stack taken down with `-v`.

## 2. A late first snapshot (`914d13b`)

`awaitsSnapshot()` ran its follow-up chain only when the timeline already held one `StatePersisted`. **Decision:
drop the `snapshotCount === 0` gate and let the pending window alone bound the chain.** A shorter chain for a saga
without a snapshot yet would need a second constant and still leave a late first snapshot wrong; asking the API
whether snapshots are on would need a new field, and the HTTP shapes must not change.
**Cost:** a saga whose host has `RecordStateSnapshots` off, with its page open, now gets the chain every other
saga gets after a push: about four timeline GETs, none once its last step is older than the window, and at most one
extra GET per 1500 ms for a saga that steps faster than the window, only while a page is open on it. That cost rests
on the spec and the code; a live saga with snapshots off was not run. `snapshotCount` is still folded and tested in
`saga-transitions` and no longer read by the page. §5.3 of the design document described the gate and now describes
the code.

- **Specs.** The `it.each` case "the saga records no snapshots", which pinned the gate, is replaced by two on a first
  step without a `StatePersisted` (the chain runs to the window, 3, 4, 5 timeline calls, then none; a snapshot that
  lands after a look is shown and ends the chain); the "older than the pending window" case stays. 238 specs in
  `saga-detail.spec.ts`. Mutation: re-adding `|| history.snapshotCount === 0` (not one token): the two new specs red,
  236 passed. `ng test` reused a stale build when only `saga-detail.ts` changed, so earlier mutation runs falsely
  passed; the counted run changed the spec text too.
- **Live** (base stack `vsaga-r3b`, a trigger sleeping 3.5 s before the `StatePersisted` insert of the first step,
  the next two `MessageReceived` appends held 8 s each, Timeline tab with step 1's Data open, sampled every 100 ms,
  UTC). Before (an image built from `6891963`'s `dashboard-web`): snapshot committed 04:54:49.702; "Not recorded
  yet" from 04:54:46.362; "No snapshot was recorded" from 04:54:51.362; the step's data only at 04:54:58.649 with
  the next push, about 9 s after the snapshot existed; timeline requests at 46.254 (load), 46.596 (push) and 58.587,
  no follow-up. After: snapshot committed 04:55:32.520; "Not recorded yet" from 04:55:29.155; the data at 04:55:32.652
  and "No snapshot was recorded" never shown; requests at 29.051 (load), 29.593 (push), 31.110 and 32.622 (the
  follow-ups, the second found it) and none until the next push at 41.593.

## 3. A conformance case for B3 (`ebe9e28`)

`Update_ThatLosesItsRaceAtTheCommit_LeavesALaterCommitInTheSameUnitOfWorkFree` in
`SnapshotStoreConformanceTests`: stage an outbox row, lose the race inside the update's own write (not at the
version pre-check), append a log entry and commit in the same unit of work; it expects `SagaConcurrencyException`,
the live object back at version 0, the log entry and the staged row durable, and the stored row holding the winner's
value at version 1. **No new capability was needed:** the suite's `RaceProbeSagaState` serialisation hook already
lands a rival write between an update's read and its write, so `IProviderFixture` and `IStoreUnitOfWork` are
unchanged and third-party providers cannot break.

- **Ran:** InMemory, EF Core SQLite, EF Core Postgres, Redis and MongoDB, none skipped. That the race is really lost
  was checked by temporarily failing the branch where the update does not throw: all five still passed. On InMemory,
  Redis and MongoDB the loss is at their single write, which is their commit, and they never had the defect, so the
  case cannot be red there; it keeps their later commit honest.
- **Mutation, not one token:** deleting the statement `Forget(entity);` in the `DbUpdateConcurrencyException` catch
  of `EfCoreSagaSnapshotStore.UpdateAsync`: red on SQLite and Postgres (2 of 2), the later commit failing with a raw
  `DbUpdateConcurrencyException`; restored and rebuilt, green. With the source of `6b45335^` the case was also red
  2 of 2 on EF Core.
- B3 and Q2 in `persistence-contracts.md` now say the case exists.

## The independent review

A read-only reviewer found no blockers and no majors and two minors, both taken in a follow-up commit: the cut at
1000 characters could split a surrogate pair (an unpaired surrogate in `ErrorMessage` that a UTF-8 writer rejects;
the cut now steps back one character), and an inner exception whose `Message` is null threw in the helper
(`.Trim()` on null, inside a catch block; now treated as blank). Two specs, each broken by its own mutation: the
cut's `IsHighSurrogate` to `IsLowSurrogate` (1 red of 12), and the null-safe trim back to `.Trim()` (not one token;
1 red of 12). It also noted, and I did not change, that the older history files still call the first-snapshot item
open (they are dated records).

## Gates, on the merged result and after the review's follow-up

| Gate | Result |
| --- | --- |
| `npm audit --audit-level=low` | 0 vulnerabilities, exit 0 |
| `npx ng build` | exit 0, no warning, Initial total 454.97 kB (119.98 kB transfer) |
| `npx ng test --watch=false` | 58 files, 2,444 tests, exit 0 |
| `dotnet build dotnet/VSaga.slnx -nologo` | 0 warnings, 0 errors, exit 0 |
| `dotnet test dotnet/VSaga.slnx --no-build -nologo` | 1,723 passed over 16 assemblies, 0 failed, exit 0 (1,702 on `6891963`, plus 16, plus 5) |

## What was not exercised

- A saga with `RecordStateSnapshots` off with a page open (the extra-GET cost in section 2).
- The failed-deferred-publish `DeliveryExhausted` entry (no spec, no live case).
- A third-party provider or a store that locks across serialising (section 3's lenient branch).
- Option (1) of section 1: mapping only a unique-index violation to `SagaAlreadyExistsException`. Still open (Q3).
- Firefox, WebKit and a screen reader, as before.
