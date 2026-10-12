#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Lets one warm standby Server Node stream this Server Node's PostgreSQL (SRS DED-20), and optionally archives the WAL
    for a recovery point within 15 minutes (SRS REL-02). Run it on the primary, the Server Node HC360 runs on.

.DESCRIPTION
    PostgreSQL stays closed to everything else: it listens on this machine's address towards the standby as well as
    localhost, and takes from the standby's address alone, over TLS, a replication connection of one account with a
    password. Nothing else can sign in from the network. In detail:
      - Creates the account hc360_replicator (REPLICATION, no other rights). Its password is turned into a SCRAM verifier
        here, so the password itself never reaches PostgreSQL, a command line or a log.
      - Makes sure wal_level is replica (PostgreSQL's default), with enough WAL senders and replication slots.
      - Makes the physical replication slot the standby streams through (hc360_standby): this server keeps the WAL the
        standby still needs while it is away, up to -StandbyWalLimitGB (max_slot_wal_keep_size), so a standby that is gone
        for good cannot fill the disk. System health warns while the standby is away.
      - Turns on TLS (ssl = on) with a certificate of its own, made here (hc360-server.crt and hc360-server.key in the data
        folder; the key readable only by PostgreSQL's service account and Administrators). The standby trusts exactly this
        certificate: copy hc360-server.crt to it.
      - Adds this machine's address towards the standby to listen_addresses, keeping localhost.
      - Writes one block in pg_hba.conf, between "# BEGIN HC360 replication" and "# END HC360 replication":
        hostssl replication hc360_replicator <standby address>/32 scram-sha-256.
      - Opens TCP 5432 in Windows Firewall for PostgreSQL, from the standby's address only, on Private and Domain networks.
      - With -WalArchiveDirectory, archives every finished WAL file there (archive_mode, archive_command, archive_timeout of
        15 minutes). Off by default. -NoWalArchive turns it off again.
    PostgreSQL then restarts when a setting needs it (listen_addresses, wal_level, archive_mode): HC360 is stopped for those
    seconds and started again.

    Run it again at any time: it writes the same settings, and a new password when one is given. -Remove takes everything
    out again: the slot, the account, the pg_hba.conf block, the firewall rule, the address and TLS, and the archiving it
    set up. Then New-HC360Standby.ps1 on the standby (docs/deployment/DEPLOYMENT.md, "Warm standby").

.EXAMPLE
    .\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12

.EXAMPLE
    # With WAL archiving to the second disk
    .\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12 -WalArchiveDirectory D:\HC360-WAL

.EXAMPLE
    .\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12 -Remove
#>
[CmdletBinding()]
param(
    # The standby's fixed IP address (a DHCP reservation).
    [Parameter(Mandatory)] [string] $StandbyAddress,
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
    # A folder on another disk, or a share, for the WAL archive. Off by default.
    [string] $WalArchiveDirectory,
    [switch] $NoWalArchive,
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ReplicationRole = 'hc360_replicator'
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

# pg_hba.conf with the block that takes the replication account from $StandbyAddress alone, over TLS, with its password.
# It goes before the first rule for replication connections, or last when there is none: PostgreSQL follows the first
# rule a connection matches, and rules for "all" databases do not match replication. It is marked apart from
# Install-HC360Server.ps1's block, which that installer writes again on every run.
function Set-ReplicationHbaBlock([string[]] $Lines, [string] $Role, [string] $StandbyAddress) {
    if ($Role -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "The account '$Role' cannot be named in pg_hba.conf as it is." }
    $address = [System.Net.IPAddress]::Parse($StandbyAddress)
    $prefix = if ($address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) { 128 } else { 32 }
    $block = @(
        '# BEGIN HC360 replication. Enable-HC360Replication.ps1 writes this block on every run: changes to it are lost.',
        "# The warm standby streams this server's WAL as $Role, from its address only, over TLS, with its password.",
        "hostssl   replication   $Role   $address/$prefix   scram-sha-256",
        '# END HC360 replication'
    )
    $rest = Remove-ReplicationHbaBlock $Lines
    $at = $rest.Count
    for ($i = 0; $i -lt $rest.Count; $i++) {
        $fields = @($rest[$i].Trim() -split '\s+')
        if ($rest[$i] -notmatch '^\s*(#|$)' -and $fields.Count -ge 2 -and (@($fields[1] -split ',') -contains 'replication')) { $at = $i; break }
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

# The archive_command that copies a finished WAL file to $Directory: through a temporary name, so the archive never holds
# part of a file under its real name, and never over a file already there. A file archived before with the same content
# counts as archived (PostgreSQL may ask again after a crash); one with other content fails, so PostgreSQL keeps the WAL,
# retries, and System health warns.
function Get-ArchiveCommand([string] $Directory) {
    if (-not [System.IO.Path]::IsPathRooted($Directory) -or $Directory.IndexOfAny([char[]] '"%^&|<>') -ge 0) {
        throw "Give the WAL archive folder as a full path (D:\HC360-WAL or \\NAS\HC360-WAL), without quotes or any of % ^ & | < >."
    }
    $dir = $Directory.TrimEnd('\')
    return "if exist `"$dir\%f`" (fc /b `"%p`" `"$dir\%f`" >nul) else (copy /b /y `"%p`" `"$dir\%f.tmp`" >nul && move /y `"$dir\%f.tmp`" `"$dir\%f`" >nul)"
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

# ---------------------------------------------------------------- PostgreSQL

# Runs SQL as the superuser on this machine's PostgreSQL. The SQL goes through standard input, so nothing in it shows in
# the process list, and $Description, not the SQL, is in the error.
function Invoke-Sql([string] $Sql, [string] $Description) {
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    try {
        $output = $Sql | & $psql -X -h localhost -p $Port -U $PostgresSuperuser -d postgres -v ON_ERROR_STOP=1 -q -A -t -f -
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
    $pending = @(Invoke-Sql 'SELECT name FROM pg_settings WHERE pending_restart' 'read the settings that wait for a restart' | Where-Object { $_ })
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

# ---------------------------------------------------------------- checks (nothing changes before these pass)
Write-Step 'Checking PostgreSQL and the addresses'
$standbyIp = $null
if (-not [System.Net.IPAddress]::TryParse($StandbyAddress, [ref] $standbyIp) -or $standbyIp.Equals([System.Net.IPAddress]::Any) -or $standbyIp.Equals([System.Net.IPAddress]::IPv6Any)) {
    throw "Give the standby's IP address, such as 10.0.5.12, not '$StandbyAddress': pg_hba.conf and the firewall take addresses."
}
$StandbyAddress = $standbyIp.ToString()

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

if (-not $Remove) {
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

    if ($WalArchiveDirectory) {
        if ($NoWalArchive) { throw 'Give -WalArchiveDirectory or -NoWalArchive, not both.' }
        $archiveCommand = Get-ArchiveCommand $WalArchiveDirectory
    }
}

# ---------------------------------------------------------------- remove
if ($Remove) {
    Write-Step 'Removing the warm standby''s access'
    $slot = ConvertTo-SqlLiteral $SlotName
    $null = Invoke-Sql "SELECT pg_terminate_backend(active_pid) FROM pg_replication_slots WHERE slot_name = $slot AND active_pid IS NOT NULL" 'disconnect the standby'
    Start-Sleep -Seconds 1
    $null = Invoke-Sql "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = $slot" "drop the replication slot $SlotName"
    $null = Invoke-Sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '$ReplicationRole'" 'disconnect the replication account'
    $null = Invoke-Sql "DROP ROLE IF EXISTS $ReplicationRole" "drop the account $ReplicationRole"

    $hba = Read-TextLines $postgres.HbaFile
    $withoutBlock = Remove-ReplicationHbaBlock $hba
    if (($withoutBlock -join "`n") -cne ($hba -join "`n")) { Write-TextLines $postgres.HbaFile $withoutBlock }
    Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule

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
    if (Test-OwnArchiveCommand (Get-Setting 'archive_command')) {
        $statements += 'ALTER SYSTEM RESET archive_mode', 'ALTER SYSTEM RESET archive_command', 'ALTER SYSTEM RESET archive_timeout'
    }
    $null = Invoke-Sql (($statements | ForEach-Object { "$_;" }) -join "`n") 'reset the replication settings'
    Restart-PostgresIfNeeded $postgres
    Write-Host ''
    Write-Host "The standby at $StandbyAddress can no longer stream from this server, and PostgreSQL listens on $(Get-Setting 'listen_addresses') only." -ForegroundColor Green
    Write-Host "    The certificate files $CertificateFile and $KeyFile stay in $data; delete them if they are not needed again."
    return
}

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

Write-Step 'Setting up the WAL for a standby'
$statements = @()
if ((Get-Setting 'wal_level') -eq 'minimal') { $statements += "ALTER SYSTEM SET wal_level = 'replica'" }
if ([int] (Get-Setting 'max_wal_senders') -lt 10) { $statements += 'ALTER SYSTEM SET max_wal_senders = 10' }
if ([int] (Get-Setting 'max_replication_slots') -lt 10) { $statements += 'ALTER SYSTEM SET max_replication_slots = 10' }
$statements += "ALTER SYSTEM SET max_slot_wal_keep_size = '${StandbyWalLimitGB}GB'"
# Left behind on a standby that was promoted and is now the primary.
$statements += 'ALTER SYSTEM RESET primary_conninfo', 'ALTER SYSTEM RESET primary_slot_name', 'ALTER SYSTEM RESET restore_command'

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

# ---------------------------------------------------------------- listening, archiving
$listen = Get-Setting 'listen_addresses'
$newListen = Add-ListenAddress $listen $ListenAddress
if ($newListen -ne $listen) { $statements += "ALTER SYSTEM SET listen_addresses = $(ConvertTo-SqlLiteral $newListen)" }
if (@($newListen -split ',' | ForEach-Object { $_.Trim() }) -contains '*') {
    Write-Warning "PostgreSQL listens on every address (listen_addresses = '*'). pg_hba.conf and the firewall still take only the standby, but listen_addresses = 'localhost,$ListenAddress' is enough."
}

if ($WalArchiveDirectory) {
    Write-Step "Archiving the WAL to $WalArchiveDirectory"
    if (-not $WalArchiveDirectory.StartsWith('\\')) {
        New-Item -ItemType Directory -Force -Path $WalArchiveDirectory | Out-Null
        # PostgreSQL's service writes the archive, and nobody else needs to: the WAL holds patient data.
        $acl = Get-Acl -LiteralPath $WalArchiveDirectory
        $inherit = [System.Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule -ArgumentList (New-Object System.Security.Principal.SecurityIdentifier $serviceSid), 'Modify', $inherit, 'None', 'Allow'))
        Set-Acl -LiteralPath $WalArchiveDirectory -AclObject $acl
    }
    elseif (-not (Test-Path -LiteralPath $WalArchiveDirectory)) {
        throw "The share $WalArchiveDirectory cannot be reached from this machine."
    }
    $statements += 'ALTER SYSTEM SET archive_mode = on', "ALTER SYSTEM SET archive_command = $(ConvertTo-SqlLiteral $archiveCommand)", "ALTER SYSTEM SET archive_timeout = '15min'"
}
elseif ($NoWalArchive -and (Test-OwnArchiveCommand (Get-Setting 'archive_command'))) {
    Write-Step 'Turning WAL archiving off'
    $statements += 'ALTER SYSTEM RESET archive_mode', 'ALTER SYSTEM RESET archive_command', 'ALTER SYSTEM RESET archive_timeout'
}
$null = Invoke-Sql (($statements | ForEach-Object { "$_;" }) -join "`n") 'change the replication settings'

$slot = ConvertTo-SqlLiteral $SlotName
$null = Invoke-Sql "SELECT pg_create_physical_replication_slot($slot, true) WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name = $slot)" "make the replication slot $SlotName"

# ---------------------------------------------------------------- pg_hba.conf and the firewall
Write-Step "Letting $StandbyAddress stream over TLS (pg_hba.conf)"
$hba = Read-TextLines $postgres.HbaFile
$hbaAfter = Set-ReplicationHbaBlock $hba $ReplicationRole $StandbyAddress
if (($hbaAfter -join "`n") -cne ($hba -join "`n")) { Write-TextLines $postgres.HbaFile $hbaAfter }
foreach ($rule in (Get-OtherReplicationRules $hbaAfter)) {
    Write-Warning "pg_hba.conf also takes replication connections with this rule, which this script did not write: $rule. Remove it unless something else needs it."
}

# PostgreSQL reads pg_hba.conf and the TLS files again now. It would not start again with a mistake in either, so each is
# checked before any restart, and put back as it was if PostgreSQL cannot use it.
$null = Invoke-Sql 'SELECT pg_reload_conf()' 'reload its configuration'
Start-Sleep -Seconds 1
$errors = @(Invoke-Sql "SELECT 'pg_hba.conf line ' || line_number || ': ' || error FROM pg_hba_file_rules WHERE error IS NOT NULL" 'read pg_hba.conf back' | Where-Object { $_ })
if ($errors) {
    Write-TextLines $postgres.HbaFile $hba
    $null = Invoke-Sql 'SELECT pg_reload_conf()' 'reload its configuration'
    throw "PostgreSQL could not read pg_hba.conf, which is back as it was: $($errors -join '; ')"
}
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

Restart-PostgresIfNeeded $postgres

# ---------------------------------------------------------------- checks
if ((Get-Setting 'ssl') -ne 'on') { throw 'PostgreSQL did not turn TLS on: its log says why.' }

if ($WalArchiveDirectory) {
    # One file now, to see that PostgreSQL's account can write the archive. The account's change above wrote WAL, so
    # there is a file to close.
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

$fingerprint = Get-CertificateFingerprint $certificatePath
Write-Host ''
Write-Host "PostgreSQL takes the warm standby at $StandbyAddress, on ${ListenAddress}:$Port over TLS." -ForegroundColor Green
Write-Host "Copy $certificatePath to the standby (it holds no secret), then run there:"
Write-Host "    .\New-HC360Standby.ps1 -PrimaryAddress $ListenAddress -PrimaryCertificate <the copy of $CertificateFile>"
Write-Host 'Its fingerprint, which New-HC360Standby.ps1 shows too:'
Write-Host "    $fingerprint"
$connected = @(Invoke-Sql "SELECT client_addr || ' ' || state FROM pg_stat_replication WHERE application_name = $slot" 'read the standby''s state' | Where-Object { $_ })
if ($connected) { Write-Host "The standby is connected: $($connected -join '; ')." }
