<#
.SYNOPSIS
    Makes a warm standby of the CI runner's PostgreSQL with the scripts themselves, Enable-HC360Replication.ps1 and
    New-HC360Standby.ps1, run in Windows PowerShell 5.1 as on a Server Node, then checks that it streams over TLS, that
    HC360 refuses to run against it, that it can be promoted, and that -Remove closes everything again (SRS DED-20, REL-02).

.DESCRIPTION
    The runner's own PostgreSQL service is the primary (port 5432). The standby is a second PostgreSQL on the same machine:
    a new cluster (initdb) on port 5433, registered as a Windows service under the same account, as Install-PostgreSQL.ps1
    leaves a standby machine. Both machines' address is 127.0.0.1. The primary's pg_hba.conf is first made as
    Install-PostgreSQL.ps1 leaves it (no rules for replication), and listen_addresses localhost only.
    Then, in order:
      - Enable-HC360Replication.ps1 on the primary, with WAL archiving to a folder; and again, which changes nothing.
      - New-HC360Standby.ps1 for the standby, which reads the primary's archive too.
      - A row written on the primary is read on the standby; the standby takes no write; the primary takes the replication
        account over TLS only, and for replication only.
      - The built HC360 server, given the standby's database, stops at once and says why.
      - The standby is promoted, and takes writes.
      - Enable-HC360Replication.ps1 -Remove on the primary.
    Needs administrator rights and a PostgreSQL service it may restart: CI runs it last in its Windows job. Everything it
    makes is removed again. Exit code 1 when a check fails.

.PARAMETER Server
    The built HyFib.HealthCare360.Server.exe.
#>
param([Parameter(Mandatory)] [string] $Server)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Install-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$functions = 'Get-PgCtlDataDirectory', 'Get-PostgresSetting', 'Read-TextLines', 'Write-TextLines', 'Find-PostgresService', 'Select-PostgresService', 'Test-StandbyDatabase'
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

