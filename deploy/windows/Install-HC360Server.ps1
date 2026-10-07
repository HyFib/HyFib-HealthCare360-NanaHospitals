#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs (or upgrades) the HyFib HealthCare360 Server Node on the hospital's dedicated WiFi-connected machine.

.DESCRIPTION
    - Copies the published server to Program Files (the data folder is kept across upgrades).
    - Creates the PostgreSQL database and a least-privilege application role (optional, -CreateDatabase).
    - Writes the site configuration (%ProgramData%\HyFib\HealthCare360\config\hc360.settings.json), readable only by
      SYSTEM, Administrators and the service's own virtual account, with the database password protected by DPAPI for
      this machine (SRS SEC-07).
    - Checks that BitLocker protects the fixed disks, and registers the "HC360 disk encryption check" scheduled task,
      which checks again as SYSTEM every day and at start-up for System health (SRS SEC-05).
    - Registers the "HC360Server" Windows service (virtual account NT SERVICE\HC360Server, delayed auto-start,
      automatic restart on failure) and its Event Log source.
    - Opens HTTPS (7360) and mDNS (UDP 5353) in Windows Firewall for Private/Domain networks only. Serial analysers
      reach the server through their lab agent on 7360. With -LabPorts it also opens the ports network analysers connect
      to (Lab setup > Analysers > Connection), limited to -LabAnalyserAddresses when given.
    - Tunes the machine for an always-on WiFi server: no sleep/hibernate, WiFi adapter never powered down.
    - Starts the service, waits until it is healthy and prints the CA fingerprint staff will verify on workstations.

.EXAMPLE
    .\Install-HC360Server.ps1 -CreateDatabase -PostgresSuperuserPassword (Read-Host -AsSecureString "postgres password")

.EXAMPLE
    # Upgrade in place (keeps configuration, database and data)
    .\Install-HC360Server.ps1 -PackagePath ..\server
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $PackagePath = (Join-Path $PSScriptRoot '..\server'),
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'HyFib\HealthCare360\Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'HyFib\HealthCare360'),
    [int] $HttpsPort = 7360,
    [string] $FacilityCode = 'NH',
    [string] $FacilityName = 'Nana Hospitals',
    [switch] $CreateDatabase,
    [string] $PostgresHost = 'localhost',
    [int] $PostgresPort = 5432,
    [string] $PostgresSuperuser = 'postgres',
    [securestring] $PostgresSuperuserPassword,
    [string] $DatabaseName = 'hc360',
    [string] $SecondaryBackupDir,
    [switch] $SkipPowerTuning,
    [switch] $SkipFirewall,
    # Ports network (TCP) analysers connect to, as set in Lab setup > Analysers > Connection, e.g. 5100,5200.
    [int[]] $LabPorts,
    # The analysers' fixed IP addresses, so only they can reach those ports.
    [string[]] $LabAnalyserAddresses
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'HC360Server'
$ServiceAccount = "NT SERVICE\$ServiceName"
$ExeName = 'HyFib.HealthCare360.Server.exe'
$EventSource = 'HyFib HealthCare360'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function New-RandomSecret([int] $Bytes = 24) {
    $buffer = New-Object byte[] $Bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    # URL-safe, no quotes or semicolons (safe inside a connection string)
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', 'A').Replace('/', 'B')
}

# Local readiness probe. The certificate is issued by the hospital's private CA, which this machine does not have
# to trust, so validation is skipped for this one localhost request only.
function Test-ServerLive([int] $Port) {
    $uri = "https://localhost:$Port/health/live"
    try {
        if ($PSVersionTable.PSVersion.Major -ge 6) {
            return (Invoke-WebRequest -Uri $uri -SkipCertificateCheck -TimeoutSec 5).StatusCode -eq 200
        }
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
        $previous = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
        try {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { param($sender, $certificate, $chain, $errors) $true }
            return (Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200
        }
        finally {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $previous
        }
    }
    catch {
        return $false
    }
}

function Find-PostgresBin {
    $candidates = Get-ChildItem -Path (Join-Path $env:ProgramFiles 'PostgreSQL') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending |
        ForEach-Object { Join-Path $_.FullName 'bin' }
    foreach ($bin in $candidates) {
        if (Test-Path (Join-Path $bin 'psql.exe')) { return $bin }
    }
    return $null
}

function Set-RestrictedAcl([string] $Path, [switch] $ServiceWrite) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    foreach ($identity in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators')) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inherit, $none, 'Allow')))
    }
    $rights = if ($ServiceWrite) { 'Modify' } else { 'ReadAndExecute' }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($ServiceAccount, $rights, $inherit, $none, 'Allow')))
    Set-Acl -Path $Path -AclObject $acl
}

