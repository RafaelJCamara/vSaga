# History: a second live run over the open paths (2026-10-14)

> Written fresh. A verification pass over what
> [`2026-10-13-live-verification-of-the-open-paths.md`](2026-10-13-live-verification-of-the-open-paths.md)
> ("What was not exercised") and the last paragraph of §13 of
> [`../design/dashboard-usability-and-access.md`](../design/dashboard-usability-and-access.md) name as never run,
> starting from `d0be897`. Every value below was observed in the run it is listed under. The defects the run
> found each have a commit with a spec and a mutation that breaks it; the commits' messages carry the detail.

---

## How it was run

Two compose stacks from the repository's own files, each under its own `-p` project:

- `vsaga-r2`: the base stack (RabbitMQ, Postgres, `dashboard-api`, `dashboard-web`, the order-processing sample),
  ports 4200 / 5080 / 5433 / 5672, signed in as `admin`.
- `vsaga-r2s`: `dashboard-api`, `dashboard-web`, Postgres and RabbitMQ only (no sample), moved to 4210 / 5090 /
  5453 / 5679 with an overlay (`!override` on the ports, as the other overlays do), first with
  `Dashboard:Admin:Username` and `Password` empty and `Dashboard:Setup:Code` preset, later with the seed keys and
  `Dashboard:Admin:ResetOnStart` set. Port 4300 was not used.

The browser was the Playwright MCP's Chromium, driven by its accessibility snapshot and by `evaluate` and
`run_code` reading attributes and `textContent`. **Not covered: Firefox, WebKit and any screen reader.** What
section 6 says a screen reader "would be told" is what Chromium's accessibility tree and the elements' attributes
expose, nothing more.

Throwaway tools, kept in the session's scratch directory and not in the repository: a Node client with a cookie
jar (sign in, list, read a correlation id's sagas and timelines, post a retry and compare every saga type on the
correlation id before and after); a console host that registers the real RabbitMQ adapter and publishes
`OrderSubmitted` under a chosen correlation id; a console host that registers the real EF Core adapter and
inserts saga instances through `ISagaSnapshotStore<T>.InsertAsync`; `psql` through `docker exec` for the
triggers that made the faults. The faults were made below the engine, in Postgres, and are listed where they
were used; every one was dropped again (checked with `pg_trigger` afterwards).

## 1. A retryable `DeliveryExhausted` retry

**What the engine does, read from the code and confirmed in the log.** A message gets six deliveries: the
`x-vsaga-delivery-attempt` header goes 0 to 5, a delivery under `MaxDeliveryAttempts` (5) is redelivered with
the same message id, and the sixth is dead-lettered with a `DeliveryExhausted` entry. The log said "attempt 1/5"
to "attempt 5/5" and then "after 5 delivery attempts; dead-lettering". The engine deduplicates a redelivery by
`MessageReceived` or `SagaStarted` with the same message id, so a body is recorded and the planner accepts the
saga only when the first five deliveries fail before that append and the sixth passes it and then fails.
The 2026-10-13 run could not make that from outside; it can, with a fault that counts.

**The fault.** On the base stack, for one order published under a correlation id I chose (so its
`OrderSaga`, `PostShipmentChoreography`, `LoyaltyLookupSaga` and `InvoiceFollowUpSaga` all share it):
a `BEFORE INSERT` trigger on `SagaEventLog` that raises for that id's `MessageReceived` of `InvoiceIssued` on
`PostShipmentChoreography` while a sequence value is 5 or less (a sequence, because a rolled-back counter
resets), and a `BEFORE INSERT OR UPDATE` trigger on `SagaInstances` that raises for that id's instance when the
sequence has passed 5 and the new status is not `Failed`. The first attempt (thresholds of 4 and 5, one delivery
too few) did not exhaust: the fifth delivery recorded the message, the sixth was deduplicated and acknowledged.

