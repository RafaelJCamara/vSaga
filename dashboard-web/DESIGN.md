# Dashboard design system

The source of truth for how the dashboard SPA looks and behaves. It documents what exists in
`src/styles.scss` and the components, and says plainly where it does not decide something. How to
change it, and the checks that go with a change, are in [`docs/frontend.md`](../docs/frontend.md). A rule here
comes before, or with, the code that depends on it. Where this file and the code disagree, one of them is a bug:
fix the other in the same change. The decisions of the redesign are in
[ADR 0010](../docs/adr/0010-dashboard-design-tokens-and-light-theme.md).

## Product and users

An operations console for a saga engine. People open it to find the saga that failed or got stuck, read its
timeline and data, retry it, and (for administrators) manage who may do what. It is dense, read-heavy and used
for long sessions, often next to a terminal. So: calm, low-glare, information first, state shown by colour and by
text and shape, never by colour alone.

## Direction

A flat console in two themes, dark and light. Surfaces are separated by a 1 px border, not by shadow; colour is
spent on status and on the one accent. The theme follows the operating system (`prefers-color-scheme`) and the user
menu overrides it (System, Light, Dark). No component library, no icon font and no web fonts: the system font stack
keeps the page inside the container's Content Security Policy and costs no request. Nothing is an icon: state is a
shape drawn in CSS beside a word.

## Tokens

All defined on `:root` in `src/styles.scss`. Use the token, never its value. A literal that is not on a scale is a
reason to ask whether the scale is wrong.

### Themes

The values below are the dark theme, which is also what a browser with no preference gets. The light theme redefines
the same names under `@media (prefers-color-scheme: light)` for `:root:not([data-theme])`, and under
`:root[data-theme='light']`. A user who chose Dark under a light OS has `data-theme='dark'`, which the media rule
skips, so the dark base stands. `color-scheme` follows, so
scrollbars, the open list of a native `select`, date pickers and autofill match the page.

The choice is made in the user menu (`ThemeService`), stored in `localStorage` under `vsaga.theme` as `light` or `dark`
(no value means System) and applied as `data-theme` on `<html>`. `public/theme-init.js`, a same-origin script
loaded from `<head>` (the CSP allows `script-src 'self'` and forbids only the inline kind), applies it before the
first paint, so a stored choice that differs from the OS does not flash; `ThemeService` applies it again when the app
starts and on every change. The script duplicates the storage key, which `theme-init.spec.ts` pins to the service's.
A browser that blocks storage keeps System.

### Colour roles

| Token | Dark | Light | Role |
| --- | --- | --- | --- |
| `--bg` | `#0b0e14` | `#f4f6fb` | Page background; also the inset of code and JSON blocks |
| `--bg-elevated` | `#12161f` | `#ffffff` | Cards, tables, inputs, menus, the top bar |
| `--bg-hover` | `#1a2030` | `#e8edf7` | Hover and selected row or menu item |
| `--border` | `#232a3b` | `#d3dae8` | Every divider and outline: quiet, decorative |
| `--border-control` | `#5b667f` | `#7a859d` | The edge of a control the user must find: `.input` (WCAG 1.4.11, 3:1) |
| `--text` | `#e6e9f0` | `#141a26` | Body and headings |
| `--text-muted` | `#8891a7` | `#586178` | Secondary text, labels, placeholders |
| `--accent` | `#5b8cff` | `#2457d6` | Links, focus ring, selected tab and toggle, primary emphasis |
| `--accent-soft` | 14 % of `--accent` | 10 % of `--accent` | Selected-state fill |
| `--accent-solid` | `#3566e0` | `#2457d6` | The fill of the one primary button, white text |
| `--danger-solid` | `#c92a3e` | `#c0243a` | The fill of a destructive button, white text |

Status colours carry a saga's state and also appear as the tone of banners and chips. The tint behind a banner or a
pill is `color-mix(in srgb, <colour> 12%, transparent)` so it follows the theme.