# ---------------------------------------------------------------- checks
Write-Step 'Checking prerequisites'
if (-not (Test-Path (Join-Path $PackagePath $ExeName))) {
    throw "The server package was not found at '$PackagePath' (expected $ExeName). Run Publish-HC360.ps1 or point -PackagePath at the extracted package."
}
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "    $($os.Caption) build $($os.BuildNumber), $([math]::Round($os.TotalVisibleMemorySize / 1MB, 1)) GB RAM"
if ([int]$os.BuildNumber -lt 19041) { throw 'Windows 10 2004 / Windows Server 2022 or newer is required.' }
$pgBin = Find-PostgresBin
if (-not $pgBin) {
    Write-Warning 'PostgreSQL was not found under Program Files. Install it first (Install-PostgreSQL.ps1) or backups will need -PgDumpPath configured manually.'
}

# ---------------------------------------------------------------- stop an existing service (upgrade)
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Step 'Stopping the running service for upgrade'
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

# ---------------------------------------------------------------- files
Write-Step "Copying server files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
robocopy $PackagePath $InstallDir /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy exit code $LASTEXITCODE)." }

foreach ($sub in @('', 'config', 'keys', 'certs', 'files', 'logs', 'backups', 'temp')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir $sub) | Out-Null
}

# ---------------------------------------------------------------- service account must exist before ACLs reference it
if (-not $existing) {
    Write-Step "Registering the Windows service '$ServiceName'"
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$(Join-Path $InstallDir $ExeName)`"" `
        -DisplayName 'HyFib HealthCare360 Server' `
        -Description 'HyFib HealthCare360 hospital information system (API, staff app, patient portal, background jobs).' `
        -StartupType Automatic | Out-Null
}
sc.exe config $ServiceName obj= $ServiceAccount start= delayed-auto | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

Set-RestrictedAcl -Path $DataDir -ServiceWrite
Set-RestrictedAcl -Path $InstallDir

if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    [System.Diagnostics.EventLog]::CreateEventSource($EventSource, 'Application')
}

# ---------------------------------------------------------------- database
$settingsPath = Join-Path $DataDir 'config\hc360.settings.json'
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { $null }