**Defect found on the way (`6b45335`).** With the thresholds right, the sixth delivery recorded `MessageReceived`
and failed the instance `UPDATE`, as intended, and then the saga stayed `Running` at version 2 with no
`DeliveryExhausted` entry: the host logged "Failed to record delivery-exhausted state for saga
PostShipmentChoreography correlation 727c3770-...", with the trigger's error, from
`EfCoreSagaEventLogStore.AppendAsync`. The failed `UpdateAsync` had left the entity `Modified` in the scoped
context and the dead-letter append re-sent it. Fixed in `EfCoreSagaSnapshotStore` (update and insert); three
SQLite specs, one-token mutation `EntityState.Detached` to `EntityState.Modified`: 3 of 3 red, 236 of 236 green
restored. After rebuilding the stack with the fix the same run, correlation id
`003fe330-6036-4d94-adfb-27b0d564394b`:

- sequence 6 (five raises, one pass); `PostShipmentChoreography` `Failed`, state `Notified`, version 3, 20
  entries, among them `MessageReceived` `InvoiceIssued` (id 14802, 107 characters of body) and `DeliveryExhausted`
  (id 14813, the message id of 14802). The entry's error text is "A 'PostShipmentChoreography' saga instance with
  correlation id '...' already exists." (see "Seen and not changed").
- `GET .../retry-plan`: `retryable: true`, `failureKind: DeliveryExhausted`, step `InvoiceIssued` from state
  `Notified` at sequence 14802, `reason: null`.
- With the triggers dropped, `POST .../retry` answered 202 with an empty body. Ten seconds later, compared on the
  correlation id: `PostShipmentChoreography` went from `Failed` / `Notified` / version 3 / 20 entries to
  `Completed` / `Invoiced` / version 5 / 28 entries (a new `MessageReceived` of `InvoiceIssued`, a
  `ChildSagaStarted`, `StepSucceeded`, `SagaCompleted`); `OrderSaga` (`Completed`, `Completed`, 3, 19),
  `LoyaltyLookupSaga` (`Completed`, `Done`, 1, 11) and `InvoiceFollowUpSaga` (`Completed`, `Archived`, 1, 10)
  were unchanged in status, state, version and entry count.
- Two `InvoiceDeliverySaga` children now name that saga as their parent: the failed delivery had already
  published `DeliverInvoice` before its persist failed (entry 14803, `ChildSagaStarted`), and the retried step
  published another. A step's side effects before its persist are not undone by a retry, which the guide says
  (`What a retry does not undo`); this is the first time it was seen on a `DeliveryExhausted` retry.

The planner and endpoint tests already cover this plan with a test double; it is now seen with a real engine,
real adapters and a body recorded by the engine itself, so nothing moves to the .NET tests.

## 2. `maxPage: 0` end to end

**Making the types.** On `vsaga-r2s`, 60 saga types `ExtraType01` to `ExtraType60`, three instances each (180
rows, a third `Failed`), inserted through `ISagaSnapshotStore<T>.InsertAsync` of the real EF Core adapter
(`SELECT count(distinct "SagaType")` said 60). Two users with the Viewer role on named types: `wide`
(`ExtraType01` to `60`) and `mid` (`ExtraType01` to `12`).

**API.** `wide`: the default list answered `400 { error: "You can see 60 saga types, more than the 50 a list
sorted by last update can combine. Choose a saga type with the sagaType filter.", maxPage: 0 }`, and the same
with `page=2&pageSize=100`; `sortBy=Status`, `status=Failed`, `kind=Orchestrated` and `search=x` each answered 400
with "...more than the 10 a list sorted by status, filtered by status or kind, or searched can combine..." and
`maxPage: 0`; `sagaType=ExtraType05` answered 200 (3 rows), also with `sortBy=Status`. `mid`: the default list
answered 200 (36 total, 25 rows) and `page=2&pageSize=100` answered 200 with no rows; the four other shapes
answered 400 with "You can see 12 saga types, more than the 10 ...", `maxPage: 0`.

