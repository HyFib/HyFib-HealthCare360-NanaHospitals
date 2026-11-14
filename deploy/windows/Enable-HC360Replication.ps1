#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Archives this Server Node's WAL for a restore to any moment, with a recovery point of minutes (SRS REL-02), and lets one
    warm standby Server Node stream its PostgreSQL (SRS DED-20): either, or both. Run it on the primary, the Server Node
    HC360 runs on.

.DESCRIPTION
    With -WalArchive, which needs no standby:
      - PostgreSQL copies every finished WAL file, its record of every change, to a folder on this server's own disk
        (-WalArchiveDirectory, by default %ProgramData%\HyFib\HealthCare360\wal-archive, never a share), at least every 15
        minutes (archive_mode, archive_command, archive_timeout). Only PostgreSQL's and HC360's service accounts,
        Administrators and SYSTEM may open the folder: the WAL holds patient data. HC360 encrypts each file from there
        into its backups within a minute, with the backup recovery key, removes the plain one, and copies the encrypted
        one to the backups' secondary directory: no WAL leaves the server unencrypted.
      - Creates the account hc360_basebackup, which may only stream the database (REPLICATION) and signs in only as the HC360
        service's Windows account, from this machine, with no password (SSPI, in pg_hba.conf and pg_ident.conf). HC360 takes
        a base backup with it at once, and then every Sunday, encrypted with the backup recovery key: the copy a restore to a
        point in time starts from.
      -NoWalArchive turns archiving off again, and removes the account.

    With -StandbyAddress, PostgreSQL stays closed to everything else: it listens on this machine's address towards the
    standby as well as localhost, and takes from the standby's address alone, over TLS, a replication connection of one
    account with a password. Nothing else can sign in from the network. In detail:
      - Creates the account hc360_replicator (REPLICATION, no other rights). Its password is turned into a SCRAM verifier
        here, so the password itself never reaches PostgreSQL, a command line or a log.
      - Makes the physical replication slot the standby streams through (hc360_standby): this server keeps the WAL the
        standby still needs while it is away, up to -StandbyWalLimitGB (max_slot_wal_keep_size), so a standby that is gone
        for good cannot fill the disk. System health warns while the standby is away.
      - Turns on TLS (ssl = on) with a certificate of its own, made here (hc360-server.crt and hc360-server.key in the data
        folder; the key readable only by PostgreSQL's service account and Administrators). The standby trusts exactly this
        certificate: copy hc360-server.crt to it.
      - Adds this machine's address towards the standby to listen_addresses, keeping localhost.
      - Opens TCP 5432 in Windows Firewall for PostgreSQL, from the standby's address only, on Private and Domain networks.
      - Makes the function public.hc360_replication_status() in the hospital database (-DatabaseName): it shows HC360's
        own account how far behind the standby is (pg_stat_replication), which that account may not read itself, and
        nothing else. System health measures the standby's lag with it.
    Either way it makes sure wal_level is replica (PostgreSQL's default), with enough WAL senders and replication slots,
    and writes one block in pg_hba.conf, between "# BEGIN HC360 replication" and "# END HC360 replication": the standby's
    hostssl replication rule, the base backup account's sspi rules, and rules that refuse both accounts every database.
    PostgreSQL then restarts when a setting needs it (listen_addresses, wal_level, archive_mode): HC360 is stopped for those
    seconds and started again.

    Run it again at any time: it writes the same settings, and a new password when one is given, and leaves what it is not
    asked about as it is. -Remove takes the standby's access out again: the slot, the account, its pg_hba.conf rule, the
    firewall rule, the address and TLS; with -NoWalArchive too, the archiving as well. Then New-HC360Standby.ps1 on the
    standby (docs/deployment/DEPLOYMENT.md, "Warm standby").

.EXAMPLE
    # A restore to any moment, on a single Server Node
    .\Enable-HC360Replication.ps1 -WalArchive

.EXAMPLE
    .\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12 -WalArchive

.EXAMPLE
    .\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12 -Remove
#>
[CmdletBinding()]
param(
    # The standby's fixed IP address (a DHCP reservation). Without it, only the WAL archive is set up (-WalArchive).
    [string] $StandbyAddress,
    # The replication account's password, at least 16 characters; asked for when not given. Give the same one to
    # New-HC360Standby.ps1 on the standby.
    [securestring] $ReplicationPassword,
    [securestring] $PostgresSuperuserPassword,
    [string] $PostgresSuperuser = 'postgres',
    [int] $Port = 5432,
    # This machine's address the standby connects to. By default, the one Windows uses to reach the standby.
    [string] $ListenAddress,
    [ValidatePattern('^hc360_[a-z0-9_]{1,57}$')] [string] $SlotName = 'hc360_standby',
    # How much WAL this server keeps for a standby that is away (max_slot_wal_keep_size).
    [ValidateRange(1, 10000)] [int] $StandbyWalLimitGB = 20,
    # Archive the WAL, for a restore to any moment (off by default). -NoWalArchive turns it off again.
    [switch] $WalArchive,
    # The folder on this server's own disk (not a share, nor a disk taken off site) PostgreSQL archives the WAL to, which
    # HC360 encrypts it from; giving it turns archiving on. By default %ProgramData%\HyFib\HealthCare360\wal-archive.
    [string] $WalArchiveDirectory,
    [switch] $NoWalArchive,
    # The Windows account the HC360 service runs as, which takes base backups. By default the HC360Server service's.
    [string] $HC360ServiceAccount,
    # The hospital database, in which HC360 reads how far behind the standby is.
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')] [string] $DatabaseName = 'hc360',
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# What this script pipes to psql is UTF-8, as psql reads it (a folder's name may not be ASCII).
$OutputEncoding = New-Object System.Text.UTF8Encoding $false

$ReplicationRole = 'hc360_replicator'
# The account HC360's base backups sign in as (PgBaseBackupTool.Role in HC360), and the pg_ident.conf map that lets the
# HC360 service's Windows account use it.
$BaseBackupRole = 'hc360_basebackup'
$BaseBackupMap = 'hc360_basebackup'
$CertificateFile = 'hc360-server.crt'
$KeyFile = 'hc360-server.key'
$FirewallGroup = 'HyFib HealthCare360 replication'
$HC360Service = 'HC360Server'

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

# ---------------------------------------------------------------- replication settings (tests/deploy/Test-StandbyScripts.ps1)

# The folder of pg_ctl.exe in a PostgreSQL service's command line, where psql.exe and postgres.exe are too.
function Get-PgCtlBinDirectory([string] $CommandLine) {
    if ($CommandLine -match '^\s*"([^"]*pg_ctl(?:\.exe)?)"' -or $CommandLine -match '^\s*(\S*pg_ctl(?:\.exe)?)\s') {
        return Split-Path -Parent $Matches[1]
    }
    return $null
}

# Why $Password cannot be the replication account's, or $null when it can: at least 16 characters, and only printable
# ASCII, which SCRAM's SASLprep leaves as it is, so the verifier made here is the one PostgreSQL would make.
function Test-ReplicationPassword([string] $Password) {
    if ($Password.Length -lt 16) { return 'Use a replication password of at least 16 characters.' }
    if ($Password -cmatch '[^\x20-\x7E]') { return 'Use only letters, digits, spaces and the punctuation on an English keyboard in the replication password.' }
    return $null
}

# The keys RFC 5802 derives from a password (as Platform/Persistence/DatabaseCredentials.cs does in HC360 itself).
function Get-ScramKeys([string] $Password, [byte[]] $Salt, [int] $Iterations) {
    $pbkdf2 = New-Object System.Security.Cryptography.Rfc2898DeriveBytes -ArgumentList ([System.Text.Encoding]::UTF8.GetBytes($Password)), $Salt, $Iterations, ([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $salted = $pbkdf2.GetBytes(32) } finally { $pbkdf2.Dispose() }
    $hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (, $salted)
    try {
        $clientKey = $hmac.ComputeHash([System.Text.Encoding]::ASCII.GetBytes('Client Key'))
        $serverKey = $hmac.ComputeHash([System.Text.Encoding]::ASCII.GetBytes('Server Key'))
    }
    finally { $hmac.Dispose() }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $storedKey = $sha.ComputeHash($clientKey) } finally { $sha.Dispose() }
    return [pscustomobject]@{ ClientKey = $clientKey; StoredKey = $storedKey; ServerKey = $serverKey }
}

# The SCRAM-SHA-256 verifier PostgreSQL stores for $Password: SCRAM-SHA-256$<iterations>:<salt>$<StoredKey>:<ServerKey>.
# Given as the account's PASSWORD, PostgreSQL stores it as it is, so the password itself is never sent.
function ConvertTo-ScramVerifier([string] $Password, [byte[]] $Salt, [int] $Iterations = 4096) {
    if (-not $Salt) {
        $Salt = New-Object byte[] 16
        $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $random.GetBytes($Salt) } finally { $random.Dispose() }
    }
    $keys = Get-ScramKeys $Password $Salt $Iterations
    return 'SCRAM-SHA-256${0}:{1}${2}:{3}' -f $Iterations, [Convert]::ToBase64String($Salt), [Convert]::ToBase64String($keys.StoredKey), [Convert]::ToBase64String($keys.ServerKey)
}

# pg_hba.conf with the block that takes the replication account $Role from $StandbyAddress alone, over TLS, with its
# password, and, with $BaseBackupRole, the base backup account from this machine alone as the HC360 service's Windows
# account (SSPI, through the pg_ident.conf map $BaseBackupMap); each refused every database, from anywhere. Without either
# there is no block. PostgreSQL follows the first rule a connection matches, so the block goes before the first rule
# (rules for "all" databases do not match replication, so the refusals leave streaming alone). It goes after the comments
# at the top and after another script's marked block there, such as Install-HC360Server.ps1's, which that installer puts
# before this one: run in either order, the two scripts leave the file the same.
function Set-ReplicationHbaBlock([string[]] $Lines, [string] $Role, [string] $StandbyAddress, [string] $BaseBackupRole, [string] $BaseBackupMap) {
    foreach ($name in @($Role, $BaseBackupRole, $BaseBackupMap)) {
        if ($name -and $name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "The account '$name' cannot be named in pg_hba.conf as it is." }
    }
    $block =@('# BEGIN HC360 replication. Enable-HC360Replication.ps1 writes this block on every run: changes to it are lost.')
    if ($StandbyAddress) {
        $address = [System.Net.IPAddress]::Parse($StandbyAddress)
        $prefix = if ($address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) { 128 } else { 32 }
        $block += @(
            "# The warm standby streams this server's WAL as $Role, from its address only, over TLS, with its password; the account opens no database.",
            "hostssl   replication   $Role   $address/$prefix   scram-sha-256",
            "host      all           $Role   all              reject",
            "local     all           $Role                    reject"
        )
    }
    if ($BaseBackupRole) {
        $block += @(
            "# HC360 takes base backups as $BaseBackupRole, signed in as its service's Windows account from this machine only (SSPI, mapped in pg_ident.conf); the account opens no database.",
            "host      replication   $BaseBackupRole   127.0.0.1/32   sspi   map=$BaseBackupMap",
            "host      replication   $BaseBackupRole   ::1/128        sspi   map=$BaseBackupMap",
            "host      replication   $BaseBackupRole   all            reject",
            "host      all           $BaseBackupRole   all            reject",
            "local     all           $BaseBackupRole                  reject"
        )
    }
    $block += '# END HC360 replication'
    $rest = Remove-ReplicationHbaBlock $Lines
    if (-not $StandbyAddress -and -not $BaseBackupRole) { return , $rest }
    $at = 0
    while ($at -lt $rest.Count) {
        if ($rest[$at].StartsWith('# BEGIN ')) {
            while ($at -lt $rest.Count -and -not $rest[$at].StartsWith('# END ')) { $at++ }
            $at++
        }
        elseif ($rest[$at] -match '^\s*(#|$)') { $at++ }
        else { break }
    }
    # In @(), so that one line is an array of one, not a string the block would be added to.
    $head = @(if ($at -gt 0) { $rest[0..($at - 1)] })
    $tail = @(if ($at -lt $rest.Count) { $rest[$at..($rest.Count - 1)] })
    return , ($head + $block + $tail)
}

# The lines of pg_hba.conf without the replication block.
function Remove-ReplicationHbaBlock([string[]] $Lines) {
    $kept = @()
    $inside = $false
    foreach ($line in $Lines) {
        if (-not $inside -and $line.StartsWith('# BEGIN HC360 replication')) { $inside = $true }
        elseif ($inside) { if ($line.StartsWith('# END HC360 replication')) { $inside = $false } }
        else { $kept += $line }
    }
    if ($inside) { throw "pg_hba.conf has a '# BEGIN HC360 replication' line without its '# END HC360 replication' line. Correct the file and run the script again." }
    return , $kept
}

# The rules outside the block that also take replication connections, which the standby's rule does not close.
function Get-OtherReplicationRules([string[]] $Lines) {
    $other = @()
    foreach ($line in (Remove-ReplicationHbaBlock $Lines)) {
        $fields = @($line.Trim() -split '\s+')
        if ($line -notmatch '^\s*(#|$)' -and $fields.Count -ge 2 -and (@($fields[1] -split ',') -contains 'replication')) { $other += $line.Trim() }
    }
    return , $other
}

# The standby's address in the block an earlier run wrote, or $null: kept when a run changes only the WAL archive.
function Get-ReplicationStandbyAddress([string[]] $Lines, [string] $Role) {
    $inside = $false
    foreach ($line in $Lines) {
        if ($line.StartsWith('# BEGIN HC360 replication')) { $inside = $true }
        elseif ($line.StartsWith('# END HC360 replication')) { $inside = $false }
        elseif ($inside -and $line -match "^hostssl\s+replication\s+$([regex]::Escape($Role))\s+(?<address>[0-9A-Fa-f:.]+)/\d+\s") { return $Matches['address'] }
    }
    return $null
}

# pg_ident.conf with the block that maps the Windows account $WindowsAccount, named as PostgreSQL's SSPI sign-in names it
# (ConvertTo-SspiName), to the base backup account $Role under its own map $Map. Install-HC360Server.ps1's block maps the
# same account to HC360's own database account under another map, and leaves this block as it is.
function Set-ReplicationIdentBlock([string[]] $Lines, [string] $Map, [string] $WindowsAccount, [string] $Role) {
    if ($WindowsAccount.Contains('"')) { throw "The Windows account '$WindowsAccount' cannot be named in pg_ident.conf." }
    $block = @(
        '# BEGIN HC360 replication. Enable-HC360Replication.ps1 writes this block on every run: changes to it are lost.',
        "$Map   `"$WindowsAccount`"   $Role",
        '# END HC360 replication'
    )
    return , (@(Remove-ReplicationIdentBlock $Lines) + $block)
}

# The lines of pg_ident.conf without the replication block.
function Remove-ReplicationIdentBlock([string[]] $Lines) {
    $kept = @()
    $inside = $false
    foreach ($line in $Lines) {
        if (-not $inside -and $line.StartsWith('# BEGIN HC360 replication')) { $inside = $true }
        elseif ($inside) { if ($line.StartsWith('# END HC360 replication')) { $inside = $false } }
        else { $kept += $line }
    }
    if ($inside) { throw "pg_ident.conf has a '# BEGIN HC360 replication' line without its '# END HC360 replication' line. Correct the file and run the script again." }
    return , $kept
}

# A Windows account written DOMAIN\name, such as NT SERVICE\HC360Server, as PostgreSQL's SSPI sign-in names it: name@DOMAIN.
# The installer's own function, kept the same (tests/deploy/Test-StandbyScripts.ps1 checks).
function ConvertTo-SspiName([string] $Account) {
    $parts = $Account.Split('\')
    if ($parts.Count -ne 2 -or -not $parts[0] -or -not $parts[1]) { throw "'$Account' is not a Windows account written DOMAIN\name." }
    return "$($parts[1])@$($parts[0])"
}

# The SID of a service's own account, NT SERVICE\<name>: S-1-5-80 and the SHA-1 of the name in capitals (UTF-16), as five
# numbers, as Windows makes it. Worked out, not looked up, so the folder can be opened to the HC360 service before it is
# installed.
function Get-ServiceSid([string] $ServiceName) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($ServiceName.ToUpperInvariant())) } finally { $sha.Dispose() }
    $parts = for ($i = 0; $i -lt 20; $i += 4) { [BitConverter]::ToUInt32($hash, $i) }
    return 'S-1-5-80-' + ($parts -join '-')
}

# The SID of the account a service runs as: a service's own account (NT SERVICE\name) worked out, any other looked up.
function Resolve-ServiceAccountSid([string] $Account) {
    if ($Account -match '^NT SERVICE\\(?<service>.+)$') { return Get-ServiceSid $Matches['service'] }
    return Resolve-AccountSid $Account
}

# listen_addresses with $Address added, the rest kept as it was; '*' already listens everywhere.
function Add-ListenAddress([string] $Current, [string] $Address) {
    $items = @($Current -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($items -contains '*' -or $items -contains $Address) { return ($items -join ',') }
    return (@($items) + $Address) -join ','
}

# listen_addresses without $Address; localhost when nothing would be left.
function Remove-ListenAddress([string] $Current, [string] $Address) {
    $items = @($Current -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne $Address })
    if (-not $items) { return 'localhost' }
    return $items -join ','
}

# The archive_command that copies a finished WAL file to $Directory, a folder on this server's own disk: HC360 encrypts
# each file from there into its backups within a minute, and removes the plain one, so no WAL leaves the server
# unencrypted (HC360 finds the folder in this very command, WalArchive.ArchiveFolder). Through a temporary name, so the
# archive never holds part of a file under its real name, and never over a file already there. A file archived before with
# the same content counts as archived (PostgreSQL may ask again after a crash); one with other content fails, so PostgreSQL
# keeps the WAL, retries, and System health warns.
function Get-ArchiveCommand([string] $Directory) {
    if (-not [System.IO.Path]::IsPathRooted($Directory) -or $Directory.StartsWith('\\') -or $Directory.IndexOfAny([char[]] '"%^&|<>') -ge 0) {
        throw "Give the WAL archive folder as a full path on this server's own disk (such as C:\HC360-WAL), not a share, and without quotes or any of % ^ & | < >: HC360 encrypts the WAL there before any copy of it leaves the server."
    }
    $dir = $Directory.TrimEnd('\')
    return "if exist `"$dir\%f`" (fc /b `"%p`" `"$dir\%f`" >nul) else (copy /b /y `"%p`" `"$dir\%f.tmp`" >nul && move /y `"$dir\%f.tmp`" `"$dir\%f`" >nul)"
}

# The function HC360 reads the standbys' lag through (StandbyReplication.StatusFunction in HC360). Made by the superuser
# and running as its maker (SECURITY DEFINER, with a fixed search_path), it reads pg_stat_replication, which HC360's own
# account may not read in full (pg_read_all_stats), and returns the lag of the standbys HC360 set up and nothing else.
# Every account of the hospital database may call it, and only HC360's two may connect to that database. A copy restored
# from a dump belongs to the database's owner and shows no lag: running this script again makes it the superuser's again.
function Get-ReplicationStatusSql {
    return @'
CREATE OR REPLACE FUNCTION public.hc360_replication_status()
RETURNS TABLE (application_name text, state text, replay_lag_seconds double precision, flush_lag_seconds double precision, replay_behind_bytes bigint, reply_time timestamptz)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $hc360$
    SELECT r.application_name::text, r.state::text, extract(epoch FROM r.replay_lag)::float8, extract(epoch FROM r.flush_lag)::float8,
           pg_wal_lsn_diff(pg_current_wal_lsn(), r.replay_lsn)::bigint, r.reply_time
    FROM pg_stat_replication r
    WHERE r.application_name LIKE 'hc360\_%'
$hc360$;
COMMENT ON FUNCTION public.hc360_replication_status() IS 'HyFib HealthCare360: how far behind each warm standby is, for System health. Enable-HC360Replication.ps1 makes it; it reads pg_stat_replication as its owner, and shows nothing else.';
'@
}

# Whether archive_command is one Get-ArchiveCommand made.
function Test-OwnArchiveCommand([string] $Command) {
    return [bool] ($Command -match '^if exist "[^"]+\\%f" \(fc /b "%p" "[^"]+\\%f" >nul\) else \(copy /b /y "%p" "[^"]+\\%f\.tmp" >nul && move /y "[^"]+\\%f\.tmp" "[^"]+\\%f" >nul\)$')
}

# $Bytes as PEM, with $Label (CERTIFICATE, PRIVATE KEY), as OpenSSL and so PostgreSQL read it.
function ConvertTo-Pem([string] $Label, [byte[]] $Bytes) {
    $base64 = [Convert]::ToBase64String($Bytes)
    $lines = for ($i = 0; $i -lt $base64.Length; $i += 64) { $base64.Substring($i, [Math]::Min(64, $base64.Length - $i)) }
    return (@("-----BEGIN $Label-----") + $lines + @("-----END $Label-----")) -join "`n"
}

# A self-signed certificate for PostgreSQL's TLS, with its RSA key, both as PEM. It is its own CA, so the standby trusts
# exactly this certificate (sslmode=verify-ca with it as the root): only the holder of its key can be the primary. It names
# this machine and $IpAddresses, for a standby that checks the name too.
function New-PostgresTlsCertificate([string] $CommonName, [string[]] $DnsNames, [string[]] $IpAddresses, [int] $Days = 3650) {
    $parameters = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $parameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $parameters.Parameters.Add((New-Object System.Security.Cryptography.CngProperty -ArgumentList 'Length', ([BitConverter]::GetBytes([int] 3072)), ([System.Security.Cryptography.CngPropertyOptions]::None)))
    # A key without a name lives in memory only. PowerShell would pass $null as an empty name, which Windows keeps on disk.
    $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::Rsa, [NullString]::Value, $parameters)
    try {
        $rsa = [System.Security.Cryptography.RSACng]::new($key)
        $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest -ArgumentList "CN=$CommonName, O=HyFib HealthCare360", $rsa,
            ([System.Security.Cryptography.HashAlgorithmName]::SHA256), ([System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension -ArgumentList $true, $true, 0, $true))
        $usage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags] 'DigitalSignature, KeyEncipherment, KeyCertSign'
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension -ArgumentList $usage, $true))
        $serverAuth = New-Object System.Security.Cryptography.OidCollection
        [void] $serverAuth.Add((New-Object System.Security.Cryptography.Oid -ArgumentList '1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension -ArgumentList $serverAuth, $false))
        $names = New-Object System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
        foreach ($name in $DnsNames) { $names.AddDnsName($name) }
        foreach ($ip in $IpAddresses) { $names.AddIpAddress([System.Net.IPAddress]::Parse($ip)) }
        $request.CertificateExtensions.Add($names.Build())
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension -ArgumentList $request.PublicKey, $false))
        $now = [DateTimeOffset]::UtcNow
        $certificate = $request.CreateSelfSigned($now.AddDays(-1), $now.AddDays($Days))
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { $fingerprint = -join ($sha.ComputeHash($certificate.RawData) | ForEach-Object { $_.ToString('X2') }) } finally { $sha.Dispose() }
            return [pscustomobject]@{
                Certificate = ConvertTo-Pem 'CERTIFICATE' $certificate.RawData
                Key = ConvertTo-Pem 'PRIVATE KEY' $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
                Fingerprint = $fingerprint
                NotAfter = $certificate.NotAfter
            }
        }
        finally { $certificate.Dispose() }
    }
    finally { $key.Dispose() }
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

