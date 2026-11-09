#!/usr/bin/env bash
# Runs one persistence sample, or all five in turn (ADR 0012).
#
#   ./run-sample.sh <inmemory|postgres|sqlserver|mongodb|redis|all> [--down | --clean]
#
# For a durable sample it starts that sample's docker-compose.yml with `up -d --wait`, runs the sample
# with `dotnet run`, and (with --down / --clean) removes the container afterwards, even if the sample
# failed. --down keeps the volume, so the sagas survive to the next run; --clean also removes it
# (`down -v`). Exit status: 0 everything passed, 1 a start or a sample failed, 2 usage error.
# run-sample.ps1 takes the same arguments.

set -u
cd "$(dirname "${BASH_SOURCE[0]}")" || exit 2

# Name, project directory, compose file ("-" for none). The order is the order `all` runs them in.
SAMPLES=(
  "inmemory|VSaga.Samples.Persistence.InMemory|-"
  "postgres|VSaga.Samples.Persistence.EFCore.Postgres|VSaga.Samples.Persistence.EFCore.Postgres/docker-compose.yml"
  "sqlserver|VSaga.Samples.Persistence.EFCore.SqlServer|VSaga.Samples.Persistence.EFCore.SqlServer/docker-compose.yml"
  "mongodb|VSaga.Samples.Persistence.MongoDB|VSaga.Samples.Persistence.MongoDB/docker-compose.yml"
  "redis|VSaga.Samples.Persistence.Redis|VSaga.Samples.Persistence.Redis/docker-compose.yml"
)

usage() {
  echo "Usage: $0 <inmemory|postgres|sqlserver|mongodb|redis|all> [--down | --clean]"
  echo "  --down   remove the sample's container afterwards, keeping its volume"
  echo "  --clean  remove the container and its volume afterwards (docker compose down -v)"
}

target=""
teardown=""
for arg in "$@"; do
  case "$arg" in
    -h|--help) usage; exit 0 ;;
    --down) [ "$teardown" = "clean" ] || teardown="down" ;;
    --clean) teardown="clean" ;;
    -*) echo "Unknown option: $arg" >&2; usage >&2; exit 2 ;;
    *)
      if [ -n "$target" ]; then echo "Only one sample may be named." >&2; usage >&2; exit 2; fi
      target="$arg" ;;
  esac
done
if [ -z "$target" ]; then usage >&2; exit 2; fi

# Compose file of the sample whose database is up right now, for the Ctrl-C handler.
active_compose=""

compose_down() {
  local file="$1"
  if [ "$teardown" = "clean" ]; then
    docker compose -f "$file" down -v
  else
    docker compose -f "$file" down
  fi
}

on_interrupt() {
  echo
  echo "Interrupted."
  if [ -n "$teardown" ] && [ -n "$active_compose" ]; then compose_down "$active_compose"; fi
  exit 130
}
trap on_interrupt INT TERM

# run_one <name>: prints the sample's output and returns 0 on success, non-zero on failure.
run_one() {
  local name="$1" row row_name project compose rc=0 started=0
  for row in "${SAMPLES[@]}"; do
    IFS='|' read -r row_name project compose <<<"$row"
    [ "$row_name" = "$name" ] && break
  done

  echo
  echo "##### $name #####"
  if [ "$compose" != "-" ]; then
    if ! command -v docker >/dev/null 2>&1; then
      echo "docker was not found on PATH; the $name sample needs a database container." >&2
      return 1
    fi
    echo "Starting $compose (up -d --wait)..."
    active_compose="$compose"
    if ! docker compose -f "$compose" up -d --wait; then
      echo "docker compose could not start the $name database." >&2
      if [ -n "$teardown" ]; then compose_down "$compose"; fi
      active_compose=""
      return 1
    fi
    started=1
  fi

  dotnet run --project "$project" || rc=$?

  if [ "$started" = 1 ]; then
    if [ -n "$teardown" ]; then compose_down "$compose" || true; else echo "Left running: docker compose -f $compose down [-v] removes it."; fi
    active_compose=""
  fi
  return "$rc"
}

if [ "$target" = "all" ]; then
  names=(); results=(); times=()
  for row in "${SAMPLES[@]}"; do names+=("${row%%|*}"); done
else
  ok=0
  for row in "${SAMPLES[@]}"; do [ "${row%%|*}" = "$target" ] && ok=1; done
  if [ "$ok" = 0 ]; then echo "Unknown sample: $target" >&2; usage >&2; exit 2; fi
  names=("$target"); results=(); times=()
fi

failed=0
for name in "${names[@]}"; do
  started_at=$SECONDS
  if run_one "$name"; then results+=("PASS"); else results+=("FAIL"); failed=1; fi
  times+=("$((SECONDS - started_at))")
done

if [ "$target" = "all" ]; then
  echo
  echo "##### Summary #####"
  printf '%-10s %-7s %s\n' "Sample" "Result" "Seconds"
  for i in "${!names[@]}"; do
    printf '%-10s %-7s %s\n' "${names[$i]}" "${results[$i]}" "${times[$i]}"
  done
fi

if [ "$failed" = 1 ]; then echo "At least one sample failed." >&2; fi
exit "$failed"