if ($CreateDatabase) {
    if (-not $pgBin) { throw 'PostgreSQL client tools (psql.exe) were not found.' }
    if (-not $PostgresSuperuserPassword) { $PostgresSuperuserPassword = Read-Host -AsSecureString "Password for PostgreSQL user '$PostgresSuperuser'" }
    Write-Step "Creating database '$DatabaseName' and application role 'hc360_app'"
    $appPassword = New-RandomSecret
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    try {
        $psql = Join-Path $pgBin 'psql.exe'
        $roleSql = "DO `$`$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'hc360_app') THEN CREATE ROLE hc360_app LOGIN PASSWORD '$appPassword'; ELSE ALTER ROLE hc360_app LOGIN PASSWORD '$appPassword'; END IF; END `$`$;"
        & $psql -h $PostgresHost -p $PostgresPort -U $PostgresSuperuser -d postgres -v ON_ERROR_STOP=1 -q -c $roleSql
        if ($LASTEXITCODE -ne 0) { throw 'Creating the application role failed.' }
        $exists = & $psql -h $PostgresHost -p $PostgresPort -U $PostgresSuperuser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'"
        if ($exists -ne '1') {
            & $psql -h $PostgresHost -p $PostgresPort -U $PostgresSuperuser -d postgres -v ON_ERROR_STOP=1 -q -c "CREATE DATABASE $DatabaseName OWNER hc360_app ENCODING 'UTF8'"
            if ($LASTEXITCODE -ne 0) { throw 'Creating the database failed.' }
        }
        # Extensions need superuser; the application role only owns its own schemas.
        & $psql -h $PostgresHost -p $PostgresPort -U $PostgresSuperuser -d $DatabaseName -v ON_ERROR_STOP=1 -q -c 'CREATE EXTENSION IF NOT EXISTS pg_trgm; REVOKE ALL ON SCHEMA public FROM PUBLIC;'
        if ($LASTEXITCODE -ne 0) { throw 'Preparing the database failed.' }
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
    $connectionString = "Host=$PostgresHost;Port=$PostgresPort;Database=$DatabaseName;Username=hc360_app;Password=$appPassword;Maximum Pool Size=200;Timeout=15;Command Timeout=60"
}
elseif ($settings -and $settings.ConnectionStrings -and $settings.ConnectionStrings.Hospital) {
    $connectionString = $settings.ConnectionStrings.Hospital
}
else {
    throw 'No database is configured yet. Re-run with -CreateDatabase (recommended) or create hc360.settings.json with ConnectionStrings.Hospital.'
}

# ---------------------------------------------------------------- configuration
Write-Step "Writing $settingsPath"
$backupDir = Join-Path $DataDir 'backups'
$config = [ordered]@{
    ConnectionStrings = [ordered]@{ Hospital = $connectionString }
    HealthCare360 = [ordered]@{
        HttpsPort = $HttpsPort
        DefaultFacilityCode = $FacilityCode
        DefaultFacilityName = $FacilityName
        Backup = [ordered]@{
            Enabled = $true
            Directory = $backupDir
            SecondaryDirectory = $SecondaryBackupDir
            PgDumpPath = if ($pgBin) { Join-Path $pgBin 'pg_dump.exe' } else { 'pg_dump' }
        }
    }
}
$config | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding UTF8

# The database password, and any other secret in the file, is kept protected with DPAPI for this machine, so a copy of
# the file is no use elsewhere (SRS SEC-07). A value carried over from an earlier install is already protected.
$exe = Join-Path $InstallDir $ExeName
& $exe settings protect --data-dir $DataDir | ForEach-Object { Write-Host "    $_" }
if ($LASTEXITCODE -ne 0) { throw "Protecting the secrets in $settingsPath failed (exit code $LASTEXITCODE)." }

# ---------------------------------------------------------------- disk encryption
# The keys that open HC360's stored data are on this machine, so its disks must be encrypted (SRS SEC-05). Reading
# BitLocker's state needs administrator rights, which the service does not have: the check runs here, and then as
# SYSTEM every day and at start-up, and System health shows what it found.
Write-Step 'Checking BitLocker on the fixed disks'
& $exe disk-encryption check --data-dir $DataDir | ForEach-Object { Write-Host "    $_" }
switch ($LASTEXITCODE) {
    0 { }
    3 { Write-Warning 'BitLocker does not protect every fixed disk. Turn it on, with the TPM, for every disk that holds HC360 data, backups or the database. System health warns until it is on.' }
    default { Write-Warning "The BitLocker check did not run (exit code $LASTEXITCODE). System health says so until the daily check runs." }
}
$taskAction = New-ScheduledTaskAction -Execute $exe -Argument "disk-encryption check --data-dir `"$($DataDir.TrimEnd('\'))`""
$taskTriggers = @((New-ScheduledTaskTrigger -Daily -At '06:15'), (New-ScheduledTaskTrigger -AtStartup))
$taskPrincipal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName 'HC360 disk encryption check' -TaskPath '\HyFib\' -Action $taskAction -Trigger $taskTriggers `
    -Principal $taskPrincipal -Settings $taskSettings -Force `
    -Description 'Records whether BitLocker protects the disks of the HC360 Server Node, for System health (SRS SEC-05).' | Out-Null

# ---------------------------------------------------------------- firewall
if (-not $SkipFirewall) {
    Write-Step 'Configuring Windows Firewall (Private and Domain networks only)'
    Get-NetFirewallRule -Group 'HyFib HealthCare360' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'HC360 Server (HTTPS)' -Group 'HyFib HealthCare360' -Direction Inbound -Protocol TCP -LocalPort $HttpsPort `
        -Program (Join-Path $InstallDir $ExeName) -Profile Private, Domain -Action Allow | Out-Null
    New-NetFirewallRule -DisplayName 'HC360 Server discovery (mDNS)' -Group 'HyFib HealthCare360' -Direction Inbound -Protocol UDP -LocalPort 5353 `
        -Program (Join-Path $InstallDir $ExeName) -Profile Private, Domain -Action Allow | Out-Null
    if ($LabPorts) {
        # A group of its own, so an upgrade without -LabPorts leaves the analysers' rule as it is.
        Get-NetFirewallRule -Group 'HyFib HealthCare360 lab analysers' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        $labRule = @{
            DisplayName = 'HC360 Server (network lab analysers)'
            Group = 'HyFib HealthCare360 lab analysers'
            Direction = 'Inbound'
            Protocol = 'TCP'
            LocalPort = $LabPorts
            Program = (Join-Path $InstallDir $ExeName)
            Profile = @('Private', 'Domain')
            Action = 'Allow'
        }
        if ($LabAnalyserAddresses) { $labRule.RemoteAddress = $LabAnalyserAddresses }
        New-NetFirewallRule @labRule | Out-Null
        Write-Host "    Network analysers may connect on port(s) $($LabPorts -join ', ')."
    }
    $public = Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public'
    foreach ($netProfile in $public) {
        Write-Warning "The network '$($netProfile.Name)' ($($netProfile.InterfaceAlias)) is marked Public, so workstations cannot connect. Mark the hospital WiFi as Private: Set-NetConnectionProfile -InterfaceAlias '$($netProfile.InterfaceAlias)' -NetworkCategory Private"
    }
}

# ---------------------------------------------------------------- always-on WiFi server
if (-not $SkipPowerTuning) {
    Write-Step 'Tuning power settings for an always-on server'
    powercfg /change standby-timeout-ac 0 | Out-Null
    powercfg /change hibernate-timeout-ac 0 | Out-Null
    powercfg /change disk-timeout-ac 0 | Out-Null
    # Wireless adapter power saving: Maximum Performance on mains power.
    powercfg /setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null
    powercfg /setactive SCHEME_CURRENT | Out-Null
    Get-NetAdapter -Physical | Where-Object { $_.NdisPhysicalMedium -eq 9 -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless|802\.11' } | ForEach-Object {
        try {
            Set-NetAdapterPowerManagement -Name $_.Name -AllowComputerToTurnOffDevice Disabled -ErrorAction Stop
            Write-Host "    WiFi adapter '$($_.Name)' will not be powered down."
        }
        catch { Write-Warning "Could not change power management for '$($_.Name)': $($_.Exception.Message)" }
    }
}

# ---------------------------------------------------------------- start and verify
Write-Step 'Starting HyFib HealthCare360'
Start-Service -Name $ServiceName
$healthy = $false
for ($i = 0; $i -lt 60 -and -not $healthy; $i++) {
    Start-Sleep -Seconds 3
    $healthy = Test-ServerLive -Port $HttpsPort
    if (-not $healthy) { Write-Host '    waiting for the server to start...' }
}
if (-not $healthy) {
    Write-Warning "The service did not report healthy within 3 minutes. Check $DataDir\logs and the Windows Application event log."
}

$caPath = Join-Path $DataDir 'certs\hc360-hospital-ca.crt'
if (Test-Path $caPath) {
    $ca = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $caPath
    $sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($ca.RawData)
    $fingerprint = -join ($sha | ForEach-Object { $_.ToString('X2') })
    $grouped = ($fingerprint -split '(.{4})' | Where-Object { $_ }) -join ' '
    Write-Host ''
    Write-Host 'Hospital CA fingerprint (staff compare this when setting up HC360 Desktop):' -ForegroundColor Green
    Write-Host "    $grouped"
}

$initialPassword = Join-Path $DataDir 'initial-admin-password.txt'
if (Test-Path $initialPassword) {
    Write-Host ''
    Write-Host 'First-time administrator sign-in:' -ForegroundColor Yellow
    Get-Content $initialPassword | ForEach-Object { Write-Host "    $_" }
    Write-Host "    Sign in once, set a new password, then delete: $initialPassword" -ForegroundColor Yellow
}

Write-Host ''
Write-Host "HyFib HealthCare360 is available at https://$($env:COMPUTERNAME):$HttpsPort/" -ForegroundColor Green
