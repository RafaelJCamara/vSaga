# ADR 0010: The dashboard gets spacing, shape and type tokens, a light theme, and tells its states apart by shape as well as colour

**Status:** **Proposed**
**Date:** 2026-11-02
**Relates to:** [`0006-dashboard-authentication-and-identity-store.md`](0006-dashboard-authentication-and-identity-store.md)
(the user menu that now also holds the theme choice); [`../frontend.md`](../frontend.md) (the workflow this follows);
[`../../dashboard-web/DESIGN.md`](../../dashboard-web/DESIGN.md) (the design system this amends: its seven open
questions are answered here).
**Design:** none beyond this record; the decided values are written into `DESIGN.md`.

---

## Context

The first pass over the dashboard (the audit fixes, commits `b76eaae` and earlier) repaired what was broken:
overflow at 360 px, a floating guide hint, an unreadable primary button, the list on a phone. It deliberately left
open what is a matter of design rather than a defect. `DESIGN.md` lists seven open questions, and the audit left
three smaller things:

1. **One theme, dark, hard-wired.** `src/styles.scss` defines the colours on `:root` for a dark page and sets
   `color-scheme: dark`. Operators who work in a bright room or whose OS is set to light get a dark page regardless.
2. **No scales.** Spacing is `rem` literals (`.25 .3 .4 .5 .6 .75 .9 1 1.25 2 3`), radii are `5 6 8 999px`, text is
   fourteen distinct `font-size` values from `.65rem` to `1.3rem`. `rem` is the browser's 16 px, not the 14 px set on
   `body`, so `.65rem` is 10.4 px, `.68rem` is 10.9 px and `.72rem` (table headings, labels, chips, the kind pill) is
   11.5 px: small for a console people read for hours.
3. **Two statuses share a colour with something else.** `--status-timedout` is the same amber as
   `--status-compensating`, and `--status-cancelled` is the same grey as muted text. The status word beside the dot
   tells them apart, but not at a glance, and the dot alone is a colour-only cue.
4. **Form controls have no visible boundary.** An `.input` uses `--border` against `--bg-elevated`, a 1.3:1 ratio;
   WCAG 1.4.11 asks 3:1 for the boundary of a control the user must find.
5. **Leftovers.** The detail page's Map and Timeline tabs are `aria-pressed` buttons, not tabs; a timeline step titled
   `PaymentFailed` reads "· succeeded" (the step handled a failure message correctly, which reads as a contradiction);
   the kind pill is the same blue on nearly every list row, so it carries no information.

Constraints that bound the options: the container's Content Security Policy forbids external fonts, scripts, inline
script and `on*=` handlers (`DESIGN.md`, "Constraints from the container"); `docs/frontend.md` forbids a new component
library, icon font or dependency without a stated benefit; routes, authentication, API contracts and SignalR
behaviour stay as they are.

## Decision

All of it is CSS, templates and one small service in `dashboard-web/`. No API, route, dependency or build change.

1. **Tokens.** `:root` gains a spacing scale (`--space-1` to `--space-8`, a 4 px base), radii (`--radius-sm`,
   `--radius-md`, `--radius-lg`, `--radius-pill`), one shadow (`--shadow-popup`) and a type scale of five steps
   (`--text-xs` to `--text-xl`: `.75rem`, `.8rem`, `.875rem`, `1.1rem`, `1.3rem`), the smallest being 12 px where the
   smallest in use is 10.4 px. The global classes use them; a component
   stylesheet moves to them when it is touched, not in a sweep. A literal that is not on the scale is a reason to ask
   whether the scale is wrong.
2. **Light theme with a choice.** The palette is a set of custom properties. The default follows
   `prefers-color-scheme`; the user menu offers **System / Light / Dark**, stored in `localStorage` under one key and
   applied as `data-theme="light|dark"` on `<html>` (absent means System), by a small script file in `<head>` before
   first paint and again by the service when the app starts. The overriding rules are
   `:root[data-theme='light']` and `:root[data-theme='dark']`, and `color-scheme` follows the theme so native widgets
   match. Every text pair in both themes is measured at 4.5:1 or better (`DESIGN.md` lists the numbers). Tinted
   backgrounds (banners, chips) are `color-mix()` of the status colour, so they follow the theme without a second
   hand-picked value.