# The SID of a Windows service's account as Win32_Service names it (StartName): LocalSystem, NT AUTHORITY\NetworkService,
# .\postgres, NT SERVICE\postgresql-x64-17.
function Resolve-AccountSid([string] $Account) {
    if (-not $Account -or $Account -eq 'LocalSystem') { return 'S-1-5-18' }
    if ($Account.StartsWith('.\')) { $Account = "$env:COMPUTERNAME\$($Account.Substring(2))" }
    return (New-Object System.Security.Principal.NTAccount -ArgumentList $Account).Translate([System.Security.Principal.SecurityIdentifier]).Value
}

# Makes $Path readable only by SYSTEM, Administrators and the account $ReaderSid (PostgreSQL's service account).
function Set-SecretFileAcl([string] $Path, [string] $ReaderSid) {
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList (New-Object System.Security.Principal.SecurityIdentifier $sid), 'FullControl', 'Allow'))
    }
    if ($ReaderSid -ne 'S-1-5-18') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList (New-Object System.Security.Principal.SecurityIdentifier $ReaderSid), 'Read', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# Gives $Path, the WAL archive folder, to SYSTEM and Administrators, and to $WriterSids (PostgreSQL's and HC360's service
# accounts) to add and remove files in, and to nobody else: the WAL holds patient data. What the folder above allows
# (Users may read under ProgramData) does not reach it.
function Set-WalArchiveAcl([string] $Path, [string[]] $WriterSids) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList (New-Object System.Security.Principal.SecurityIdentifier $sid), 'FullControl', $inherit, 'None', 'Allow'))
    }
    foreach ($sid in @($WriterSids | Where-Object { $_ -and $_ -notin @('S-1-5-18', 'S-1-5-32-544') } | Select-Object -Unique)) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList (New-Object System.Security.Principal.SecurityIdentifier $sid), 'Modify', $inherit, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# ---------------------------------------------------------------- PostgreSQL