**SPA, `mid`.** The default list: "36 total", 25 rows, no banner. Choosing the status `Failed`: URL
`/sagas?status=Failed`, a `role="alert"` banner with the server's sentence, 0 rows, "0 total". Adding the type
`ExtraType05`: 1 row, "1 total", the banner gone. Clearing the type: 0 rows and the banner again. Clearing the
status: 25 rows, "36 total", no banner. **The rows are not kept, by design**: `onListRefused` clears them and
goes to page 1 for a `maxPage` of 0, since nothing of the refused shape can be served and the old rows would
contradict the toolbar; "the last good page with rows on screen" is the case of a `maxPage` of 1 or more (seen on
2026-10-13), not this one. **SPA, `wide`**: signing in landed on `/sagas` with the banner for the 50-type bound
(the first request is already refused), 0 rows, and a saga-type dropdown with 61 options (60 types and "All saga
types"), through which the list is reachable.

## 3. A preset `Dashboard:Setup:Code` and `ResetOnStart`

Stack `vsaga-r2s`, `Dashboard:Setup:Code` = `preset code-for-r2 test-9`.

- Start-up logged `FirstAdministratorService[7211]` at Warning: "No dashboard user exists and Dashboard:Admin:*
  is not set, so first-run setup is open. Open the dashboard and create the first administrator with the setup
  code set in Dashboard:Setup:Code". No `[7210]`, and no line of the log contained the code (searched for it
  with spaces and hyphens removed and for the word "preset").
- `GET /api/auth/session`: `setupRequired: true, setupAvailable: true`; `GET /` went to `/setup`.
- Wrong codes by the API: `AAAA-BBBB-CCCC-DDDD`, `preset code-for-r2` (the first part of the right one) and `x`
  each answered `400 invalid_credentials`, "The setup code is not correct. ...", with an audit line `user.setup
  ... invalid_credentials` (three of them, `7101`).
- The right code in the browser, typed with the keyboard only into the form as `  PRESET Code-FOR  r2 test 9 `
  (upper case, spaces, hyphen, leading and trailing space): accepted, `/sagas`, audit `user.setup user ...
  'preset-admin' ... succeeded (first administrator created with the setup code)` and the sign-in.
- A restart of `dashboard-api` after that logged no `7210` and no `7211`.
- **`ResetOnStart`.** 12 wrong passwords locked `preset-admin`; the right password then answered 401. The API was
  recreated with `Dashboard:Admin:Username` = `preset-admin`, a new `Dashboard:Admin:Password` and
  `ResetOnStart` = `true`: audit `user.reset-on-start ... succeeded (password reset, enabled, unlocked,
  Administrator for all saga types)` and a Warning `[7214]` ("Dashboard administrator 'preset-admin' reset at
  start ..."). The old password answered 401, the new one 200. The browser tab that was open answered with
  `/login?returnUrl=%2Fsagas&reason=expired` (8 `502` from nginx while the API was down, then 401s on the hub's
  reconnects). A second administrator then disabled `preset-admin` and gave it Viewer on `ExtraType01` only;
  after a restart with the key still set it was enabled again and had the Administrator grant for all saga types
  **in addition to** the Viewer grant, which was not removed.

## 4. The pending window and the 1500 ms follow-up

**The fault.** On the base stack, a `BEFORE INSERT` trigger on `SagaEventLog` that waits 3.5 s before the
`StatePersisted` insert of one saga's last step (the append is under the engine's own 5 s deadline, so it lands;
the persist itself is not slowed). The page was opened on the saga's Timeline tab with the last step's Data
inspector open and sampled every 100 ms (absolute times below, UTC).

- **A finished saga** (`OrderShipped` last; the snapshot appended 22:25:09.83 and committed 22:25:13.33): the
  page read "No snapshot was recorded for this step" from 22:25:10.42, made timeline requests at 22:25:10.21 (the
  push) and 22:25:11.72 (the follow-up, 1.5 s later) and none after; it was still on screen when sampling ended,
  about nine seconds after the snapshot existed. **Defect (`ddcc688`).** Each follow-up that still finds the
  snapshot missing now schedules the next, so the chain ends with the window (about four follow-ups, the last
  just after it). Two specs; one-token mutation `scheduleSnapshotFollowUp` to `cancelSnapshotFollowUp`: 2
  failed, 233 passed in the file. After rebuilding `dashboard-web` the same scenario (`e2c8a88f-...`): push at
  22:35:07.18, "No snapshot was recorded" at 22:35:07.37, follow-ups at 22:35:08.71 and 22:35:10.21, and at
  22:35:10.32 the step's data ("Changes / Full state / Message ...").
