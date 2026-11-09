# ADR 0012: One launcher script runs any persistence sample, or all five in turn

**Status:** **Accepted** — 2026-11-09, by the maintainer. **Implemented** — 2026-11-09, `9aa3c56`.
**Date:** 2026-11-09
**Relates to:** [`0009-sql-server-persistence-provider.md`](0009-sql-server-persistence-provider.md) (the
fifth provider, and the fifth sample, which made "start the right compose file, then the right project"
a five-way choice); [`0011-sample-orders-are-seeded-then-on-demand.md`](0011-sample-orders-are-seeded-then-on-demand.md)
(the other demo-ergonomics decision, for the reference stack). The samples are described in
[`../../dotnet/samples/Persistence/README.md`](../../dotnet/samples/Persistence/README.md).

---

## Context

`dotnet/samples/Persistence/` holds five console samples, one per persistence provider (in-memory,
EF Core on Postgres, EF Core on SQL Server, MongoDB, Redis), over a provider-neutral
`VSaga.Samples.Persistence.Common` project. Each has its own `Program.cs`, `README.md` and, for the four
durable ones, a `docker-compose.yml` on its own host port (`5434`, `1436`, `27019`, `6380`). The design
point of the set is that **only `Program.cs` differs**: comparing two of them shows exactly what switching
providers costs.

Running one is a two-step recipe whose second step depends on the first, and whose paths differ per
provider:

```
docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis/docker-compose.yml up -d --wait
dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis
```

Someone who wants to see "what does Redis persistence look like" has to learn the directory naming
(`EFCore.Postgres`, not `Postgres`), remember `--wait` (without it the sample starts before the database
and either retries noisily or fails), and later find the matching `down`. Someone who wants to see all
five has to repeat that five times and compare the output by eye. Nothing says which sample failed, and
a failed `dotnet run` leaves its container running.

The maintainer asked on 2026-11-09 for a command that runs any one provider's demo without knowing the
others exist, and all five in turn.

Constraints that shape the answer:

- The repository is developed on Windows (PowerShell, Git Bash) and CI runs Linux, so both a `.ps1` and
  a `.sh` entry point are needed. There is no `make`, `just` or task runner in the repository and adding
  one is a new dependency (AGENTS.md, Rule 4).
- The four compose files already name their own project (`vsaga-sample-<provider>`) and the volumes keep
  data across `down`/`up`, so the launcher needs no state of its own.
- The Windows checkout uses `core.autocrlf=true`, which would turn a `.sh` file into CRLF on checkout and
  break it under bash.

---

## Decision

**Add `run-sample.ps1` and `run-sample.sh` to `dotnet/samples/Persistence/`. Each takes
`inmemory|postgres|sqlserver|mongodb|redis|all`, starts that sample's compose file with `up -d --wait`
(nothing for in-memory), runs the existing sample project with `dotnet run`, and optionally tears the
database down.**

- **Same interface in both scripts.** `run-sample.sh redis --down` and `run-sample.ps1 redis --down`.
  The options are `--down` (run `docker compose down` after the sample, keeping the volume, so the
  sagas survive to the next run) and `--clean` (`down -v`, which also removes the volume and implies
  `--down`). The PowerShell script also accepts the PowerShell spellings `-Down` and `-Clean`. Without
  either option the database is left running, as the per-sample READMEs describe, so it can be inspected.
- **Teardown happens even when the sample fails.** `--down` / `--clean` run in a `finally` (and on Ctrl-C
  in the shell script), so a failed run does not leave a container behind.
- **Exit code.** `0` when everything that was asked for succeeded, `1` when a compose start or a sample
  failed, `2` for a usage error. The sample's own failure (an unhandled exception in `Program.cs`, which
  `CheckoutDemo` raises when a saga does not finish) is what makes `dotnet run` exit non-zero; the
  launcher does not parse output.
