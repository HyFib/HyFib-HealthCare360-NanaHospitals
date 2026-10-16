<#
.SYNOPSIS
    Checks the warm standby scripts' own functions (SRS DED-20, REL-02): what Enable-HC360Replication.ps1 writes in
    pg_hba.conf and postgresql.conf on the primary, the SCRAM verifier and the TLS certificate it makes, what
    New-HC360Standby.ps1 writes on the standby, and when Install-HC360Server.ps1 refuses to run on a standby. CI runs it in
    Windows PowerShell 5.1 and in PowerShell 7, the two the scripts run in.

.DESCRIPTION
    Takes the functions from the scripts themselves (running them would change PostgreSQL) and applies them to files like
    a hospital server's. Exit code 1 when a check fails. CI's Windows job then runs the scripts themselves against the
    runner's PostgreSQL and a standby of it (Test-StandbyReplication.ps1).
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ScriptFunctions([string] $Script) {
    $path = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\deploy\windows\$Script")).Path
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $null, [ref] $null)
    $found = @{}
    foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) { $found[$function.Name] = $function.Extent.Text }
    return $found
}

$installer = Get-ScriptFunctions 'Install-HC360Server.ps1'
$primary = Get-ScriptFunctions 'Enable-HC360Replication.ps1'
$standby = Get-ScriptFunctions 'New-HC360Standby.ps1'
foreach ($set in $installer, $primary, $standby) {
    foreach ($text in $set.Values) { . ([scriptblock]::Create($text)) }
}

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

function Test-Throws([scriptblock] $Action, [string] $Expected) {
    try { & $Action; return $false }
    catch { return $_.Exception.Message -like "*$Expected*" }
}

Write-Host 'The scripts find PostgreSQL as the installer does'
foreach ($name in 'Get-PgCtlDataDirectory', 'Get-PostgresSetting', 'Read-TextLines', 'Write-TextLines', 'Find-PostgresService', 'Select-PostgresService') {
    Assert ($primary.ContainsKey($name) -and $primary[$name] -ceq $installer[$name]) "Enable-HC360Replication.ps1 has the installer's $name"
    Assert ($standby.ContainsKey($name) -and $standby[$name] -ceq $installer[$name]) "New-HC360Standby.ps1 has the installer's $name"
}
foreach ($name in 'Get-PgCtlBinDirectory', 'Resolve-AccountSid', 'Get-CertificateFingerprint') {
    Assert ($primary.ContainsKey($name) -and $primary[$name] -ceq $standby[$name]) "both scripts have the same $name"
}
Assert ((Get-PgCtlBinDirectory '"C:\Program Files\PostgreSQL\17\bin\pg_ctl.exe" runservice -N "postgresql-x64-17" -D "C:\Program Files\PostgreSQL\17\data" -w') -ceq 'C:\Program Files\PostgreSQL\17\bin') 'the bin folder of the PostgreSQL Windows installer''s service'
Assert ((Get-PgCtlBinDirectory 'C:\pgsql\bin\pg_ctl.exe runservice -N pgsql -D D:\pgdata -w') -ceq 'C:\pgsql\bin') 'an unquoted one'
Assert ((Resolve-AccountSid 'LocalSystem') -ceq 'S-1-5-18' -and (Resolve-AccountSid 'NT AUTHORITY\NetworkService') -ceq 'S-1-5-20' -and (Resolve-AccountSid 'NT AUTHORITY\LocalService') -ceq 'S-1-5-19') 'the service accounts PostgreSQL runs as, by SID'