# Runs SQL as the superuser on this machine's PostgreSQL, in $Database. The SQL goes through standard input, so nothing
# in it shows in the process list, and $Description, not the SQL, is in the error.
function Invoke-Sql([string] $Sql, [string] $Description, [string] $Database = 'postgres') {
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    try {
        $output = $Sql | & $psql -X -h localhost -p $Port -U $PostgresSuperuser -d $Database -v ON_ERROR_STOP=1 -q -A -t -f -
        if ($LASTEXITCODE -ne 0) { throw "PostgreSQL refused to $Description." }
        return $output
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

function Get-Setting([string] $Name) { return (Invoke-Sql "SELECT current_setting('$Name')" "read $Name") }

# A SQL string literal.
function ConvertTo-SqlLiteral([string] $Value) { return "'" + $Value.Replace("'", "''") + "'" }

# Restarts PostgreSQL when a changed setting needs it, with HC360 stopped meanwhile (it is started again if it ran).
function Restart-PostgresIfNeeded($Postgres) {
    $null = Invoke-Sql 'SELECT pg_reload_conf()' 'reload its configuration'
    Start-Sleep -Seconds 1
    # pg_file_settings reads the files again in this connection, and names each setting it cannot apply without a
    # restart. pg_settings.pending_restart is not enough on Windows: a new connection's process does not inherit it.
    $pending = @(Invoke-Sql "SELECT name FROM pg_file_settings WHERE error = 'setting could not be applied' UNION SELECT name FROM pg_settings WHERE pending_restart ORDER BY 1" 'read the settings that wait for a restart' |
        Where-Object { $_ })
    if (-not $pending) { return }
    Write-Step "Restarting PostgreSQL for $($pending -join ', ')"
    $hc360 = Get-Service -Name $HC360Service -ErrorAction SilentlyContinue
    $wasRunning = $hc360 -and $hc360.Status -eq 'Running'
    if ($wasRunning) {
        Write-Host '    HC360 stops for a few seconds meanwhile.'
        Stop-Service -Name $HC360Service -Force
    }
    try {
        Restart-Service -Name $Postgres.Name -Force
        & (Join-Path $bin 'pg_isready.exe') -h localhost -p $Port -t 60 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "PostgreSQL ($($Postgres.Name)) did not take connections within a minute of restarting. Its log, in the log folder of $($Postgres.DataDirectory), says why." }
    }
    finally {
        if ($wasRunning) { Start-Service -Name $HC360Service }
    }
}

# Takes HC360's base backups' way in out: the account, once disconnected, and its pg_ident.conf block. Its pg_hba.conf
# rules go with the block the caller writes next.
function Remove-BaseBackupAccess($Postgres) {
    $null = Invoke-Sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '$BaseBackupRole'" 'disconnect the base backup account'
    $null = Invoke-Sql "DROP ROLE IF EXISTS $BaseBackupRole" "drop the account $BaseBackupRole"
    $ident = Read-TextLines $Postgres.IdentFile
    $identAfter = Remove-ReplicationIdentBlock $ident
    if (($identAfter -join "`n") -cne ($ident -join "`n")) { Write-TextLines $Postgres.IdentFile $identAfter }
}

# ---------------------------------------------------------------- checks (nothing changes before these pass)
Write-Step 'Checking PostgreSQL and the addresses'
$archiveRequested = $WalArchive.IsPresent -or [bool] $WalArchiveDirectory
if (-not $StandbyAddress -and -not $archiveRequested -and -not $NoWalArchive) {
    throw 'Give -StandbyAddress to let a warm standby stream from this server, -WalArchive to archive the WAL for a restore to any moment, or both.'
}
if ($archiveRequested -and $NoWalArchive) { throw 'Give -WalArchive or -NoWalArchive, not both.' }
if ($Remove -and -not $StandbyAddress) { throw "-Remove takes the standby's access out: give the standby's address (-StandbyAddress). To stop archiving the WAL, run the script with -NoWalArchive." }
if ($Remove -and $archiveRequested) { throw '-Remove takes the standby''s access out, and -WalArchive sets archiving up: run the script once for each.' }
if ($StandbyAddress) {
    $standbyIp = $null
    if (-not [System.Net.IPAddress]::TryParse($StandbyAddress, [ref] $standbyIp) -or $standbyIp.Equals([System.Net.IPAddress]::Any) -or $standbyIp.Equals([System.Net.IPAddress]::IPv6Any)) {
        throw "Give the standby's IP address, such as 10.0.5.12, not '$StandbyAddress': pg_hba.conf and the firewall take addresses."
    }
    $StandbyAddress = $standbyIp.ToString()
}

$postgres = Find-PostgresService $Port
if (-not $postgres) { throw "No running PostgreSQL service on this machine listens on port $Port. Start it, or give its port with -Port." }
$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($postgres.Name)'"
$bin = Get-PgCtlBinDirectory $service.PathName
$psql = Join-Path $bin 'psql.exe'
if (-not (Test-Path -LiteralPath $psql)) { throw "psql.exe was not found beside PostgreSQL's pg_ctl.exe, in $bin." }
$data = $postgres.DataDirectory
if ((Test-Path -LiteralPath (Join-Path $data 'standby.signal')) -or (Test-Path -LiteralPath (Join-Path $data 'recovery.signal'))) {
    throw "This PostgreSQL ($($postgres.Name)) is itself a standby (standby.signal in $data). Run Enable-HC360Replication.ps1 on the primary Server Node."
}
$serviceSid = Resolve-AccountSid $service.StartName
Write-Host "    PostgreSQL service $($postgres.Name) ($($service.StartName)), data folder $data"

if (-not $PostgresSuperuserPassword) { $PostgresSuperuserPassword = Read-Host -AsSecureString "Password for PostgreSQL user '$PostgresSuperuser'" }
Write-Host "    PostgreSQL $(Get-Setting 'server_version')"

# Whether PostgreSQL archives the WAL once this run is done: asked for now, or set up by an earlier run and not turned off.
# HC360's base backups go with it.
$ownArchive = Test-OwnArchiveCommand (Get-Setting 'archive_command')
$archiving = $archiveRequested -or ($ownArchive -and -not $NoWalArchive)

if (-not $Remove) {
    if ($StandbyAddress) {
        if (-not $ListenAddress) {
            $ListenAddress = Find-NetRoute -RemoteIPAddress $StandbyAddress -ErrorAction SilentlyContinue |
                Where-Object { $_.PSObject.Properties['IPAddress'] } | Select-Object -First 1 -ExpandProperty IPAddress
            if (-not $ListenAddress) { throw "Windows has no route to $StandbyAddress. Check the network, or give this machine's address with -ListenAddress." }
        }
        $listenIp = $null
        if (-not [System.Net.IPAddress]::TryParse($ListenAddress, [ref] $listenIp)) { throw "-ListenAddress takes an IP address of this machine, not '$ListenAddress'." }
        $ListenAddress = $listenIp.ToString()
        if (-not $listenIp.Equals([System.Net.IPAddress]::Loopback) -and -not (Get-NetIPAddress -IPAddress $ListenAddress -ErrorAction SilentlyContinue)) {
            throw "$ListenAddress is not an address of this machine."
        }

        if (-not $ReplicationPassword) {
            $ReplicationPassword = Read-Host -AsSecureString "New password for the replication account $ReplicationRole (16 characters or more)"
            $again = Read-Host -AsSecureString 'The same password again'
            if ([System.Net.NetworkCredential]::new('', $ReplicationPassword).Password -cne [System.Net.NetworkCredential]::new('', $again).Password) { throw 'The two passwords differ.' }
        }
        $problem = Test-ReplicationPassword ([System.Net.NetworkCredential]::new('', $ReplicationPassword).Password)
        if ($problem) { throw $problem }

        $ssl = Get-Setting 'ssl'
        $certificateSetting = Get-Setting 'ssl_cert_file'
        if ($ssl -eq 'on' -and $certificateSetting -ne $CertificateFile) {
            throw "PostgreSQL already uses TLS with its own certificate ($certificateSetting). Enable-HC360Replication.ps1 sets up a certificate of its own: turn the other one off first (ALTER SYSTEM RESET ssl), or set up the standby by hand as docs/deployment/DEPLOYMENT.md describes."
        }
    }

    if ($archiveRequested) {
        if (-not $WalArchiveDirectory) { $WalArchiveDirectory = Join-Path $env:ProgramData 'HyFib\HealthCare360\wal-archive' }
        $archiveCommand = Get-ArchiveCommand $WalArchiveDirectory
    }

    if ($archiving) {
        # The HC360 service's account takes the base backups, as HC360 itself signs in to its database: with no password.
        if (-not $HC360ServiceAccount) {
            $hc360Service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$HC360Service'" -ErrorAction SilentlyContinue
            $HC360ServiceAccount = if ($hc360Service -and $hc360Service.StartName) { $hc360Service.StartName } else { "NT SERVICE\$HC360Service" }
        }
        if ($HC360ServiceAccount -eq 'LocalSystem') { $HC360ServiceAccount = 'NT AUTHORITY\SYSTEM' }
        $hc360Sspi = ConvertTo-SspiName $HC360ServiceAccount
        $hc360Sid = Resolve-ServiceAccountSid $HC360ServiceAccount
    }
}

# ---------------------------------------------------------------- remove
if ($Remove) {
    Write-Step 'Removing the warm standby''s access'
    # The standby's way in goes first: a standby that is still running connects again within seconds of being
    # disconnected, and its slot cannot be dropped while it streams. HC360's base backups keep their rules while the WAL
    # is still archived.
    $hba = Read-TextLines $postgres.HbaFile
    $withoutStandby = if ($archiving) { Set-ReplicationHbaBlock $hba $ReplicationRole '' $BaseBackupRole $BaseBackupMap } else { Remove-ReplicationHbaBlock $hba }
    if (($withoutStandby -join "`n") -cne ($hba -join "`n")) {
        Write-TextLines $postgres.HbaFile $withoutStandby
        $null = Invoke-Sql 'SELECT pg_reload_conf()' 'reload its configuration'
    }
    Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    $slot = ConvertTo-SqlLiteral $SlotName
    $null = Invoke-Sql "SELECT pg_terminate_backend(active_pid) FROM pg_replication_slots WHERE slot_name = $slot AND active_pid IS NOT NULL" 'disconnect the standby'
    Start-Sleep -Seconds 1
    $null = Invoke-Sql "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = $slot" "drop the replication slot $SlotName"
    $null = Invoke-Sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '$ReplicationRole'" 'disconnect the replication account'
    $null = Invoke-Sql "DROP ROLE IF EXISTS $ReplicationRole" "drop the account $ReplicationRole"
    if ((Invoke-Sql "SELECT count(*) FROM pg_database WHERE datname = $(ConvertTo-SqlLiteral $DatabaseName)" "look for the database $DatabaseName") -eq '1') {
        $null = Invoke-Sql 'DROP FUNCTION IF EXISTS public.hc360_replication_status()' 'drop the function HC360 read the standby''s lag through' $DatabaseName
    }

    $statements = @('ALTER SYSTEM RESET max_slot_wal_keep_size')
    $listen = Get-Setting 'listen_addresses'
    $ListenAddress = if ($ListenAddress) { $ListenAddress } else {
        Find-NetRoute -RemoteIPAddress $StandbyAddress -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties['IPAddress'] } | Select-Object -First 1 -ExpandProperty IPAddress
    }
    if ($ListenAddress) {
        $statements += "ALTER SYSTEM SET listen_addresses = $(ConvertTo-SqlLiteral (Remove-ListenAddress $listen $ListenAddress))"
    }
    if ((Get-Setting 'ssl_cert_file') -eq $CertificateFile) {
        $statements += 'ALTER SYSTEM RESET ssl', 'ALTER SYSTEM RESET ssl_cert_file', 'ALTER SYSTEM RESET ssl_key_file'
    }
    if (-not $archiving) {
        if ($ownArchive) { $statements += 'ALTER SYSTEM RESET archive_mode', 'ALTER SYSTEM RESET archive_command', 'ALTER SYSTEM RESET archive_timeout' }
        Remove-BaseBackupAccess $postgres
    }
    $null = Invoke-Sql (($statements | ForEach-Object { "$_;" }) -join "`n") 'reset the replication settings'
    Restart-PostgresIfNeeded $postgres
    Write-Host ''
    Write-Host "The standby at $StandbyAddress can no longer stream from this server, and PostgreSQL listens on $(Get-Setting 'listen_addresses') only." -ForegroundColor Green
    Write-Host "    The certificate files $CertificateFile and $KeyFile stay in $data; delete them if they are not needed again."
    if ($archiving) { Write-Host '    PostgreSQL still archives the WAL, and HC360 still takes base backups: -NoWalArchive stops both.' }
    return
}