- **`all` runs the five in the order of the README table** (in-memory, Postgres, SQL Server, MongoDB,
  Redis), keeps going after a failure so one broken provider does not hide the others, tears each
  database down as it goes when `--down`/`--clean` is given (so only one database runs at a time,
  which matters for SQL Server's 2 GB), and ends with a summary table: sample, PASS/FAIL, seconds.
- **The scripts hold a five-row table** (name, project directory, compose file or none) and nothing else
  about providers. They change the working directory to their own, so they run from anywhere, and they
  pass compose-relative paths, which also sidesteps Git Bash path translation.
- **`.gitattributes` in the same directory pins `*.sh` to LF**, so a `core.autocrlf=true` checkout does
  not break the shell script. `.ps1` stays on the default.
- The sample projects, `Common`, the compose files and the ports do not change. The `Program.cs` header
  comments that said "identical in all four persistence samples" are corrected to five and now name the
  launcher as the first way to run each one.

---

## Options considered

| Option | Verdict |
| --- | --- |
| **Launcher scripts over the existing projects** | **Chosen.** Keeps "only `Program.cs` differs": each sample is still a project that references exactly one provider package. The scripts are about 100 lines each and are the only new code. A reader who wants the mechanism still sees the two raw commands, which the per-sample READMEs keep. |
| A single `VSaga.Samples.Persistence` project with a `--provider` switch | Rejected. It would reference all five provider packages (and their transitive trees: Npgsql, `Microsoft.Data.SqlClient`, the MongoDB driver, StackExchange.Redis), so the sample for one provider would load and compile against every other. The registration for each provider would become one branch of a `switch` in one `Program.cs`, which removes the side-by-side comparison the set exists for, and `--provider` would still need the compose file started by something. It also breaks the guarantee that `Common` and a sample compile against a single store. |
| A `Makefile` or `justfile` | Rejected. Neither is available on a default Windows machine, and adding a task runner is a dependency the repository has not taken. |
| One cross-platform script (PowerShell 7 everywhere, or a `dotnet` tool / file-based app) | Rejected for now. PowerShell 7 is not on every Linux box, and a `dotnet run script.cs` file-based app would compile on every launch and introduce a seventh project-like thing to maintain. Two thin scripts with one interface cost less than a toolchain requirement. |
| Teach each sample to start its own compose file (Testcontainers, or shelling out to `docker compose` from `Program.cs`) | Rejected. It would put infrastructure code into the files that are meant to show only provider registration, and Testcontainers is a new dependency in a sample. |
| Make `--down` the default | Rejected. The per-sample READMEs tell the reader to inspect the data the run wrote (`psql`, `mongosh`, `redis-cli`), and a durable store that is torn down by default makes the "the count keeps growing across runs" behaviour invisible. |
| Add a `docker-compose.yml` at `Persistence/` with all four databases | Rejected. Four databases at once, including a 2 GB SQL Server, for a command that is meant to run one provider. |

---

## Consequences

### Positive

- `./run-sample.sh redis` (or `.\run-sample.ps1 redis`) is the whole instruction for a first-time
  reader; the root README and the Persistence README lead with it.
- `all` is a quick, repeatable check that the five providers still behave the same, and a failure names
  the provider.
- A failed run no longer leaves a database behind when `--down` is given, and the scripts exit non-zero
  so they can be called from CI or another script.

### Negative and stated limitations

- Two scripts that must stay in step. They are short and have the same five-row table, and the verification
  for this decision ran both, but nothing in CI compares them.
- The launcher needs `dotnet` and, for four of the five, a running Docker daemon. When `docker` is missing
  or its daemon is down, that sample is reported as FAILED rather than skipped; `all` still runs the
  samples that do not need it, so in-memory passes on a machine with no Docker.
- `all` takes minutes on a cold machine (image pulls, and SQL Server's start-up is the slowest). It
  does not run samples in parallel, because they share the build output and a parallel `dotnet run` of
  five projects would fight for the same `obj` folders of `Common`.
- The scripts do not parse the sample's output, so a sample that prints a wrong report but exits `0`
  passes. Exit status is the contract; the report's shape is covered by reading the output.
- The host ports are still fixed (`5434`, `1436`, `27019`, `6380`). If a port is taken the compose start
  fails and is reported; the launcher does not pick another.
- A new persistence provider adds one row to each script, one sample project and one compose file.