$primary = Find-PostgresService 5432
if (-not $primary) { throw 'There is no running PostgreSQL service on port 5432.' }
$primaryService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($primary.Name)'"
$bin = Split-Path -Parent ([regex]::Match($primaryService.PathName, '^\s*"?([^"]*pg_ctl(?:\.exe)?)').Groups[1].Value)
$superuser = if ($env:PGUSER) { $env:PGUSER } else { 'postgres' }
$superPassword = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { 'root' }
$replicationPassword = 'Ci-replication-' + [guid]::NewGuid().ToString('N')
$base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$work = Join-Path $base ('hc360-standby-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$standbyRoot = Join-Path $work 'PostgreSQL'
$standbyData = Join-Path $standbyRoot 'data'
$archive = Join-Path $work 'wal-archive'
$standbyName = 'hc360-standby-ci'
$standbyPort = 5433
$hbaBefore = [System.IO.File]::ReadAllBytes($primary.HbaFile)
Write-Host "Primary: PostgreSQL service $($primary.Name) ($($primaryService.StartName)), data folder $($primary.DataDirectory)"

function Invoke-Sql([int] $Port, [string] $Sql) {
    $env:PGPASSWORD = $superPassword
    $out = & (Join-Path $bin 'psql.exe') -X -h localhost -p $Port -U $superuser -d postgres -v ON_ERROR_STOP=1 -q -A -t -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed on port ${Port}: $Sql" }
    return $out
}

# The exit code of psql with $ConnInfo running $Sql, its errors shown; the password from $Password, for this call only.
function Test-SignIn([string] $ConnInfo, [string] $Sql, [string] $Password) {
    $env:PGPASSWORD = $Password
    try { & (Join-Path $bin 'psql.exe') -X -d $ConnInfo -A -t -c $Sql | Out-Null; return $LASTEXITCODE }
    finally { $env:PGPASSWORD = $superPassword }
}

function Restart-Primary {
    Restart-Service -Name $primary.Name -Force
    & (Join-Path $bin 'pg_isready.exe') -h localhost -p 5432 -t 60 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The primary did not take connections within a minute of restarting.' }
}

# Runs a deploy script in Windows PowerShell 5.1 with no way to ask for anything, as an administrator would run it, and
# returns its exit code. $Secrets become SecureString parameters, from environment variables of that run alone.
function Invoke-DeployScript([string] $Script, [string] $Arguments, [hashtable] $Secrets) {
    $path = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\deploy\windows\$Script")).Path
    $secretArguments = @($Secrets.Keys | ForEach-Object { "-$_ (ConvertTo-SecureString `$env:HC360_CI_$_ -AsPlainText -Force)" }) -join ' '
    $command = "& '$path' $Arguments $secretArguments; exit 0"
    foreach ($name in $Secrets.Keys) { Set-Item -Path "Env:\HC360_CI_$name" -Value $Secrets[$name] }
    try {
        Write-Host "  run   $Script $Arguments"
        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand ([Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($command))) |
            ForEach-Object { Write-Host "        $_" }
        return $LASTEXITCODE
    }
    finally {
        foreach ($name in $Secrets.Keys) { Remove-Item -Path "Env:\HC360_CI_$name" -ErrorAction SilentlyContinue }
    }
}

$superuserSecret = @{ PostgresSuperuserPassword = $superPassword }
$bothSecrets = @{ PostgresSuperuserPassword = $superPassword; ReplicationPassword = $replicationPassword }
try {
    Write-Host 'The primary, as Install-PostgreSQL.ps1 leaves it'
    # Install-PostgreSQL.ps1 writes pg_hba.conf without rules for replication, which the runner's has, and listens on
    # localhost only.
    $hba = foreach ($line in (Read-TextLines $primary.HbaFile)) { if ($line -match '^\s*(local|host\w*)\s+replication\s') { "#ci $line" } else { $line } }
    Write-TextLines $primary.HbaFile $hba
    $null = Invoke-Sql 5432 "ALTER SYSTEM SET listen_addresses = 'localhost'"
    Restart-Primary
    Assert ((Invoke-Sql 5432 'SHOW listen_addresses') -eq 'localhost') 'it listens on localhost only'

    Write-Host 'Enable-HC360Replication.ps1 on the primary'
    $enable = "-StandbyAddress 127.0.0.1 -ListenAddress 127.0.0.1 -Port 5432 -PostgresSuperuser $superuser -WalArchiveDirectory '$archive'"
    Assert ((Invoke-DeployScript 'Enable-HC360Replication.ps1' $enable $bothSecrets) -eq 0) 'it runs to the end'
    Assert ((Invoke-Sql 5432 'SHOW listen_addresses') -eq 'localhost,127.0.0.1') 'PostgreSQL listens on the address towards the standby too, after a restart'
    Assert ((Invoke-Sql 5432 'SHOW ssl') -eq 'on') 'TLS is on'
    Assert ((Invoke-Sql 5432 "SELECT rolreplication::text || rolsuper::text || rolcanlogin::text || (rolpassword LIKE 'SCRAM-SHA-256`$4096:%')::text FROM pg_authid WHERE rolname = 'hc360_replicator'") -eq 'truefalsetruetrue') 'the replication account may replicate and sign in, nothing more, with a SCRAM verifier'
    Assert ((Invoke-Sql 5432 "SELECT active::text || wal_status FROM pg_replication_slots WHERE slot_name = 'hc360_standby'") -eq 'falsereserved') 'the slot keeps WAL for the standby'
    Assert ((Invoke-Sql 5432 'SHOW max_slot_wal_keep_size') -eq '20GB') 'up to 20 GB'
    Assert ((Invoke-Sql 5432 "SELECT type || ' ' || array_to_string(database, ',') || ' ' || array_to_string(user_name, ',') || ' ' || address || ' ' || auth_method FROM pg_hba_file_rules WHERE 'replication' = ANY (database)") -eq 'hostssl replication hc360_replicator 127.0.0.1 scram-sha-256') 'pg_hba.conf takes replication from the standby alone, over TLS, with a password'
    $rule = Get-NetFirewallRule -Group 'HyFib HealthCare360 replication'
    $address = $rule | Get-NetFirewallAddressFilter
    $port = $rule | Get-NetFirewallPortFilter
    Assert (@($rule).Count -eq 1 -and "$($address.RemoteAddress)" -eq '127.0.0.1' -and "$($port.LocalPort)" -eq '5432' -and "$($rule.Profile)" -eq 'Domain, Private') 'Windows Firewall opens 5432 to the standby''s address alone, on Private and Domain networks'
    $keyAcl = Get-Acl -LiteralPath (Join-Path $primary.DataDirectory 'hc360-server.key')
    $readers = @($keyAcl.Access | ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique)
    $serviceSid = if ($primaryService.StartName -eq 'LocalSystem') { 'S-1-5-18' } else { (New-Object System.Security.Principal.NTAccount $primaryService.StartName).Translate([System.Security.Principal.SecurityIdentifier]).Value }
    Assert ($keyAcl.AreAccessRulesProtected -and -not @($readers | Where-Object { $_ -notin @('S-1-5-18', 'S-1-5-32-544', $serviceSid) })) "the TLS key is readable by SYSTEM, Administrators and PostgreSQL's account alone (found: $($readers -join ', '))"
    $archived = @(Get-ChildItem -LiteralPath $archive -File | Where-Object { $_.Name -match '^[0-9A-F]{24}$' })
    Assert ($archived.Count -ge 1) "WAL is archived ($($archived.Count) file(s) in the archive)"
    Assert ((Invoke-Sql 5432 'SELECT failed_count FROM pg_stat_archiver') -eq '0') 'with no failure'
    Assert ((Invoke-DeployScript 'Enable-HC360Replication.ps1' $enable $bothSecrets) -eq 0) 'it runs again'
    Assert (@((Read-TextLines $primary.HbaFile) | Where-Object { $_ -like '# BEGIN HC360 replication*' }).Count -eq 1) 'and pg_hba.conf still has one block'

    Write-Host 'A standby machine, as Install-PostgreSQL.ps1 leaves it'
    New-Item -ItemType Directory -Force -Path $standbyRoot | Out-Null
    $passwordFile = Join-Path $work 'initdb-password.txt'
    [System.IO.File]::WriteAllText($passwordFile, $superPassword)
    & (Join-Path $bin 'initdb.exe') -D $standbyData -U $superuser "--pwfile=$passwordFile" -A scram-sha-256 -E UTF8 --locale=C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed.' }
    Remove-Item -LiteralPath $passwordFile
    [System.IO.File]::AppendAllText((Join-Path $standbyData 'postgresql.conf'), "`nport = $standbyPort`nlisten_addresses = 'localhost'`n")
    & icacls.exe $standbyData /grant "*$($serviceSid):(OI)(CI)F" /T /Q | Out-Null
    $account = if ($primaryService.StartName -eq 'LocalSystem') { @() } else { @('-U', $primaryService.StartName) }
    & (Join-Path $bin 'pg_ctl.exe') register -N $standbyName -D $standbyData -S auto @account | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'pg_ctl register failed.' }
    Start-Service -Name $standbyName
    & (Join-Path $bin 'pg_isready.exe') -h localhost -p $standbyPort -t 60 | Out-Null
    Assert ($LASTEXITCODE -eq 0) "its PostgreSQL runs as the service $standbyName on port $standbyPort"

    Write-Host 'New-HC360Standby.ps1 on the standby'
    $certificate = Join-Path $primary.DataDirectory 'hc360-server.crt'
    $arguments = "-PrimaryAddress 127.0.0.1 -PrimaryPort 5432 -Port $standbyPort -PrimaryCertificate '$certificate' -PostgresSuperuser $superuser -WalArchiveDirectory '$archive'"
    Assert ((Invoke-DeployScript 'New-HC360Standby.ps1' $arguments $bothSecrets) -eq 0) 'it runs to the end'
    Assert ((Invoke-Sql $standbyPort 'SELECT pg_is_in_recovery()') -eq 't') 'the standby is in recovery: read-only, following the primary'
    Assert ((Invoke-Sql $standbyPort "SELECT status || ' ' || slot_name FROM pg_stat_wal_receiver") -eq 'streaming hc360_standby') 'it streams through the slot'
    Assert (Wait-Until { (Invoke-Sql 5432 "SELECT r.state || ' ' || s.ssl::text || ' ' || r.usename FROM pg_stat_replication r JOIN pg_stat_ssl s USING (pid) WHERE r.application_name = 'hc360_standby'") -eq 'streaming true hc360_replicator' } 30) 'the primary streams to it over TLS, as the replication account'
    Assert ((Invoke-Sql 5432 "SELECT active FROM pg_replication_slots WHERE slot_name = 'hc360_standby'") -eq 't') 'the slot is in use'
    $conninfo = Invoke-Sql $standbyPort 'SHOW primary_conninfo'
    Assert ($conninfo -like "*sslmode='verify-ca'*" -and $conninfo -like '*hc360-standby/replication.pgpass*' -and $conninfo -notlike '*hc360-replication-*') 'it trusts the primary''s certificate alone, and reads its password from its own file, not the one used meanwhile'
    Assert ((Invoke-Sql $standbyPort 'SHOW listen_addresses') -eq 'localhost' -and (Invoke-Sql $standbyPort 'SHOW port') -eq "$standbyPort") 'it listens on localhost, on its own port'
    Assert ((Invoke-Sql $standbyPort 'SHOW archive_mode') -eq 'off' -and (Invoke-Sql $standbyPort 'SHOW restore_command') -like "*$archive*") 'it archives nothing, and reads the primary''s archive'
    $passFile = Join-Path $standbyRoot 'hc360-standby\replication.pgpass'
    $passReaders = @((Get-Acl -LiteralPath $passFile).Access | ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique)
    Assert (-not @($passReaders | Where-Object { $_ -notin @('S-1-5-18', 'S-1-5-32-544', $serviceSid) })) "its password file is readable by SYSTEM, Administrators and PostgreSQL's account alone (found: $($passReaders -join ', '))"
    Assert (-not (Get-ChildItem -LiteralPath $env:TEMP -Filter 'hc360-replication-*.pgpass' -ErrorAction SilentlyContinue)) 'the temporary password file is gone'
    Assert (@(Get-ChildItem -LiteralPath $standbyRoot -Directory -Filter 'data.before-standby-*').Count -eq 1) 'the data folder that was there is kept aside'
    Assert ((Get-CimInstance -ClassName Win32_Service -Filter "Name='$standbyName'").StartMode -eq 'Auto') 'its service starts automatically again'
    Assert (Test-StandbyDatabase $standbyData) 'Install-HC360Server.ps1 sees a standby there (and needs -Standby)'

    Write-Host 'What the primary writes, the standby has'
    $null = Invoke-Sql 5432 "CREATE TABLE hc360_standby_ci (id int PRIMARY KEY, note text); INSERT INTO hc360_standby_ci VALUES (1, 'written on the primary')"
    Assert (Wait-Until { try { (Invoke-Sql $standbyPort 'SELECT note FROM hc360_standby_ci WHERE id = 1') -eq 'written on the primary' } catch { $false } } 30) 'a row written on the primary is read on the standby'
    $env:PGPASSWORD = $superPassword
    & (Join-Path $bin 'psql.exe') -X -h localhost -p $standbyPort -U $superuser -d postgres -q -c "INSERT INTO hc360_standby_ci VALUES (2, 'written on the standby')" 2>$null
    Assert ($LASTEXITCODE -ne 0) 'the standby takes no write'
    $replication = "host=127.0.0.1 port=5432 user=hc360_replicator dbname=replication replication=true"
    Assert ((Test-SignIn "$replication sslmode=require" 'IDENTIFY_SYSTEM' $replicationPassword) -eq 0) 'the replication account streams over TLS'
    Assert ((Test-SignIn "$replication sslmode=disable" 'IDENTIFY_SYSTEM' $replicationPassword) -ne 0) 'not without TLS'
    Assert ((Test-SignIn 'host=127.0.0.1 port=5432 user=hc360_replicator dbname=postgres sslmode=require' 'SELECT 1' $replicationPassword) -ne 0) 'and it opens no database'
    Assert ((Test-SignIn "$replication sslmode=require" 'IDENTIFY_SYSTEM' 'not-the-password-at-all') -ne 0) 'not with another password'

    Write-Host 'HC360 against the standby'
    $hc360Data = Join-Path $work 'hc360-data'
    $variables = @{
        HealthCare360__DataDirectory = $hc360Data
        HC360_HealthCare360__DataDirectory = $hc360Data
        HC360_HealthCare360__HttpsPort = '17360'
        HC360_HealthCare360__PortalHttpsPort = '17361'
        HC360_ConnectionStrings__Hospital = "Host=localhost;Port=$standbyPort;Database=postgres;Username=$superuser;Password=$superPassword"
    }
    foreach ($name in $variables.Keys) { Set-Item -Path "Env:\$name" -Value $variables[$name] }
    try {
        $out = Join-Path $work 'hc360.out.txt'
        $err = Join-Path $work 'hc360.err.txt'
        $process = Start-Process -FilePath (Resolve-Path -LiteralPath $Server).Path -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -NoNewWindow
        # Without its handle taken now, a process started this way has no exit code once it has ended.
        $null = $process.Handle
        $stopped = $process.WaitForExit(120000)
        if (-not $stopped) { Stop-Process -Id $process.Id -Force }
        $said = (Get-Content -LiteralPath $out, $err -Raw -ErrorAction SilentlyContinue) -join "`n"
        Assert ($stopped -and $process.ExitCode -ne 0) "the server stops at start-up (exit code $(if ($stopped) { $process.ExitCode } else { 'none: still running after 2 minutes' }))"
        Assert ($said -like '*read-only warm standby*') 'and says it is a read-only warm standby'
        if ($said -notlike '*read-only warm standby*') { Write-Host $said }
    }
    finally {
        foreach ($name in $variables.Keys) { Remove-Item -Path "Env:\$name" -ErrorAction SilentlyContinue }
    }

    Write-Host 'Failing over: the standby is promoted'
    Assert ((Invoke-Sql $standbyPort 'SELECT pg_promote(true, 60)') -eq 't') 'pg_promote() finishes'
    Assert ((Invoke-Sql $standbyPort 'SELECT pg_is_in_recovery()') -eq 'f') 'the standby is a primary now'
    $null = Invoke-Sql $standbyPort "INSERT INTO hc360_standby_ci VALUES (2, 'written after the failover')"
    Assert ((Invoke-Sql $standbyPort 'SELECT count(*) FROM hc360_standby_ci') -eq '2') 'and takes writes'
    Assert (-not (Test-StandbyDatabase $standbyData)) 'Install-HC360Server.ps1 no longer sees a standby there'
    Assert (Wait-Until { (Invoke-Sql 5432 "SELECT active FROM pg_replication_slots WHERE slot_name = 'hc360_standby'") -eq 'f' } 30) 'the old primary''s slot is no longer in use'

    Write-Host 'Enable-HC360Replication.ps1 -Remove on the primary'
    Stop-Service -Name $standbyName -Force
    Assert ((Invoke-DeployScript 'Enable-HC360Replication.ps1' "-StandbyAddress 127.0.0.1 -ListenAddress 127.0.0.1 -Port 5432 -PostgresSuperuser $superuser -Remove" $superuserSecret) -eq 0) 'it runs to the end'
    Assert ((Invoke-Sql 5432 "SELECT count(*) FROM pg_replication_slots WHERE slot_name = 'hc360_standby'") -eq '0') 'the slot is gone'
    Assert ((Invoke-Sql 5432 "SELECT count(*) FROM pg_roles WHERE rolname = 'hc360_replicator'") -eq '0') 'the replication account is gone'
    Assert ((Invoke-Sql 5432 "SELECT count(*) FROM pg_hba_file_rules WHERE 'replication' = ANY (database)") -eq '0') 'pg_hba.conf takes no replication'
    Assert (-not (Get-NetFirewallRule -Group 'HyFib HealthCare360 replication' -ErrorAction SilentlyContinue)) 'the firewall rule is gone'
    Assert ((Invoke-Sql 5432 'SHOW listen_addresses') -eq 'localhost' -and (Invoke-Sql 5432 'SHOW ssl') -eq 'off' -and (Invoke-Sql 5432 'SHOW archive_mode') -eq 'off') 'PostgreSQL listens on localhost only again, without TLS or archiving'
}
finally {
    # Whatever failed, the runner's PostgreSQL is left as it was, and the standby removed.
    if (Get-Service -Name $standbyName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $standbyName -Force -ErrorAction SilentlyContinue
        & (Join-Path $bin 'pg_ctl.exe') unregister -N $standbyName | Out-Null
    }
    Get-NetFirewallRule -Group 'HyFib HealthCare360 replication' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    # pg_hba.conf first: each new connection reads it on Windows, so the statements below can sign in again.
    [System.IO.File]::WriteAllBytes($primary.HbaFile, $hbaBefore)
    try {
        $null = Invoke-Sql 5432 "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = 'hc360_standby' AND NOT active"
        $null = Invoke-Sql 5432 'DROP TABLE IF EXISTS hc360_standby_ci'
        $null = Invoke-Sql 5432 'DROP ROLE IF EXISTS hc360_replicator'
        foreach ($setting in 'listen_addresses', 'ssl', 'ssl_cert_file', 'ssl_key_file', 'archive_mode', 'archive_command', 'archive_timeout', 'max_slot_wal_keep_size') {
            $null = Invoke-Sql 5432 "ALTER SYSTEM RESET $setting"
        }
    }
    catch { Write-Warning "The runner's PostgreSQL could not be put back as it was: $($_.Exception.Message)" }
    try { Restart-Primary } catch { Write-Warning $_.Exception.Message }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
