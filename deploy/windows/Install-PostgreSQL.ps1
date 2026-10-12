#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs PostgreSQL on the HC360 Server Node, reachable from this machine only, and tunes it for the hardware.

.DESCRIPTION
    Uses winget to install the EDB PostgreSQL build silently (the superuser password is passed through a temporary
    option file readable only by Administrators, never on the command line), then:
      - listens on localhost only (HC360 is the only client; workstations never talk to the database),
      - enforces scram-sha-256 password authentication,
      - sizes memory settings from the installed RAM,
      - logs slow statements without their parameter values, which can be patient details,
      - has Windows restart the service when its process fails (SRS DED-01). Install-HC360Server.ps1 then adds the
        "HC360 database watchdog" task, which also starts it again when the database itself crashed.

.EXAMPLE
    .\Install-PostgreSQL.ps1 -SuperuserPassword (Read-Host -AsSecureString 'New postgres password')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [securestring] $SuperuserPassword,
    [ValidateSet('16', '17', '18')] [string] $Version = '17',
    [int] $Port = 5432
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# Has Windows restart the service $Name when its process ends unexpectedly, or it stops with an error: after 5 seconds,
# 15 seconds, then every minute, counting again after a day without a failure (SRS DED-01). Install-HC360Server.ps1 has
# the same function.
function Set-RestartOnFailure([string] $Name) {
    sc.exe failure $Name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -eq 0) { sc.exe failureflag $Name 1 | Out-Null }
    if ($LASTEXITCODE -ne 0) { Write-Warning "Windows did not take the restart-on-failure settings of the service $Name (sc.exe exit code $LASTEXITCODE)." }
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'winget is not available. Install "App Installer" from the Microsoft Store, or install PostgreSQL manually from https://www.postgresql.org/download/windows/ and re-run Install-HC360Server.ps1.'
}

$plain = [System.Net.NetworkCredential]::new('', $SuperuserPassword).Password
if ($plain.Length -lt 12) { throw 'Use a PostgreSQL superuser password of at least 12 characters.' }

$optionFile = Join-Path $env:TEMP ("pg-options-{0}.txt" -f [guid]::NewGuid().ToString('N'))
try {
    Set-Content -Path $optionFile -Encoding ASCII -Value @(
        'mode=unattended',
        'unattendedmodeui=none',
        "superpassword=$plain",
        "serverport=$Port",
        'enable-components=server,commandlinetools',
        'disable-components=pgAdmin,stackbuilder'
    )
    $acl = Get-Acl $optionFile
    $acl.SetAccessRuleProtection($true, $false)
    $acl.Access | ForEach-Object { [void]$acl.RemoveAccessRule($_) }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', 'Allow')))
    Set-Acl -Path $optionFile -AclObject $acl

    Write-Step "Installing PostgreSQL $Version (this takes a few minutes)"
    winget install --exact --id "PostgreSQL.PostgreSQL.$Version" --accept-package-agreements --accept-source-agreements --silent `
        --override "--optionfile `"$optionFile`""
    if ($LASTEXITCODE -ne 0) { throw "winget failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item $optionFile -Force -ErrorAction SilentlyContinue
}

$root = Join-Path $env:ProgramFiles "PostgreSQL\$Version"
$bin = Join-Path $root 'bin'
$data = Join-Path $root 'data'
if (-not (Test-Path (Join-Path $bin 'psql.exe'))) { throw "PostgreSQL was not found at $root after installation." }

Write-Step 'Restricting PostgreSQL to this machine and tuning memory'
$ramMb = [math]::Floor((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB)
$sharedBuffers = [math]::Max(256, [math]::Floor($ramMb / 4))
$effectiveCache = [math]::Max(512, [math]::Floor($ramMb / 2))
$maintenance = [math]::Min(1024, [math]::Max(128, [math]::Floor($ramMb / 16)))

$env:PGPASSWORD = $plain
try {
    $psql = Join-Path $bin 'psql.exe'
    $statements = @(
        "ALTER SYSTEM SET listen_addresses = 'localhost'",
        "ALTER SYSTEM SET password_encryption = 'scram-sha-256'",
        "ALTER SYSTEM SET shared_buffers = '${sharedBuffers}MB'",
        "ALTER SYSTEM SET effective_cache_size = '${effectiveCache}MB'",
        "ALTER SYSTEM SET maintenance_work_mem = '${maintenance}MB'",
        "ALTER SYSTEM SET work_mem = '16MB'",
        "ALTER SYSTEM SET max_connections = 300",
        "ALTER SYSTEM SET wal_compression = on",
        "ALTER SYSTEM SET log_min_duration_statement = 2000",
        # Slow statements are logged for tuning, but never their parameter values: those can be patient details.
        "ALTER SYSTEM SET log_parameter_max_length = 0",
        "ALTER SYSTEM SET log_parameter_max_length_on_error = 0",
        "ALTER SYSTEM SET timezone = 'Asia/Kolkata'"
    )
    foreach ($sql in $statements) {
        & $psql -h localhost -p $Port -U postgres -d postgres -v ON_ERROR_STOP=1 -q -c $sql
        if ($LASTEXITCODE -ne 0) { throw "Failed: $sql" }
    }
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# Only local connections, always with scram-sha-256.
$hba = Join-Path $data 'pg_hba.conf'
Copy-Item $hba "$hba.bak" -Force
Set-Content -Path $hba -Encoding ASCII -Value @(
    '# HyFib HealthCare360: the database is used only by the HC360 service on this machine.',
    '# TYPE  DATABASE  USER  ADDRESS        METHOD',
    'local   all       all                  scram-sha-256',
    'host    all       all   127.0.0.1/32   scram-sha-256',
    'host    all       all   ::1/128        scram-sha-256'
)

$service = Get-Service | Where-Object { $_.Name -like "postgresql*$Version*" } | Select-Object -First 1
if ($service) {
    Write-Step "Restarting $($service.Name)"
    Restart-Service -Name $service.Name -Force
    Set-Service -Name $service.Name -StartupType Automatic
    Set-RestartOnFailure $service.Name
}

Write-Host ''
Write-Host "PostgreSQL $Version is ready on localhost:$Port. Next: .\Install-HC360Server.ps1 -CreateDatabase" -ForegroundColor Green
