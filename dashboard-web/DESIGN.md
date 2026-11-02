# Dashboard design system

The source of truth for how the dashboard SPA looks and behaves. It documents what exists in
`src/styles.scss` and the components today, and says plainly where it does not yet decide something. How to
change it, and the checks that go with a change, are in [`docs/frontend.md`](../docs/frontend.md). A rule here
comes before, or with, the code that depends on it. Where this file and the code disagree, one of them is a bug:
fix the other in the same change.

## Product and users

An operations console for a saga engine. People open it to find the saga that failed or got stuck, read its
timeline and data, retry it, and (for administrators) manage who may do what. It is dense, read-heavy and used
for long sessions, often next to a terminal. So: calm, low-glare, information first, state shown by colour and by
text, never by colour alone.

## Direction

A dark, flat, single-theme console. Surfaces are separated by a 1 px border, not by shadow; colour is spent on
status and on the one accent. There is no light theme and no `prefers-color-scheme` handling today (open
question below). No component library, no icon font and no web fonts: the system font stack keeps the page inside
the container's Content Security Policy and costs no request.

## Tokens

All defined on `:root` in `src/styles.scss`. Use the token, never its hex.

### Colour roles

| Token | Value | Role |
| --- | --- | --- |
| `--bg` | `#0b0e14` | Page background; also the inset of code and JSON blocks |
| `--bg-elevated` | `#12161f` | Cards, tables, inputs, menus, the top bar |
| `--bg-hover` | `#1a2030` | Hover and selected row or menu item |
| `--border` | `#232a3b` | Every divider and outline |
| `--text` | `#e6e9f0` | Body and headings |
| `--text-muted` | `#8891a7` | Secondary text, labels, placeholders |
| `--accent` | `#5b8cff` | Links, focus ring, selected tab and toggle, primary emphasis |
| `--accent-soft` | `rgba(91, 140, 255, .14)` | Selected-state fill, chip fill |
| `--accent-solid` | `#3566e0` | The fill of the one primary button, white text (5.1:1; the plain accent under white is 3.2:1) |
| `--danger-solid` | `#c92a3e` | The fill of a destructive button, white text |

Status colours carry a saga's state and also appear as the tone of banners:

| Token | Value | Used for |
| --- | --- | --- |
| `--status-running` | `#5b8cff` | Running |
| `--status-completed` | `#35c98c` | Completed; the success banner |
| `--status-failed` | `#ef5566` | Failed; the error banner; field errors |
| `--status-compensating` | `#f2a93b` | Compensating; the warning banner |
| `--status-compensated` | `#b98af2` | Compensated |
| `--status-timedout` | `#f2a93b` | Timed out (shares the warning amber) |
| `--status-cancelled` | `#8891a7` | Cancelled (shares `--text-muted`) |

Measured contrast against `--bg-elevated` (WCAG: 4.5:1 for text, 3:1 for large text and UI components):
`--text` 14.9, `--text-muted` 5.7, `--accent` 5.7, completed 8.5, failed 5.3, warning 9.1, compensated 6.9. White
on `--danger-solid` is 5.4. On `--bg-hover` the failed red drops to 4.8 and muted to 5.2, still above 4.5.
A new colour pair is measured, not assumed; `--danger-solid` exists because the status red under white text is
only 3.4.

State is never carried by colour alone: a status badge is a dot **and** the status word.

The root sets `color-scheme: dark`, so the browser's own widgets (scrollbars, the open list of a native `select`,
autofill) match the page rather than showing light.

### Typography

| Token | Value |
| --- | --- |
| `--font-sans` | `-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif` |
| `--font-mono` | `'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, monospace` |

Base size is `14px` at line-height `1.5`. In use today (rem against that base): page title `1.3`, section title `1.1`,
body and table cells `.85`, buttons `.78`, status badges `.78`, JSON `.78`, chips and uppercase labels `.72`,
micro labels `.68`. Labels (`.label`, table headers, `.micro-label`) are uppercase with `.04em` tracking and
`--text-muted`. Use `--font-mono` for identifiers, JSON and anything a person might copy.

### Spacing, shape, elevation

There is no spacing token yet; values are `rem` literals, and the scale is irregular (`.25 .3 .4 .5 .6 .75 .9 1
1.25 2 3`). Radii in use: `5px` buttons and menu items, `6px` inputs, banners and JSON blocks, `8px` cards, tables
and menus, `999px` chips. The only shadow is the menu's `0 8px 24px rgba(0,0,0,.4)`; nothing else floats.

## Components

Global classes in `src/styles.scss`. Reuse them before writing a new one.

| Class | Use | States it defines |
| --- | --- | --- |
| `.btn` | The default bordered button; `--primary` for the one main action of a form or page (sign-in, setup); `--quiet` for toolbars and step headers; `--danger` for a confirmed destructive action | hover, `aria-pressed`, `aria-expanded`, `:disabled` and `aria-disabled` (kept focusable while busy), focus ring |
| `.banner` | A page- or tab-level message; `--error`, `--warning`, `--info`, `--success` | |
| `.field`, `.label`, `.input`, `.field-hint`, `.field-error` | A labelled control with hint and inline error | `aria-invalid`, `:disabled` |
| `.card`, `.auth-card` | A bordered panel; the narrow centred one for sign-in, setup, account | |
| `.data-table`, `.table-scroll` | Records; the scroll box is focusable so a keyboard can scroll it | row hover is the owning component's |
| `.chip` | A pill beside a name: `--muted`, `--danger` | |
| `.menu` | A pop-up list of links and buttons | hover |
| `.subtabs` | Tabs under a page header | `.active` |
| `.page-header`, `.page-title`, `.section-title`, `.toolbar`, `.actions` | Page structure and the row that ends a form | |
| `.json-block` | Pretty-printed state or payload; focusable where it scrolls | |
| `.empty`, `.muted`, `.micro-label`, `.mono`, `.sr-only` | Utility text styles | |