Write-Host 'The replication password and its SCRAM verifier'
Assert ($null -eq (Test-ReplicationPassword 'correct horse battery staple')) 'a long passphrase is taken'
Assert ((Test-ReplicationPassword 'short-but-safe1') -like '*at least 16 characters*') 'one under 16 characters is refused'
Assert ((Test-ReplicationPassword ('Pässwort-' * 3)) -like '*only letters*') 'one outside printable ASCII is refused (SASLprep would change it)'
Assert ((Test-ReplicationPassword "tab`there-is-not-allowed") -like '*only letters*') 'a control character is refused'
# RFC 7677, section 3: user "user", password "pencil" (as tests/HyFib.HealthCare360.Platform.Tests/DatabasePasswordTests.cs).
$salt = [Convert]::FromBase64String('W22ZaJ0SNY7soEsUEjb6gQ==')
$authMessage = [System.Text.Encoding]::UTF8.GetBytes('n=user,r=rOprNGfwEbeRWgbNEkqO,' +
    'r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,' +
    'c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0')
$keys = Get-ScramKeys 'pencil' $salt 4096
$signature = (New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (, $keys.StoredKey)).ComputeHash($authMessage)
$proof = [byte[]] (0..31 | ForEach-Object { $keys.ClientKey[$_] -bxor $signature[$_] })
Assert ([Convert]::ToBase64String($proof) -ceq 'dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=') 'the client proof is RFC 7677''s'
Assert ([Convert]::ToBase64String((New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (, $keys.ServerKey)).ComputeHash($authMessage)) -ceq '6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=') 'the server signature is RFC 7677''s'
Assert ((ConvertTo-ScramVerifier 'pencil' $salt) -ceq ('SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$' + [Convert]::ToBase64String($keys.StoredKey) + ':' + [Convert]::ToBase64String($keys.ServerKey))) 'the verifier is in the form PostgreSQL stores'
Assert ((ConvertTo-ScramVerifier 'same password, twice') -cne (ConvertTo-ScramVerifier 'same password, twice')) 'each verifier has its own salt'

Write-Host 'pg_hba.conf on the primary'
# As Install-PostgreSQL.ps1 leaves it, with Install-HC360Server.ps1's block.
$hba = Set-HbaBlock @(
    '# HyFib HealthCare360: the database is used only by the HC360 service on this machine.',
    '# TYPE  DATABASE  USER  ADDRESS        METHOD',
    'local   all       all                  scram-sha-256',
    'host    all       all   127.0.0.1/32   scram-sha-256',
    'host    all       all   ::1/128        scram-sha-256'
) 'hc360_app' 'hc360'
$rule = 'hostssl   replication   hc360_replicator   10.0.5.12/32   scram-sha-256'
$block = @('# BEGIN HC360 replication. Enable-HC360Replication.ps1 writes this block on every run: changes to it are lost.',
    '# The warm standby streams this server''s WAL as hc360_replicator, from its address only, over TLS, with its password; the account opens no database.',
    $rule,
    'host      all           hc360_replicator   all              reject',
    'local     all           hc360_replicator                    reject',
    '# END HC360 replication')