$statements = @()
if ($archiving -or $StandbyAddress) {
    Write-Step 'Setting up the WAL for a standby and for base backups'
    # Archiving needs more than the minimal WAL, and a base backup two WAL senders and a temporary slot, as a standby does.
    if ((Get-Setting 'wal_level') -eq 'minimal') { $statements += "ALTER SYSTEM SET wal_level = 'replica'" }
    if ([int] (Get-Setting 'max_wal_senders') -lt 10) { $statements += 'ALTER SYSTEM SET max_wal_senders = 10' }
    if ([int] (Get-Setting 'max_replication_slots') -lt 10) { $statements += 'ALTER SYSTEM SET max_replication_slots = 10' }
    # Left behind on a standby that was promoted and is now the primary.
    $statements += 'ALTER SYSTEM RESET primary_conninfo', 'ALTER SYSTEM RESET primary_slot_name', 'ALTER SYSTEM RESET restore_command'
}

if ($StandbyAddress) {
    # ---------------------------------------------------------------- the replication account and the slot
    Write-Step "Creating the replication account $ReplicationRole"
    $verifier = ConvertTo-SqlLiteral (ConvertTo-ScramVerifier ([System.Net.NetworkCredential]::new('', $ReplicationPassword).Password))
    $null = Invoke-Sql @"
DO `$`$ BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = '$ReplicationRole') THEN
    ALTER ROLE $ReplicationRole WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS PASSWORD $verifier;
  ELSE
    CREATE ROLE $ReplicationRole WITH LOGIN REPLICATION NOINHERIT PASSWORD $verifier;
  END IF;