Shared Angular components: `status-badge`, `kind-badge`, `confirm-button`, `local-time`, `user-menu`,
`access-summary`, `saga-timeline`, `saga-map`, `saga-data-inspector`, `saga-data-overview`, `guide-overlay`,
`guide-toggle`.

Rules for them:

- **At most one primary action in view**, and it is the only filled blue button (`.btn--primary`). A page whose
  buttons are all equals (the saga list's Refresh and pager) uses plain `.btn` for all of them. Destructive actions
  use `.btn--danger` and ask for confirmation (`confirm-button`).
- **A first-run offer or notice never floats over content.** The guide-mode offer is a bar in the page flow under
  the top bar (`app-guide-hint`); an element that must appear over the page takes the focus and can be dismissed.
- **A control has an accessible name that does not depend on its placeholder** (`aria-label` for a search box that
  has no visible label), and a pointer target of at least 24 px in each direction.
- **Say it in words as well as colour.** A failed saga's summary states where it stopped; the colour is the
  second cue.
- **Every list and tab has a loading, an empty and an error state**, using `.empty` and `.banner--error`. A
  degraded but usable view (live updates down, stale content) uses `.banner--warning`.
- **A form control has a visible label**, a hint when the format is not obvious, and an inline `.field-error`
  with `aria-invalid` on failure. Placeholder text is never the label.
- **Disable by `aria-disabled` when the control holds focus while busy**; a native `disabled` drops focus.

## Interaction

- **Focus.** A 2 px `--accent` ring at 2 px offset on `:focus-visible`, globally. Never remove an outline without
  replacing it. Any region that scrolls is focusable.
- **Motion.** There is almost none, by choice. What exists (the guide overlay, the saga map) honours
  `prefers-reduced-motion`; new motion must too, and must say what it communicates.
- **Forced colours.** The guide overlay has a `forced-colors` rule; new custom-drawn controls need one.

## Responsive behaviour

`<meta name="viewport" content="width=device-width, initial-scale=1">`, and `100dvh` for the shell. Breakpoints in use are
`max-width: 640px` (the guide toggle and the timeline) and `max-width: 860px` (the saga list, whose six columns need that
much room). Supported from 320 px
with no horizontal page scroll; verify at about 360, 768 and 1280 px (see
[`docs/frontend.md`](../docs/frontend.md#verifying)). How content gives way:

- **Wide tables** scroll inside `.table-scroll`, not the page, except the **saga list**, which at 860 px and below becomes a list of two-line rows (type and status, state and time, id and kind) so that the status is never
  off-screen; its two sort headings stay as a bar above. The other headings stay in the accessibility tree.
- **A drawing that must stay legible** (the saga map) keeps a minimum width (`36rem`) and scrolls inside a focusable
  region, rather than shrinking its labels to a few pixels.
- **Rows of controls and metadata** (a saga's created and updated times, the map's playback controls) wrap, and a
  long value breaks rather than widening the page.

## Constraints from the container

The nginx image sets a strict Content Security Policy: no inline `<script>`, no `on*=` handlers, no external
fonts, styles or scripts. A visual change that needs one is not available; CI checks the built `index.html`.

## Research behind these rules

The installed UI UX Pro Max skill was run for this product (`--design-system` for an internal, dark, dense
operations console, and `--domain ux` for each concern the audit raised). What it returned and what we did with it:

- **Used (sourced).** The Dark Mode (OLED) style fits a low-light console and requires 4.5:1 text contrast, visible
  focus and reduced motion, which this file already holds; the operational green, incident red and maintenance amber
  match the status colours in use. Its UX guidance is that users must be able to skip tutorials and onboarding must
  not lock the screen, and that tables should scroll or turn into cards on a phone. Its 4.5:1 text-contrast
  rule is what the white-on-accent button failed.
- **Rejected.** The suggested page pattern was a landing page, the typography was a cyberpunk pairing loaded from
  Google Fonts (the container's CSP forbids external fonts, and the tone is wrong for an ops tool), and the primary
  colour was green where the app's accent is blue. None applies.
- **Our own judgement, not from the tool.** The exact `--accent-solid` value, the 860 px two-line row layout for the
  list, the failure line in the saga summary, and keeping the map's minimum width at `36rem`.
- **Not found.** A search of the Angular stack guidance for responsive layout and tables returned no match, so
  nothing here cites it.

## Open questions

Not decided here, for the design research to settle, not to be assumed:

1. **Light theme and `prefers-color-scheme`.** Absent today. Whether operators want one is unanswered.
2. **Spacing, radius and shadow tokens.** Literals today; a scale would remove drift but touches every component.
3. **A type scale in tokens.** Sizes are literals between `.68rem` and `1.3rem`; the small end (`.68`, `.72`) is
   tight for a read-heavy console.
4. **Density.** Whether the saga list and timeline want a compact/comfortable switch.
5. **Status colour sharing.** `timedout` shares the warning amber and `cancelled` shares muted grey; whether those
   should be told apart at a glance.
6. **Table row affordance.** Which rows link somewhere and how that is shown is each component's own today.
7. **Icons.** There are none; whether any earn their place is open, and an icon font is out (CSP, dependency).
