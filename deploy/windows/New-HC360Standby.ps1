#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Makes this machine's PostgreSQL a warm standby of the primary Server Node (SRS DED-20): a copy of its database that
    follows every change as it is made, read-only, ready to take over by a manual failover.

.DESCRIPTION
    Run it on the standby after Install-PostgreSQL.ps1 (the same PostgreSQL version as the primary), once
    Enable-HC360Replication.ps1 has let this machine in on the primary. Everything that can be checked is checked first:
    the primary's certificate, the replication password, the PostgreSQL version and the replication slot, over the
    connection the standby will use. Then:
      1. Sets this machine's PostgreSQL service to start manually (so the database watchdog leaves it alone meanwhile), and
         stops it.
      2. Moves its data folder aside, to data.before-standby-<time>: nothing is deleted.
      3. Copies the primary's database with pg_basebackup over TLS (sslmode=verify-ca, trusting the primary's own
         certificate and no other), streaming the WAL as it copies, through the primary's replication slot.
      4. Sets the copy up as a standby: it follows the primary, listens on localhost only, archives nothing, keeps this
         machine's port and memory settings, and has the data folder's permissions PostgreSQL's service account needs.
         The replication password is kept where PostgreSQL needs it to reconnect, in a file beside the data folder that
         only PostgreSQL's service account and Administrators can read (hc360-standby\replication.pgpass).
      5. Starts PostgreSQL, sets it to start automatically again, and checks that it is a standby (pg_is_in_recovery) and
         streams from the primary (pg_stat_wal_receiver).
    If a step up to the start fails, the data folder that was moved aside is put back and PostgreSQL started on it again.

    Then install HC360 here with its service disabled: .\Install-HC360Server.ps1 -Standby. HC360 must never run against a
    standby. To fail over, see docs/deployment/DEPLOYMENT.md, "Failing over to the standby".

    Run it again to set the standby up again, for example after System health on the primary said it cannot catch up.
    On a machine whose PostgreSQL holds databases and is not a standby, such as the old primary after a failover, it
    stops unless -ReplaceExistingData is given.

.EXAMPLE
    .\New-HC360Standby.ps1 -PrimaryAddress 10.0.5.11 -PrimaryCertificate E:\hc360-server.crt