- **A running saga** (step 1's snapshot delayed 3.5 s and the next two messages held 8 s each, so the saga stays
  `Running`): "Not recorded yet. The step may still be committing; this view refreshes by itself." at 22:26:51.80
  (the pending state); "No snapshot was recorded for this step" from 22:26:56.61, which is the age window running
  out (the step's newest row was written at about 22:26:51.55, and 5 s plus the 50 ms slack later is 22:26:56.6),
  while the snapshot had been committed at 22:26:55.01; the data appeared at 22:27:04.21 with the next push. So the
  window turns "pending" into "missing" as designed, and for a snapshot that lands inside it the page is wrong
  between the landing and the next refresh.
  **Left as designed:** no follow-up ran in this case (one timeline request at 22:26:52.20, which was the push), because
  the follow-up needs the timeline to hold at least one `StatePersisted` already (otherwise a saga with snapshots
  switched off would fetch after every push), and a saga's first snapshot being late is exactly the case with none.
  It costs a wrong "missing" until the next push for that one step.

## 5. The three open questions of §13

**The User guide link.** The link is `https://github.com/RafaelJCamara/vSaga/blob/main/docs/dashboard-guide.md`
(`USER_GUIDE_URL`), `target="_blank"` with `rel="noopener noreferrer"`. `HEAD` on it answered 200 from the
network the session ran on. All seven `docsAnchor` values (`#the-saga-list`, `#the-saga-detail-page`, `#map`,
`#timeline`, `#saga-data`, `#retrying-a-saga`, `#administration`) are headings of `docs/dashboard-guide.md` at
this commit. What stays open is the product decision: a fork or an installation without GitHub gets upstream
content or nothing, and a build older than `main` gets a newer guide. **Decision needed from you**: keep the
constant (recommended until there is a renderer to ship), or serve the guide from the image. Nothing changed.

**The identity key ring.** In the identity database of `vsaga-r2s` (`/var/lib/vsaga-dashboard/identity.db`) the
`DataProtectionKeys` table holds the key as XML: `<masterKey ... requiresEncryption="true">` followed by the key
in clear, with the framework's "unencrypted form" comment; the log carries no warning about it. That is what
`docs/dashboard.md` already says. **Decision needed from you**: whether vSaga should ship a configuration key for
`ProtectKeysWithCertificate` (for example a certificate path and password under `Dashboard:Identity`) or keep
leaving it to the deployer, who can add it to the Data Protection builder. Nothing changed.

**The Vitest `splitting` notice.** `@angular/build@22.2.1` prints "Option "splitting" is deprecated: No longer
needed with Vitest 5." on every `ng test`, with Vitest 4.1.11 and also with Vitest 5.0.4 installed (it is
printed by the builder, not by Vitest); Vitest 5 is `latest` on npm (5.0.4) and the builder's peer range is
`^4.0.8 || ^5.0.0`. With the option removed from `angular.json` and everything else as it is, in throwaway worktrees:
Vitest 4.1.11, 12 runs, 12 exit 0, 2437 specs, no `Cannot resolve '/hubs/saga'`; Vitest 5.0.4, 12 runs, the
same. With the machine under load (three suites at once, two on 4.1.11 and one on
5.0.4, 10 runs each, a few other builds running): 20 of 20 exit 0 on 4.1.11 and 10 of 10 on 5.0.4, none with the
hub failure. dd37bd1 measured 12 of 20 failing without the option; none of the 54 runs here did. **Decided and
made**: Vitest `^5.0.4` and no option (see "Decided" below); nothing proves the failure cannot return, and
`README.md` names `"splitting": false` and `"isolate": true` as the ways back.

## 6. An accessibility pass of the pages the last round touched

Chromium only; keyboard-only for the forms. What the accessibility tree and the attributes expose:

- **Setup** (`/setup`): a `banner` with the logo link, `main`, a `region` with the level-1 heading "Set up the
  dashboard", five labelled textboxes (Username, Display name, Password, Repeat the password, Setup code) and the
  button "Create administrator". The password hint ("At least 12 characters.") and the setup-code hint are
  `aria-describedby` of their fields; `autocomplete` is `username`, `name`, `new-password` twice and `off`; all five
  carry `aria-required="true"`. Tab order: logo link, the five fields in order, the button. Pressing Enter on the
  empty form shows "Enter a username.", "Enter a display name.", "Enter a password." and "Enter the setup code."
  (ids `setup-*-error`, set as `aria-describedby` of their fields, with `aria-invalid="true"` on them) and moves the
  focus to the username field. The form-level error banner is `role="alert"` in the template (a wrong code was
  seen to show it on 2026-10-13; it was not shown again here).