END `$`$;
"@ "create the account $ReplicationRole"
    $statements += "ALTER SYSTEM SET max_slot_wal_keep_size = '${StandbyWalLimitGB}GB'"

    # ---------------------------------------------------------------- TLS
    $certificatePath = Join-Path $data $CertificateFile
    $keyPath = Join-Path $data $KeyFile
    $keep = $false
    if ((Test-Path -LiteralPath $certificatePath) -and (Test-Path -LiteralPath $keyPath)) {
        $existing = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $certificatePath
        try { $keep = $existing.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow.AddDays(30) } finally { $existing.Dispose() }
    }
    if ($keep) {
        Write-Step "Keeping PostgreSQL's TLS certificate ($CertificateFile)"
    }
    else {
        Write-Step "Making a TLS certificate for PostgreSQL ($CertificateFile)"
        $machine = $env:COMPUTERNAME.ToLowerInvariant()
        $made = New-PostgresTlsCertificate "$machine PostgreSQL" @($machine, "$machine.local") @($ListenAddress)
        $keyTemp = "$keyPath.tmp"
        # The key's file is closed to everyone but PostgreSQL's account and Administrators before the key is written in it.
        [System.IO.File]::WriteAllText($keyTemp, '')
        Set-SecretFileAcl $keyTemp $serviceSid
        [System.IO.File]::WriteAllText($keyTemp, $made.Key + "`n", (New-Object System.Text.UTF8Encoding $false))
        Move-Item -LiteralPath $keyTemp -Destination $keyPath -Force
        [System.IO.File]::WriteAllText($certificatePath, $made.Certificate + "`n", (New-Object System.Text.UTF8Encoding $false))
    }
    $statements += "ALTER SYSTEM SET ssl_cert_file = '$CertificateFile'", "ALTER SYSTEM SET ssl_key_file = '$KeyFile'", 'ALTER SYSTEM SET ssl = on'

    # ---------------------------------------------------------------- listening
    $listen = Get-Setting 'listen_addresses'
    $newListen = Add-ListenAddress $listen $ListenAddress
    if ($newListen -ne $listen) { $statements += "ALTER SYSTEM SET listen_addresses = $(ConvertTo-SqlLiteral $newListen)" }
    if (@($newListen -split ',' | ForEach-Object { $_.Trim() }) -contains '*') {
        Write-Warning "PostgreSQL listens on every address (listen_addresses = '*'). pg_hba.conf and the firewall still take only the standby, but listen_addresses = 'localhost,$ListenAddress' is enough."
    }
}

