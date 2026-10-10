#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs (or upgrades) the HyFib HealthCare360 Server Node on the hospital's dedicated WiFi-connected machine.

.DESCRIPTION
    - Copies the published server to Program Files (the data folder is kept across upgrades).
    - Creates the PostgreSQL database and a least-privilege application role (optional, -CreateDatabase).
    - Has the service sign in to PostgreSQL with its Windows account (SSPI), so the settings file holds no database
      password (-DatabaseSignIn Windows, the default). The switch is made once the server has started as it was, and is
      undone if the server then cannot reach the database. Needs PostgreSQL on this machine; -DatabaseSignIn Password
      goes back to a password.
    - Writes the site configuration (%ProgramData%\HyFib\HealthCare360\config\hc360.settings.json), readable only by
      SYSTEM, Administrators and the service's own virtual account, with the database password protected by DPAPI for
      this machine (SRS SEC-07). Every setting already in the file is kept, including those added by hand. Of the
      installer's own, one the file has changes only when its parameter is given again (-HttpsPort, -PortalHttpsPort,
      -FacilityCode, -FacilityName, -SecondaryBackupDir, -CreateDatabase); pg_dump follows the newest PostgreSQL found.
    - Checks that BitLocker protects the fixed disks, and registers the "HC360 disk encryption check" scheduled task,
      which checks again as SYSTEM every day and at start-up for System health (SRS SEC-05).
    - Registers the "HC360Server" Windows service (virtual account NT SERVICE\HC360Server, delayed auto-start,
      automatic restart on failure) and its Event Log source.
    - Opens HTTPS for the staff app (7360) and for the patient portal, which has an address of its own (7361), and mDNS
      (UDP 5353) in Windows Firewall for Private/Domain networks only. Serial analysers reach the server through their
      lab agent on 7360. With -LabPorts it also opens the ports network analysers connect
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
    [int] $PortalHttpsPort = 7361,
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
    [string[]] $LabAnalyserAddresses,
    # How the service signs in to PostgreSQL: with its Windows account (no password anywhere), or with a password.
    [ValidateSet('Windows', 'Password')] [string] $DatabaseSignIn = 'Windows'
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

# Local health probe: "live" when the server is up, "ready" when it also reaches the database. The certificate is issued
# by the hospital's private CA, which this machine does not have to trust, so validation is skipped for this one
# localhost request only.
function Test-ServerHealth([int] $Port, [string] $Probe = 'live') {
    $uri = "https://localhost:$Port/health/$Probe"
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

# Waits up to 3 minutes for the server to be up and to reach the database.
function Wait-ServerReady([int] $Port) {
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        if (Test-ServerHealth -Port $Port -Probe 'ready') { return $true }
        Write-Host '    waiting for the server to start...'
    }
    return $false
}

function Stop-HC360Service {
    $service = Get-Service -Name $ServiceName
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
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

# ---------------------------------------------------------------- site settings (hc360.settings.json)
# The file belongs to the site and survives upgrades: the installer changes only its own settings in it, and keeps the
# rest as they are, such as HealthCare360:Pdf:BrowserPath, a reporting replica and protected (dpapi:) values. Settings are
# named as the server reads them, Section:Name.

# The settings file, or an empty one when there is none yet. It is read as UTF-8, as the server reads it: Windows
# PowerShell reads a file without a byte order mark as ANSI otherwise.
function Read-SettingsFile([string] $Path) {
    $text = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 }
    if (-not $text -or -not $text.Trim()) { return [pscustomobject]@{} }
    try { $settings = $text | ConvertFrom-Json }
    catch { throw "$Path is not valid JSON: $($_.Exception.Message) Correct it and run the installer again (Windows PowerShell does not accept comments in it). Nothing has been changed." }
    if ($settings -isnot [System.Management.Automation.PSCustomObject]) {
        throw "$Path does not hold a JSON object. Correct it and run the installer again. Nothing has been changed."
    }
    return $settings
}

