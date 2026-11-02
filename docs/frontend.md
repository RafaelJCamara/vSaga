# Working on the dashboard UI

How we change the look, feel and usability of the dashboard SPA (`dashboard-web/`). It applies to a redesign
and equally to a single new control. [`dashboard-web/DESIGN.md`](../dashboard-web/DESIGN.md) is the design
system it works from; [`CONTRIBUTING.md`](../CONTRIBUTING.md) has the build, test and CI commands this page
refers to; [`AGENTS.md`](../AGENTS.md) makes both binding for AI agents. A new UI capability also needs an ADR
first ([`adr/`](adr/)).

## What we do not do

- **Rewrite to impose a stack.** We improve the Angular app that exists. We do not add or migrate a component
  library (PrimeNG, Angular Material, Tailwind, an icon font) without first writing down the concrete benefit and
  getting approval. The app has none today and styles itself with the tokens and classes in `src/styles.scss`.
- **Break working behaviour.** Routes, authentication and authorization, API contracts, business rules and the
  SignalR integration keep working. The SPA is a thin client: it shows what the API allows and decides nothing
  about access itself (see [`dashboard.md`](dashboard.md)).
- **Fake it.** No static mockups in place of working screens, no hard-coded demo data, no controls that do
  nothing.
- **Decorate.** No gradients, cards, icons, animation or dependencies unless they serve a stated purpose.
- **Claim what we did not do.** A tool, page inspection or test is reported as run only if it was.

## Tools, and what each is for

The recommended set is installed **at the repository level** so every contributor and agent has the same one.
`skills.ps1` at the repository root holds the commands; run it from the repository root in PowerShell 7+.

| Tool | Role | Install (in `skills.ps1`) |
| --- | --- | --- |
| **UI UX Pro Max** | Design research first: style, palette, typography, UX guidelines and Angular guidance for *this* product. Read the installed skill's instructions and use its own search script; do not guess the script path or arguments. | `npx -y ui-ux-pro-max-cli init --ai claude` |
| **frontend-design** (Claude Code plugin) | Implementation quality, after a direction is chosen. It complements the research and does not replace it, or the inspection of the existing app. | `claude plugin install frontend-design@claude-plugins-official --scope project` |
| **Context7** (`ctx7`) | Current Angular documentation for APIs and version-specific behaviour. Never a substitute for reading the code. | `npx -y ctx7 setup --claude --project --yes` |
| **Playwright MCP** | Browser verification of the running app: real pages, real interactions, breakpoints, console errors. | `claude mcp add --scope project playwright npx @playwright/mcp@latest` |

### Local MCP configuration (`.mcp.json`)

`.mcp.json` registers the MCP servers (Playwright and Context7) for Claude Code in this repository. It is
**git-ignored**, because the Context7 server needs your personal API key and a literal key must never be committed.
[`.mcp.json.example`](../.mcp.json.example) is the committed template: it reads the key from an environment variable.
To set up:

1. Copy it: `cp .mcp.json.example .mcp.json` (PowerShell: `Copy-Item .mcp.json.example .mcp.json`).
2. Get a key from the Context7 dashboard (**Create API Key**; leave "Management access" off, since this key only
   fetches documentation), and put it in the user-level environment variable `CONTEXT7_API_KEY`:
   - Windows (PowerShell): `[Environment]::SetEnvironmentVariable('CONTEXT7_API_KEY', '<key>', 'User')`
   - macOS or Linux: `export CONTEXT7_API_KEY=<key>` in your shell profile.
3. Restart Claude Code, then run `claude mcp list` and check that `context7` and `playwright` connect. An unset
   variable does not stop the config loading: Claude Code warns and sends the literal `${CONTEXT7_API_KEY}`, which
   the server rejects.

`skills.ps1`'s last step (`npx -y ctx7 setup ...`) can write your key into `.mcp.json` as a literal. If it does,
replace the value with `Bearer ${CONTEXT7_API_KEY}` as in the example. To rotate the key: create a new one, update the
environment variable, then delete the old one in the dashboard. The old key stops working at once.