# ---------------------------------------------------------------- archiving
if ($archiveRequested) {
    Write-Step "Archiving the WAL to $WalArchiveDirectory"
    New-Item -ItemType Directory -Force -Path $WalArchiveDirectory | Out-Null
    # PostgreSQL's service writes the archive and HC360's encrypts and removes what is in it, and nobody else may open it:
    # the WAL holds patient data.
    Set-WalArchiveAcl $WalArchiveDirectory @($serviceSid, $hc360Sid)
    $statements +='ALTER SYSTEM SET archive_mode = on', "ALTER SYSTEM SET archive_command = $(ConvertTo-SqlLiteral $archiveCommand)", "ALTER SYSTEM SET archive_timeout = '15min'"
}
elseif ($NoWalArchive -and $ownArchive) {
    Write-Step 'Turning WAL archiving off'
    $statements += 'ALTER SYSTEM RESET archive_mode', 'ALTER SYSTEM RESET archive_command', 'ALTER SYSTEM RESET archive_timeout'
}
if ($statements) { $null = Invoke-Sql (($statements | ForEach-Object { "$_;" }) -join "`n") 'change the replication settings' }

if ($StandbyAddress) {
    $slot = ConvertTo-SqlLiteral $SlotName
    $null = Invoke-Sql "SELECT pg_create_physical_replication_slot($slot, true) WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name = $slot)" "make the replication slot $SlotName"

    # How far behind the standby is, in time, for System health: HC360's own account reads it through this function.
    if ((Invoke-Sql "SELECT count(*) FROM pg_database WHERE datname = $(ConvertTo-SqlLiteral $DatabaseName)" "look for the database $DatabaseName") -eq '1') {
        Write-Step "Letting HC360 measure how far behind the standby is (public.hc360_replication_status() in $DatabaseName)"
        $null = Invoke-Sql (Get-ReplicationStatusSql) 'make the function HC360 reads the standby''s lag through' $DatabaseName
    }
    else {
        Write-Warning "There is no database $DatabaseName here, so System health cannot measure how far behind the standby is in time. Run this script again with -DatabaseName <the hospital database>."
    }
}