| Token | Dark | Light | Used for |
| --- | --- | --- | --- |
| `--status-running` | `#5b8cff` | `#2457d6` | Running |
| `--status-completed` | `#35c98c` | `#0b7a52` | Completed; the success banner |
| `--status-failed` | `#ef5566` | `#c0243a` | Failed; the error banner; field errors |
| `--status-compensating` | `#f2a93b` | `#935300` | Compensating; the warning banner |
| `--status-compensated` | `#b98af2` | `#7a42c8` | Compensated |
| `--status-timedout` | `#ff8a4c` | `#b4410b` | Timed out (an orange of its own, apart from the compensating amber) |
| `--status-cancelled` | `#8891a7` | `#586178` | Cancelled (shares `--text-muted`: the quiet, final state) |
| `--kind-orchestrated` | `#5b8cff` | `#2457d6` | The Orchestrated kind pill |
| `--kind-choreographed` | `#2fc1d3` | `#08626f` | The Choreographed kind pill |

Measured contrast (WCAG: 4.5:1 for text, 3:1 for large text and UI components), computed from the values above, not
assumed. Text on `--bg-elevated`, then on `--bg-hover`:

| Pair | Dark | Light |
| --- | --- | --- |
| `--text` | 14.9, 13.4 | 17.4, 14.8 |
| `--text-muted` | 5.7, 5.2 | 6.2, 5.3 |
| `--accent` (also running, Orchestrated) | 5.7, 5.1 | 6.2, 5.3 |
| completed | 8.5, 7.6 | 5.4, 4.6 |
| failed | 5.3, 4.8 | 5.9, 5.0 |
| compensating | 9.1, 8.1 | 6.0, 5.1 |
| compensated | 6.9, 6.2 | 6.1, 5.2 |
| timed out | 7.8, 7.0 | 5.7, 4.8 |
| Choreographed | 8.4, 7.5 | 7.0, 6.0 |
| white on `--accent-solid` | 5.1 | 6.2 |
| white on `--danger-solid` | 5.4 | 5.9 |
| `--border-control` on `--bg-elevated` | 3.2 | 3.7 |

Status text on its own 12 % tint (banners, pills): dark 4.6 to 7.3, light 4.5 to 5.2, measured on `--bg-elevated`. A pill also sits on `--bg` and `--bg-hover` (the sibling links on the detail page, a hovered row), where the light teal first measured 4.39 and 4.06 and was darkened to `#08626f` (5.4 and 5.0 on those tints). A rendered-page scan (computed colours of every text element, both themes, three widths) is how that was found; repeat it after a palette change. The lowest are the light
"completed" (4.53) and the dark failed red (4.64). A new colour pair is measured, not assumed; `--danger-solid` exists
because the status red under white text is only 3.4.

State is never carried by colour alone: a status badge is a **shape** and the status word (below).

### Typography

| Token | Value |
| --- | --- |
| `--font-sans` | `-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif` |
| `--font-mono` | `'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, monospace` |

`body` is `14px` at line-height `1.5`. `rem` is the browser's root size (16 px), not that 14 px. The scale, in `rem`
and in pixels:

| Token | rem | px | For |
| --- | --- | --- | --- |
| `--text-xs` | `.75` | 12 | Uppercase labels, table headings, micro labels, chips, the kind pill |
| `--text-sm` | `.8` | 12.8 | Buttons, status badges, JSON, hints and field errors |
| `--text-md` | `.875` | 14 | Body, table cells, inputs, banners, menus |
| `--text-lg` | `1.1` | 17.6 | Section titles |
| `--text-xl` | `1.3` | 20.8 | Page titles |

Nothing is set below 12 px. Labels (`.label`, table headings, `.micro-label`) are uppercase with `.04em` tracking and
`--text-muted`. Use `--font-mono` for identifiers, JSON and anything a person might copy.

### Spacing, shape, elevation