$once = Set-ReplicationHbaBlock $hba 'hc360_replicator' '10.0.5.12'
$start = [array]::IndexOf($once, $block[0])
Assert ($start -eq 9) 'the block goes after the comments at the top and the installer''s block, before the first rule'
Assert ((($once[$start..($start + 5)]) -join "`n") -ceq ($block -join "`n")) 'it takes the replication account from the standby''s address alone, over TLS, with its password, and refuses it every database'
Assert ((($once[0..($start - 1)] + $once[($start + 6)..($once.Count - 1)]) -join "`n") -ceq ($hba -join "`n")) 'the rest of the file stays as it was'
Assert (((Set-ReplicationHbaBlock $once 'hc360_replicator' '10.0.5.12') -join "`n") -ceq ($once -join "`n")) 'a second run writes the same file'
$moved = Set-ReplicationHbaBlock $once 'hc360_replicator' '10.0.5.13'
Assert (((@($moved | Where-Object { $_ -like 'hostssl*' })) -join "`n") -ceq 'hostssl   replication   hc360_replicator   10.0.5.13/32   scram-sha-256') 'a new standby address replaces the old one'
Assert (((@(Set-ReplicationHbaBlock $hba 'hc360_replicator' 'fd00::12') | Where-Object { $_ -like 'hostssl*' }) -join '') -like '*fd00::12/128*') 'an IPv6 address is one address too'
Assert (((Remove-ReplicationHbaBlock $once) -join "`n") -ceq ($hba -join "`n")) 'removing the block gives back the file as it was'
Assert ((Get-OtherReplicationRules $once).Count -eq 0) 'no other rule takes replication connections'
Assert (Test-Throws { Remove-ReplicationHbaBlock @($once | Where-Object { $_ -ne '# END HC360 replication' }) } "without its '# END HC360 replication' line") 'a block without its END line stops the script instead of losing the rules after it'
Assert (Test-Throws { Set-ReplicationHbaBlock $hba 'hc360 replicator' '10.0.5.12' } 'cannot be named') 'an account name pg_hba.conf would read differently is refused'
# PostgreSQL's own default file has rules for replication from this machine: the standby's goes before them.
$withDefaults = @($hba) + @('local   replication   all                  scram-sha-256', 'host    replication   all   127.0.0.1/32   scram-sha-256')
$beforeDefaults = Set-ReplicationHbaBlock $withDefaults 'hc360_replicator' '10.0.5.12'
Assert ([array]::IndexOf($beforeDefaults, $rule) -lt [array]::IndexOf($beforeDefaults, 'local   replication   all                  scram-sha-256')) 'it goes before the rules for replication connections, which PostgreSQL would follow first'
Assert ([array]::IndexOf($beforeDefaults, $block[3]) -lt [array]::IndexOf($beforeDefaults, 'host    all       all   127.0.0.1/32   scram-sha-256')) 'and its refusal before the rules that take every account with a password'
Assert ((Get-OtherReplicationRules $beforeDefaults).Count -eq 2) 'the other replication rules are found, for a warning'
# The installer writes its block again on every run, and must not touch this one; nor this script the installer's.
$installerAgain = Set-HbaBlock $once 'hc360_app' 'hc360'
Assert (($installerAgain -join "`n") -ceq ($once -join "`n")) 'Install-HC360Server.ps1 run again leaves the replication block as it is'
Assert (((Remove-InstallerBlock $once 'pg_hba.conf') -join "`n") -ceq ((Set-ReplicationHbaBlock (Remove-InstallerBlock $hba 'pg_hba.conf') 'hc360_replicator' '10.0.5.12') -join "`n")) 'removing the installer''s block keeps the replication block'
$replicationFirst = Set-HbaBlock (Set-ReplicationHbaBlock (@('# PostgreSQL Client Authentication', 'host replication all 10.0.0.0/8 reject') + @($hba[2..($hba.Count - 1)])) 'hc360_replicator' '10.0.5.12') 'hc360_app' 'hc360'
$begin = [array]::IndexOf($replicationFirst, ($replicationFirst | Where-Object { $_ -like '# BEGIN HC360 replication*' }))
Assert ((($replicationFirst[$begin..($begin + 5)]) -join "`n") -ceq ($block -join "`n")) 'the installer''s block never lands inside the replication block, even when that comes first'
$fresh = @('# top', 'local all all scram-sha-256')
$mineFirst = Set-HbaBlock (Set-ReplicationHbaBlock $fresh 'hc360_replicator' '10.0.5.12') 'hc360_app' 'hc360'
$installerFirst = Set-ReplicationHbaBlock (Set-HbaBlock $fresh 'hc360_app' 'hc360') 'hc360_replicator' '10.0.5.12'
Assert (($mineFirst -join "`n") -ceq ($installerFirst -join "`n")) 'run in either order, the two scripts leave the same file'
Assert (((Set-ReplicationHbaBlock (Set-HbaBlock $mineFirst 'hc360_app' 'hc360') 'hc360_replicator' '10.0.5.12') -join "`n") -ceq ($mineFirst -join "`n")) 'and run again, they change nothing'