# Every setting as the server reads it: its name (Section:Name, array items by index) and its value as text. A null
# value holds nothing to keep, and is left out.
function ConvertTo-SettingTable($Node, [string] $Key = '', [hashtable] $Table = @{}) {
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Node.PSObject.Properties) {
            $name = if ($Key) { "${Key}:$($property.Name)" } else { $property.Name }
            $null = ConvertTo-SettingTable $property.Value $name $Table
        }
    }
    elseif ($Node -is [System.Collections.IList]) {
        for ($i = 0; $i -lt $Node.Count; $i++) { $null = ConvertTo-SettingTable $Node[$i] "${Key}:$i" $Table }
    }
    elseif ($null -ne $Node) {
        if ($Table.ContainsKey($Key)) { throw "The setting $Key is in hc360.settings.json twice, once with ':' in a name. The server does not start like that: keep one of them." }
        $Table[$Key] = "$Node"
    }
    return $Table
}

# The property that holds the setting $Key, nested ("Backup": { "Directory": ... }) or with colons in its name
# ("Backup:Directory": ...), as the server reads either; $null when the file does not have it.
function Find-Setting($Node, [string] $Key) {
    if ($Node -isnot [System.Management.Automation.PSCustomObject]) { return $null }
    foreach ($property in $Node.PSObject.Properties) {
        if ($property.Name -eq $Key) { return $property }
        if ($Key.StartsWith($property.Name + ':', [System.StringComparison]::OrdinalIgnoreCase)) {
            $found = Find-Setting $property.Value $Key.Substring($property.Name.Length + 1)
            if ($found) { return $found }
        }
    }
    return $null
}

# Sets one of the installer's own settings, and returns its name when that changes the file. A setting the file has is
# replaced only with -Replace, where it is; a missing one is added, in nested sections.
function Set-Setting($Settings, [string] $Key, $Value, [switch] $Replace) {
    $setting = Find-Setting $Settings $Key
    if ($setting) {
        if ($Replace -and "$($setting.Value)" -cne "$Value") {
            $setting.Value = $Value
            return $Key
        }
        return
    }
    $node = $Settings
    $names = $Key -split ':'
    for ($i = 0; $i -lt $names.Count - 1; $i++) {
        $section = $node.PSObject.Properties[$names[$i]]
        if (-not $section) {
            $node | Add-Member -NotePropertyName $names[$i] -NotePropertyValue ([pscustomobject]@{})
            $section = $node.PSObject.Properties[$names[$i]]
        }
        elseif ($section.Value -isnot [System.Management.Automation.PSCustomObject]) {
            $section.Value = [pscustomobject]@{}
        }
        $node = $section.Value
    }
    $node | Add-Member -NotePropertyName $names[-1] -NotePropertyValue $Value
    return $Key
}

# The settings as JSON. ConvertTo-Json can lose a value without a word (Windows PowerShell cuts off what lies deeper
# than -Depth), so every setting but $Changed must read back exactly as it was in $Before, or this stops.
function ConvertTo-SettingsJson($Settings, [hashtable] $Before, [string[]] $Changed) {
    $json = $Settings | ConvertTo-Json -Depth 100
    $after = ConvertTo-SettingTable ($json | ConvertFrom-Json)
    $lost = @($Before.Keys | Where-Object { $Changed -notcontains $_ -and $after[$_] -cne $Before[$_] })
    if ($lost) {
        throw "PowerShell $($PSVersionTable.PSVersion) cannot write the settings back without changing $($lost -join ', '). Run the installer in the other PowerShell (powershell.exe or pwsh.exe). hc360.settings.json was left as it was."
    }
    return $json
}

# Writes the settings over the file, which keeps its ACL, as UTF-8 with the byte order mark Windows PowerShell 5.1 needs.
function Save-SettingsFile($Settings, [hashtable] $Before, [string[]] $Changed, [string] $Path) {
    $json = ConvertTo-SettingsJson $Settings $Before $Changed
    $encoding = if ($PSVersionTable.PSVersion.Major -ge 6) { 'utf8BOM' } else { 'UTF8' }
    Set-Content -LiteralPath $Path -Value $json -Encoding $encoding
}