3. **Status by shape and colour.** Each status dot has its own shape beside its own word: filled circle (running,
   completed, compensated), diamond (failed), half-filled circle (compensating), ring (timed out), slashed ring
   (cancelled). `--status-timedout` becomes its own orange, distinct from the compensating amber.
4. **Kind pills in two colours.** The pill stays on every row. Orchestrated keeps the accent blue; Choreographed is
   teal. Both are filled-tint pills with the kind in words.
5. **Control boundary.** A new `--border-control` token (3:1 or better against the surfaces it sits on) is the border
   of `.input`; `--border` stays the quiet divider.
6. **Row affordance.** The saga list row already opens on a click anywhere and on Enter on the id. It now also looks
   like it does: the id is link-coloured and a chevron ends the row on wide screens.
7. **Tabs.** Map and Timeline become `role="tablist"` / `role="tab"` / `role="tabpanel"` with `aria-selected`, a
   roving `tabindex` and Left/Right/Home/End, per the WAI-ARIA authoring practices. `.subtabs` as a router nav
   (administration) is unchanged: it is navigation, not tabs.
8. **Timeline wording.** "succeeded" becomes "handled": the step handled its message and the saga moved on.
9. **Not done, on purpose.** No compact/comfortable density switch (one density, tuned by the tokens). No icons: shape
   carries state, a chevron is drawn in CSS, and an icon font is out (CSP, dependency).

## Options weighed

- **Dark only, no toggle.** Cheapest, and consistent with the product brief ("low-glare, for long sessions"). Rejected
  by the maintainer's choice of a light theme: a dark-only page also ignores the OS setting.
- **OS setting only, no toggle.** Smaller (no stored state, no menu change). Rejected: an operator who wants light in
  a dark OS, or the reverse, has no way to say so.
- **A toggle in the top bar.** The bar already carries Guide, Replay tour, User guide and the user menu and wraps on
  a phone. The user menu has room and is where account-level preferences live.
- **An inline script in `index.html` to set the theme before first paint.** Avoids a flash for a user whose stored
  choice differs from the OS. Impossible: the CSP forbids inline script. A same-origin file does the same job and
  `script-src 'self'` allows it, so that is what is used (`public/theme-init.js`, loaded from `<head>`).
- **Server-side or per-account theme storage.** Would follow a user across browsers but needs an API and a schema
  change for a cosmetic setting. Rejected.
- **A utility framework (Tailwind) or a component library for the tokens.** Forbidden without a concrete benefit; the
  app has twenty small stylesheets and one global one, and custom properties do the job.
- **A distinct hue for every status.** The palette has five usable hue families on a dark surface before pairs become
  confusable. Shape plus word is more robust than a seventh hue, so only timed out (the pair most confused with
  another status) gets a new hue; cancelled stays grey because it is the quiet, final, uninteresting state.
- **Icons per status or kind.** Would need an icon set. CSS shapes cover the need without a dependency or a request.
- **A density switch.** A stored preference and a second layout of the list and timeline to keep working and verify,
  for a benefit nobody has asked for. Deferred; the spacing tokens make it cheap to add later.
- **Colour-only kind pills (no words).** Rejected: it would break the rule that state is never carried by colour.

## Consequences

- **Easier.** A new component takes its sizes from a short scale instead of inventing a number; a colour change is one
  place; a theme is a set of values, so a high-contrast variant later is cheap; the status of a saga reads at a glance
  and survives colour-blindness; text below 12 px is gone.
- **Harder.** Every colour pair now exists twice and is measured twice; `DESIGN.md` and the guide have more to keep
  true. The guide mentions of colours and the user menu change (`docs/dashboard-guide.md`, the user-menu tour copy).
- **No flash on load, at the price of one render-blocking request.** `theme-init.js` is a few lines that read the
  stored choice and set `data-theme` before the first paint. It blocks rendering for one small same-origin request
  (served revalidating, like `index.html`), and the key it reads is duplicated from `ThemeService`, which a spec
  pins. A browser that blocks storage gets System.
- **A stated limitation: the choice is per browser.** It lives in `localStorage`, so it does not follow the account
  to another browser, and it is unavailable (falling back to System) in a browser that blocks storage.
- **Not covered here.** The saga map's SVG colours that are not tokens are moved to tokens as part of the theme work
  (the audit found three hard-coded colour values); any colour a later change adds must be a token.