# ---------------------------------------------------------------- the base backup account
$identBefore = Read-TextLines $postgres.IdentFile
if ($archiving) {
    Write-Step "Letting HC360 take base backups as $BaseBackupRole, signed in as $HC360ServiceAccount"
    $null = Invoke-Sql @"
DO `$`$ BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = '$BaseBackupRole') THEN
    ALTER ROLE $BaseBackupRole WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS PASSWORD NULL;
  ELSE
    CREATE ROLE $BaseBackupRole WITH LOGIN REPLICATION NOINHERIT PASSWORD NULL;
  END IF;
END `$`$;
"@ "create the account $BaseBackupRole"
    $identAfter = Set-ReplicationIdentBlock $identBefore $BaseBackupMap $hc360Sspi $BaseBackupRole
    if (($identAfter -join "`n") -cne ($identBefore -join "`n")) { Write-TextLines $postgres.IdentFile $identAfter }
}
elseif ($NoWalArchive) {
    Remove-BaseBackupAccess $postgres
}

# ---------------------------------------------------------------- pg_hba.conf and the firewall
$hba = Read-TextLines $postgres.HbaFile
# The standby's rule stays when this run is only about the archive, and the base backups' while the WAL is archived.
$standbyInBlock = if ($StandbyAddress) { $StandbyAddress } else { Get-ReplicationStandbyAddress $hba $ReplicationRole }
$baseBackupInBlock = if ($archiving) { $BaseBackupRole } else { '' }
Write-Step $(if ($StandbyAddress) { "Letting $StandbyAddress stream over TLS (pg_hba.conf)" } else { 'Writing the replication rules (pg_hba.conf)' })
$hbaAfter = Set-ReplicationHbaBlock $hba $ReplicationRole $standbyInBlock $baseBackupInBlock $BaseBackupMap
if (($hbaAfter -join "`n") -cne ($hba -join "`n")) { Write-TextLines $postgres.HbaFile $hbaAfter }
foreach ($rule in (Get-OtherReplicationRules $hbaAfter)) {
    Write-Warning "pg_hba.conf also takes replication connections with this rule, which this script did not write: $rule. Remove it unless something else needs it."
}