Write-Host 'postgresql.conf on the primary'
Assert ((Add-ListenAddress 'localhost' '10.0.5.11') -ceq 'localhost,10.0.5.11') 'the address towards the standby is added, localhost kept'
Assert ((Add-ListenAddress 'localhost, 10.0.5.11' '10.0.5.11') -ceq 'localhost,10.0.5.11') 'once'
Assert ((Add-ListenAddress '*' '10.0.5.11') -ceq '*') 'every address already takes it'
Assert ((Remove-ListenAddress 'localhost,10.0.5.11' '10.0.5.11') -ceq 'localhost') 'it is taken out again'
Assert ((Remove-ListenAddress '10.0.5.11' '10.0.5.11') -ceq 'localhost') 'leaving localhost when nothing else is left'
$archive = Get-ArchiveCommand 'D:\HC360-WAL\'
Assert ($archive -ceq 'if exist "D:\HC360-WAL\%f" (fc /b "%p" "D:\HC360-WAL\%f" >nul) else (copy /b /y "%p" "D:\HC360-WAL\%f.tmp" >nul && move /y "D:\HC360-WAL\%f.tmp" "D:\HC360-WAL\%f" >nul)') 'a WAL file is copied under a temporary name, then renamed, and never over one already archived'
Assert ((Get-ArchiveCommand '\\nas\HC360 WAL') -like '*"\\nas\HC360 WAL\%f.tmp"*') 'a share, with a space in its name'
Assert ((Test-OwnArchiveCommand $archive) -and -not (Test-OwnArchiveCommand 'copy "%p" "E:\wal\%f"')) 'the script knows its own archive_command from one set by hand'
Assert (Test-Throws { Get-ArchiveCommand 'HC360-WAL' } 'full path') 'a relative folder is refused'
Assert (Test-Throws { Get-ArchiveCommand 'D:\WAL & more' } 'without quotes') 'a folder cmd.exe would read differently is refused'

