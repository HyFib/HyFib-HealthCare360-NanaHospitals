<#
.SYNOPSIS
    Crashes the CI runner's PostgreSQL and checks that the database watchdog starts it again, and that it leaves a
    PostgreSQL someone stopped alone (SRS DED-01).

.DESCRIPTION
    Runs "HyFib.HealthCare360.Server.exe database watchdog" as the "HC360 database watchdog" scheduled task does, against
    the PostgreSQL service it finds as Install-HC360Server.ps1 does. It kills the database's server process (postgres.exe,
    the postmaster), as a crash would: PostgreSQL's service then reports a clean stop, which is why Windows' own restart
    on failure does not help and the watchdog is needed. Needs administrator rights and a PostgreSQL service on this
    machine, which may be stopped, started and crashed: CI runs it on its Windows runner after the tests.
    Exit code 1 when a check fails.

.PARAMETER Server
    The built HyFib.HealthCare360.Server.exe.
#>
param([Parameter(Mandatory)] [string] $Server)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Install-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$functions = 'Get-PgCtlDataDirectory', 'Set-RestartOnFailure'
foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($functions -contains $function.Name) { . ([scriptblock]::Create($function.Extent.Text)) }
}

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

function Wait-Until([scriptblock] $Condition, [int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { return $false }
        Start-Sleep -Milliseconds 500
    }
    return $true
}

$service = Get-CimInstance -ClassName Win32_Service | Where-Object { $_.PathName -match 'pg_ctl(\.exe)?"?\s+runservice' } | Select-Object -First 1
if (-not $service) { throw 'There is no PostgreSQL service on this machine.' }
$name = $service.Name
$data = Get-PgCtlDataDirectory $service.PathName
$record = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-watchdog-' + [guid]::NewGuid().ToString('N'))
Write-Host "PostgreSQL service $name, data folder $data"

function Invoke-Watchdog {
    $output = @(& $Server database watchdog --service $name --pgdata $data --data-dir $record)
    $output | ForEach-Object { Write-Host "        $_" }
    return $LASTEXITCODE
}

function Get-Record { Get-Content -LiteralPath (Join-Path $record 'config\database-watchdog.json') -Raw | ConvertFrom-Json }

function Get-Status { (Get-Service -Name $name).Status.ToString() }

try {
    # As the installers leave it.
    Set-Service -Name $name -StartupType Automatic
    Set-RestartOnFailure $name
    if ((Get-Status) -ne 'Running') { Start-Service -Name $name }

    Write-Host 'PostgreSQL that runs is left as it is'
    Assert ((Invoke-Watchdog) -eq 0) 'exit code 0'
    $first = Get-Record
    Assert ($first.serviceState -eq 'Running' -and $first.clusterState -eq 'InProduction') "the record says it runs (found: $($first.serviceState), $($first.clusterState))"
    Assert (@($first.restarts).Count -eq 0) 'it was not started'

    Write-Host 'PostgreSQL that crashed is started again'
    $pgCtl = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$name'").ProcessId
    $postmaster = Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId=$pgCtl AND Name='postgres.exe'" | Select-Object -First 1
    if (-not $postmaster) { throw "PostgreSQL's server process (postgres.exe, started by pg_ctl $pgCtl) was not found." }
    Stop-Process -Id $postmaster.ProcessId -Force
    Assert (Wait-Until { (Get-Status) -eq 'Stopped' } 60) 'the service stops once its database is killed'
    # What the watchdog is for: PostgreSQL's service reports a clean stop, so Windows does not restart it, failure actions
    # or not. Only noted, since this is PostgreSQL's behaviour, not HC360's.
    Start-Sleep -Seconds 8
    Write-Host "  note  8 s later, Windows has left the service $(Get-Status)"
    if ((Get-Status) -ne 'Stopped') { $null = Wait-Until { (Get-Status) -eq 'Running' } 60 }
    else {
        # The postmaster's own children notice it is gone and end; a start before then would find its memory in use.
        $null = Wait-Until { -not (Get-Process -Name postgres -ErrorAction SilentlyContinue) } 60
        Assert ((Invoke-Watchdog) -eq 0) 'exit code 0'
        Assert ((Get-Status) -eq 'Running') 'the service runs again'
        $started = @((Get-Record).restarts)
        Assert ($started.Count -eq 1 -and $started[0].succeeded) 'the record has the start, and that it worked'
        & (Join-Path $env:PGBIN 'pg_isready.exe') -h localhost -p 5432 -t 30 | Out-Null
        Assert ($LASTEXITCODE -eq 0) 'PostgreSQL takes connections'
    }

    Write-Host 'PostgreSQL that someone stopped is left stopped'
    Stop-Service -Name $name
    Assert ((Invoke-Watchdog) -eq 0) 'exit code 0'
    Assert ((Get-Status) -eq 'Stopped') 'the service is still stopped'
    $stopped = Get-Record
    Assert ($stopped.clusterState -eq 'ShutDown') "the data folder says it was shut down cleanly (found: $($stopped.clusterState))"
    Assert (@($stopped.restarts).Count -le 1) 'it was not started'
}
finally {
    if ((Get-Status) -ne 'Running') { Start-Service -Name $name }
    Remove-Item -LiteralPath $record -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