#>
[CmdletBinding()]
param(
    # The primary's address, as Enable-HC360Replication.ps1 printed it.
    [Parameter(Mandatory)] [string] $PrimaryAddress,
    # A copy of the primary's hc360-server.crt (no secret in it).
    [Parameter(Mandatory)] [string] $PrimaryCertificate,
    # The replication account's password, as given to Enable-HC360Replication.ps1; asked for when not given.
    [securestring] $ReplicationPassword,
    # The primary's postgres password: once the standby is a copy of the primary, it has the primary's accounts. Used
    # for the checks at the end; asked for when not given.
    [securestring] $PostgresSuperuserPassword,
    [string] $PostgresSuperuser = 'postgres',
    [int] $PrimaryPort = 5432,
    # The port of this machine's PostgreSQL.
    [int] $Port = 5432,
    [ValidatePattern('^hc360_[a-z0-9_]{1,57}$')] [string] $SlotName = 'hc360_standby',
    # The primary's WAL archive (-WalArchiveDirectory there), if this machine can read it: the standby then catches up from
    # it too, when the primary no longer has the WAL it needs.
    [string] $WalArchiveDirectory,
    # Replace a PostgreSQL that holds databases and is not a standby, such as the old primary after a failover.
    [switch] $ReplaceExistingData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ReplicationRole = 'hc360_replicator'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# ---------------------------------------------------------------- PostgreSQL's files (as in Install-HC360Server.ps1)
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

# Writes a PostgreSQL configuration file, keeping its ACL, as UTF-8 without a byte order mark: PostgreSQL would read one
# as part of the first line.
function Write-TextLines([string] $Path, [string[]] $Lines) {
    [System.IO.File]::WriteAllLines($Path, $Lines, (New-Object System.Text.UTF8Encoding $false))
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

# ---------------------------------------------------------------- standby settings (tests/deploy/Test-StandbyScripts.ps1)

# The folder of pg_ctl.exe in a PostgreSQL service's command line, where pg_basebackup.exe and psql.exe are too.
function Get-PgCtlBinDirectory([string] $CommandLine) {
    if ($CommandLine -match '^\s*"([^"]*pg_ctl(?:\.exe)?)"' -or $CommandLine -match '^\s*(\S*pg_ctl(?:\.exe)?)\s') {
        return Split-Path -Parent $Matches[1]
    }
    return $null
}

# A value for a libpq connection string, quoted: a path may hold spaces, and a quote or backslash in it is escaped.
function ConvertTo-ConnInfoValue([string] $Value) { return "'" + $Value.Replace('\', '\\').Replace("'", "\'") + "'" }

# The connection string to the primary: TLS, trusting exactly the primary's own certificate ($RootCertificate), as the
# replication account $Role, with its password from $PassFile. The empty password makes libpq read $PassFile even where
# PGPASSWORD is set in the environment, as it is machine-wide on some servers. The password file is for the database
# "replication", which libpq looks it up for when dbname says so (pg_basebackup and the standby's WAL receiver say it anyway).
function New-PrimaryConnInfo([string] $Address, [int] $Port, [string] $Role, [string] $RootCertificate, [string] $PassFile, [string] $ApplicationName) {
    $options = [ordered] @{
        host = $Address
        port = "$Port"
        user = $Role
        dbname = 'replication'
        password = ''
        passfile = $PassFile.Replace('\', '/')
        sslmode = 'verify-ca'
        sslrootcert = $RootCertificate.Replace('\', '/')
        application_name = $ApplicationName
        connect_timeout = '10'
    }
    return (@($options.Keys | ForEach-Object { "$_=$(ConvertTo-ConnInfoValue $options[$_])" }) -join ' ')
}

# A pgpass line for the replication account's physical replication connections, to any host: ':' and '\' escaped.
function New-PgPassLine([string] $Role, [string] $Password) {
    return "*:*:replication:${Role}:" + $Password.Replace('\', '\\').Replace(':', '\:')
}

# A value for postgresql.conf, quoted, with its quotes doubled and its backslashes escaped, as ALTER SYSTEM writes it.
function ConvertTo-ConfValue([string] $Value) { return "'" + $Value.Replace('\', '\\').Replace("'", "''") + "'" }

# postgresql.auto.conf with each of $Settings set, in place of the lines that set it before; a $null value takes the
# setting out, so postgresql.conf or the default applies.
function Set-ConfSettings([string[]] $Lines, [System.Collections.IDictionary] $Settings) {
    $kept = @($Lines | Where-Object {
        $line = $_
        -not @($Settings.Keys | Where-Object { $line -match "^\s*$([regex]::Escape($_))\s*=" }).Count
    })
    foreach ($name in $Settings.Keys) {
        if ($null -ne $Settings[$name]) { $kept += "$name = $(ConvertTo-ConfValue ([string] $Settings[$name]))" }
    }
    return , [string[]] $kept
}

# The settings a copy of the primary needs to be this machine's standby: it follows the primary through the slot, takes
# connections on this machine only, archives nothing (the primary does), and keeps this machine's port and the memory
# settings Install-PostgreSQL.ps1 sized for this machine ($OldConf, $OldAutoConf: its configuration before).
function Get-StandbySettings([string[]] $OldConf, [string[]] $OldAutoConf, [int] $Port, [string] $PrimaryConnInfo, [string] $SlotName, [string] $RestoreCommand) {
    $settings = [ordered] @{
        primary_conninfo = $PrimaryConnInfo
        primary_slot_name = $SlotName
        hot_standby = 'on'
        listen_addresses = 'localhost'
        port = "$Port"
        archive_mode = 'off'
        archive_command = $null
        archive_timeout = $null
        restore_command = if ($RestoreCommand) { $RestoreCommand } else { $null }
    }
    foreach ($name in 'shared_buffers', 'effective_cache_size', 'maintenance_work_mem') {
        $value = Get-PostgresSetting $OldConf $OldAutoConf $name
        if ($value) { $settings[$name] = $value }
    }
    return $settings
}

# The restore_command that reads a WAL file from the primary's archive, as Enable-HC360Replication.ps1 writes it there.
function Get-RestoreCommand([string] $Directory) {
    if (-not [System.IO.Path]::IsPathRooted($Directory) -or $Directory.IndexOfAny([char[]] '"%^&|<>') -ge 0) {
        throw "Give the WAL archive folder as a full path (D:\HC360-WAL or \\NAS\HC360-WAL), without quotes or any of % ^ & | < >."
    }
    return "copy /b `"$($Directory.TrimEnd('\'))\%f`" `"%p`" >nul"
}

# Why the PostgreSQL data folder $Data may not be replaced, or $null when it may: it is a standby already, or as
# Install-PostgreSQL.ps1 left it (template0, template1 and postgres, nothing more), unless $Replace.
function Test-ReplaceableCluster([string] $Data, [bool] $Replace) {
    if ($Replace -or (Test-Path -LiteralPath (Join-Path $Data 'standby.signal'))) { return $null }
    $databases = @(Get-ChildItem -LiteralPath (Join-Path $Data 'base') -Directory -ErrorAction SilentlyContinue).Count
    if ($databases -le 3) { return $null }
    return "This machine's PostgreSQL holds $($databases - 3) database(s) of its own and is not a standby ($Data). New-HC360Standby.ps1 replaces it with a copy of the primary: run it on a new standby, after Install-PostgreSQL.ps1. If this is the old primary after a failover, run it with -ReplaceExistingData (its data folder is moved aside, not deleted)."
}

# The SID of a Windows service's account as Win32_Service names it (StartName): LocalSystem, NT AUTHORITY\NetworkService,
# .\postgres, NT SERVICE\postgresql-x64-17.
function Resolve-AccountSid([string] $Account) {
    if (-not $Account -or $Account -eq 'LocalSystem') { return 'S-1-5-18' }
    if ($Account.StartsWith('.\')) { $Account = "$env:COMPUTERNAME\$($Account.Substring(2))" }
    return (New-Object System.Security.Principal.NTAccount -ArgumentList $Account).Translate([System.Security.Principal.SecurityIdentifier]).Value
}

# Gives $Path, a folder, to SYSTEM, Administrators and PostgreSQL's service account ($AccountSid) alone, with full control,
# for every file in it: pg_basebackup made it as Administrator, under a parent PostgreSQL's account may not write in.
# Windows applies the change to everything already in the folder.
function Set-DataFolderAcl([string] $Path, [string] $AccountSid, [string] $Rights = 'FullControl') {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544', $AccountSid) | Select-Object -Unique) {
        $right = if ($sid -eq $AccountSid -and $sid -notin @('S-1-5-18', 'S-1-5-32-544')) { $Rights } else { 'FullControl' }
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList (New-Object System.Security.Principal.SecurityIdentifier $sid), $right, $inherit, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# The SHA-256 fingerprint of the certificate in a PEM or DER file, grouped by four for reading aloud.
function Get-CertificateFingerprint([string] $Path) {
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $Path
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $hex = -join ($sha.ComputeHash($certificate.RawData) | ForEach-Object { $_.ToString('X2') }) } finally { $sha.Dispose() }
        return (($hex -split '(.{4})' | Where-Object { $_ }) -join ' ')
    }
    finally { $certificate.Dispose() }
}

# ---------------------------------------------------------------- PostgreSQL

# A command over the replication connection to the primary (READ_REPLICATION_SLOT, or a psql \echo), its rows as text.
function Invoke-PrimaryQuery([string] $Query, [string] $ConnInfo) {
    $output = $Query | & $psql -X -d "$ConnInfo replication=true" -v ON_ERROR_STOP=1 -q -A -t -f -
    if ($LASTEXITCODE -ne 0) { throw "The primary at ${PrimaryAddress}:$PrimaryPort refused the connection or '$Query'. The message above says why: the password, the certificate (-PrimaryCertificate), or the primary not letting this machine in (Enable-HC360Replication.ps1 -StandbyAddress <this machine's address>)." }
    return $output
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

function Wait-Service([string] $Name, [string] $Status, [int] $Seconds) {
    (Get-Service -Name $Name).WaitForStatus($Status, [TimeSpan]::FromSeconds($Seconds))
}

# ---------------------------------------------------------------- checks (nothing changes before these pass)
Write-Step 'Checking this machine''s PostgreSQL'
$postgres = Find-PostgresService $Port
if (-not $postgres) { throw "No running PostgreSQL service on this machine listens on port $Port. Install it with Install-PostgreSQL.ps1 (the primary's version), or start it." }
$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($postgres.Name)'"
$bin = Get-PgCtlBinDirectory $service.PathName
$psql = Join-Path $bin 'psql.exe'
$basebackup = Join-Path $bin 'pg_basebackup.exe'
foreach ($tool in $psql, $basebackup) { if (-not (Test-Path -LiteralPath $tool)) { throw "$tool was not found beside PostgreSQL's pg_ctl.exe." } }
$data = $postgres.DataDirectory.TrimEnd('\')
$parent = Split-Path -Parent $data
if (-not $parent) { throw "PostgreSQL's data folder is the root of a drive ($data), which cannot be moved aside. Move it to a folder first." }
$problem = Test-ReplaceableCluster $data $ReplaceExistingData.IsPresent
if ($problem) { throw $problem }
$localVersion = (Get-Content -LiteralPath (Join-Path $data 'PG_VERSION') -TotalCount 1).Trim()
$accountSid = Resolve-AccountSid $service.StartName
Write-Host "    PostgreSQL $localVersion, service $($postgres.Name) ($($service.StartName)), data folder $data"

if (-not (Test-Path -LiteralPath $PrimaryCertificate)) { throw "There is no file at $($PrimaryCertificate): copy hc360-server.crt from the primary's PostgreSQL data folder." }
$fingerprint = Get-CertificateFingerprint $PrimaryCertificate
Write-Host "    The primary's certificate: $fingerprint"
Write-Host '    It must be the fingerprint Enable-HC360Replication.ps1 printed on the primary.'

if (-not $ReplicationPassword) { $ReplicationPassword = Read-Host -AsSecureString "Password of the replication account $ReplicationRole" }
if (-not $PostgresSuperuserPassword) { $PostgresSuperuserPassword = Read-Host -AsSecureString "The primary's password for PostgreSQL user '$PostgresSuperuser'" }
$restoreCommand = if ($WalArchiveDirectory) { Get-RestoreCommand $WalArchiveDirectory } else { $null }

# Nothing from the environment may stand in for the password file, nor PGPASSWORD for the replication account's password.
foreach ($name in 'PGPASSWORD', 'PGPASSFILE', 'PGSERVICE', 'PGSERVICEFILE') { Remove-Item "Env:\$name" -ErrorAction SilentlyContinue }

# The password is in a file only the person running this can read, for as long as this runs.
$tempPassFile = Join-Path $env:TEMP ("hc360-replication-{0}.pgpass" -f [guid]::NewGuid().ToString('N'))
$moved = $null
$startType = $service.StartMode
try {
    [System.IO.File]::WriteAllText($tempPassFile, '')
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList ([System.Security.Principal.WindowsIdentity]::GetCurrent().User), 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $tempPassFile -AclObject $acl
    [System.IO.File]::WriteAllText($tempPassFile, (New-PgPassLine $ReplicationRole ([System.Net.NetworkCredential]::new('', $ReplicationPassword).Password)) + "`n", (New-Object System.Text.UTF8Encoding $false))

    Write-Step "Checking the connection to the primary at ${PrimaryAddress}:$PrimaryPort"
    $checkConnInfo = New-PrimaryConnInfo $PrimaryAddress $PrimaryPort $ReplicationRole (Resolve-Path -LiteralPath $PrimaryCertificate).Path $tempPassFile $SlotName
    # The version the primary gave when the connection opened: psql has it without asking the server anything.
    $answer = "$(Invoke-PrimaryQuery '\echo :SERVER_VERSION_NUM' $checkConnInfo)".Trim()
    if ($answer -notmatch '^\d{5,}$') { throw "The primary did not give its PostgreSQL version over the replication connection (psql answered '$answer')." }
    $primaryVersion = [int] $answer
    if ([math]::Floor($primaryVersion / 10000) -ne [int] $localVersion) {
        throw "The primary runs PostgreSQL $([math]::Floor($primaryVersion / 10000)), this machine $localVersion. A standby needs the same version: install it with Install-PostgreSQL.ps1 -Version $([math]::Floor($primaryVersion / 10000))."
    }
    # slot_type|restart_lsn|restart_tli, all empty for a slot the primary does not have.
    $slot = "$(Invoke-PrimaryQuery "READ_REPLICATION_SLOT $SlotName" $checkConnInfo)".Trim()
    if ($slot -notmatch '\|') { throw "The primary did not answer READ_REPLICATION_SLOT (psql answered '$slot')." }
    if (-not ($slot -split '\|')[0]) { throw "The primary has no replication slot $SlotName. Run Enable-HC360Replication.ps1 there first." }
    Write-Host "    The primary takes this machine over TLS, runs PostgreSQL $([math]::Floor($primaryVersion / 10000)) and has the slot $SlotName."

    # ---------------------------------------------------------------- copy the primary
    Write-Step "Stopping PostgreSQL ($($postgres.Name)) and moving $data aside"
    # Started by hand meanwhile, the database watchdog leaves it alone, and Windows does not start it.
    Set-Service -Name $postgres.Name -StartupType Manual
    Stop-Service -Name $postgres.Name -Force
    Wait-Service $postgres.Name 'Stopped' 120
    $moved = "$data.before-standby-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Rename-Item -LiteralPath $data -NewName (Split-Path -Leaf $moved)

    Write-Step "Copying the primary's database (pg_basebackup)"
    & $basebackup -d $checkConnInfo -D $data -R -X stream -S $SlotName --checkpoint=fast --progress
    if ($LASTEXITCODE -ne 0) { throw "pg_basebackup failed (exit code $LASTEXITCODE): the message above says why." }
    $copiedVersion = (Get-Content -LiteralPath (Join-Path $data 'PG_VERSION') -TotalCount 1).Trim()
    if ($copiedVersion -ne $localVersion) { throw "The copy is of PostgreSQL $copiedVersion, this machine runs $localVersion." }
    if (-not (Test-Path -LiteralPath (Join-Path $data 'standby.signal'))) { throw 'pg_basebackup did not make the copy a standby (standby.signal).' }

    Write-Step 'Setting the copy up as this machine''s standby'
    $standbyFolder = Join-Path $parent 'hc360-standby'
    New-Item -ItemType Directory -Force -Path $standbyFolder | Out-Null
    Set-DataFolderAcl $standbyFolder $accountSid 'ReadAndExecute'
    $rootCertificate = Join-Path $standbyFolder 'primary.crt'
    $passFile = Join-Path $standbyFolder 'replication.pgpass'
    # Written, not copied, so that each file takes the folder's permissions.
    [System.IO.File]::WriteAllBytes($rootCertificate, [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PrimaryCertificate).Path))
    [System.IO.File]::WriteAllBytes($passFile, [System.IO.File]::ReadAllBytes($tempPassFile))

    $oldConf = Read-TextLines (Join-Path $moved 'postgresql.conf')
    $oldAuto = Read-TextLines (Join-Path $moved 'postgresql.auto.conf')
    $connInfo = New-PrimaryConnInfo $PrimaryAddress $PrimaryPort $ReplicationRole $rootCertificate $passFile $SlotName
    $autoPath = Join-Path $data 'postgresql.auto.conf'
    Write-TextLines $autoPath (Set-ConfSettings (Read-TextLines $autoPath) (Get-StandbySettings $oldConf $oldAuto $Port $connInfo $SlotName $restoreCommand))
    Set-DataFolderAcl $data $accountSid

    Write-Step "Starting PostgreSQL ($($postgres.Name)) as a standby"
    Start-Service -Name $postgres.Name
    Wait-Service $postgres.Name 'Running' 180
    & (Join-Path $bin 'pg_isready.exe') -h localhost -p $Port -t 120 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL did not take connections within two minutes. Its log, in the log folder of $data, says why." }
    # A copy that came up as a second primary would take writes the primary never sees: it is undone like a failed copy.
    $inRecovery = Invoke-LocalSql 'SELECT pg_is_in_recovery()'
    if ($inRecovery -ne 't') { throw 'PostgreSQL started as a primary, not as a standby.' }
}
catch {
    $failure = $_
    if ($moved -and (Test-Path -LiteralPath $moved)) {
        Write-Warning "$($failure.Exception.Message) Putting the data folder that was here back, and starting PostgreSQL on it."
        try {
            Stop-Service -Name $postgres.Name -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $data) {
                # Only the copy of the primary made a moment ago: the data that was here is in $moved.
                Remove-Item -LiteralPath $data -Recurse -Force
            }
            Rename-Item -LiteralPath $moved -NewName (Split-Path -Leaf $data)
        }
        catch {
            Write-Warning "The data folder could not be put back ($($_.Exception.Message)). It is $($moved): rename it to $data and start $($postgres.Name)."
            throw $failure
        }
    }
    if ((Get-Service -Name $postgres.Name).Status -ne 'Running') {
        try { Start-Service -Name $postgres.Name }
        catch { Write-Warning "PostgreSQL ($($postgres.Name)) could not be started again: $($_.Exception.Message)" }
    }
    throw $failure
}
finally {
    Remove-Item -LiteralPath $tempPassFile -Force -ErrorAction SilentlyContinue
    if ($startType -eq 'Auto') { Set-Service -Name $postgres.Name -StartupType Automatic -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- checks
Write-Step 'Checking that it streams from the primary'
$streaming = $null
for ($i = 0; $i -lt 60 -and -not $streaming; $i++) {
    $receiver = Invoke-LocalSql "SELECT status || '|' || coalesce(slot_name, '') || '|' || coalesce(sender_host, '') FROM pg_stat_wal_receiver"
    if ($receiver -and $receiver.StartsWith('streaming|')) { $streaming = $receiver } else { Start-Sleep -Seconds 1 }
}
$asideNote = "The data folder that was here is kept as $($moved): delete it once you no longer need it."
if (-not $streaming) {
    Write-Warning "PostgreSQL runs as a standby but does not stream from the primary yet. Its log, in the log folder of $data, says why. System health on the primary warns while the standby is not connected."
}
else {
    Write-Host ''
    Write-Host "This machine's PostgreSQL is a warm standby of ${PrimaryAddress}: it streams through the slot $SlotName, over TLS." -ForegroundColor Green
}
Write-Host "    $asideNote"
Write-Host 'Next: install HC360 here with its service disabled, and the hospital CA, so workstations trust it after a failover:'
Write-Host '    .\Install-HC360Server.ps1 -Standby -HospitalCaFile <the CA exported on the primary>'
