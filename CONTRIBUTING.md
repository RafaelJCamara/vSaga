# Contributing to vSaga

## Prerequisites

- **.NET SDK** matching `dotnet/global.json` (currently `10.0.301`, `rollForward: latestFeature` — an
  older SDK scaffolds/targets the wrong framework and every `dotnet` command below fails from the
  start).
- **Node.js ≥ 22**, for the Angular dashboard SPA only (`dashboard-web/package.json`'s `engines` field;
  also what CI's `setup-node` installs).
- **Docker**: seven `dotnet/tests/*` suites use Testcontainers (RabbitMQ, MassTransit, Wolverine,
  Brighter, Postgres, Redis, MongoDB — see the Test section below). Without Docker, those suites fail
  outright rather than skip.

Every command below uses `&&` to chain steps — that's a parse error in Windows PowerShell 5.1
(`powershell.exe`). Use PowerShell 7+ (`pwsh`, which supports `&&` the same as bash) or Git Bash/WSL
instead.

## Build

```bash
dotnet build dotnet/VSaga.slnx
```

Must stay clean with **zero warnings** — `TreatWarningsAsErrors` is on
(`dotnet/Directory.Build.props`) across every project, backed by SonarAnalyzer.CSharp,
Meziantou.Analyzer, and AsyncFixer.

The Angular dashboard SPA in `dashboard-web/` is not part of the .NET solution; it builds with npm and
the Angular CLI (the line ends by returning to the repo root, so it's safe to paste as-is):

```bash
cd dashboard-web && npm ci && npx ng build && cd ..
```

## Test

```bash
dotnet test dotnet/VSaga.slnx
```

800+ tests across every `dotnet/tests/*` project. Seven suites are Testcontainers-backed (RabbitMQ,
MassTransit, Wolverine, Brighter, Postgres, Redis, MongoDB — the last needs a replica set, which its
fixture starts) and need Docker; everything else runs without it. If
Docker isn't available, say so rather than skipping silently — this repo's own history treats "these
suites were only compiled, never run" as an explicit, carried-forward caveat, not a pass.

```bash
cd dashboard-web && npx ng test --watch=false && cd ..
```

**Live verification**, for anything touching message flow, envelope headers, or timing (a new
transport adapter, a change to correlation/causation, anything outbox- or timeout-related):

```bash
docker compose up -d --build
# and, for fault-injection-relevant changes:
docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build
# and, for anything touching the Redis or MongoDB persistence provider, its own overlay (docs/persistence.md):
docker compose -p vsaga-redis -f docker-compose.yml -f docker-compose.redis.yml up -d --build
docker compose -p vsaga-mongo -f docker-compose.yml -f docker-compose.mongo.yml up -d --build
# and, for anything touching the Wolverine, MassTransit, Brighter or HTTP transport adapter, its own
# overlay (docs/transports/index.md; plain `docker compose up` runs the RabbitMQ adapter):
docker compose -p vsaga-wolverine -f docker-compose.yml -f docker-compose.wolverine.yml up -d --build
docker compose -p vsaga-masstransit -f docker-compose.yml -f docker-compose.masstransit.yml up -d --build
docker compose -p vsaga-brighter -f docker-compose.yml -f docker-compose.brighter.yml up -d --build
docker compose -p vsaga-http -f docker-compose.yml -f docker-compose.http.yml up -d --build
```

Filter queries by `createdAtUtc`/`updatedAtUtc` after the container's own start timestamp — the named
Postgres volume is **not** reset by `docker compose up` (see
[`docs/persistence.md`](docs/persistence.md#the-volume-caveat)). Use `docker compose down -v` for a
genuinely clean read.

**Mutation testing**, for anything envelope/header/linkage-adjacent: deliberately break the change
(comment out a header copy, revert a scoping predicate, remove a guard), confirm *exactly* the tests
written for it fail and nothing else does, then restore. This repo's commit history
(`docs/history/`) is full of concrete examples of this discipline and the bugs it caught that a
"does a test exist" check alone would have missed.

## Commit conventions

One logical change per commit. A subject line in imperative present tense, specific about what
changed, e.g.:

```
Add SagaState.BusinessKey with a partial-unique reservation index
Fix SagaTimeoutDispatcherHostedService's captive-dependency bug
Route PublishChildSagaFinishedAsync through the outbox
```

The body explains **what** changed and, more importantly, **why** — the design trade-off, the bug
being fixed, or the constraint that forced the shape. `git log --oneline` is this repo's own best
reference for the expected tone and level of detail.

Never skip pre-commit hooks or a failing check to get a commit in. If a build or test is red, fix the
underlying issue before committing, not after.

## Before opening a PR

- `dotnet build dotnet/VSaga.slnx` is clean (zero warnings) and `dotnet test dotnet/VSaga.slnx` passes.
- If you touched `dashboard-web`: `npm ci && npm audit --audit-level=low && npx ng build && npx ng test --watch=false`
  pass from that directory. `npm audit` fails on any advisory rated `low` through `critical`, dev
  dependencies included, exactly as CI's does.
- If your change touches message flow, headers, correlation, or timing, you've live-verified it
  against `docker compose up` (and the chaos overlay, where relevant) — not just unit tests.
- New reference behaviour is documented in `docs/`, not left only in a commit message or code comment.

CI (`.github/workflows/ci.yml`) runs two independent jobs on every push/PR to `main`, and both must
pass:

- `.NET build & test`: `dotnet restore`, then `dotnet build` and `dotnet test` on `dotnet/VSaga.slnx`
  with `--configuration Release` (the commands above build Debug), on the SDK `dotnet/global.json`
  pins, with Testcontainers' Ryuk sidecar disabled (`TESTCONTAINERS_RYUK_DISABLED`).
- `Angular build & test`: in `dashboard-web/` on Node 22, `npm ci`, then `npm audit --audit-level=low`,
  then `npx ng build`, then `npx ng test --watch=false`. `npm ci` only reports known vulnerabilities;
  the `npm audit` step fails the job on **any** advisory rated `low` through `critical`, in dev
  dependencies too. Neither job is
  path-filtered, so a newly published advisory can fail a PR that never touched `dashboard-web/`.