| Token | Value | Token | Value |
| --- | --- | --- | --- |
| `--space-1` | `.25rem` (4 px) | `--space-5` | `1.25rem` (20 px) |
| `--space-2` | `.5rem` (8 px) | `--space-6` | `1.5rem` (24 px) |
| `--space-3` | `.75rem` (12 px) | `--space-7` | `2rem` (32 px) |
| `--space-4` | `1rem` (16 px) | `--space-8` | `3rem` (48 px) |

Radii: `--radius-sm` `5px` (buttons, menu items), `--radius-md` `6px` (inputs, banners, JSON blocks), `--radius-lg` `8px`
(cards, tables, menus), `--radius-pill` `999px` (chips, pills). The only shadow is `--shadow-popup`
(`0 8px 24px`, 40 % black in dark and 16 % ink in light), on the user menu; nothing else floats. The guide overlay's
scrim and spotlight are the one place a literal colour stays: it is a dim layer over either theme.

Every spacing and radius value that equals a scale step is a token, in the global classes and in every component
stylesheet (the computed padding, margin, gap, radius and font size of every element on nine pages, at 360 and
1280 px, were identical before and after that change). About fifty spacing values sit between steps (`.3rem`, `.35rem`,
`.4rem`, `.6rem`, `.9rem` and the like) and are still literals: snapping them moves pixels, so it is a design
decision for the maintainer, not a mechanical one.

### Status and kind marks

A status badge is a dot of the status colour in a **shape of its own**, then the word:

| Status | Shape |
| --- | --- |
| Running, Completed, Compensated | filled circle |
| Failed | filled diamond |
| Compensating | half-filled circle |
| Timed out | ring |
| Cancelled | ring with a slash |

The shape is drawn in CSS (`status-badge.scss`) and is `aria-hidden`; the word is the accessible name. The kind pill
(`app-kind-badge`) is a filled-tint pill in `--kind-orchestrated` or `--kind-choreographed`, with the kind in words,
on every saga row and on the detail page.

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
| `.menu` | A pop-up list of links, buttons and (the theme) radio items | hover, `aria-checked` |
| `.subtabs` | Tabs under a page header: real `role="tab"` buttons on the saga page, plain links in the administration nav | `.active`, `aria-selected` |
| `.page-header`, `.page-title`, `.section-title`, `.toolbar`, `.actions` | Page structure and the row that ends a form | |
| `.json-block` | Pretty-printed state or payload; focusable where it scrolls | |
| `.empty`, `.muted`, `.micro-label`, `.mono`, `.sr-only` | Utility text styles | |

Shared Angular components: `status-badge`, `kind-badge`, `confirm-button`, `local-time`, `user-menu`,
`access-summary`, `saga-timeline`, `saga-map`, `saga-data-inspector`, `saga-data-overview`, `guide-overlay`,
`guide-toggle`, `guide-hint`; and `ThemeService`.

Rules for them:

