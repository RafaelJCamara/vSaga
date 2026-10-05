# Dashboard user guide

> **Keeping this guide true.** A change that alters what the dashboard shows or does updates this guide and the
> tour step definitions
> ([`guide-tours.ts`](../dashboard-web/src/app/components/guide-overlay/guide-tours.ts)) in the same change: a
> label, a control, a banner, a status, a permission rule, a page or the outcome of an action that a section
> below describes. Bump the `version` of a tour's area in
> [`guide-areas.ts`](../dashboard-web/src/app/services/guide-areas.ts) when its copy changes, so people who saw
> the old tour see the new one once. The app links into this document by heading (the tours' **User guide** link
> opens the section of the area being explained: `docsAnchor` in `guide-areas.ts`), so renaming one of the
> headings the tours use means changing that file in the same change. The rule is also in
> [`CONTRIBUTING.md`](../CONTRIBUTING.md).

This guide explains the vSaga dashboard's web interface: what each page shows, what its labels, statuses and
banners mean, and how to do the things people do there: sign in, find a saga, read its timeline and its data,
retry a failed one and manage who may do what. The tours that **Guide** starts in the app are the short form of
the same text. What the HTTP API answers, every configuration key and the security model are in
[`dashboard.md`](dashboard.md) and [`configuration.md`](configuration.md#dashboard); this guide stays on the
screen.

## Opening the dashboard

The dashboard is a web page that the compose stack serves. From the repository root:

```bash
docker compose up -d --build
```

Then open <http://localhost:4200>. The first build is slow (it builds the page inside the `dashboard-web`
image), and the page is reachable once the dashboard API reports healthy. The demo's sample submits orders
continuously, so the list fills on its own. Docker is all it needs; see
["Run the demo"](../README.md#run-the-demo) for the other services.

Every stack serves its own page on its own port, always the API's port minus 880, so several stacks can run side
by side (their commands are under
["Running an adapter's own overlay"](transports/index.md#running-an-adapters-own-overlay)):

| Stack | Dashboard page |
| --- | --- |
| The base stack (and the chaos overlay, which reuses its ports) | <http://localhost:4200> |
| Wolverine | <http://localhost:4300> |
| MassTransit | <http://localhost:4400> |
| Brighter | <http://localhost:4500> |
| HTTP transport | <http://localhost:4600> |
| MongoDB persistence | <http://localhost:4700> |
| Redis persistence | <http://localhost:4800> |

Open the port of the stack you want to look at. Two stacks in one browser behave well, with one quirk: see
[Two stacks in one browser](#two-stacks-in-one-browser).

The API's own port (5080 in the base stack) is for scripts and probes, which sign in with an API key; the
dashboard page never uses it. See [API key](dashboard.md#api-key).

**What you see first.**

| Situation | What opens |
| --- | --- |
| You have no session | **Sign in**. After you sign in you land on the page you asked for, or on the saga list. |
| No user exists yet and no administrator is configured | **Set up the dashboard**: [first-run setup](#first-run-setup-and-the-setup-code). |
| You are signed in | The saga list. |
| The API cannot be reached | A banner on the sign-in page, which asks again every few seconds. |

**The top bar.** Once you are signed in it holds, left to right:

| Control | What it does |
| --- | --- |
| **vSaga Saga Dashboard** | Takes you to the saga list. |
| **Sagas** | The saga list. |
| **Administration** | The [administration area](#administration). Only people who hold **Manage access** see it. |
| **Guide** | A switch: [guide mode](#guide-mode) explains the saga list, a saga's page and the administration pages the first time you open each. |
| **Replay tour** | Appears while Guide is on and there is a tour to repeat. |
| **User guide** | Opens this document in a new tab. The link goes to the copy on GitHub's `main` branch (`USER_GUIDE_URL` in `guide-areas.ts`), so it needs internet access and can be newer than the build you run; the file in your checkout is the one that matches it. |
| Your name, with a small arrow | A menu: **Account** and **Sign out**. |

Times are shown in your browser's local time, and the page is in English.

## Signing in

Every page except **Sign in** and **Set up the dashboard** needs a signed-in user. A visit without a session goes
to **Sign in** and remembers the page, so signing in brings you back to it. The page has two fields, **Username**
and **Password**, and a **Sign in** button.

A refused sign-in always says the same sentence, whatever the reason (an unknown username, a wrong password, a
locked account or a disabled one), so that the answer does not tell anyone which usernames exist:

> Sign-in failed. Check the username and password; repeated failures lock the account for a while.

Other things the page can say:

| What you see | What it means |
| --- | --- |
| Too many attempts. Try again in N s. | A rate limit: 20 sign-in attempts a minute for one address and username (`Dashboard:RateLimit:AuthPerMinute`). |
| Cannot reach the dashboard API. Check that it is running, then try again. | The form is replaced by this banner; the page asks again every few seconds and shows the form when the API answers. |
| Sign-in is unavailable: the API is running, but its identity store is not ready. Ask an administrator to check the identity entry of the API's /health. | The API is up but the file that holds the users is not usable. Nobody can sign in until it is; see [Troubleshooting](#sign-in-is-unavailable-or-the-api-cannot-be-reached). |
| Your session expired. Sign in again to continue. | You were sent here because your session ended: [how long a session lasts](#how-long-a-session-lasts). |
| The administrator account was created. Sign in to continue. | Setup finished but the browser did not keep the session. |
| Your password was changed. Sign in with the new password. | The change ended the session. |
| Too many wrong passwords locked your account for a while, and you have been signed out. Try again later. | Wrong current passwords on the [Account](#your-account) page locked the account. |

### The seeded administrator

In the compose stack the first user already exists. `docker-compose.yml` seeds an administrator when the identity
volume is empty (`Dashboard__Admin__Username` and `Dashboard__Admin__Password`):

| Username | Password |
| --- | --- |
| `admin` | `dev-local-only-change-me` |

That password is public, which is why the stack binds the dashboard to `127.0.0.1`: only your own machine can
reach it. The seeded administrator is not forced to change the password, but change it on the
[Account](#your-account) page before anyone else can reach the dashboard. The seed applies only to an empty
identity volume: it creates the account once and never touches it again, so editing the password in the compose
file later changes nothing. To start over, `docker compose down -v` removes every volume of the stack, the saga
data included, and with them every user (the administrator is seeded again on the next start); to get back into
an account you cannot sign in to, see [Locked out of an account](#locked-out-of-an-account).

Outside compose the same thing happens when both `Dashboard:Admin:Username` and `Dashboard:Admin:Password` are
set and the identity store has no users.

### First-run setup and the setup code

When no user exists and neither seed key is set, every guarded page goes to **Set up the dashboard**, which
creates the first administrator. It asks for a **Username**, a **Display name**, a **Password**, **Repeat the
password** and a **Setup code**, and its button reads **Create administrator**. Afterwards you are signed in and
land on the saga list.

The setup code proves you control the API:

- The API generates a one-time code when it starts with no users and no seed keys, and writes it **once** to its
  log as a warning. It has 16 characters in four groups of four, such as `K7QD-M2XH-9TPA-W4RC`. In a container,
  `docker compose logs dashboard-api` shows it. (The demo stack seeds an administrator, so setup stays closed
  there unless you remove the `Dashboard__Admin__*` keys and start from an empty identity volume.)
- Or you choose it: `Dashboard:Setup:Code` presets the code (16 to 128 characters, not counting spaces and
  hyphens) so an unattended install knows it in advance. A preset code is never logged.
- Letters may be upper or lower case, and spaces and hyphens are ignored. There is no time window: the code
  works until a user exists, then never again. A restart before then generates a new code; a preset one stays.
- A missing or wrong code is refused under the **Setup code** field.

The username is 3 to 64 letters, digits or `. _ @ + -`, starting with a letter or a digit, and `api-key` is
reserved (in any case); it cannot be changed later. The password has at least 12 characters by default
(`Dashboard:Password:MinLength`, 8 to 128 when configured), at most 128, and is not the username.

If setup is not open, the page says **First-run setup is closed**: it is offered only while no user exists and
neither seed key is set. A banner adds the API's reason when it has one (a seed that could not be applied names
the setting to fix), and the **Check again** and **Go to sign in** buttons are under it.

### Locked accounts

Five wrong passwords in a row lock an account for 15 minutes (`Dashboard:Lockout:MaxFailedAttempts` and
`Dashboard:Lockout:Minutes`). While it is locked even the right password is refused, with the same sentence as
any other refusal, so you cannot tell a lock from a wrong password on the sign-in page. A successful sign-in or
an administrator's **Unlock** resets the count, and a wrong *current* password on the Account page counts as a
failure too.

An administrator sees a **Locked** chip on the account in the Users list, and **Locked until** with an
**Unlock** button on the user's page: see [Users](#users). A lock is also a weakness: anyone who knows a
username (the demo's `admin` is public) can keep it locked by failing five times every fifteen minutes. How to
end a lock early, and what does not, is under [Locked out of an account](#locked-out-of-an-account).

### Forced password change

An administrator who creates a user, or sets a user's password, normally leaves **Require a change at next
sign-in** ticked. That user signs in with the password they were given and is taken to the [Account](#your-account)
page, the only page open to them, under a banner:

> You need to choose a new password before you can use the dashboard.

Until they change it their access is empty: no saga list, no administration. After the change they are signed in
again and land on the saga list. The seeded and the setup administrator do not have to change theirs.

### How long a session lasts

| Limit | Default | What happens |
| --- | --- | --- |
| Idle timeout | 480 minutes (8 hours), `Dashboard:Session:IdleTimeoutMinutes` | Using the dashboard renews the session, but only once more than half of the window has passed since it was last renewed. A session that has been idle for less than half of the window never ends; one idle for all of it always does. |
| Absolute timeout | 24 hours, `Dashboard:Session:AbsoluteTimeoutHours` | A session ends this long after you signed in, however busy you were. Sign in again. |

The session lives in a cookie with no expiry date of its own, so a browser normally drops it when you quit the
browser. Restarting or recreating the API does not end it (the keys that protect sessions are kept in the
identity volume), and neither does a short outage: the page shows **Reconnecting to live updates…** and carries
on. These end a session at once: changing or resetting the password, disabling the user, deleting the user, and a
lock caused by wrong current passwords on the Account page.

The page learns that a session ended the next time it talks to the API: any request, the live connection
reconnecting, or you returning to the tab (it checks at most once a minute). It then goes to **Sign in** with the
page you were on remembered, under the notice "Your session expired. Sign in again to continue."

### Signing out

Open the menu with your name and choose **Sign out** (while it works, the menu button shows **Signing out…** in place of your name). The page
closes its live connection, ends this browser's session and goes to **Sign in**. Signing out does not invalidate a
copy of the cookie held somewhere else; changing the password, or an administrator disabling the account, ends
every session.

## Guide mode

Guide mode is a walkthrough built into the page. While it is on, the saga list, a saga's page (its summary, map,
timeline, saga data and retry row) and the administration pages each explain themselves the first time they are
shown, in a short tour of a few steps. The sign-in, setup and Account pages have no tour. It is off until you
switch it on, and it never starts a tour by itself while it is off.

### Turning Guide on and off

The **Guide** button in the top bar is a switch. The first time you open a page that has a tour, a small hint
hangs under it: "New here? Turn on Guide for a walkthrough of each page." with **Start the tour** and **No
thanks**. The hint does not take the keyboard focus and does not hide anything. Answering it either way, or
switching Guide on, ends it for good.

Switching Guide on treats everything as unseen again, then starts the tour of the page you are on. Switching it
off drops whatever is running or waiting, and does not mark the tour that was running as seen.

### When an area explains itself

A page is divided into areas, each with its own short tour. An area starts when it is first shown with Guide
on:

| Area | Starts when | Needs | Explained in |
| --- | --- | --- | --- |
| The saga list | You open the list. | **View sagas** for some saga type | [The saga list](#the-saga-list) |
| A saga's summary | You open a saga. | **View sagas** for its type | [Summary](#summary) |
| The map | The **Map** tab is shown. | **View sagas** for the type | [Map](#map) |
| The timeline | The **Timeline** tab is shown. | **View sagas** for the type | [Timeline](#timeline) |
| Saga data | A **Saga data** view (**At start**, **At end** or **Current**, **Compare**) or a step's **Data** panel is opened. | **View saga data** for the type | [Saga data](#saga-data) |
| Retry | The retry row (**Retry this saga**) is shown on a **Failed** or **TimedOut** saga. | **Retry sagas** for the type | [Retrying a saga](#retrying-a-saga) |
| Administration | The administration pages have loaded (not merely when the address opens). | **Manage access** | [Administration](#administration) |

An area you may not use never starts, and a step that explains something you may not use is left out of its tour,
so "Step n of m" counts only what you see. A tour also waits for its page: if nothing it points at is on the page
after about five seconds (the page failed to load, or it is empty), the tour is dropped without dimming the
screen and is not marked as seen.

A tour that is waiting its turn starts after the one that is running; **Done** moves to the next, whereas leaving
a tour with **Escape** or **Skip tour** drops the whole queue (those areas explain themselves the next time they
are shown).

Each area explains itself once. Opening the same page again, reloading it or coming back later does not repeat a
tour you went through or skipped. Switching Guide off and on again, clearing the browser's site data, or a new
version of a tour (its copy changed) does.

Switching Guide on while you are on a saga's page starts the summary and then only the part that was shown last
(for example the map). Parts that were shown earlier are not explained then; each explains itself the next time
it is shown, which for the retry row means the next time it appears (open the saga again from the list, or open
another failed saga).

### During a tour

A tour is a dialog over a dimmed page, with the element it explains highlighted. While it is open the page
behind it is inert: clicks outside the dialog do nothing, and Tab stays inside it.

| Control | What it does |
| --- | --- |
| **Next** / **Done** | The next step; on the last step, **Done**. Finishing the tour marks the area as seen and starts the next one that is waiting. |
| **Back** | The previous step (not shown on the first). |
| **Skip tour**, or **Escape** | Ends the tour, marks the area as seen and drops anything waiting. |
| **User guide** | Opens this document at the section of the area being explained (the same GitHub copy as the top bar's link). |
| Right and Left arrow | Next and previous (Right does nothing on the last step: use **Done**). |
| Tab and Shift+Tab | Move between the dialog's controls and wrap around. |

Focus goes to the dialog's primary button when a step opens, and returns to where it was when the tour ends (or
to the **Guide** switch if that element is gone). Under the browser's "reduce motion" setting the dialog does not
animate. A tour ends without being marked as seen when you leave the page, when you switch Guide off, or when
your session no longer holds the permission the area needs.

### Replay and what the browser remembers

**Replay tour** repeats the last part that was announced, and only that: the page's own area until another part
is shown, then the map, the timeline, the data or the retry row, whichever was shown last. It appears only while
Guide is on, the page has an area you may see (the saga list, a saga's page, the administration pages) and no
tour is running or waiting.

Guide mode keeps one entry in the browser's `localStorage`, under the key `vsaga.guide`: whether Guide is on,
which version of each area's tour you have been through, and whether you answered the hint. It is stored per
origin (host and port), so each compose stack keeps its own, and not per user: someone who signs in as another
user in the same browser keeps the Guide setting. Clearing the site's data forgets it, so Guide is off and the
tours show again. If the browser blocks storage, guide mode still works but forgets everything when the page is
reloaded.

## The saga list

The list is the first page after signing in and the **Sagas** link of the top bar. It shows one row per saga
instance that your account may see, across all saga types, newest update first. The heading carries the count
(**N total**), and the rows update live.

### What the list shows

| Column | What it holds |
| --- | --- |
| **Correlation Id** | The first eight characters of the saga's correlation id, then `…`. It is a link, and the only part of a row that keyboard users tab to. |
| **Saga Type** | The saga's type name. |
| **Kind** | **Orchestrated** or **Choreographed**: see below. |
| **Current State** | The state the saga is in. |
| **Status** | One of the seven [statuses](#statuses-and-kinds). |
| **Updated** | When the saga last changed, in your local time. |

Click anywhere in a row, or press Enter on its correlation id, to open the saga's page.

### Statuses and kinds

| Status | In plain words |
| --- | --- |
| **Running** | In progress. It can still change. |
| **Completed** | Finished its work. |
| **Failed** | Ended in failure: a step threw and its retries ran out, a message could not be processed after its redeliveries (it was dead-lettered), or a step ended the saga as failed, as a declined payment does. |
| **Compensating** | Undoing earlier work. |
| **Compensated** | Finished undoing; it ended without completing. |
| **TimedOut** | Waited too long in a state and ended as timed out. |
| **Cancelled** | Ended on purpose. |

The engine itself writes only **Running** and **Failed**. Every other status is the one the saga's own definition
finalizes with, so what **Compensating**, **Compensated**, **TimedOut** and **Cancelled** mean for a saga type is
what its author decided. Only **Failed** and **TimedOut** sagas can be [retried](#retrying-a-saga). The statuses
are listed in the order **Status** sorts by, which is the lifecycle order from **Running** to **Cancelled**.

| Kind | Meaning (also the badge's hover text) |
| --- | --- |
| **Orchestrated** | vSaga's engine drives this saga's steps directly. |
| **Choreographed** | vSaga observes this saga's progress through events; it does not drive it. |

### Filtering, searching, sorting and paging

- **Filters.** Three drop-downs (**All statuses**, **All kinds**, **All saga types**) and a search box ("Search
  by type or correlation id…"). The search is a case-insensitive substring match on the saga type and on the
  correlation id, so a part of an id works, and it runs as you type. **Refresh** reads the list again.
- **Sorting.** Select the **Status** or **Updated** heading to sort by it (an arrow shows the direction) and
  select it again to reverse. The first selection sorts ascending: **Status** starts with **Running**, and
  **Updated** starts with the oldest, the opposite of the default. The server sorts the whole result, not just
  this page. Without a choice the list is newest update first.
- **Paging.** **Rows per page** (25, 50, 75 or 100), **Previous**, **Next**, "Page X of Y" and **Go to page**
  (type a number and press Enter).
- **The address holds the view.** Status, kind, saga type, search, sort, page and rows per page are in the page's
  address, so a bookmarked or shared link opens the same view. A value the page does not know is ignored.

### Live updates and banners

New and changed sagas arrive without a refresh. A row that changes updates in place; a new saga that matches
your filters is added to page 1 where the sort puts it.

| Banner | What it means |
| --- | --- |
| Reconnecting to live updates… (warning) | The live connection dropped (the API restarted, say) and the page keeps trying to re-establish it. |
| Live updates disconnected. **Refresh** (warning) | The live connection, which had worked, is closed. The rows may be out of date: use **Refresh**. |
| N new saga(s) since this page loaded — **Refresh** (info) | You are past page 1, where the page does not move the rows you are reading. **Refresh** reads the list again. |
| Could not reach the vSaga Dashboard API. Is it running? (error) | The list could not be read. It clears when live updates come back. |
| You do not have access to these sagas. (error) | The API refused the list for your session. |
| The API could not list sagas for these filters. (error) | The API refused this request and gave no reason of its own. |

An empty result says "No sagas match these filters yet."

### When your access is limited

What you see follows your [grants](#grants-and-scope), and the API enforces it, not the page:

- The list, the **All saga types** filter and a saga's links show only the saga types you may view. A saga type
  you may not view is not refused in the list; it is simply not there.
- With no view permission at all the page says: "Your account has no access to any saga type yet. Ask an
  administrator for sagas.view."
- If your access covers several saga types but not all, the API merges them. A list sorted by **Status**, filtered
  by status or kind, or searched can combine at most 10 of those types and reach 500 rows (page times rows per
  page); the plain list by **Updated** at most 50 types and 10,000 rows. Past a bound the page shows the server's
  message, goes back to the last page it could read and asks you to choose a saga type with the saga type filter,
  which always works.
- Opening a saga of a type you may not view shows: "You do not have access to `<type>` sagas. Ask an
  administrator for sagas.view on this saga type." Without **View saga data** the business data is withheld
  ([Saga data](#saga-data)), and without **Retry sagas** there is no retry button.

## The saga detail page

Select a saga in the list to open its page. The **← All sagas** link goes back. The page shows a
[summary](#summary), the retry row when a retry is possible, the **Saga data** bar and two tabs, **Map** (the one
it opens on) and **Timeline**. The page keeps its place in its address: `?tab=timeline`, `?entry=` followed by a
timeline entry's sequence number, and `?data=` with `start`, `end` or `compare` for the open **Saga data** view.
Back and Forward therefore move between tabs and focused entries, and a link or a reload lands on the same view.

The live connection keeps the page current. A banner under the **← All sagas** link says **Reconnecting to live
updates…** while the connection is re-established and **Live updates disconnected.** when it is down after having
worked. A saga that cannot be loaded says "Could not load this saga. It may not exist."

### Summary

The card at the top shows:

- The saga's **type** (the page heading), its **correlation id**, the **kind** and **status** badges.
- **Current state**, **Version** (the saga's save counter: the engine raises it each time it stores the saga's
  state, and a retry uses it to notice a saga that moved) and **Created** and **Updated**, each with your
  time zone in the label (`Created (UTC+02:00)`); hover a time for UTC.
- **Also tracking this correlation id**: other saga types that track the same business transaction, each a link
  with its kind, status and state. A correlation id alone does not identify a saga, because two saga types may
  track the same one.
- **Started by**: the saga that started this one as a sub-saga, as a link (plain text with "(no access)" when you
  may not view its type).
- **Started N sub-saga(s)**: the sagas this one started, as links.

At the bottom of the card, a **Failed** or **TimedOut** saga shows the retry row
([Retrying a saga](#retrying-a-saga)).

### Map

The **Map** tab draws the saga as a service graph: the boxes are the parties to it (the saga itself, whoever
started it and each participant), the arrows are the messages between them, and the replay shows them in the order
they were recorded.

| Element | Meaning |
| --- | --- |
| Box with a highlighted border | The saga itself (the orchestrator). |
| Dashed box labelled `?` | A destination the dashboard could not resolve to a known service (see [Saga Map](dashboard.md#saga-map) for how services are identified). |
| Solid arrow (**Answered**) | A message that got a reply. |
| Dashed arrow (**No response**) | A message nothing answered. |
| Dotted arrow (**Compensation**) | Part of undoing earlier work. |
| Red arrow (**Failed hop**) | The hop that failed. A box gets a red border when the failure touches it. |

During a replay, boxes and arrows the replay has not reached yet are faint, the entry being shown is in the accent
colour and what already happened is grey.

The controls under the legend replay the history. They are icon buttons whose tooltips name them: **Restart**
(⇤), **Play** (▶, which becomes **Pause**, ❚❚) and **Step** (⇥, one entry forward), then a slider to jump to any
entry and speed buttons of 0.5×, 1×, 2× and 4×. Playback stops at the failure.
A line under the controls names the entry shown (`#3/12 — StepSucceeded · PaymentCharged · recorded at …`).
Once the replay reaches the failure, a card shows its entry type, and the exception text when you may see saga
data.

**Opening on an entry.** Clicking a timeline entry opens the map as of that entry, and so does `?entry=` in the
address; for a **Failed** or **TimedOut** saga opened without one, the map opens on its failure. A banner then
reads "As of entry #12 of 34: StepSucceeded, recorded at 14:03:07.140 (+1.224 s)", with **Back to this entry in
the timeline**. Further lines can follow it:

- "Nothing moved between services at this entry, so `<saga>` is highlighted." for a plain event such as a step's
  outcome, which has no arrow of its own.
- "The selected entry is not on the map yet; showing the closest earlier entry." when the map you have is older
  than the entry (the page fetches the map again).
- "The selected entry is not on this saga's map; showing its first entry." when `?entry=` names an entry the
  saga does not have.

Playing, stepping, restarting or dragging the slider takes the replay over from the focus.

A saga that failed on its very first outbound message has nothing to draw but one box. The page then adds a card
headed **This saga failed with nothing to map**.

### Timeline

The **Timeline** tab lists what the engine recorded, grouped into numbered steps. A line above it says that times
are in your local time with the zone (`UTC+02:00`), that hovering a time shows UTC and that the offset counts
from the saga's first entry.

**Steps.** A step starts when the saga starts, receives a message, fires a timeout, is retried or dead-letters a
message, and holds the entries that step caused. Its header reads **Step N**, a title, an outcome, and sometimes
a marker and a **Data** button:

| Title | The step began with |
| --- | --- |
| Started by `<message type>` | The message that created the saga. |
| `<message type>` | A message the saga received. |
| Timeout in `<state>` | A state timeout that fired. |
| Manual retry of `<message type>` | A retry someone requested. |
| `<message type>` dead-lettered | A message that could not be processed after its redeliveries and never reached the saga. |

| Outcome | Meaning |
| --- | --- |
| · succeeded | The step finished and the saga moved on (possibly to the same state). |
| · failed | An action in the step threw. |
| · not handled | The saga received a message its current state does not handle. |
| · dead-lettered, · never handled | The message could not be processed after its redeliveries; **never handled** when the saga never got to handle it. |
| · requested by `<actor>` | A retry: see [Who asked](#who-asked). |
| · in progress | The last step of a **Running** or **Compensating** saga that has no outcome yet. |
| · no outcome recorded | A step with no outcome and nothing still running. |

A step's title is a button: it opens the map as of the end of that step. **Failed here** marks the step the saga
failed in, and **Re-run starts here** the step a retry would re-run when that is a different one (a timeout
fails in a later step than the one it re-runs). Both come from the [retry plan](#how-the-step-is-chosen), which
needs only **View sagas**, so every viewer sees the markers whether or not they can retry.

**Entries.** Each row shows its position (`#3`, the same number the map uses), the entry type, the states it moves
between, the message type and the service it went to or came from. Select a row to open the map as of that entry;
a row that records a failure is shown in the error style. **Recorded at** is when the engine wrote the entry:
`HH:mm:ss.SSS` in your local time (with the date when it differs from the first entry's), UTC on hover, and the
offset from the saga's first entry (`+1.204 s`, `+2:05.300`, `+1:02:03`, `+2d 01:02:03`). Entry types:

| Entry type | What it records |
| --- | --- |
| `SagaStarted` | The saga was created by a message. |
| `MessageReceived` | A message arrived for the saga. |
| `UnexpectedEvent` | A message the saga's current state does not handle. |
| `StepSucceeded` | A step finished: the states it moved between. |
| `StepFailed` | A step threw an exception; the exception text shows when you may see saga data. |
| `CompensationStarted`, `CompensationStepSucceeded`, `CompensationStepFailed` | Undoing earlier work, and how each undo went. |
| `MessagePublished`, `MessageSent` | The saga published a message to whoever subscribes, or sent one to a named destination. |
| `ChildSagaStarted`, `ChildSagaFinished` | A sub-saga was started (on the parent's timeline), or a sub-saga that ended through an unhandled exception or a timeout told its parent so (on the sub-saga's own timeline). |
| `TimeoutScheduled`, `TimeoutFired` | A state timeout was set, or ran out. |
| `ManualRetryRequested` | Someone retried the saga from the dashboard. |
| `DeliveryExhausted` | A message was dead-lettered after its redeliveries ran out. |
| `SagaFinalized` | The saga ended; its `to` state carries the outcome. (Stored as `SagaCompleted`. The engine writes it for every ending, a failure included, and "completed" next to a red **Failed** badge would read as a contradiction.) |

The log can hold other entry types too; the page shows each under its own name. The saga's data after each step is
stored as `StatePersisted` entries; they are never rows, they become the step's **Data**.

### Saga data

Data is the saga's own business state, and what its messages carried. It needs **View saga data** for the saga's
type; without it the data is withheld by the API and the page says so: "Saga data is hidden for your role. It
needs the sagas.data permission." The **Saga data** bar shows its three buttons disabled beside that sentence,
steps show no **Data** button, and exception text is left out of the timeline and the map.

**The Saga data bar**, under the summary card:

| Button | Shows |
| --- | --- |
| **At start** | The message that started the saga, and the state after the first step that recorded one. |
| **At end** (reads **Current** until the saga's status is final, that is while it is **Running** or **Compensating**) | The state the saga is stored with now. |
| **Compare** | The first recorded state against the stored one: what changed since the start. Disabled, with a tooltip, when either side is missing. |

Select a button again to close it.

**A step's Data.** On the Timeline each step has a **Data** button that opens that step's inspector under it: the
saga's data as it was saved when the step finished. A view has:

- **Changes**: a table of the fields that differ from the nearest earlier recorded state (**Field**, **Before**,
  **After**, with `+`, `−` and `~` for added, removed and changed). The state's `Version` and `UpdatedAtUtc`,
  which the engine rewrites on every save, are listed on a separate line, "Engine bookkeeping also changed". When
  nothing differs it says so.
- **Full state**: all of it.
- **Message**: the message that ran the step, where it was recorded. Only a step's **Data** has it.
- **Copy JSON**: copies the stored text exactly (shown when the browser allows copying).

**When a step has no data to show**, the inspector says why:

| What it says | Why |
| --- | --- |
| The state was too large to snapshot at this step (N bytes, limit L). | The state exceeded the per-snapshot cap. |
| The state was not snapshotted at this step: the saga's snapshot budget of B bytes was used up. | The saga's snapshots reached their budget; the ones after a failed step are still recorded in full. |
| This step did not persist a new state, so the data is unchanged. | An unhandled message, a retry request, a timeout nobody handled. |
| Not recorded yet. The step may still be committing; this view refreshes by itself. | The saga is still running and the step's newest entry is less than five seconds old. |
| No snapshot was recorded for this step: the saga ran before snapshots existed, they are switched off, or the step lost a concurrent update and never committed. | Snapshots are best effort and cost storage; see [State snapshots](dashboard.md#state-snapshots). |

### Retrying a saga

A retry re-runs **one step, the one the saga failed in, for this saga only**. The retry row sits at the bottom of
the summary card of a **Failed** or **TimedOut** saga, for people who hold **Retry sagas** for its type. Anyone
else, on a saga a retry would accept, reads "You do not have permission to retry `<type>` sagas." there instead.

#### How the step is chosen

The dashboard works out the step from the saga's timeline: it takes the **latest** failure entry, so a saga that
failed technically, was retried and then failed for a business reason re-runs the later step.

| What happened (the plan's name) | The step that re-runs | The saga is first put back to |
| --- | --- | --- |
| A step threw an exception (`StepFailed`) | That step, with the message it was handling | The state it was in before the step |
| A message was dead-lettered (`DeliveryExhausted`) | That message | The saga's current state: the step never committed |
| A step ended the saga as failed without an exception, such as a declined payment (`BusinessFailure`) | The step whose message ended the saga | The state before that step |
| A state timeout fired (`TimedOut`) | The step that entered the timed-out state, which is an earlier step than the timeout's | The state before that earlier step |

When no retry is possible the button is dimmed and the reason is beside it:

| Reason | Meaning |
| --- | --- |
| No failed step could be identified in this saga's timeline. | There is no failure entry to start from. |
| The timed-out state was entered by a timeout, which has no message to replay. | A timeout moved the saga there, so there is no step with a message. |
| This saga was recorded before vSaga stored the message of every step, so the `<message type>` message that ran the step to re-run cannot be replayed. | The message body was never recorded. A dead-lettered message adds "The message may also have been dead-lettered before it was recorded at all." Sagas recorded before this was stored stay retryable only when the failing step threw or was the first step. |
| The message that ran this step was too large to be recorded, so it cannot be replayed. | MongoDB stored a size marker instead of the message. |

#### The confirmation

**Retry this saga** asks before it does anything:

> Re-run step 2 (PaymentFailed, Gathering) for this saga only?
> Other services that consume PaymentFailed still receive it.

The step number is the **Step N** of the timeline; the first name in the brackets is the message type that ran the
step, and the second is the state the saga goes back to first. **Yes, retry** goes ahead (focus starts on
**Cancel**, so a held Enter does not retry), and **Cancel** leaves the saga alone.

#### What the outcomes mean

| Outcome | What the page shows beside the button | What it means and what to do |
| --- | --- | --- |
| **202 Accepted** | Retry accepted — redriving the failed step. | The saga is back to the state before the step with status **Running**, and the step's message was published again, addressed to this saga type. The retry row goes away while the saga runs (it comes back if the saga ends **Failed** or **TimedOut** again). Watch the status and the timeline; the new step is **Manual retry of `<message type>`**, followed by the re-run. |
| **409 Conflict** | The API's own sentence: "Saga '…' instance '…' cannot be retried while its status is '…'; only 'Failed' or 'TimedOut' sagas can be retried." or "Saga '…' instance '…' was modified concurrently with this retry; reload and try again." | The saga is no longer in a state that can be retried: someone else retried it, or it moved. Nothing is written when the status was the problem. When the saga moved between the API's read and its reset, a **Manual retry** step stays in the timeline with nothing after it. Reload and look at where the saga is now. |
| **422 Unprocessable** | The plan's reason (the ones above). | The page normally stops you first by dimming the button. You see a 422 only if the saga changed after the page loaded its plan, or the page could not load the plan (the API then decides and says why not). Nothing is written. |
| **502 Bad Gateway** | "Saga '…' instance '…' could not be retried: *the cause* The saga was restored to state '…' with status '…'." | The message could not be published (for example the broker did not answer). The dashboard puts the saga back as it found it and says whether that worked. A **Manual retry** step stays in the timeline with nothing after it. Try again when the broker is back. If the sentence says the saga could not be restored instead, follow it (reload the saga, or read the dashboard API log): a saga left **Running** with no redrive in flight is no longer **Failed**, so the dashboard cannot retry it again. |
| **403 Forbidden** | "This needs the sagas.retry permission for saga type '…'. See docs/dashboard.md#authentication." | Your access changed after the page loaded. |

Anything else shows the server's text, or "Retry failed." when it sent none.

#### What a retry does not undo

- **Business fields are not rolled back.** Only the saga's current state and status go back; what **Saga data**
  shows stays as it was. A step that appends to a list or increments a counter does so again.
- **Every side effect the step has happens again**: its publishes, `.CallHttp` calls, sub-sagas it starts and
  compensations it runs.
- **Other services that consume the same message still receive it.** The redrive is a publish of that message
  type, so a participant that is not a saga receives it as a new message and may act on it again, and another
  saga type subscribed to the same message type receives it too. Another saga type acknowledges it and ignores it
  when its host's engine is new enough: see [The engine version a targeted retry needs](#the-engine-version-a-targeted-retry-needs).
- **A failure decided by the message alone fails again.** A retry re-runs the step with the same message. When the
  failure came from what the message says, as in the demo's `OrderSaga`, where a declined payment arrives as a
  `PaymentFailed` message that ends the saga as failed, the re-run reaches the same outcome and the saga returns
  to **Failed**, with the new step visible in the timeline. A retry helps when the outcome depends on something
  that has changed since (a participant, a lookup, configuration, a fixed bug), or when the step was interrupted
  (an exception, a dead-letter, a timeout waiting for a reply that can now arrive).

For example, retrying a timed-out `InvoiceFollowUpSaga` re-runs the step that entered the state that timed out, its
first step, which replays the `InvoiceIssued` message: **Failed here** marks the timeout step and **Re-run starts
here** that first one, and because the first step runs again its own side effects do too. `PostShipmentChoreography`,
which handles the same `InvoiceIssued` message under the same correlation id, ignores the replay, so no second
`InvoiceDeliverySaga` starts.

#### Who asked

A retry is recorded in the saga's timeline with who requested it. The **Manual retry** step reads "· requested by
`<username>`", and its entry row says `from dashboard:<username>`. A retry made with the API key reads "requested
by api-key" (`dashboard:api-key`, a name no user can take). A retry with no recorded requester, such as one made
in-process by the host, reads just "requested".

## Administration

The administration area manages who may sign in and what each person can see and do. The **Administration** link
and the pages behind it exist only for people who hold **Manage access** for all saga types; anyone else who opens
an administration address is sent to the saga list. The area has three tabs, **Users**, **Teams** and **Roles**,
each a list with **New user**, **New team** or **New role** and a page per item. The API key never holds Manage
access, so a script that uses it cannot call the administration endpoints either.

A change made here takes effect on the user's next request. The API closes the live connections of the people
affected, their page reconnects, and what they see follows. Changing a password or disabling an account ends that
user's sessions.

### Users

The list has a **Filter by username, name or team** box and a count (**N total**, or **2 of 5** while filtering):

| Column | What it holds |
| --- | --- |
| **Username** | A link to the user ("(you)" marks your own account). |
| **Display name** | The person's name. |
| **Status** | Chips: **Disabled** (cannot sign in and holds no access), **Locked** (too many wrong passwords) and **Must change password**. |
| **Teams** | The teams the user is in, as links, or **None**. |
| **Access** | The user's *own* grants, one line each such as `Operator · all types` or `Viewer · 2 types`, or **None directly**. |
| **Last sign-in** | A date and time, or **Never**. |

**New user** asks for a **Username** (the rules are under [first-run setup](#first-run-setup-and-the-setup-code);
it cannot be changed later), a **Display name**, a **Password** and its repeat (share it with the user: it cannot
be shown again) and **Require a change at next sign-in**, ticked by default. The user's page then has:

- **Enabled** (untick to disable: the user cannot sign in and holds no access, and any open session ends). You
  cannot disable your own account.
- **Teams**: read-only here. Membership is changed on the team's page.
- **Access**: the user's own [grants](#grants-and-scope).
- **Effective access**: what the user would hold with the grants as edited, saved or not, with their teams'
  grants included, and where each permission comes from (`direct: Operator`, `team Payments: Viewer`). Until a
  user who must change their password has done so they hold no access; the preview shows what they hold
  afterwards.
- **Save** (or **Create user**).

An existing user's page also has an **Account** section:

- **Reset password** opens a panel with **New password**, its repeat and **Require a change at next sign-in**
  (ticked by default); **Set password** applies it. It ends the user's sessions, yours included if it is your own
  account (use [Your account](#your-account) to change your own password). It does not clear a lock: select
  **Unlock** as well when the account shows **Locked**.
- **Locked until** *time* and **Unlock**, shown while the account is locked.
- **Delete user**, which asks "Delete the user X? Their grants go with them. This cannot be undone." You cannot
  delete your own account.

### Teams

A team is a named group of users. Its members hold the team's access on top of what they hold directly. The list
shows each team's **Name** (and description), **Members** (a count, or **No members**) and **Access** (its grants,
or **No access**).

A team's page has a **Name**, a **Description**, **Members** (a checklist with a **Filter by username or name**
box; disabled users carry a **Disabled** chip), **Access of every member** (the [grants](#grants-and-scope)) and a
preview, **What the team gives its members**. **Save** replaces the team's members and access as a whole with what
the page shows. **Delete team** asks "Delete the team X? Its members lose the access it gives them. This cannot
be undone."

### Roles and permissions

A role is a set of permissions. There are four:

| Permission | Label | Allows | Applies |
| --- | --- | --- | --- |
| `sagas.view` | **View sagas** | The list, a saga's summary, its timeline and map without payloads, state or error text, its sub-sagas, the retry plan and live updates. | Per saga type |
| `sagas.data` | **View saga data** | The saga's state, its messages' payloads and exception text. Includes **View sagas** for the same saga types. | Per saga type |
| `sagas.retry` | **Retry sagas** | The retry button. Includes **View sagas** for the same saga types. | Per saga type |
| `access.manage` | **Manage access** | The administration area. | Only in a grant for all saga types |

Three roles are built in, and cannot be changed or deleted:

| Role | **View sagas** | **View saga data** | **Retry sagas** | **Manage access** |
| --- | --- | --- | --- | --- |
| **Administrator** | yes | yes | yes | yes |
| **Operator** | yes | yes | yes | no |
| **Viewer** | yes | yes | no | no |

A custom role is any non-empty combination of the four, under a name of 1 to 64 characters that no other role has
(ignoring case) and an optional description. A role with **View sagas** alone shows people statuses without the
business data. The roles list shows each role's **Name**, **Type** (**Built-in** or **Custom**), **Permissions**
and **In use** ("Used by N grants" or "Not used"). A built-in role's page is read-only under "A built-in role
cannot be changed or deleted. Duplicate it to start a custom role with the same permissions." with **Duplicate as
custom role**. A custom role cannot be deleted while a grant uses it (the page says how many).

### Grants and scope

People hold access through **grants**. A grant pairs one role with a scope: **All saga types** (every type,
including types that have not run yet and types added later) or **Selected saga types** (1 to 100 named ones).
A user and a team each hold at most 20 grants, one per role. A user's effective access is the union of their own
grants and those of every team they belong to.

The grants editor, on a user's page and on a team's page:

- **Add grant** adds a grant for the role that gives the least access, scoped to **Selected saga types** with
  none selected yet ("Pick at least one saga type." until you do); **Remove** deletes one. A role appears in at
  most one grant, so the **Role** list offers the roles not used by the others.
- Under **Selected saga types**, tick the saga types the dashboard knows. For one that has not run yet, type its
  **Exact saga type name** and press **Add type**; case matters, because names are matched exactly. A type the
  grant names that is not known stays listed until you untick it.
- A role's **Manage access** is ignored in a scoped grant: the editor says so ("access.manage is ignored in a
  scoped grant: it counts only for all saga types").

**A worked example.** The team `payments` holds **Operator** for `InvoiceFollowUpSaga` only. Dana is a member of
`payments`, and also holds **Viewer** directly for `OrderSaga` only. Her effective access, as the **Effective
access** preview shows it and as her own [Account](#your-account) page lists it, is:

| Saga types | What she may do |
| --- | --- |
| `InvoiceFollowUpSaga` | **View sagas**, **View saga data** and **Retry sagas** |
| `OrderSaga` | **View sagas** and **View saga data** |

There is no **All saga types** row, because nothing is granted for every type. The preview also names where each
permission comes from: `team payments: Operator` for the first row and `direct: Viewer` for the second.

What that means on screen:

- Her saga list, its **All saga types** filter and every link show those two types and no others.
- Opening a `PostShipmentChoreography` saga says "You do not have access to PostShipmentChoreography sagas. Ask an
  administrator for sagas.view on this saga type." Pasting the address of one she knows changes nothing: the API
  refuses it before reading it.
- A **Failed** `InvoiceFollowUpSaga` shows **Retry this saga**. A **Failed** `OrderSaga` shows "You do not have
  permission to retry OrderSaga sagas." instead.
- There is no **Administration** link. Neither **Operator** nor **Viewer** includes **Manage access**, and that
  permission counts only in a grant for all saga types in any case.

Now change Dana's own **Viewer** grant from **Selected saga types** to **All saga types** (a user holds one grant
per role, so the grant is edited, not added again). She can open every saga and read its data, and she can still
retry only `InvoiceFollowUpSaga` sagas, because what a team gives is added to what she holds and takes nothing
away. If `payments` loses its grant, Dana's next request already has no access to `InvoiceFollowUpSaga`; her live
connection is closed and her page reconnects under the new access.

### The last-administrator rule

The dashboard refuses any change that would leave **no enabled user who holds Manage access for all saga types**:
deleting or disabling the last one, removing their grant or their membership of the team that gave it, editing or
deleting that team, and editing or deleting a custom role they depend on. The page shows an error banner in the
server's words, "This change would leave no enabled user who can manage access for all saga types.", followed by
"Give another enabled user an all-saga-types grant whose role includes access.manage, then try again.", and keeps
what you typed. If you remove your own access to administration, a warning banner says so first ("You are removing
your own access to administration. If you save, you will no longer be able to manage access.").

## Your account

Open your name's menu and choose **Account**. The page shows:

- **Signed in as**: your **Display name** and **Username**.
- **Change password**: **Current password**, **New password** ("At least 12 characters, and not the same as the
  current one." with the default policy) and **Repeat the new password**; the button reads **Change password**
  (**Changing…** while it works). A wrong current password is refused under its field and counts against the
  [lock](#locked-accounts); if it is the failure that locks the account, you are signed out and the sign-in page
  says why. After a successful change you are signed in again, your other sessions end and you land on the saga
  list.
- **Your access**: a table of the saga types you hold permissions for (**All saga types** first, then each saga
  type granted on its own), with **What you may do** as chips such as **View sagas** and **Retry sagas**. If only
  some saga types are listed, "Saga types that are not listed are not available to you." With nothing it says "You
  hold no permissions yet. An administrator can grant you access.", and while you must still change your password
  "Your access is empty until you have changed your password."

When you must change your password, this is the page you are sent to, under the banner "You need to choose a new
password before you can use the dashboard."

## Troubleshooting

### Locked out of an account

Five wrong passwords lock an account for 15 minutes, and the sign-in page cannot tell you so (it gives the same
sentence as for a wrong password). What ends a lock:

| Way | Ends a lock in force? |
| --- | --- |
| Another administrator signs in and selects **Unlock** on the user's page (Administration, **Users**). | Yes, at once. |
| Starting the API with `Dashboard:Admin:ResetOnStart=true` and both seed keys (`Dashboard:Admin:Username` naming the locked account and `Dashboard:Admin:Password` the password you want). In compose the seed keys are already set: add `Dashboard__Admin__ResetOnStart: "true"` to the `dashboard-api` environment, put the password you want in `Dashboard__Admin__Password` and run `docker compose up -d`. The start sets that password, enables and unlocks the account, clears a forced password change, ends its sessions and gives the account an Administrator grant for all saga types (its other grants stay; an account that does not exist yet is created). Whoever you name becomes an administrator, so name an administrator's account. A password the policy rejects resets nothing, and the `identity` entry of `/health` says so. It repeats at every start while it is `true`, so set it back to `false` once you are in. | Yes, at once. |
| Waiting out `Dashboard:Lockout:Minutes` (15 by default). | Yes, when it passes. |
| Restarting with `Dashboard:Lockout:MaxFailedAttempts=0`. | **No.** It only stops *new* locks; a lock already in force still refuses sign-in until it ends. Use it to stop someone locking an account again while you do one of the above. |

If you only forgot a password, ask an administrator to reset it (**Users**, the user, **Reset password**); the user
then chooses a new one at next sign-in unless the box was unticked. A reset does not clear a lock, and someone who
forgot a password has often tried it enough times to be locked, so the administrator also selects **Unlock** when
the account shows **Locked**. If you are the only administrator and forgot
yours, use `ResetOnStart` as above. More in [Passwords, lockout and rate limits](dashboard.md#passwords-lockout-and-rate-limits).

### Two stacks in one browser

Browsers scope cookies by host name, not by port, so <http://localhost:4200> and <http://localhost:4300> share
one cookie jar. Compose names each stack's session cookie after its project (`vsaga.session.vsaga`,
`vsaga.session.vsaga-wolverine`) and each stack has its own antiforgery cookie, so both stay signed in and each
keeps its own sign-in, even after a reload.

They do share one cookie, `XSRF-TOKEN`: the readable cookie whose value the page copies into every change it
sends. Each stack's sign-in and session read overwrites it, so the first change you make after switching to the
other stack carries the other stack's token and the API refuses it once (HTTP 400, `antiforgery`). The page
recovers by itself: it reads the session again, which issues the right token, and sends the same request once
more. You see nothing, apart from one failed request per switch in the browser's developer tools (Chromium also
logs one console error); a second change in the same tab needs no retry. Only if the second attempt fails as well
does the page show the server's message. Reloading the page reads the session and fixes the token.

For two stacks without that failed request, give each its own cookie jar: a separate browser profile (or a
private window), or reach one of them by another host name, for example <http://127.0.0.1:4300> beside
<http://localhost:4200>, since cookies are kept per host name.

### The engine version a targeted retry needs

A dashboard retry republishes the failed step's message and marks it with the header `x-vsaga-target-saga-type`,
so that every saga type other than the retried one acknowledges it and ignores it. Only an engine that contains
that check does so (`MessageEnvelope.TargetSagaTypeHeader` in `VSaga.Abstractions`, read by the saga engine in
`VSaga.Core`). A host running an older engine ignores the header and processes the replay as a new message, with
nothing on the dashboard to show it.

There is no version number to give yet: the repository has no release tags, so the minimum is the first release
that contains the targeted redrive, and it applies to every host that runs a saga type subscribed to a replayed
message type. The dashboard does not check it: it cannot see which engine a host runs. The compose stack builds
the API and the sample hosts from the same source, so there it always holds. To tell for a host you run, retry a
saga whose message another saga type also handles and look at that other saga type. An engine that has the check
logs "Ignoring `<message type>` `<message id>` for saga `<saga type>`: it is targeted at saga type `<target>`" (at
Debug level, so only where the host's log level shows it) and writes nothing to the saga's timeline, whereas an
older engine processes the message and the timeline gains new entries. Details in
[Manual retry](dashboard.md#manual-retry) and [ADR 0008](adr/0008-dashboard-retry-reruns-the-failed-step.md).

### A retry fails again

A step whose outcome the message alone decides reaches the same outcome again: see [What a retry does not
undo](#what-a-retry-does-not-undo). Look at the new step in the timeline to see what happened this time.

### Signed out unexpectedly

| What you see | Why |
| --- | --- |
| "Your session expired. Sign in again to continue." | The [idle or absolute timeout](#how-long-a-session-lasts) passed, or an administrator disabled or deleted your account or changed your password. |
| The same after `docker compose down -v` | `-v` removes the identity volume, so every user and every session is gone. A tab with live updates connected goes to **Sign in** by itself after the stack comes back (about 17 seconds in the live check); a tab with no live connection (an administration page you reloaded, say) stays where it is until it next talks to the API: its first change, which the API refuses with 401, or your coming back to the tab, which reads the session (at most once a minute). It then goes to **Sign in**. Sign in again: the seeded administrator exists again, and everyone and everything else is gone. |
| "The server accepted the sign-in but no session was established. Check that the browser accepts cookies for this site." | The browser dropped the session cookie. |

### Sign-in is unavailable, or the API cannot be reached

"Sign-in is unavailable: the API is running, but its identity store is not ready." means the file that holds the
users is unusable: an unset path in a container, a read-only volume, a file that is not a database. The `identity`
entry of the API's `/health` says why (read it on the API's own port, for example
<http://localhost:5080/health>); the saga views and the API key keep working meanwhile, and the API tries the store
again when it is asked, at most every ten seconds. "Cannot reach the dashboard API" means the API is down or not
yet healthy: `docker compose ps` shows which. See [The identity store](dashboard.md#the-identity-store).

### A page says you have no access

| What you see | What to ask for |
| --- | --- |
| "Your account has no access to any saga type yet. Ask an administrator for sagas.view." | **View sagas** for some saga type, in a grant for you or a team. |
| "You do not have access to `<type>` sagas. Ask an administrator for sagas.view on this saga type." | **View sagas** for that type. |
| "Saga data is hidden for your role. It needs the sagas.data permission." | **View saga data** for that type. |
| "You do not have permission to retry `<type>` sagas." | **Retry sagas** for that type. |
| No **Administration** link | **Manage access** in a grant for all saga types. |

A grant that names the type must match it exactly, case included. You can read what you hold on your
[Account](#your-account) page; an administrator can read it for any user in the **Effective access** preview.

### A tour does not start

- Guide must be on, and the area must be unseen. **Replay tour** repeats the last part shown; switching Guide off
  and on makes everything unseen again.
- Your access must include the permission in [the table](#when-an-area-explains-itself).
- The area must appear within about five seconds of the page opening; a page that failed to load has nothing to
  point at and the tour is dropped.
- A tour is waiting behind the one that is running. A tour you left with **Escape** or **Skip tour** is marked as
  seen.
- If the browser blocks site data, the setting is forgotten when the page is reloaded.
