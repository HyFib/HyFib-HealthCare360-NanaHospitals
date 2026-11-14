#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Fails over to this warm standby Server Node (SRS DED-20): promotes its PostgreSQL once the old primary is gone, and
    records the promotion, so that the old primary refuses to run HC360 when it comes back.

.DESCRIPTION
    Run it on the standby, in place of promoting PostgreSQL by hand (docs/deployment/DEPLOYMENT.md, "Failing over to the
    standby"). In order:
      1. Checks that this machine's PostgreSQL is a standby, and says how far behind the primary it is: when it last
         applied a change made there (what a failover now loses since), and how much WAL it has received but not applied
         yet, which the promotion applies.
      2. Refuses while the old primary's PostgreSQL still answers at the address the standby streams from: two primaries
         would each take patient data the other never sees. Stop it first, or switch the machine off, or unplug its
         network. A primary that does not answer is taken to be gone.
      3. Promotes PostgreSQL (pg_promote) and waits until it takes writes, on a new timeline.
      4. Records the promotion, in config\hc360-primary.json in HC360's data folder here and beside the backups' second
         copy (the -SecondaryBackupDir HC360 was installed with, on the NAS or disk both servers are given): the cluster's
         system identifier, the new timeline and this machine's name. HC360 on the old primary reads it there, and refuses
         to start, or stops within a minute if it runs, until that machine is set up again as this one's standby
         (New-HC360Standby.ps1 -ReplaceExistingData). Without a second copy it can reach, the old primary cannot learn it was
         replaced: keep it off the network until then.
    Then put the keys and documents back and start HC360 here (the failover's next steps). HC360 takes a full backup within
    five minutes of starting, as this server has none of its own yet, and a base backup once WAL archiving is set up here.

    PostgreSQL promoted by hand (pg_promote, pg_ctl promote): run it with -RecordOnly, which records the promotion and
    changes nothing else.

.EXAMPLE
    .\Invoke-HC360Failover.ps1
#>
[CmdletBinding()]
param(
    # The primary's postgres password: the standby is a copy of the primary, with its accounts. Asked for when not given.
    [securestring] $PostgresSuperuserPassword,
    [string] $PostgresSuperuser = 'postgres',
    [int] $Port = 5432,
    # HC360's data folder on this machine, where its settings are and the promotion is recorded.
    [string] $DataDirectory = (Join-Path $env:ProgramData 'HyFib\HealthCare360'),
    # Where to record the promotion for the old primary to find: by default the backups' second copy HC360 is set up with.
    [string] $SecondaryBackupDir,
    # How long PostgreSQL may take to promote, the WAL it received but has not applied included.
    [ValidateRange(10, 3600)] [int] $PromoteTimeoutSeconds = 300,
    # Only record a promotion made by hand.
    [switch] $RecordOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# What this script pipes to psql is UTF-8, as psql reads it.
$OutputEncoding = New-Object System.Text.UTF8Encoding $false

$RecordFile = 'hc360-primary.json'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# ---------------------------------------------------------------- PostgreSQL's files and HC360's settings (as in Install-HC360Server.ps1)
# These are the installer's own functions, kept the same (tests/deploy/Test-StandbyScripts.ps1 checks).

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

# The PostgreSQL service of this machine that runs and listens on $Port, with the files its sign-in rules are in; $null
# when there is none.
function Find-PostgresService([int] $Port) {
    return Select-PostgresService @(Get-CimInstance -ClassName Win32_Service) $Port
}

# Of $Services (Name, PathName and State, as Win32_Service has them), the PostgreSQL service that runs and is set to
# listen on $Port. A stopped one is passed over: after an upgrade to a new PostgreSQL version, the old version's service
# often stays, stopped, set to the same port.
function Select-PostgresService([object[]] $Services, [int] $Port) {
    foreach ($service in $Services) {
        if ($service.State -ne 'Running' -or $service.PathName -notmatch 'pg_ctl(\.exe)?"?\s+runservice') { continue }
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

# ---------------------------------------------------------------- the failover's own (tests/deploy/Test-StandbyScripts.ps1)

# The folder of pg_ctl.exe in a PostgreSQL service's command line, where psql.exe and pg_isready.exe are too.
function Get-PgCtlBinDirectory([string] $CommandLine) {
    if ($CommandLine -match '^\s*"([^"]*pg_ctl(?:\.exe)?)"' -or $CommandLine -match '^\s*(\S*pg_ctl(?:\.exe)?)\s') {
        return Split-Path -Parent $Matches[1]
    }
    return $null
}

# The keywords and values of a libpq connection string (the standby's primary_conninfo): a value quoted, its \' and \\
# read back, as New-HC360Standby.ps1 writes them.
function ConvertFrom-ConnInfo([string] $ConnInfo) {
    $values = @{}
    foreach ($match in [regex]::Matches($ConnInfo, "(?<key>\w+)\s*=\s*(?:'(?<quoted>(?:[^'\\]|\\.)*)'|(?<plain>[^\s']+))")) {
        $values[$match.Groups['key'].Value] = if ($match.Groups['quoted'].Success) { $match.Groups['quoted'].Value -replace '\\(.)', '$1' } else { $match.Groups['plain'].Value }
    }
    return $values
}

# The timeline a WAL file's name starts with: 00000002... is timeline 2.
function Get-WalTimeline([string] $WalFile) {
    if ($WalFile -notmatch '^[0-9A-F]{8}') { throw "'$WalFile' is not the name of a WAL file." }
    return [Convert]::ToInt32($WalFile.Substring(0, 8), 16)
}

# The record of a promotion, as HC360 reads it (PrimaryRecord): the cluster, its new timeline, the machine promoted, when,
# and the address it streamed from until then. The system identifier is text: a 64-bit number, which a JSON number does
# not keep whole.
function New-PrimaryRecord([string] $SystemIdentifier, [int] $Timeline, [string] $Primary, [DateTimeOffset] $PromotedAt, [string] $PreviousPrimary) {
    $record = [ordered] @{
        format = 1
        systemIdentifier = $SystemIdentifier
        timeline = $Timeline
        primary = $Primary
        promotedAt = $PromotedAt.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
        previousPrimary = $PreviousPrimary
    }
    return ($record | ConvertTo-Json)
}

# Writes the record to $Path, under a temporary name first so HC360 never reads half of it; straight to its name where a
# share does not let the server rename (an append-only share).
function Write-PrimaryRecord([string] $Path, [string] $Json) {
    $folder = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    $encoding = New-Object System.Text.UTF8Encoding $false
    $temp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::WriteAllText($temp, $Json, $encoding)
        Move-Item -LiteralPath $temp -Destination $Path -Force
    }
    catch {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllText($Path, $Json, $encoding)
    }
}

# A query as the superuser on this machine's PostgreSQL, its rows as text.
function Invoke-LocalSql([string] $Sql) {
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    try {
        $output = $Sql | & $psql -X -h localhost -p $Port -U $PostgresSuperuser -d postgres -v ON_ERROR_STOP=1 -q -A -t -f -
        if ($LASTEXITCODE -ne 0) { throw "This machine's PostgreSQL refused: $Sql" }
        return $output
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- checks (nothing changes before these pass)
Write-Step 'Checking this machine''s PostgreSQL'
$postgres = Find-PostgresService $Port
if (-not $postgres) { throw "No running PostgreSQL service on this machine listens on port $Port. Start it, or give its port with -Port." }
$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($postgres.Name)'"
$bin = Get-PgCtlBinDirectory $service.PathName
$psql = Join-Path $bin 'psql.exe'
$pgIsReady = Join-Path $bin 'pg_isready.exe'
foreach ($tool in $psql, $pgIsReady) { if (-not (Test-Path -LiteralPath $tool)) { throw "$tool was not found beside PostgreSQL's pg_ctl.exe." } }
$isStandby = Test-Path -LiteralPath (Join-Path $postgres.DataDirectory 'standby.signal')
if (-not $isStandby -and -not $RecordOnly) {
    throw "This machine's PostgreSQL ($($postgres.Name)) is not a standby (no standby.signal in $($postgres.DataDirectory)): there is nothing to promote. Run this on the warm standby; if its PostgreSQL was promoted by hand, run it again with -RecordOnly."
}
if ($isStandby -and $RecordOnly) { throw "This machine's PostgreSQL ($($postgres.Name)) is still a standby: run the script without -RecordOnly to promote it." }
if (-not $PostgresSuperuserPassword) { $PostgresSuperuserPassword = Read-Host -AsSecureString "The primary's password for PostgreSQL user '$PostgresSuperuser'" }

$previousPrimary = $null
if ($isStandby) {
    if ((Invoke-LocalSql 'SELECT pg_is_in_recovery()') -ne 't') { throw "PostgreSQL ($($postgres.Name)) is not in recovery: it was promoted already. Run the script again with -RecordOnly." }
    $endpoint = ConvertFrom-ConnInfo (Invoke-LocalSql "SELECT current_setting('primary_conninfo')")
    $previousPrimary = $endpoint['host']
    $primaryPort = if ($endpoint['port']) { [int] $endpoint['port'] } else { 5432 }

    # How far behind the standby is: what a failover now loses, and what the promotion still applies.
    $lastApplied = "$(Invoke-LocalSql 'SELECT pg_last_xact_replay_timestamp()')".Trim()
    $delay = "$(Invoke-LocalSql 'SELECT round(extract(epoch FROM now() - pg_last_xact_replay_timestamp()))::bigint')".Trim()
    $unapplied = "$(Invoke-LocalSql 'SELECT coalesce(pg_wal_lsn_diff(pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn())::bigint, 0)')".Trim()
    $receiver = Invoke-LocalSql "SELECT coalesce((SELECT status FROM pg_stat_wal_receiver), 'stopped')"
    if ($lastApplied) { Write-Host "    The standby last applied a change made on the primary at $lastApplied ($delay seconds ago): changes made after that on the primary are lost." }
    else { Write-Host '    The standby has applied no change made on the primary since it last started.' }
    if ([long] $unapplied -gt 0) { Write-Host "    It has received $unapplied bytes of WAL it has not applied yet: the promotion applies them first." }
    Write-Host "    Its link to the primary: $receiver."

    # Two primaries would each take patient data the other never sees.
    if ($previousPrimary) {
        & $pgIsReady -h $previousPrimary -p $primaryPort -t 5 | Out-Null
        if ($LASTEXITCODE -in 0, 1) {
            throw "The old primary's PostgreSQL at ${previousPrimary}:$primaryPort still answers. Promoting this standby now would leave two primaries, each taking patient data the other never sees. Stop HC360 and PostgreSQL there first (Stop-Service HC360Server, then its PostgreSQL service), or switch that machine off or unplug its network, then run this again."
        }
        Write-Host "    The old primary at ${previousPrimary}:$primaryPort does not answer: it is taken to be gone."
    }

    # ---------------------------------------------------------------- promote
    Write-Step "Promoting PostgreSQL ($($postgres.Name))"
    if ((Invoke-LocalSql "SELECT pg_promote(true, $PromoteTimeoutSeconds)") -ne 't') {
        throw "PostgreSQL did not finish promoting within $PromoteTimeoutSeconds seconds; it goes on by itself. Its log, in the log folder of $($postgres.DataDirectory), says why. Once SELECT pg_is_in_recovery() answers f, run this again with -RecordOnly."
    }
}
if ((Invoke-LocalSql 'SELECT pg_is_in_recovery()') -ne 'f') { throw 'PostgreSQL still does not take writes: its log says why.' }

# ---------------------------------------------------------------- record
$systemIdentifier = "$(Invoke-LocalSql 'SELECT system_identifier FROM pg_control_system()')".Trim()
$timeline = Get-WalTimeline ("$(Invoke-LocalSql 'SELECT pg_walfile_name(pg_current_wal_lsn())')".Trim())
$record = New-PrimaryRecord $systemIdentifier $timeline $env:COMPUTERNAME ([DateTimeOffset]::Now) $previousPrimary
Write-Step "Recording that $($env:COMPUTERNAME) is the primary from timeline $timeline on"
$localRecord = Join-Path $DataDirectory "config\$RecordFile"
Write-PrimaryRecord $localRecord $record
Write-Host "    $localRecord"
if (-not $SecondaryBackupDir) {
    # PostgreSQL is promoted by now: a settings file that cannot be read must not stop the record here.
    try {
        $setting = Find-Setting (Read-SettingsFile (Join-Path $DataDirectory 'config\hc360.settings.json')) 'HealthCare360:Backup:SecondaryDirectory'
        if ($setting -and $setting.Value) { $SecondaryBackupDir = [string] $setting.Value }
    }
    catch { Write-Warning "HC360's settings could not be read: $($_.Exception.Message) Give the backups' second copy with -SecondaryBackupDir, and run this again with -RecordOnly." }
}
$shared = $false
if ($SecondaryBackupDir) {
    try {
        Write-PrimaryRecord (Join-Path $SecondaryBackupDir $RecordFile) $record
        Write-Host "    $(Join-Path $SecondaryBackupDir $RecordFile), where the old primary finds it"
        $shared = $true
    }
    catch { Write-Warning "The promotion could not be recorded in $($SecondaryBackupDir): $($_.Exception.Message)" }
}
if (-not $shared) {
    Write-Warning 'The old primary cannot learn that it was replaced: there is no second copy of the backups both servers reach. Keep it switched off or off the network until it is set up again as this server''s standby (New-HC360Standby.ps1 -ReplaceExistingData).'
}

Write-Host ''
Write-Host "This machine's PostgreSQL is the primary now, on timeline $timeline." -ForegroundColor Green
Write-Host 'Next, as docs/deployment/DEPLOYMENT.md, "Failing over to the standby", says: put the keys and documents back from the latest backup'
Write-Host '(HyFib.HealthCare360.Server.exe backup restore --in <backup>), then start HC360 with .\Install-HC360Server.ps1, without -Standby.'
Write-Host 'HC360 takes a full backup within five minutes of starting. To archive the WAL here: .\Enable-HC360Replication.ps1 -WalArchive.'