- **Sign in** (`/login`): the username field has the focus on arrival; both fields are labelled and
  `aria-required`. A wrong password gives a `role="alert"` banner ("Sign-in failed. Check the username and
  password; repeated failures lock the account for a while."), empties the password and leaves the focus on it.
- **Session expiry** (`/login?returnUrl=...&reason=expired`): the message "Your session expired. Sign in again to
  continue." is in a `role="status"` region and the username field has the focus.
- **The scoped list**: tab order status, kind and type selects, the search box, Refresh, the table's scrollable
  region (`region "Sagas table"`, `tabindex="0"`), the two sort buttons (`aria-sort` is bound on both columns and was `null` on all six headers under the default
  order), then
  one link per row. **Defect (`7363492`)**: the three selects had no name (`combobox` with only the selected
  option's text); now "Filter by status", "Filter by kind" and "Filter by saga type". A refused list's banner is
  `role="alert"`. The page-count text ("Page 1 of 2") and the row count are plain text, not announced when they
  change.
- **The detail page and the retry confirmation**: from the page start, 12 Tab presses reach "Retry this saga";
  Enter opens the confirmation with the focus on Cancel; "Yes, retry" and Cancel are both `aria-describedby`
  "Re-run step 1 (LoyaltyPointsAwarded, Start) for this saga only?" and "Other services that consume
  LoyaltyPointsAwarded still receive it."; Enter on Cancel puts the focus back on "Retry this saga". Escape does
  not close the confirmation. **Defect (`319eb75`)**: the Map / Timeline switch and the replay speeds were buttons
  whose chosen state was a CSS class; they now carry `aria-pressed`, like the data overview's At start / At end /
  Compare.

## What was fixed

| Commit | Defect | Spec and mutation |
| --- | --- | --- |
| `6b45335` | A failed `UpdateAsync` or `InsertAsync` left its entity tracked, so the dead-letter append re-sent it and the saga stayed `Running` with no `DeliveryExhausted` entry | 3 SQLite specs; `Detached` to `Modified`: 3 red |
| `ddcc688` | One follow-up fetch for a snapshot the engine may append up to 5 s late: "No snapshot was recorded" stayed on screen for a snapshot that existed | 2 specs; `scheduleSnapshotFollowUp` to `cancelSnapshotFollowUp`: 2 red |
| `7363492` | The list's three filter selects had no accessible name | 1 spec; `aria-label` to `title`: 1 red |
| `319eb75` | Map / Timeline and the replay speeds did not say which was chosen | 2 specs; `aria-pressed` to `aria-selected` on each: 1 red each |

## Decided

- `adf42af` moves the unit tests to Vitest 5.0.4 and removes `"splitting": false` (section 5). Not a defect: it
  closes the §13 question about the deprecation notice, on the evidence of 54 runs without the option.

## Seen and not changed

- The `DeliveryExhausted` entry's error text above says an instance "already exists" for a transient database
  error: `UpdateAsync` maps every `DbUpdateException` to `SagaAlreadyExistsException` (its comment says so) and the
  entry records the outer message; the cause is the inner exception. A reader of the timeline sees a wrong cause.
  **Decision for you**: map only the unique-index violation, or record the inner message too.
- A saga's first snapshot being late is not followed up (section 4).
- `ResetOnStart` adds the Administrator grant and leaves the others (section 3).
- The API's 400 text names "the sagaType filter"; the control is labelled "All saga types" and, since `7363492`,
  "Filter by saga type".
- The message of `7363492` says what a screen reader "reads" for the unnamed selects; that is from the
  accessibility tree, no reader was run.

## What was not exercised

- A screen reader, Firefox and WebKit.
- A retry through the dashboard over MassTransit or Wolverine that reaches the saga host (the dashboard host does
  not run those adapters).
- A saga whose first snapshot is late while it stays `Running` and idle with the follow-up working (it is not
  started, by design).
- A `maxPage` of 0 on Redis or MongoDB (the stack was Postgres); the bound is computed in the API, above the
  provider.
