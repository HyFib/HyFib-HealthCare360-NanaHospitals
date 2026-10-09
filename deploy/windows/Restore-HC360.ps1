#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Restores an encrypted HC360 backup (.hc360bak) into PostgreSQL, e.g. on replacement hardware.

.DESCRIPTION
    Everything that can be checked is checked before anything changes, and the backup's encryption key is installed
    last, once its database is in place. If a step fails after the database was touched, the previous database is put
    back and HC360 is started again on it.

    1. Checks the PostgreSQL sign-in.
    2. Checks the whole backup with its recovery key and extracts its database, using the server CLI's "backup decrypt".
       The key is the one that was current when the backup was made: Administration > Backups lists it, and a wrong key
       is answered with the id of the right one. Backups made before HC360 named their key open with the original key;
       on the Server Node that made them, entering "original" uses that key without typing it. The key is passed through
       an environment variable, never on a command line. A damaged backup, the wrong key, or a backup from a newer HC360
       version stops here: nothing has changed and HC360 is still running.
    3. Stops the HC360 service, keeps the current database as hc360_before_restore_<timestamp> (nothing is dropped),
       creates a fresh one and runs pg_restore into it.
    4. Puts back the stored documents and then installs the backup's field encryption keys ("backup restore"). A different
       key already here is kept under a new name, never deleted. On new hardware, later backups open with the recovery
       key entered here.
    5. Starts the service again.

    Backups made before HC360 archive backups hold the database only. To restore one of those on new hardware, copy
    the old data folder's "keys" directory back first, or the encrypted identifiers cannot be read.

.EXAMPLE
    .\Restore-HC360.ps1 -BackupFile D:\HC360-Backups\hc360-20260925-0130.hc360bak
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)] [string] $BackupFile,
    [securestring] $RecoveryKey,
    [securestring] $PostgresSuperuserPassword,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'HyFib\HealthCare360\Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'HyFib\HealthCare360'),
    [string] $DatabaseName = 'hc360',
    [string] $PostgresHost = 'localhost',
    [int] $PostgresPort = 5432
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ServiceName = 'HC360Server'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

if (-not (Test-Path $BackupFile)) { throw "Backup file not found: $BackupFile" }
$pgBin = Get-ChildItem (Join-Path $env:ProgramFiles 'PostgreSQL') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | ForEach-Object { Join-Path $_.FullName 'bin' } |
    Where-Object { Test-Path (Join-Path $_ 'pg_restore.exe') } | Select-Object -First 1
if (-not $pgBin) { throw 'PostgreSQL client tools were not found.' }
if (-not $RecoveryKey) { $RecoveryKey = Read-Host -AsSecureString 'Backup recovery key' }
if (-not $PostgresSuperuserPassword) { $PostgresSuperuserPassword = Read-Host -AsSecureString 'PostgreSQL superuser (postgres) password' }

if (-not $PSCmdlet.ShouldProcess($DatabaseName, "Restore from $BackupFile (the current database is kept under a new name)")) { return }

$server = Join-Path $InstallDir 'HyFib.HealthCare360.Server.exe'
$psql = Join-Path $pgBin 'psql.exe'
$pgArgs = @('-h', $PostgresHost, '-p', $PostgresPort, '-U', 'postgres')
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$keptName = "${DatabaseName}_before_restore_$stamp"
$dump = Join-Path $DataDir "temp\restore-$stamp.dump"
New-Item -ItemType Directory -Force -Path (Split-Path $dump) | Out-Null

# Runs SQL statements, each on its own (DROP DATABASE cannot run in a transaction), and says whether all succeeded.
function Invoke-Sql([string] $Database, [string[]] $Statements) {
    $sqlArgs = $pgArgs + @('-d', $Database, '-v', 'ON_ERROR_STOP=1', '-q')
    foreach ($statement in $Statements) { $sqlArgs += @('-c', $statement) }
    & $psql @sqlArgs | Out-Null
    return ($LASTEXITCODE -eq 0)
}