If a recommended tool is missing, say so, carry on with the best workflow left, and list what stays manual. Never describe
a tool's output you did not get. Verify a tool's instructions and configuration before relying on it; a
recommended tool being listed here does not mean it is connected.

## The workflow

### 1. Audit before editing

Read `package.json`, `angular.json`, the routes (`app.routes.ts`), `src/styles.scss`, the page and component you
are changing and `DESIGN.md`. Identify the users and their primary tasks: operators watching sagas, finding the
one that failed, reading its timeline and state, retrying it, and administrators managing access. Run the app
(see Verifying) and look at it. Judge visual hierarchy, typography, spacing, alignment, density, navigation,
forms, feedback, responsive behaviour, accessibility and the loading, empty, error and success states. Look for
inconsistent components, excessive cards and unclear calls to action.

Write a short prioritized audit before changing anything: **issue, user impact, proposed fix, page or
component.** Do not begin a broad redesign until the plan is clear.

### 2. Establish the direction in `DESIGN.md`

Research with UI UX Pro Max for the audience and product, with Angular as the target stack. Separate what the
tool recommended from your own judgement, and compare it with what the app already has and reuse that. Then
update `DESIGN.md` with concrete values: colour roles, type scale, spacing, surfaces and borders, radii, shadows,
focus states, motion, responsive behaviour and component states. A rule goes in `DESIGN.md` before, or in the
same change as, the code that depends on it. Pick styles for this product, not because they are fashionable.

### 3. Implement incrementally

Start with the shared shell and navigation and the highest-impact journey, then make related components
consistent. Prefer small changes that reuse the existing Angular patterns and global classes (`.btn`, `.card`,
`.banner`, `.field`, `.data-table`, `.chip`). Make the primary action of each screen obvious with hierarchy and
whitespace rather than decoration. Give every interactive element its states: hover, focus, disabled, loading,
empty, success and error where they apply. Do not make a large or hard-to-reverse change without explaining it
first.

Standards that every change meets, not only redesigns:

- Semantic HTML, accessible names, labels on every form control, visible validation feedback.
- Keyboard operability and a visible focus ring (the global `:focus-visible`); a scrolling region is focusable.
- Contrast of at least 4.5:1 for text (the destructive button's red is set for exactly this; see `DESIGN.md`).
- `prefers-reduced-motion` respected; the guide overlay and the saga map already do.
- Works from a 320 px viewport up, without horizontal page scroll.
- No inline `<script>` or `on*=` handlers, and no external fonts or scripts: the container's Content Security
  Policy forbids them.

### 4. Keep the user guide and tours true

A change to what a page shows or does updates [`dashboard-guide.md`](dashboard-guide.md) and the tour steps in
`src/app/components/guide-overlay/guide-tours.ts`, and bumps the area's `version` in
`src/app/services/guide-areas.ts` when tour copy changed. A removed `data-tour` anchor fails a spec; copy that no
longer matches fails nothing, which is why it is a rule.

## Verifying

Run, from `dashboard-web/`, what CONTRIBUTING lists: `npm ci`, `npm audit --audit-level=low`, `npx ng build`,
the inline-script check on the built `index.html`, and `npx ng test --watch=false`.

Then look at the real thing, in the compose stack and not only under `ng serve`, because the CSP and the nginx
proxy exist only in the image (http://localhost:4200, signed in as the seeded administrator):

- Visit each route you touched and exercise the interaction, with Playwright MCP where it is installed.
- Check a narrow phone width (about 360 px), a tablet width (about 768 px) and a desktop width. Look for
  overflow, clipped menus and dialogs, broken navigation, inconsistent spacing and controls a keyboard cannot reach.
- Read the browser console for CSP violations and runtime errors, fix them and repeat.

Report exactly what ran and what did not. Visual verification is never reported as done if the rendered UI was
not inspected.

## Reporting

A UI change ends with: the audit (or the part of it the change answers), the design direction and what in it came
from research versus judgement, the `DESIGN.md` and code changes, the checks run with their results, and the
limitations, missing tools and next steps.
