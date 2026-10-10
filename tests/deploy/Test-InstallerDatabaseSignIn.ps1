<#
.SYNOPSIS
    Checks how Install-HC360Server.ps1 sets up the service's Windows sign-in to PostgreSQL: the blocks it writes in
    pg_hba.conf and pg_ident.conf, and how it finds PostgreSQL's files. CI runs it in Windows PowerShell 5.1 and in
    PowerShell 7, the two the installer runs in.

.DESCRIPTION
    Takes the installer's functions from the script itself (running it would install HC360) and applies them to files
    like a hospital server's. Exit code 1 when a check fails. CI's Windows job then has PostgreSQL read blocks written by
    the same functions (Enable-TestWindowsSignIn.ps1).
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Install-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$functions = 'Remove-InstallerBlock', 'Set-HbaBlock', 'Set-IdentBlock', 'ConvertTo-SspiName', 'Get-PgCtlDataDirectory', 'Get-PostgresSetting',
    'Read-TextLines', 'Write-TextLines', 'Select-PostgresService', 'Save-SignInFiles', 'Restore-SignInFiles'
foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($functions -contains $function.Name) { . ([scriptblock]::Create($function.Extent.Text)) }
}
foreach ($name in $functions) {
    if (-not (Get-Command -Name $name -CommandType Function -ErrorAction SilentlyContinue)) { throw "Install-HC360Server.ps1 no longer defines $name." }
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

# pg_hba.conf as PostgreSQL's Windows installer leaves it, after Install-PostgreSQL.ps1.
$hba = @(
    '# PostgreSQL Client Authentication Configuration File',
    '# ===================================================',
    '',
    '# HyFib HealthCare360: the database is used only by the HC360 service on this machine.',
    '# TYPE  DATABASE  USER  ADDRESS        METHOD',
    'local   all       all                  scram-sha-256',
    'host    all       all   127.0.0.1/32   scram-sha-256',
    'host    all       all   ::1/128        scram-sha-256'
)
$rules = @(
    'local   all   hc360_app                  reject',
    'host    all   hc360_app   127.0.0.1/32   sspi   map=hc360',
    'host    all   hc360_app   ::1/128        sspi   map=hc360',
    'host    all   hc360_app   all            reject'
)

Write-Host 'The Windows account, as PostgreSQL names it'
Assert ((ConvertTo-SspiName 'NT SERVICE\HC360Server') -ceq 'HC360Server@NT SERVICE') 'the service''s virtual account is HC360Server@NT SERVICE'
Assert ((ConvertTo-SspiName 'FV-AZ12\runneradmin') -ceq 'runneradmin@FV-AZ12') 'a local account is name@COMPUTER'
Assert (Test-Throws { ConvertTo-SspiName 'HC360Server' } 'is not a Windows account written DOMAIN\name') 'an account without its domain is refused'

Write-Host 'pg_hba.conf'
$once = Set-HbaBlock $hba 'hc360_app' 'hc360'
$start = [array]::IndexOf($once, '# BEGIN HyFib HealthCare360. Install-HC360Server.ps1 writes this block on every run: changes to it are lost.')
Assert ($start -eq 5) 'the block goes before the first rule, below the comments at the top'
Assert ((($once[($start + 2)..($start + 5)]) -join "`n") -ceq ($rules -join "`n")) 'it takes hc360_app only with its Windows account (SSPI), and only from this machine'
Assert ($once[$start + 6] -ceq '# END HyFib HealthCare360') 'it ends with its END line'
Assert ((($once[($start + 7)..($once.Count - 1)]) -join "`n") -ceq (($hba[5..7]) -join "`n")) 'the rules for every other account follow, as they were'
$twice = Set-HbaBlock $once 'hc360_app' 'hc360'
Assert (($twice -join "`n") -ceq ($once -join "`n")) 'a second run writes the same file'
$moved = Set-HbaBlock (@($hba) + $once[$start..($start + 6)]) 'hc360_app' 'hc360'
Assert (($moved -join "`n") -ceq ($once -join "`n")) 'a block found after the rules is moved before them'
$renamed = Set-HbaBlock $once 'hc360_main' 'hc360'
Assert ((@($renamed | Where-Object { $_ -like '*hc360_app*' })).Count -eq 0 -and (@($renamed | Where-Object { $_ -like '*hc360_main*' })).Count -eq 5) 'the block follows the account the server signs in as'
Assert ((Set-HbaBlock @() 'hc360_app' 'hc360').Count -eq 7) 'an empty file gets the block alone'
Assert (((Remove-InstallerBlock $once 'pg_hba.conf') -join "`n") -ceq ($hba -join "`n")) 'removing the block gives back the file as it was'
Assert (((Remove-InstallerBlock $hba 'pg_hba.conf') -join "`n") -ceq ($hba -join "`n")) 'a file without the block is left as it is'
Assert (Test-Throws { Remove-InstallerBlock (@($once[0..($start + 3)]) + $hba[5..7]) 'pg_hba.conf' } "pg_hba.conf has a '# BEGIN HyFib HealthCare360' line without its '# END HyFib HealthCare360' line") 'a block without its END line stops the installer instead of losing the rules after it'
Assert (Test-Throws { Set-HbaBlock $hba 'hc360 app' 'hc360' } 'cannot be named in pg_hba.conf') 'an account name pg_hba.conf would read differently is refused'

Write-Host 'pg_ident.conf'
$ident = @(
    '# PostgreSQL User Name Maps',
    '# MAPNAME       SYSTEM-USERNAME         PG-USERNAME'
)
$mapped = Set-IdentBlock $ident 'hc360' 'HC360Server@NT SERVICE' 'hc360_app'
Assert (($mapped[0..1] -join "`n") -ceq ($ident -join "`n")) 'the lines already there stay first'
Assert ($mapped[3] -ceq 'hc360   "HC360Server@NT SERVICE"   hc360_app') 'the service''s Windows account is mapped to hc360_app, quoted for the space in NT SERVICE'
Assert (((Set-IdentBlock $mapped 'hc360' 'HC360Server@NT SERVICE' 'hc360_app') -join "`n") -ceq ($mapped -join "`n")) 'a second run writes the same file'
Assert (((Remove-InstallerBlock $mapped 'pg_ident.conf') -join "`n") -ceq ($ident -join "`n")) 'removing the block gives back the file as it was'

Write-Host 'Finding PostgreSQL''s files'
Assert ((Get-PgCtlDataDirectory '"C:\Program Files\PostgreSQL\17\bin\pg_ctl.exe" runservice -N "postgresql-x64-17" -D "C:\Program Files\PostgreSQL\17\data" -w') -ceq 'C:\Program Files\PostgreSQL\17\data') 'the data folder of the PostgreSQL Windows installer''s service'
Assert ((Get-PgCtlDataDirectory 'C:\pgsql\bin\pg_ctl.exe runservice -N pgsql -D D:\pgdata -w') -ceq 'D:\pgdata') 'an unquoted data folder'
Assert ($null -eq (Get-PgCtlDataDirectory '"C:\Windows\System32\svchost.exe" -k netsvcs')) 'none for another service'
$conf = @("listen_addresses = 'localhost'", '#port = 5433', 'port = 5432                # (change requires restart)', "hba_file = 'D:\pg\it''s\pg_hba.conf'")
$auto = @('# Do not edit this file manually!', "port = '5440'")
Assert ((Get-PostgresSetting $conf $auto 'port') -ceq '5440') 'ALTER SYSTEM''s setting (postgresql.auto.conf) wins over postgresql.conf'
Assert ((Get-PostgresSetting $conf @() 'port') -ceq '5432') 'a comment after a setting, and a commented-out line, are ignored'
Assert ((Get-PostgresSetting $conf $auto 'hba_file') -ceq "D:\pg\it's\pg_hba.conf") 'a quoted value, with its doubled quote'
Assert ($null -eq (Get-PostgresSetting $conf $auto 'ident_file')) 'none for a setting neither file has'
Assert ($null -eq (Get-PostgresSetting @() @() 'port')) 'none from no files'

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-signin-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    Write-Host 'Choosing the PostgreSQL that runs'
    # Two versions set to the same port, as after an upgrade to a new PostgreSQL version: only the new one runs.
    $folders = @{}
    foreach ($name in 'old', 'new', 'other') {
        $folders[$name] = Join-Path $root $name
        New-Item -ItemType Directory -Path $folders[$name] | Out-Null
    }
    Write-TextLines (Join-Path $folders['old'] 'postgresql.conf') @('port = 5432')
    Write-TextLines (Join-Path $folders['new'] 'postgresql.conf') @('#port = 5432', "hba_file = '$(Join-Path $folders['new'] 'rules\hba.conf')'")
    Write-TextLines (Join-Path $folders['other'] 'postgresql.conf') @('port = 5433')
    function New-Service([string] $Name, [string] $State, [string] $Folder) {
        [pscustomobject]@{ Name = $Name; State = $State; PathName = "`"C:\pgin\pg_ctl.exe`" runservice -N `"$Name`" -D `"$Folder`" -w" }
    }
    $services = @(
        (New-Service 'postgresql-x64-16' 'Stopped' $folders['old']),
        [pscustomobject]@{ Name = 'Spooler'; State = 'Running'; PathName = 'C:\Windows\System32\spoolsv.exe' },
        (New-Service 'postgresql-x64-17' 'Running' $folders['new']),
        (New-Service 'postgresql-x64-17-reports' 'Running' $folders['other'])
    )
    $found = Select-PostgresService $services 5432
    Assert ($found.Name -ceq 'postgresql-x64-17') 'the running service is chosen, not an older stopped one set to the same port'
    Assert ($found.HbaFile -ceq (Join-Path $folders['new'] 'rules\hba.conf')) 'its pg_hba.conf where hba_file says'
    Assert ($found.IdentFile -ceq (Join-Path $folders['new'] 'pg_ident.conf')) 'its pg_ident.conf in its data folder, where hba_file does not say otherwise'
    Assert ((Select-PostgresService $services 5433).Name -ceq 'postgresql-x64-17-reports') 'another port, another service'
    Assert ($null -eq (Select-PostgresService @($services[0], $services[1]) 5432)) 'none when only a stopped one is set to the port'

    Write-Host 'Putting the files back after a failed change'
    $kept = Join-Path $root 'kept.conf'; $empty = Join-Path $root 'empty.conf'; $absent = Join-Path $root 'absent.conf'
    Write-TextLines $kept @('host all all 127.0.0.1/32 scram-sha-256')
    [System.IO.File]::WriteAllBytes($empty, [byte[]] @())
    $before = [System.IO.File]::ReadAllBytes($kept)
    $saved = Save-SignInFiles @($kept, $empty, $absent)
    Write-TextLines $kept @('changed'); Write-TextLines $empty @('changed'); Write-TextLines $absent @('created')
    Restore-SignInFiles $saved
    Assert ([System.Linq.Enumerable]::SequenceEqual([byte[]] [System.IO.File]::ReadAllBytes($kept), [byte[]] $before)) 'a failed change puts a file back byte for byte'
    Assert ((Get-Item -LiteralPath $empty).Length -eq 0) 'an empty file stays, empty'
    Assert (-not (Test-Path -LiteralPath $absent)) 'a file that did not exist is removed again'

    Write-Host 'Writing the files'

    $path = Join-Path $root 'pg_hba.conf'
    Assert ((Read-TextLines $path).Count -eq 0) 'a missing file reads as no lines'
    Write-TextLines $path $once
    $bytes = [System.IO.File]::ReadAllBytes($path)
    Assert (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) 'no byte order mark, which PostgreSQL would read as part of the first line'
    Assert (((Read-TextLines $path) -join "`n") -ceq ($once -join "`n")) 'the lines read back as written'
    Write-TextLines $path @('only line')
    Assert (@(Read-TextLines $path).Count -eq 1) 'a file of one line reads as one line'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
