# AGENTS.md

Instructions for AI coding agents (and a short orientation for people) working in this repository.
[`CONTRIBUTING.md`](CONTRIBUTING.md) is the source of truth for build, test, CI and commit conventions;
this file does not repeat it. Read it first, then the rules below.

## Layout

- `dotnet/src/` — the libraries (`VSaga.Abstractions`, `VSaga.Core`, persistence providers, transport
  adapters, the dashboard API). `dotnet/tests/` — one test project per library, plus the cross-provider
  `VSaga.Persistence.Conformance` suite. `dotnet/samples/` — runnable samples.
- `dashboard-web/` — the Angular dashboard SPA (not part of the .NET solution).
- `docs/` — reference docs; `docs/adr/` — decision records; `docs/design/` — long-form plans;
  `docs/history/` — how each feature was built and verified.

## Rule 1: every new feature starts with an ADR

**Writing an architecture decision record is required whenever a new feature is created.** Write it
before the implementation, in the same pull request or earlier, and do not start building a feature that
has none.

"New feature" includes: a new persistence provider, transport adapter or library project; a new
user-visible capability of the engine, DSL or dashboard; a new public configuration key or option; a new
wire or storage format; and a redesign of an existing area. A bug fix, a refactor that changes no
behaviour, a dependency bump and a docs-only change do not need one. If you are unsure, write the ADR:
a short one costs less than a decision that exists only in a commit message.

How:

1. Copy the shape of an existing record in [`docs/adr/`](docs/adr/) (for example
   [`0008`](docs/adr/0008-dashboard-retry-reruns-the-failed-step.md)). Number it with the next free
   four-digit number and name the file `NNNN-short-kebab-title.md`.
2. It carries, in this order: the title (`# ADR NNNN: <decision as a sentence>`); `Status`, `Date` and
   `Relates to` lines; **Context** (the forces and constraints, with file references); **Decision**; the
   **options weighed**, including the ones rejected and why; and **Consequences** (what gets easier, what
   gets harder, what is now a stated limitation). Link a design plan from `docs/design/` rather than
   repeating it when there is one.
3. Status starts as **Proposed**. It becomes **Accepted** only when the maintainer says so; an agent
   never marks its own ADR Accepted. Once the feature ships, update the status to **Implemented**
   with the date and the commits.
4. Add the record to the `adr/` list in [`docs/README.md`](docs/README.md) in the same change.
5. An accepted ADR is not rewritten to hide history. A later decision supersedes it with a new ADR, and
   the old one gets a one-line pointer.

When the maintainer has already asked for the feature, write the ADR as Proposed, implement, and flag the
decisions you took in your report so they can accept or amend it. Do not stop to ask unless a decision is
expensive to reverse.

## Rule 2: the same change updates the docs

A change is not done until the documentation that describes it is correct. CONTRIBUTING lists the cases;
the ones agents miss most:

- New reference behaviour goes in `docs/`, not only in a commit message or a code comment.
- A change to what a dashboard page shows or does updates `docs/dashboard-guide.md`, the tour steps in
  `dashboard-web/src/app/components/guide-overlay/guide-tours.ts`, and (when tour copy changed) that
  area's `version` in `dashboard-web/src/app/services/guide-areas.ts`.
- A new or changed option goes in `docs/configuration.md`; a persistence provider goes in
  `docs/persistence.md`.
- The sentence in the docs that your change made false is part of your change.

## Rule 3: verify, and report what you did not

- `dotnet build dotnet/VSaga.slnx` must stay at zero warnings (`TreatWarningsAsErrors`). Do not suppress
  an analyzer to get green; fix the cause, or justify a narrow suppression in a comment.
- Run the tests that cover what you touched, and the full suite before you call the work finished. Seven
  suites need Docker (Testcontainers). If Docker is unavailable, say the suites were not run; never
  report them as passing.
- UI changes are verified in the running compose stack, with the browser console open, not only under
  `ng serve` (see CONTRIBUTING).
- Anything touching message flow, headers, correlation, timing or a persistence provider is live-verified
  against the matching compose overlay, not only by unit tests.
- Report outcomes faithfully: what ran, what failed, what was skipped. Do not claim a tool ran, a page was
  inspected or a test passed unless it did.

## Rule 4: working conventions

- Generated code stays generated: EF Core migrations come from `dotnet ef` (commands in CONTRIBUTING),
  never by hand.
- Do not add a dependency, UI library or framework without stating the concrete benefit and getting
  approval. Package versions live in `dotnet/Directory.Packages.props` and `dashboard-web/package.json`.
- Frontend work follows [`docs/frontend.md`](docs/frontend.md) (how we design and verify UI) and
  [`dashboard-web/DESIGN.md`](dashboard-web/DESIGN.md) (the design-system source of truth).
- Match the surrounding code: naming, comment density, idiom. Keep changes small and one logical change
  per commit; commit messages explain why (CONTRIBUTING, "Commit conventions").
- Do not commit, push or open a PR unless asked. Never skip hooks or a failing check.
- Preserve working behaviour, routes, authentication and authorization, and API contracts unless the
  task is to change them.