# Runs a server CLI command and returns its exit code. The recovery key is in the environment only during the call.
function Invoke-ServerCli([string[]] $Arguments) {
    $env:HC360_RECOVERY_KEY = [System.Net.NetworkCredential]::new('', $RecoveryKey).Password
    try { & $server @Arguments | Out-Host }
    finally { Remove-Item Env:\HC360_RECOVERY_KEY -ErrorAction SilentlyContinue }
    return $LASTEXITCODE
}

$stopped = $false
$renamed = $false
$created = $false
$finished = $false
$env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
try {
    Write-Step 'Checking the PostgreSQL sign-in'
    $exists = & $psql @pgArgs -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'"
    if ($LASTEXITCODE -ne 0) { throw 'Could not sign in to PostgreSQL as postgres. Nothing was changed.' }

    Write-Step 'Checking the whole backup and extracting its database'
    $decryptExit = Invoke-ServerCli @('backup', 'decrypt', '--in', $BackupFile, '--out', $dump, '--data-dir', $DataDir)
    if ($decryptExit -eq 3) { throw 'This backup was made by a newer version of HC360. Install that version first. Nothing was changed.' }
    if ($decryptExit -ne 0) { throw 'The backup could not be read (wrong recovery key or damaged backup). Nothing was changed.' }

    Write-Step 'Stopping HC360'
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    $stopped = $true

    if ($exists -eq '1') {
        Write-Step "Keeping the current database as $keptName"
        $renamedOk = Invoke-Sql 'postgres' @(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$DatabaseName'",
            "ALTER DATABASE $DatabaseName RENAME TO $keptName")
        if (-not $renamedOk) { throw 'Could not rename the current database. Nothing was changed.' }
        $renamed = $true
    }

    Write-Step 'Creating a fresh database and restoring'
    if (-not (Invoke-Sql 'postgres' @("CREATE DATABASE $DatabaseName OWNER hc360_app ENCODING 'UTF8'"))) { throw 'Could not create the database.' }
    $created = $true
    if (-not (Invoke-Sql $DatabaseName @('CREATE EXTENSION IF NOT EXISTS pg_trgm'))) { throw 'Could not enable the pg_trgm extension.' }
    & (Join-Path $pgBin 'pg_restore.exe') @pgArgs -d $DatabaseName --no-owner --role=hc360_app --exit-on-error $dump
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed.' }

    # Only now that the backup's database is in place does this server switch to the backup's encryption key.
    Write-Step 'Putting back the documents, then the backup''s encryption key'
    if ((Invoke-ServerCli @('backup', 'restore', '--in', $BackupFile, '--data-dir', $DataDir)) -ne 0) {
        throw 'The documents and the encryption key could not be put back; the key was not changed.'
    }
    $finished = $true
}
catch {
    $previousBack = $true
    if (-not $finished -and ($created -or $renamed)) {
        Write-Step 'Putting the previous database back'
        $statements = @("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$DatabaseName'")
        if ($created) { $statements += "DROP DATABASE IF EXISTS $DatabaseName" }
        if ($renamed) { $statements += "ALTER DATABASE $keptName RENAME TO $DatabaseName" }
        $previousBack = Invoke-Sql 'postgres' $statements
        if (-not $previousBack) {
            Write-Warning "The previous database could not be put back. It is kept as $keptName; rename it to $DatabaseName before starting HC360."
        }
    }

    if ($stopped -and $previousBack) {
        Write-Step 'Starting HC360 again on the previous data'
        try { Start-Service -Name $ServiceName } catch { Write-Warning "HC360 could not be started: $($_.Exception.Message)" }
    }

    throw
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item $dump -Force -ErrorAction SilentlyContinue
}

Write-Step 'Starting HC360'
Start-Service -Name $ServiceName
if ($renamed) {
    Write-Host "Restore complete. Sign in and check recent records. When satisfied, drop $keptName." -ForegroundColor Green
}
else {
    Write-Host 'Restore complete. Sign in and check recent records.' -ForegroundColor Green
}