- **At most one primary action in view**, and it is the only filled blue button (`.btn--primary`). A page whose
  buttons are all equals (the saga list's Refresh and pager) uses plain `.btn` for all of them. Destructive actions
  use `.btn--danger` and ask for confirmation (`confirm-button`).
  A form's submit (Save, Create, Change password, Sign in) is that primary; the way out (Cancel, Back) stays plain.
- **A first-run offer or notice never floats over content.** The guide-mode offer is a bar in the page flow under
  the top bar (`app-guide-hint`); an element that must appear over the page takes the focus and can be dismissed.
- **A control has an accessible name that does not depend on its placeholder** (`aria-label` for a search box that
  has no visible label), and a pointer target of at least 24 px in each direction.
- **Say it in words as well as colour and shape.** A failed saga's summary states where it stopped; the colour is the
  second cue.
- **Every list and tab has a loading, an empty and an error state**, using `.empty` and `.banner--error`. A
  degraded but usable view (live updates down, stale content) uses `.banner--warning`.
- **A form control has a visible label**, a hint when the format is not obvious, and an inline `.field-error`
  with `aria-invalid` on failure. Placeholder text is never the label.
- **Disable by `aria-disabled` when the control holds focus while busy**; a native `disabled` drops focus.
- **A row that opens something looks like it does.** The saga list's row opens on a click anywhere in it and on
  Enter on its id; the id is link-coloured and a chevron ends the row on a wide screen. Hover alone is never the
  only cue (it does not exist on touch).
- **Tabs are tabs.** Switching views of one page in place uses `role="tablist"`, `role="tab"` with `aria-selected`
  and a roving `tabindex` (Left, Right, Home, End move and select), and a `role="tabpanel"` labelled by its tab. Links
  to other pages are not tabs.

## Interaction

- **Focus.** A 2 px `--accent` ring at 2 px offset on `:focus-visible`, globally. Never remove an outline without
  replacing it. Any region that scrolls is focusable.
- **Motion.** There is almost none, by choice. What exists (the guide overlay, the saga map) honours
  `prefers-reduced-motion`; new motion must too, and must say what it communicates. A theme change is instant.
- **Forced colours.** The guide overlay has a `forced-colors` rule; new custom-drawn controls need one. The status
  shapes are drawn with `currentColor` so they keep their colour in forced-colours mode, and the status word is
  always present.

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
- **Rows of controls and metadata** (a saga's created and updated times, the map's playback controls, the links to
  sagas that share a correlation id, the retry confirmation's question and its two buttons) wrap, and a long value
  breaks rather than widening the page. A flex row that must not widen the page sets `flex-wrap: wrap` and
  `max-width: 100%`; the first redesign pass missed two that did not.

## Constraints from the container

The nginx image sets a strict Content Security Policy: no inline `<script>`, no `on*=` handlers, no external
fonts, styles or scripts. A visual change that needs one is not available; CI checks the built `index.html`.

## Research behind these rules

The installed UI UX Pro Max skill was run for this product, first with `--design-system` and `--domain ux` for each
concern the audit raised, then (for the redesign) with `--domain ux`, `color` and `typography` for theming, row
affordance, touch targets and density, and icons. What it returned and what we did with it:

- **Used (sourced).** The Dark Mode (OLED) style fits a low-light console and requires 4.5:1 text contrast, visible
  focus and reduced motion, which this file already holds. Its UX guidance is that colour must not carry information
  alone (the status shapes), that body text needs 4.5:1, that hover effects do not work on touch (the row's chevron
  and link-coloured id), that the web target size is 24 CSS px with about 8 px between targets, that tables should
  scroll or turn into cards on a phone, and that users must be able to skip tutorials.
- **Rejected.** The suggested page pattern was a landing page; the typography results (a cyberpunk pairing, Inter,
  Fira) are all loaded from Google Fonts, which the container's CSP forbids; the primary colour was green where the
  app's accent is blue; the colour results were light analytics and IoT palettes with no status guidance. None
  applies.
- **Returned nothing usable.** Queries about theming, density, icons and type scale returned only the generic rules
  above. The decisions on those are our own judgement.
- **Our own judgement, not from the tool.** The exact `--accent-solid` value, the 860 px two-line row layout, the
  failure line in the saga summary, the map's `36rem` minimum; and, for the redesign: the whole light palette and the
  `--border-control` value (each measured, not looked up), the five-step type scale and its 12 px floor, the spacing
  scale, the shape for each status, the orange for timed out, the teal for Choreographed, the System / Light / Dark
  menu, ARIA tabs, "handled" on the timeline, and not adding a density switch or icons.
- **Not found.** A search of the Angular stack guidance for responsive layout and tables returned no match, so
  nothing here cites it.

## Decided, and what is left

Answered by ADR 0010: light theme (System / Light / Dark), spacing, radius and shadow tokens, a five-step type scale,
status colour and shape, table row affordance, icons (none), and density (deferred).

Still open:

1. **Density.** One density today. A compact/comfortable switch is cheap to add on the spacing tokens if operators ask
   for it; nobody has.
2. **A higher-contrast theme.** Possible on the same token set; not asked for.
