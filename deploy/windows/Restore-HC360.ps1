#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Restores an encrypted HC360 backup (.hc360bak) into PostgreSQL, e.g. on replacement hardware.

.DESCRIPTION
    1. Stops the HC360 service.
    2. Restores the backup with the recovery key (Administration > Backups > Recovery key), using the server CLI's
       "backup restore". The key is passed through an environment variable, never on a command line. The whole backup
       is read and checked first; then the backup's field encryption key is installed (a different key already here
       is kept under a new name, never deleted) and the stored documents are put back. A damaged backup, the wrong
       key, or a backup from a newer HC360 version stops the script before the database is touched.
    3. Keeps the current database as hc360_before_restore_<timestamp> (nothing is dropped), creates a fresh one and
       runs pg_restore into it.
    4. Starts the service again.

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

$dump = Join-Path $DataDir ("temp\restore-{0}.dump" -f (Get-Date -Format 'yyyyMMddHHmmss'))
New-Item -ItemType Directory -Force -Path (Split-Path $dump) | Out-Null

Write-Step 'Stopping HC360'
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

try {
    Write-Step 'Checking the backup, then restoring the encryption key and documents'
    $env:HC360_RECOVERY_KEY = [System.Net.NetworkCredential]::new('', $RecoveryKey).Password
    & (Join-Path $InstallDir 'HyFib.HealthCare360.Server.exe') backup restore --in $BackupFile --database-out $dump --data-dir $DataDir
    $restoreExit = $LASTEXITCODE
    Remove-Item Env:\HC360_RECOVERY_KEY -ErrorAction SilentlyContinue
    if ($restoreExit -eq 3) { throw 'This backup was made by a newer version of HC360. Install that version first.' }
    if ($restoreExit -ne 0) { throw 'The backup could not be restored (wrong recovery key or damaged backup). Nothing was changed.' }

    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    $psql = Join-Path $pgBin 'psql.exe'
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $exists = & $psql -h $PostgresHost -p $PostgresPort -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'"
    if ($exists -eq '1') {
        Write-Step "Keeping the current database as ${DatabaseName}_before_restore_$stamp"
        & $psql -h $PostgresHost -p $PostgresPort -U postgres -d postgres -v ON_ERROR_STOP=1 -q `
            -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$DatabaseName'" `
            -c "ALTER DATABASE $DatabaseName RENAME TO ${DatabaseName}_before_restore_$stamp"
        if ($LASTEXITCODE -ne 0) { throw 'Could not rename the current database.' }
    }

    Write-Step 'Creating a fresh database and restoring'
    & $psql -h $PostgresHost -p $PostgresPort -U postgres -d postgres -v ON_ERROR_STOP=1 -q -c "CREATE DATABASE $DatabaseName OWNER hc360_app ENCODING 'UTF8'"
    & $psql -h $PostgresHost -p $PostgresPort -U postgres -d $DatabaseName -v ON_ERROR_STOP=1 -q -c 'CREATE EXTENSION IF NOT EXISTS pg_trgm'
    & (Join-Path $pgBin 'pg_restore.exe') -h $PostgresHost -p $PostgresPort -U postgres -d $DatabaseName --no-owner --role=hc360_app --exit-on-error $dump
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed. The previous database is still available under its renamed name.' }
}
finally {
    Remove-Item Env:\HC360_RECOVERY_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item $dump -Force -ErrorAction SilentlyContinue
}

Write-Step 'Starting HC360'
Start-Service -Name $ServiceName
Write-Host "Restore complete. Sign in and check recent records. When satisfied, drop ${DatabaseName}_before_restore_$stamp." -ForegroundColor Green