# PostgreSQL reads pg_hba.conf, pg_ident.conf and the TLS files again now. It would not start again with a mistake in any
# of them, so each is checked before any restart, and put back as it was if PostgreSQL cannot use it.
$null = Invoke-Sql 'SELECT pg_reload_conf()' 'reload its configuration'
Start-Sleep -Seconds 1
$errors = @(Invoke-Sql "SELECT 'pg_hba.conf line ' || line_number || ': ' || error FROM pg_hba_file_rules WHERE error IS NOT NULL" 'read pg_hba.conf back' | Where-Object { $_ })
if ([int] (Get-Setting 'server_version_num') -ge 150000) {
    $errors += @(Invoke-Sql "SELECT 'pg_ident.conf line ' || line_number || ': ' || error FROM pg_ident_file_mappings WHERE error IS NOT NULL" 'read pg_ident.conf back' | Where-Object { $_ })
}
if ($errors) {
    Write-TextLines $postgres.HbaFile $hba
    Write-TextLines $postgres.IdentFile $identBefore
    $null = Invoke-Sql 'SELECT pg_reload_conf()' 'reload its configuration'
    throw "PostgreSQL could not read pg_hba.conf or pg_ident.conf, which are back as they were: $($errors -join '; ')"
}
if ($StandbyAddress) {
    $env:PGSSLMODE = 'require'
    try { $tls = (Invoke-Sql 'SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()' 'connect over TLS') -eq 't' }
    catch { $tls = $false }
    finally { Remove-Item Env:\PGSSLMODE -ErrorAction SilentlyContinue }
    if (-not $tls) {
        $null = Invoke-Sql "ALTER SYSTEM RESET ssl;`nSELECT pg_reload_conf();" 'turn TLS off again'
        throw "PostgreSQL could not use the TLS certificate $certificatePath and its key, so TLS is off again. PostgreSQL's log, in the log folder of $data, says why: often its service account ($($service.StartName)) cannot read $keyPath."
    }

    Write-Step "Opening port $Port in Windows Firewall for $StandbyAddress only (Private and Domain networks)"
    Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'HC360 PostgreSQL replication (warm standby)' -Group $FirewallGroup -Direction Inbound -Protocol TCP -LocalPort $Port `
        -RemoteAddress $StandbyAddress -Program (Join-Path $bin 'postgres.exe') -Profile Private, Domain -Action Allow | Out-Null
}

Restart-PostgresIfNeeded $postgres

# ---------------------------------------------------------------- checks
if ($StandbyAddress -and (Get-Setting 'ssl') -ne 'on') { throw 'PostgreSQL did not turn TLS on: its log says why.' }

if ($archiveRequested) {
    # One file now, to see that PostgreSQL's account can write the archive. The changes above wrote WAL, so there is a
    # file to close.
    $before = [long] (Invoke-Sql 'SELECT archived_count FROM pg_stat_archiver' 'read the archiver')
    $null = Invoke-Sql 'SELECT pg_switch_wal()' 'start a new WAL file'
    $archived = $false
    for ($i = 0; $i -lt 30 -and -not $archived; $i++) {
        Start-Sleep -Seconds 1
        $archived = [long] (Invoke-Sql 'SELECT archived_count FROM pg_stat_archiver' 'read the archiver') -gt $before
    }
    if ($archived) { Write-Host "    PostgreSQL archives the WAL to $WalArchiveDirectory." }
    else { Write-Warning "PostgreSQL has not archived a WAL file to $WalArchiveDirectory yet. Its log says why: often its service account ($($service.StartName)) cannot write there. System health warns while archiving fails." }
}

Write-Host ''
if ($archiving) {
    Write-Host "PostgreSQL archives the WAL, at least every 15 minutes, for a restore to any moment." -ForegroundColor Green
    Write-Host '    HC360 encrypts each WAL file with the backup recovery key within a minute, and copies it to the backups'' secondary directory.'
    Write-Host "    It takes a base backup within a quarter of an hour, and then every Sunday at 03:30, signed in as $BaseBackupRole"
    Write-Host '    (its service''s Windows account, no password), encrypted the same way. System health says when either fails.'
}
elseif ($NoWalArchive) {
    Write-Host 'PostgreSQL no longer archives the WAL, and HC360 no longer takes base backups. Those already made stay in its backup folder.' -ForegroundColor Green
}
if ($StandbyAddress) {
    $fingerprint = Get-CertificateFingerprint $certificatePath
    Write-Host "PostgreSQL takes the warm standby at $StandbyAddress, on ${ListenAddress}:$Port over TLS." -ForegroundColor Green
    Write-Host "Copy $certificatePath to the standby (it holds no secret), then run there:"
    Write-Host "    .\New-HC360Standby.ps1 -PrimaryAddress $ListenAddress -PrimaryCertificate <the copy of $CertificateFile>"
    Write-Host 'Its fingerprint, which New-HC360Standby.ps1 shows too:'
    Write-Host "    $fingerprint"
    $connected = @(Invoke-Sql "SELECT client_addr || ' ' || state FROM pg_stat_replication WHERE application_name = $slot" 'read the standby''s state' | Where-Object { $_ })
    if ($connected) { Write-Host "The standby is connected: $($connected -join '; ')." }
}