Write-Host 'The TLS certificate for PostgreSQL'
$made = New-PostgresTlsCertificate 'hc360-srv PostgreSQL' @('hc360-srv', 'hc360-srv.local') @('10.0.5.11')
Assert (-not [System.Security.Cryptography.CngKey]::Exists('')) 'the key is made in memory: nothing is left in Windows'' key store'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-standby-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $certificatePath = Join-Path $root 'hc360-server.crt'
    [System.IO.File]::WriteAllText($certificatePath, $made.Certificate + "`n")
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $certificatePath
    $constraints = @($certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] })
    $usage = @($certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] })
    $names = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' })
    Assert ($certificate.Subject -ceq $certificate.Issuer) 'it is self-signed'
    Assert ($constraints.Count -eq 1 -and $constraints[0].CertificateAuthority) 'it is its own CA, so the standby can trust exactly it (sslmode=verify-ca)'
    Assert ($usage.Count -eq 1 -and @($usage[0].EnhancedKeyUsages | ForEach-Object { $_.Value }) -contains '1.3.6.1.5.5.7.3.1') 'it is for a TLS server'
    Assert ($names.Count -eq 1 -and $names[0].Format($false) -like '*hc360-srv.local*' -and $names[0].Format($false) -like '*10.0.5.11*') 'it names the machine and its address'
    Assert ($certificate.NotAfter -gt (Get-Date).AddYears(9)) 'it lasts ten years'
    Assert ($made.Key.StartsWith("-----BEGIN PRIVATE KEY-----`n") -and $made.Key.EndsWith('-----END PRIVATE KEY-----')) 'the key is PKCS#8 PEM, as OpenSSL reads it'
    Assert (@($made.Certificate -split "`n" | Where-Object { $_.Length -gt 64 }).Count -eq 0) 'PEM lines are 64 characters at most'
    $der = [Convert]::FromBase64String((($made.Key -split "`n") | Where-Object { $_ -notlike '-----*' }) -join '')
    $key = [System.Security.Cryptography.CngKey]::Import($der, [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    $modulus = ([System.Security.Cryptography.RSACng]::new($key)).ExportParameters($false).Modulus
    $public = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate).ExportParameters($false).Modulus
    Assert ([Convert]::ToBase64String($modulus) -ceq [Convert]::ToBase64String($public) -and $modulus.Length -eq 384) 'the key is the certificate''s, RSA 3072'
    Assert ((Get-CertificateFingerprint $certificatePath).Replace(' ', '') -ceq $made.Fingerprint) 'the fingerprint printed on both machines is the certificate''s SHA-256'
    $certificate.Dispose()

    Write-Host 'postgresql.auto.conf on the standby'
    Assert ((ConvertTo-ConnInfoValue "C:\Program Files\it's") -ceq "'C:\\Program Files\\it\'s'") 'a connection string value is quoted, its quote and backslashes escaped'
    $connInfo = New-PrimaryConnInfo '10.0.5.11' 5432 'hc360_replicator' 'C:\Program Files\PostgreSQL\17\hc360-standby\primary.crt' 'C:\Program Files\PostgreSQL\17\hc360-standby\replication.pgpass' 'hc360_standby'
    Assert ($connInfo -ceq "host='10.0.5.11' port='5432' user='hc360_replicator' dbname='replication' password='' passfile='C:/Program Files/PostgreSQL/17/hc360-standby/replication.pgpass' sslmode='verify-ca' sslrootcert='C:/Program Files/PostgreSQL/17/hc360-standby/primary.crt' application_name='hc360_standby' connect_timeout='10'") 'the standby connects over TLS, trusting the primary''s certificate alone, with its password from the file'
    Assert ((New-PgPassLine 'hc360_replicator' 'a:b\c d') -ceq '*:*:replication:hc360_replicator:a\:b\\c d') 'the password file escapes : and \ in the password'
    Assert ((ConvertTo-ConfValue "copy ""D:\WAL\%f"" it's") -ceq "'copy ""D:\\WAL\\%f"" it''s'") 'a postgresql.conf value doubles its quotes and escapes its backslashes, as ALTER SYSTEM writes it'
    # As pg_basebackup -R leaves it on a copy of a primary set up by Install-PostgreSQL.ps1 and Enable-HC360Replication.ps1.
    $copied = @(
        '# Do not edit this file manually!',
        '# It will be overwritten by the ALTER SYSTEM command.',
        "listen_addresses = 'localhost,10.0.5.11'",
        "shared_buffers = '8192MB'",
        "archive_mode = 'on'",
        "archive_command = 'if exist ...'",
        "ssl = 'on'",
        "primary_conninfo = 'user=hc360_replicator passfile=''C:\\Users\\admin\\AppData\\Local\\Temp\\x.pgpass'' host=10.0.5.11'",
        "primary_slot_name = 'hc360_standby'"
    )
    $ownConf = @('port = 5433', "#shared_buffers = 128MB")
    $ownAuto = @("shared_buffers = '4096MB'", "effective_cache_size = '8192MB'", "listen_addresses = 'localhost'")
    $settings = Get-StandbySettings $ownConf $ownAuto 5433 $connInfo 'hc360_standby' $null
    $written = Set-ConfSettings $copied $settings
    Assert ($written[0] -ceq '# Do not edit this file manually!') 'the file''s header stays'
    Assert ((Get-PostgresSetting @() $written 'primary_conninfo') -ceq $connInfo) 'the standby follows the primary with its own connection, not the temporary password file pg_basebackup used'
    Assert (@($written | Where-Object { $_ -like '*Temp*' }).Count -eq 0) 'nothing of the temporary password file is left'
    Assert ((Get-PostgresSetting @() $written 'listen_addresses') -ceq 'localhost') 'the standby takes connections on its own machine only'
    Assert ((Get-PostgresSetting @() $written 'port') -ceq '5433') 'on this machine''s port'
    Assert ((Get-PostgresSetting @() $written 'shared_buffers') -ceq '4096MB' -and (Get-PostgresSetting @() $written 'effective_cache_size') -ceq '8192MB') 'with the memory Install-PostgreSQL.ps1 sized for this machine'
    Assert ((Get-PostgresSetting @() $written 'archive_mode') -ceq 'off' -and $null -eq (Get-PostgresSetting @() $written 'archive_command')) 'it archives nothing: the primary does'
    Assert ((Get-PostgresSetting @() $written 'ssl') -ceq 'on') 'the rest of the primary''s settings stay'
    Assert ($null -eq (Get-PostgresSetting @() $written 'restore_command')) 'without the primary''s archive, no restore_command'
    Assert (@($written | Where-Object { $_ -match '^\s*primary_conninfo\s*=' }).Count -eq 1) 'each setting once'
    $restore = Get-RestoreCommand '\\nas\HC360-WAL\'
    Assert ($restore -ceq 'copy /b "\\nas\HC360-WAL\%f" "%p" >nul') 'with it, the standby reads WAL from the primary''s archive'
    Assert ((Get-PostgresSetting @() (Set-ConfSettings $copied (Get-StandbySettings @() @() 5432 $connInfo 'hc360_standby' $restore)) 'restore_command') -ceq 'copy /b "\\\\nas\\HC360-WAL\\%f" "%p" >nul') 'written for postgresql.conf, its backslashes escaped'

    Write-Host 'Which PostgreSQL may be replaced by a copy of the primary'
    $fresh = Join-Path $root 'fresh'
    foreach ($database in '1', '4', '5') { New-Item -ItemType Directory -Path (Join-Path $fresh "base\$database") | Out-Null }
    Assert ($null -eq (Test-ReplaceableCluster $fresh $false)) 'one as Install-PostgreSQL.ps1 left it'
    New-Item -ItemType Directory -Path (Join-Path $fresh 'base\16384') | Out-Null
    Assert ((Test-ReplaceableCluster $fresh $false) -like '*holds 1 database(s) of its own*-ReplaceExistingData*') 'not one with a database of its own, such as the primary'
    Assert ($null -eq (Test-ReplaceableCluster $fresh $true)) 'unless -ReplaceExistingData says so (the old primary after a failover)'
    [System.IO.File]::WriteAllText((Join-Path $fresh 'standby.signal'), '')
    Assert ($null -eq (Test-ReplaceableCluster $fresh $false)) 'a standby, set up again'

    Write-Host 'Install-HC360Server.ps1 on a standby'
    Assert ((Test-StandbyDatabase $fresh) -and -not (Test-StandbyDatabase (Join-Path $root 'missing'))) 'a standby''s data folder has standby.signal'
    Assert ($null -eq (Get-StandbyProblem $false $false $false 'Automatic' $true)) 'a primary installs and upgrades as before'
    Assert ($null -eq (Get-StandbyProblem $false $true $false $null $false)) 'a new primary with -CreateDatabase too'
    Assert ($null -eq (Get-StandbyProblem $true $false $true 'Disabled' $false)) '-Standby installs on a standby'
    Assert ((Get-StandbyProblem $true $true $true $null $false) -like '*-CreateDatabase cannot be given with -Standby*') 'not with -CreateDatabase'
    Assert ((Get-StandbyProblem $false $false $true 'Disabled' $false) -like '*is a warm standby*-Standby*') 'without -Standby it stops on a standby'
    Assert ((Get-StandbyProblem $false $false $false 'Disabled' $false) -like '*backup restore*') 'on a promoted standby it stops until the keys are back from the backup'
    Assert ($null -eq (Get-StandbyProblem $false $false $false 'Disabled' $true)) 'and then installs and starts HC360'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