# ---------------------------------------------------------------- database sign-in (pg_hba.conf, pg_ident.conf)
# The service signs in to PostgreSQL with its Windows account (SSPI), so there is no database password to keep, change or
# lose (ASVS V13.2.1). The installer writes one block in pg_hba.conf and one in pg_ident.conf, each between a
# "# BEGIN HyFib HealthCare360" and a "# END HyFib HealthCare360" line, and writes them again on every run: a change made
# to them by hand is lost.

# The lines of a PostgreSQL configuration file without the installer's block.
function Remove-InstallerBlock([string[]] $Lines, [string] $Name) {
    $kept = @()
    $inside = $false
    foreach ($line in $Lines) {
        if (-not $inside -and $line.StartsWith('# BEGIN HyFib HealthCare360')) { $inside = $true }
        elseif ($inside) { if ($line.StartsWith('# END HyFib HealthCare360')) { $inside = $false } }
        else { $kept += $line }
    }
    if ($inside) {
        throw "$Name has a '# BEGIN HyFib HealthCare360' line without its '# END HyFib HealthCare360' line. Correct the file and run the installer again."
    }
    return , $kept
}

# pg_hba.conf with the block that lets $Role sign in only with the Windows account pg_ident.conf maps to it under $Map,
# and only from this machine. It goes before the first rule: PostgreSQL follows the first rule a connection matches.
function Set-HbaBlock([string[]] $Lines, [string] $Role, [string] $Map) {
    if ($Role -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "The database account '$Role' cannot be named in pg_hba.conf as it is: use an account named with letters, digits and underscores."
    }
    $block = @(
        '# BEGIN HyFib HealthCare360. Install-HC360Server.ps1 writes this block on every run: changes to it are lost.',
        "# $Role signs in only with the HC360 service's Windows account (SSPI, mapped in pg_ident.conf), from this machine.",
        "local   all   $Role                  reject",
        "host    all   $Role   127.0.0.1/32   sspi   map=$Map",
        "host    all   $Role   ::1/128        sspi   map=$Map",
        "host    all   $Role   all            reject",
        '# END HyFib HealthCare360'
    )
    $rest = Remove-InstallerBlock $Lines 'pg_hba.conf'
    $first = 0
    while ($first -lt $rest.Count -and $rest[$first] -match '^\s*(#|$)') { $first++ }
    $head = if ($first -gt 0) { @($rest[0..($first - 1)]) } else { @() }
    $tail = if ($first -lt $rest.Count) { @($rest[$first..($rest.Count - 1)]) } else { @() }
    return , ($head + $block + $tail)
}

