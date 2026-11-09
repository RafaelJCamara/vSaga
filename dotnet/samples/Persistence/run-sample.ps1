# Runs one persistence sample, or all five in turn (ADR 0012).
#
#   ./run-sample.ps1 <inmemory|postgres|sqlserver|mongodb|redis|all> [--down | --clean]
#
# For a durable sample it starts that sample's docker-compose.yml with `up -d --wait`, runs the sample
# with `dotnet run`, and (with --down / --clean) removes the container afterwards, even if the sample
# failed. --down keeps the volume, so the sagas survive to the next run; --clean also removes it
# (`down -v`). Exit status: 0 everything passed, 1 a start or a sample failed, 2 usage error.
# run-sample.sh takes the same arguments. -Down and -Clean are accepted as well as --down and --clean.

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Sample,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Options = @()
)

$ErrorActionPreference = 'Continue'
Set-Location -LiteralPath $PSScriptRoot

# Name, project directory, compose file ($null for none). The order is the order `all` runs them in.
$samples = @(
    @{ Name = 'inmemory'; Project = 'VSaga.Samples.Persistence.InMemory'; Compose = $null }
    @{ Name = 'postgres'; Project = 'VSaga.Samples.Persistence.EFCore.Postgres'; Compose = 'VSaga.Samples.Persistence.EFCore.Postgres/docker-compose.yml' }
    @{ Name = 'sqlserver'; Project = 'VSaga.Samples.Persistence.EFCore.SqlServer'; Compose = 'VSaga.Samples.Persistence.EFCore.SqlServer/docker-compose.yml' }
    @{ Name = 'mongodb'; Project = 'VSaga.Samples.Persistence.MongoDB'; Compose = 'VSaga.Samples.Persistence.MongoDB/docker-compose.yml' }
    @{ Name = 'redis'; Project = 'VSaga.Samples.Persistence.Redis'; Compose = 'VSaga.Samples.Persistence.Redis/docker-compose.yml' }
)

function Show-Usage {
    Write-Host "Usage: ./run-sample.ps1 <inmemory|postgres|sqlserver|mongodb|redis|all> [--down | --clean]"
    Write-Host "  --down   remove the sample's container afterwards, keeping its volume"
    Write-Host "  --clean  remove the container and its volume afterwards (docker compose down -v)"
}

$teardown = ''
foreach ($option in $Options) {
    switch -Regex ($option) {
        '^(-h|--help|-help|-\?)$' { Show-Usage; exit 0 }
        '^--?clean$' { $teardown = 'clean' }
        '^--?down$' { if ($teardown -ne 'clean') { $teardown = 'down' } }
        default { Write-Error "Unknown option: $option" -ErrorAction Continue; Show-Usage; exit 2 }
    }
}
if (-not $Sample) { Show-Usage; exit 2 }
if ($Sample -in '-h', '--help', '-help') { Show-Usage; exit 0 }

if ($Sample -eq 'all') {
    $selected = $samples
} else {
    $selected = @($samples | Where-Object { $_.Name -eq $Sample })
    if ($selected.Count -eq 0) {
        Write-Error "Unknown sample: $Sample" -ErrorAction Continue
        Show-Usage
        exit 2
    }
}

function Stop-Database([string]$ComposeFile) {
    if ($teardown -eq 'clean') {
        docker compose -f $ComposeFile down -v | Out-Host
    } else {
        docker compose -f $ComposeFile down | Out-Host
    }
}

# Returns $true when the sample ran and exited 0.
function Invoke-Sample($entry) {
    Write-Host ''
    Write-Host "##### $($entry.Name) #####"
    $compose = $entry.Compose
    $started = $false
    if ($compose) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            Write-Host "docker was not found on PATH; the $($entry.Name) sample needs a database container." -ForegroundColor Red
            return $false
        }
        Write-Host "Starting $compose (up -d --wait)..."
        docker compose -f $compose up -d --wait | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Host "docker compose could not start the $($entry.Name) database." -ForegroundColor Red
            if ($teardown) { Stop-Database $compose }
            return $false
        }
        $started = $true
    }

    $ok = $false
    try {
        dotnet run --project $entry.Project | Out-Host
        $ok = ($LASTEXITCODE -eq 0)
    } finally {
        if ($started) {
            if ($teardown) {
                Stop-Database $compose
            } else {
                Write-Host "Left running: docker compose -f $compose down [-v] removes it."
            }
        }
    }
    return $ok
}

$rows = @()
$failed = $false
foreach ($entry in $selected) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = [bool](Invoke-Sample $entry | Select-Object -Last 1)
    $clock.Stop()
    if (-not $ok) { $failed = $true }
    $rows += [pscustomobject]@{
        Sample  = $entry.Name
        Result  = if ($ok) { 'PASS' } else { 'FAIL' }
        Seconds = [int][math]::Round($clock.Elapsed.TotalSeconds)
    }
}

if ($Sample -eq 'all') {
    Write-Host ''
    Write-Host '##### Summary #####'
    $rows | Format-Table -AutoSize | Out-Host
}

if ($failed) {
    Write-Host 'At least one sample failed.' -ForegroundColor Red
    exit 1
}
exit 0
