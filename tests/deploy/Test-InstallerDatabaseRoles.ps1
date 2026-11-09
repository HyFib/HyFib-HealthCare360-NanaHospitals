<#
.SYNOPSIS
    Checks how Install-HC360Server.ps1 sets up the database's two accounts (docs/deployment/DEPLOYMENT.md, "Two database
    accounts"): hc360_owner, which owns the schema, and hc360_app, which the server works as. CI runs it in Windows
    PowerShell 5.1 and in PowerShell 7, the two the installer runs in.

.DESCRIPTION
    Takes the installer's functions and account names from the script itself (running it would install HC360) and checks
    the statements and psql arguments they make, and the SQL file they run. What that SQL does on a real PostgreSQL is
    checked by the integration tests (DatabaseRolesTests), which run the same file. Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Install-HC360Server.ps1')).Path
$restore = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Restore-HC360.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$functions = 'Get-RoleSql', 'Get-RolesSqlArguments', 'New-HospitalConnectionString'
foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($functions -contains $function.Name) { . ([scriptblock]::Create($function.Extent.Text)) }
}
foreach ($name in $functions) {
    if (-not (Get-Command -Name $name -CommandType Function -ErrorAction SilentlyContinue)) { throw "Install-HC360Server.ps1 no longer defines $name." }
}
# The account names, as the installer sets them.
foreach ($assignment in $ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] }) {
    if (@('$OwnerRole', '$AppRole', '$RolesSqlFile') -contains $assignment.Left.Extent.Text) { . ([scriptblock]::Create($assignment.Extent.Text)) }
}
foreach ($name in 'OwnerRole', 'AppRole', 'RolesSqlFile') {
    if (-not (Get-Variable -Name $name -ErrorAction SilentlyContinue)) { throw "Install-HC360Server.ps1 no longer sets `$$name." }
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

Write-Host 'The two accounts'
Assert ($OwnerRole -eq 'hc360_owner') 'the schema''s owner is hc360_owner'
Assert ($AppRole -eq 'hc360_app') 'the server works as hc360_app, as installed before the split'
Assert ($OwnerRole -ne $AppRole) 'they are two accounts'

$sql = Get-RoleSql 'hc360_owner' 'abc-DEF_123'
Assert ($sql -like "*CREATE ROLE hc360_owner LOGIN PASSWORD 'abc-DEF_123'*") 'a new account is made, able to sign in with its password'
Assert ($sql -like "*ALTER ROLE hc360_owner LOGIN PASSWORD 'abc-DEF_123'*") 'an account that exists gets the new password'
Assert ($sql -notlike '*SUPERUSER*' -and $sql -notlike '*CREATEROLE*' -and $sql -notlike '*CREATEDB*') 'neither may do more than sign in'
Assert (Test-Throws { Get-RoleSql 'hc360_owner' "x'; DROP ROLE postgres; --" } 'letters, digits') 'a password that could break out of the statement is refused'
Assert (Test-Throws { Get-RoleSql 'HC360 Owner' 'abc' } 'lower-case letters') 'an account name that would need quoting is refused'

Write-Host 'Running hc360-database-roles.sql'
$sqlFile = Join-Path (Split-Path -Parent $installer) $RolesSqlFile
Assert (Test-Path -LiteralPath $sqlFile) "$RolesSqlFile is beside the installer"
$arguments = @(Get-RolesSqlArguments 'localhost' 5432 'postgres' 'hc360' $sqlFile)
$joined = $arguments -join ' '
Assert ($joined -like '*-d hc360 *') 'in the hospital database, not postgres'
Assert ($joined -like '*-v ON_ERROR_STOP=1*') 'stopping at the first error'
$command = [array]::IndexOf($arguments, '-c')
$file = [array]::IndexOf($arguments, '-f')
Assert ($command -ge 0 -and $file -gt $command) 'the accounts are named before the file runs'
Assert ($arguments[$command + 1] -like "*set_config('hc360.owner_role', 'hc360_owner', false)*set_config('hc360.app_role', 'hc360_app', false)*") 'for the whole session, as the file reads them'
Assert ($arguments[$file + 1] -eq $sqlFile) 'the file beside the installer'
Assert (Test-Throws { Get-RolesSqlArguments 'localhost' 5432 'postgres' 'hc360' (Join-Path $PSScriptRoot 'missing.sql') } 'was not found') 'a missing file stops the installer'

Write-Host 'What the SQL grants'
$text = Get-Content -LiteralPath $sqlFile -Raw
Assert ($text -match "current_setting\('hc360\.owner_role'\)" -and $text -match "current_setting\('hc360\.app_role'\)") 'it takes the accounts from the session'
Assert ($text -match 'REASSIGN OWNED BY %I TO %I') 'a database hc360_app owned before the split is handed to the owner'
Assert ($text -match 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES') 'hc360_app reads and writes rows'
Assert ($text -match 'REVOKE TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES') 'and may not empty a table or add a trigger'
Assert ($text -match 'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES') 'tables the owner makes later get the same rights'
Assert ($text -match "REVOKE %I FROM %I") 'hc360_app is no member of the owner'
Assert ($text -cnotmatch 'GRANT\s+ALL\b') 'nothing grants everything'
Assert ($text -cnotmatch 'GRANT[^;]*\bTRUNCATE\b') 'nothing grants TRUNCATE'

Write-Host 'The connection strings'
$app = New-HospitalConnectionString 'localhost' 5432 'hc360' 'hc360_app' 'secret1'
$owner = New-HospitalConnectionString 'localhost' 5432 'hc360' 'hc360_owner' 'secret2'
Assert ($app -like 'Host=localhost;Port=5432;Database=hc360;Username=hc360_app;Password=secret1;*') 'ConnectionStrings:Hospital signs in as hc360_app'
Assert ($app -like '*Maximum Pool Size=200*') 'with the pool the server works with'
Assert ($owner -like '*Username=hc360_owner;Password=secret2;*') 'ConnectionStrings:HospitalOwner signs in as hc360_owner'
Assert ($owner -like '*Maximum Pool Size=5*') 'with a small pool: only start-up and the partition jobs use it'
Assert ((New-HospitalConnectionString 'localhost' 5432 'hc360' 'hc360_app' $null) -notlike '*Password*') 'without a password (the Windows sign-in), none is written'

Write-Host 'The installer and Restore-HC360.ps1 use them'
$installerText = Get-Content -LiteralPath $installer -Raw
Assert ($installerText -match "CREATE DATABASE \`$DatabaseName OWNER \`$OwnerRole") 'a new database is the owner''s'
Assert ($installerText -match "'ConnectionStrings:HospitalOwner'") 'the owner''s connection string is written to the settings file'
$restoreText = Get-Content -LiteralPath $restore -Raw
Assert ($restoreText -match "rolname = 'hc360_owner'") 'Restore-HC360.ps1 finds whether the accounts are split'
Assert ($restoreText -match '--role=\$owner') 'and restores the tables as their owner'
Assert ($restoreText -match 'hc360-database-roles\.sql') 'then gives hc360_app its rights again'

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