# pg_ident.conf with the block that maps the Windows account $WindowsAccount, named as PostgreSQL names it
# (ConvertTo-SspiName), to the database account $Role under $Map.
function Set-IdentBlock([string[]] $Lines, [string] $Map, [string] $WindowsAccount, [string] $Role) {
    if ($WindowsAccount.Contains('"')) { throw "The Windows account '$WindowsAccount' cannot be named in pg_ident.conf." }
    $block = @(
        '# BEGIN HyFib HealthCare360. Install-HC360Server.ps1 writes this block on every run: changes to it are lost.',
        "$Map   `"$WindowsAccount`"   $Role",
        '# END HyFib HealthCare360'
    )
    $rest = Remove-InstallerBlock $Lines 'pg_ident.conf'
    return , (@($rest) + $block)
}

# A Windows account written DOMAIN\name, such as NT SERVICE\HC360Server, as PostgreSQL's SSPI sign-in names it: name@DOMAIN.
# PostgreSQL compares it without regard to case.
function ConvertTo-SspiName([string] $Account) {
    $parts = $Account.Split('\')
    if ($parts.Count -ne 2 -or -not $parts[0] -or -not $parts[1]) { throw "'$Account' is not a Windows account written DOMAIN\name." }
    return "$($parts[1])@$($parts[0])"
}

# The data folder in a PostgreSQL service's command line: pg_ctl runservice -N "name" -D "folder".
function Get-PgCtlDataDirectory([string] $CommandLine) {
    if ($CommandLine -match '(?:^|\s)-D\s*"([^"]+)"') { return $Matches[1] }
    if ($CommandLine -match '(?:^|\s)-D\s*([^\s"]+)') { return $Matches[1] }
    return $null
}

# A setting of PostgreSQL's configuration as the server reads it: the last line that sets it, postgresql.auto.conf
# (ALTER SYSTEM) after postgresql.conf. $null when neither sets it.
function Get-PostgresSetting([string[]] $Conf, [string[]] $AutoConf, [string] $Name) {
    $value = $null
    foreach ($line in @($Conf) + @($AutoConf)) {
        if ($line -match "^\s*$Name\s*=?\s*(?:'(?<quoted>(?:[^']|'')*)'|(?<plain>[^\s#']+))") {
            $value = if ($Matches.ContainsKey('quoted')) { $Matches['quoted'].Replace("''", "'") } else { $Matches['plain'] }
        }
    }
    return $value
}

# A text file's lines; none when there is no file.
function Read-TextLines([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return , @() }
    return , [string[]][System.IO.File]::ReadAllLines($Path)
}

# Writes a PostgreSQL configuration file, keeping its ACL, as UTF-8 without a byte order mark: PostgreSQL would read one
# as part of the first line.
function Write-TextLines([string] $Path, [string[]] $Lines) {
    [System.IO.File]::WriteAllLines($Path, $Lines, (New-Object System.Text.UTF8Encoding $false))
}

# The PostgreSQL service of this machine that listens on $Port, with the files its sign-in rules are in; $null when there
# is none.
function Find-PostgresService([int] $Port) {
    foreach ($service in @(Get-CimInstance -ClassName Win32_Service | Where-Object { $_.PathName -match 'pg_ctl(\.exe)?"?\s+runservice' })) {
        $data = Get-PgCtlDataDirectory $service.PathName
        if (-not $data -or -not (Test-Path -LiteralPath (Join-Path $data 'postgresql.conf'))) { continue }
        $conf = Read-TextLines (Join-Path $data 'postgresql.conf')
        $auto = Read-TextLines (Join-Path $data 'postgresql.auto.conf')
        $configured = Get-PostgresSetting $conf $auto 'port'
        if (-not $configured) { $configured = '5432' }
        if (($configured -as [int]) -ne $Port) { continue }
        $files = @{}
        foreach ($setting in @(@('hba_file', 'pg_hba.conf'), @('ident_file', 'pg_ident.conf'))) {
            $file = Get-PostgresSetting $conf $auto $setting[0]
            if (-not $file) { $file = $setting[1] }
            $files[$setting[0]] = if ([System.IO.Path]::IsPathRooted($file)) { $file } else { Join-Path $data $file }
        }
        return [pscustomobject]@{ Name = $service.Name; DataDirectory = $data; HbaFile = $files['hba_file']; IdentFile = $files['ident_file'] }
    }
    return $null
}

# Restarts PostgreSQL, which then reads pg_hba.conf and pg_ident.conf again, and waits until it takes connections.
function Restart-Postgres($Postgres, [int] $Port) {
    Restart-Service -Name $Postgres.Name -Force
    $isReady = if ($pgBin) { Join-Path $pgBin 'pg_isready.exe' } else { $null }
    if ($isReady -and (Test-Path -LiteralPath $isReady)) {
        & $isReady -h localhost -p $Port -t 60 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "PostgreSQL ($($Postgres.Name)) did not take connections within a minute of restarting." }
    }
    else {
        Start-Sleep -Seconds 10
    }
}

# Runs one statement as the PostgreSQL superuser.
function Invoke-SuperuserSql([string] $Server, [int] $Port, [string] $Sql) {
    if (-not $PostgresSuperuserPassword) { $script:PostgresSuperuserPassword = Read-Host -AsSecureString "Password for PostgreSQL user '$PostgresSuperuser'" }
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    try {
        & (Join-Path $pgBin 'psql.exe') -h $Server -p $Port -U $PostgresSuperuser -d postgres -v ON_ERROR_STOP=1 -q -c $Sql
        if ($LASTEXITCODE -ne 0) { throw "PostgreSQL refused: $Sql" }
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
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

# Checked before anything changes: settings that cannot be read, or written back as they are, stop the installer while
# the installed version runs. They are read again once the service has stopped.
$settingsPath = Join-Path $DataDir 'config\hc360.settings.json'
$settings = Read-SettingsFile $settingsPath
$settingsBefore = ConvertTo-SettingTable $settings
$null = ConvertTo-SettingsJson $settings $settingsBefore @()

# Without -HttpsPort (an upgrade, or a run for -LabPorts) the server keeps its port, and the firewall and checks follow it.
$installedPort = $settingsBefore['HealthCare360:HttpsPort'] -as [int]
if ($installedPort -and -not $PSBoundParameters.ContainsKey('HttpsPort')) { $HttpsPort = $installedPort }
$installedPortalPort = $settingsBefore['HealthCare360:PortalHttpsPort'] -as [int]
if ($installedPortalPort -and -not $PSBoundParameters.ContainsKey('PortalHttpsPort')) { $PortalHttpsPort = $installedPortalPort }
if ($PortalHttpsPort -eq $HttpsPort) { throw "The patient portal needs a port of its own: -PortalHttpsPort and -HttpsPort are both $HttpsPort." }

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
# Until it stopped, the service could still change the settings (a new database password from System health).
$settings = Read-SettingsFile $settingsPath
$settingsBefore = ConvertTo-SettingTable $settings

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
elseif (-not $settingsBefore['ConnectionStrings:Hospital']) {
    throw 'No database is configured yet. Re-run with -CreateDatabase (recommended) or create hc360.settings.json with ConnectionStrings.Hospital.'
}

# ---------------------------------------------------------------- configuration
# The installer's own settings. One the file has changes only when its parameter is given again, so an upgrade keeps the
# port, facility and backup folders as they are; pg_dump follows the newest PostgreSQL found. One it lacks is added.
Write-Step "Configuring $settingsPath"
$changed = @(
    if ($CreateDatabase) { Set-Setting $settings 'ConnectionStrings:Hospital' $connectionString -Replace }
    Set-Setting $settings 'HealthCare360:HttpsPort' $HttpsPort -Replace:$PSBoundParameters.ContainsKey('HttpsPort')
    Set-Setting $settings 'HealthCare360:PortalHttpsPort' $PortalHttpsPort -Replace:$PSBoundParameters.ContainsKey('PortalHttpsPort')
    Set-Setting $settings 'HealthCare360:DefaultFacilityCode' $FacilityCode -Replace:$PSBoundParameters.ContainsKey('FacilityCode')
    Set-Setting $settings 'HealthCare360:DefaultFacilityName' $FacilityName -Replace:$PSBoundParameters.ContainsKey('FacilityName')
    Set-Setting $settings 'HealthCare360:Backup:Enabled' $true
    Set-Setting $settings 'HealthCare360:Backup:Directory' (Join-Path $DataDir 'backups')
    Set-Setting $settings 'HealthCare360:Backup:SecondaryDirectory' $SecondaryBackupDir -Replace:$PSBoundParameters.ContainsKey('SecondaryBackupDir')
    if ($pgBin) { Set-Setting $settings 'HealthCare360:Backup:PgDumpPath' (Join-Path $pgBin 'pg_dump.exe') -Replace }
    else { Set-Setting $settings 'HealthCare360:Backup:PgDumpPath' 'pg_dump' }
)
if ($changed) {
    Save-SettingsFile $settings $settingsBefore $changed $settingsPath
    Write-Host "    Set $($changed -join ', ')."
}
else {
    Write-Host '    Nothing to change.'
}

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
    New-NetFirewallRule -DisplayName 'HC360 Server (patient portal, HTTPS)' -Group 'HyFib HealthCare360' -Direction Inbound -Protocol TCP -LocalPort $PortalHttpsPort `
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
$ready = Wait-ServerReady -Port $HttpsPort
if (-not $ready) {
    Write-Warning "The service was not ready, up and reaching the database, within 3 minutes. Check $DataDir\logs and the Windows Application event log."
}

# ---------------------------------------------------------------- database sign-in
# The service signs in to PostgreSQL with its Windows account (SSPI), and the settings file then holds no database
# password (ASVS V13.2.1). The switch is made only once the server has started as it was, so an upgrade's database changes
# are done first. Then the server must reach the database within 3 minutes, or the settings file, pg_hba.conf and
# pg_ident.conf go back as they were, and the service signs in with its password as before.
Write-Step 'Checking how the service signs in to the database'
$signIn = $null
$shown = @(& $exe database sign-in show --data-dir $DataDir)
if ($LASTEXITCODE -eq 0) { $signIn = ($shown -join "`n") | ConvertFrom-StringData }
else { Write-Warning 'The database sign-in could not be read from the settings file, so it is left as it is.' }
$postgres = $null
if ($signIn -and $signIn['on-this-machine'] -ne 'yes') {
    Write-Host "    PostgreSQL is reached at $($signIn['host']), not on this machine: the service signs in with its password."
}
elseif ($signIn) {
    $postgres = Find-PostgresService ([int] $signIn['port'])
    if (-not $postgres) { Write-Warning "No PostgreSQL service on this machine listens on port $($signIn['port']), so the database sign-in is left as it is." }
}

if ($postgres) {
    $role = $signIn['username']
    $databasePort = [int] $signIn['port']
    $withPassword = $signIn['sign-in'] -eq 'password'
    $hbaBefore = Read-TextLines $postgres.HbaFile
    $identBefore = Read-TextLines $postgres.IdentFile
    if ($DatabaseSignIn -eq 'Windows') {
        $hbaAfter = Set-HbaBlock $hbaBefore $role 'hc360'
        $identAfter = Set-IdentBlock $identBefore 'hc360' (ConvertTo-SspiName $ServiceAccount) $role
    }
    else {
        $hbaAfter = Remove-InstallerBlock $hbaBefore 'pg_hba.conf'
        $identAfter = Remove-InstallerBlock $identBefore 'pg_ident.conf'
    }
    $filesChange = ($hbaAfter -join "`n") -cne ($hbaBefore -join "`n") -or ($identAfter -join "`n") -cne ($identBefore -join "`n")

    if ($DatabaseSignIn -eq 'Windows' -and $withPassword -and -not $ready) {
        Write-Warning 'The service still signs in to the database with its password: it is switched to its Windows account only once the server is ready. Run the installer again when it is.'
    }
    elseif ($DatabaseSignIn -eq 'Windows' -and $withPassword) {
        Write-Step 'Switching the service to sign in to the database with its Windows account'
        $saved = @{}
        $saved[$settingsPath] = [System.IO.File]::ReadAllBytes($settingsPath)
        $saved[$postgres.HbaFile] = [System.IO.File]::ReadAllBytes($postgres.HbaFile)
        $identExisted = Test-Path -LiteralPath $postgres.IdentFile
        if ($identExisted) { $saved[$postgres.IdentFile] = [System.IO.File]::ReadAllBytes($postgres.IdentFile) }
        $switched = $false
        try {
            Stop-HC360Service
            Write-TextLines $postgres.HbaFile $hbaAfter
            Write-TextLines $postgres.IdentFile $identAfter
            & $exe database sign-in windows --data-dir $DataDir | ForEach-Object { Write-Host "    $_" }
            if ($LASTEXITCODE -ne 0) { throw "The settings file could not be changed (exit code $LASTEXITCODE)." }
            Restart-Postgres $postgres $databasePort
            Start-Service -Name $ServiceName
            $switched = Wait-ServerReady -Port $HttpsPort
            if (-not $switched) { throw 'The server did not reach the database with its Windows account within 3 minutes.' }
        }
        catch {
            Write-Warning "$($_.Exception.Message) Putting the settings file, pg_hba.conf and pg_ident.conf back as they were: the service signs in with its password, as before. $DataDir\logs says why the Windows sign-in failed."
            # The files go back even when the service does not stop: it reads its settings only when it starts.
            try { Stop-HC360Service } catch { Write-Warning "The service did not stop: $($_.Exception.Message)" }
            foreach ($file in $saved.Keys) { [System.IO.File]::WriteAllBytes($file, $saved[$file]) }
            if (-not $identExisted) { Remove-Item -LiteralPath $postgres.IdentFile -Force -ErrorAction SilentlyContinue }
            Restart-Postgres $postgres $databasePort
            Start-Service -Name $ServiceName
            $ready = Wait-ServerReady -Port $HttpsPort
        }
        if ($switched) {
            Write-Host "    The service signs in to the database with its Windows account ($ServiceAccount): the settings file holds no database password."
            if ($PostgresSuperuserPassword -and $pgBin) {
                try {
                    Invoke-SuperuserSql $signIn['host'] $databasePort "ALTER ROLE $role PASSWORD NULL"
                    Write-Host "    The database account $role has no password any more."
                }
                catch {
                    Write-Warning "The old password of $role could not be removed ($($_.Exception.Message)). PostgreSQL no longer accepts it anyway: it takes that account only from this service's Windows account."
                }
            }
            else {
                Write-Host "    PostgreSQL no longer accepts the old password of ${role}: it takes that account only from this service's Windows account."
            }
        }
    }
    elseif ($DatabaseSignIn -eq 'Password' -and -not $withPassword) {
        if (-not $pgBin) { throw 'PostgreSQL client tools (psql.exe) were not found, so the database account cannot be given a password.' }
        Write-Step 'Switching the service to sign in to the database with a password'
        # The rules that take only the Windows account go last, once the settings file holds the password: until then the
        # service can still sign in as it did.
        $appPassword = New-RandomSecret
        Stop-HC360Service
        try {
            Invoke-SuperuserSql $signIn['host'] $databasePort "ALTER ROLE $role PASSWORD '$appPassword'"
            $env:HC360_DATABASE_PASSWORD = $appPassword
            & $exe database sign-in password --data-dir $DataDir | ForEach-Object { Write-Host "    $_" }
            if ($LASTEXITCODE -ne 0) { throw "The settings file could not be changed (exit code $LASTEXITCODE)." }
            Write-TextLines $postgres.HbaFile $hbaAfter
            Write-TextLines $postgres.IdentFile $identAfter
            Restart-Postgres $postgres $databasePort
        }
        finally {
            Remove-Item Env:\HC360_DATABASE_PASSWORD -ErrorAction SilentlyContinue
            Start-Service -Name $ServiceName
            $ready = Wait-ServerReady -Port $HttpsPort
        }
        if (-not $ready) { Write-Warning "The server did not reach the database with its new password within 3 minutes. Check $DataDir\logs." }
    }
    elseif ($filesChange) {
        # pg_hba.conf or pg_ident.conf lost the block (PostgreSQL reinstalled, say), or still has one it no longer needs.
        Write-Step 'Writing the database sign-in rules again'
        Stop-HC360Service
        Write-TextLines $postgres.HbaFile $hbaAfter
        Write-TextLines $postgres.IdentFile $identAfter
        Restart-Postgres $postgres $databasePort
        Start-Service -Name $ServiceName
        $ready = Wait-ServerReady -Port $HttpsPort
        if (-not $ready) { Write-Warning "The service was not ready, up and reaching the database, within 3 minutes. Check $DataDir\logs." }
    }
    else {
        $how = if ($withPassword) { 'with its password' } else { "with its Windows account ($ServiceAccount)" }
        Write-Host "    The service signs in to the database $how."
    }
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
Write-Host "The patient portal is at https://$($env:COMPUTERNAME):$PortalHttpsPort/portal/" -ForegroundColor Green
