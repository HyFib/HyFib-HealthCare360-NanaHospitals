<#
.SYNOPSIS
    Lets the CI runner's Windows account sign in to the runner's PostgreSQL with SSPI and no password, as the HC360
    service signs in on a hospital server, for WindowsDatabaseSignInTests.

.DESCRIPTION
    Uses Install-HC360Server.ps1's own functions: it finds the PostgreSQL service and its files as the installer does,
    and writes the same pg_hba.conf and pg_ident.conf blocks, with the runner's account standing in for the service's
    (NT SERVICE\HC360Server). PostgreSQL must then read them without an error. Creates the account hc360_sspi, without a
    password, and its database, and writes HC360_TEST_WINDOWS_SIGN_IN to $env:GITHUB_ENV for the tests.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Install-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$functions = 'Remove-InstallerBlock', 'Set-HbaBlock', 'Set-IdentBlock', 'ConvertTo-SspiName', 'Get-PgCtlDataDirectory', 'Get-PostgresSetting',
    'Read-TextLines', 'Write-TextLines', 'Find-PostgresService', 'Select-PostgresService'
foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($functions -contains $function.Name) { . ([scriptblock]::Create($function.Extent.Text)) }
}

$port = 5432
$role = 'hc360_sspi'
$postgres = Find-PostgresService $port
if (-not $postgres) { throw "Find-PostgresService found no PostgreSQL service listening on port ${port}: the installer would not find this one either." }
Write-Host "PostgreSQL service $($postgres.Name), data folder $($postgres.DataDirectory)"

$psql = Join-Path $env:PGBIN 'psql.exe'
$env:PGPASSWORD = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { 'root' }
$superuser = if ($env:PGUSER) { $env:PGUSER } else { 'postgres' }
function Invoke-Sql([string] $Database, [string] $Sql) {
    $out = & $psql -h localhost -p $port -U $superuser -d $Database -v ON_ERROR_STOP=1 -qAt -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $Sql" }
    return $out
}

Invoke-Sql 'postgres' "CREATE ROLE $role LOGIN"
Invoke-Sql 'postgres' "CREATE DATABASE $role OWNER $role"
Invoke-Sql $role "SET ROLE $role; CREATE TABLE sign_in_check (id int PRIMARY KEY, note text); INSERT INTO sign_in_check VALUES (1, 'dumped with SSPI')"

$account = ConvertTo-SspiName ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
Write-TextLines $postgres.HbaFile (Set-HbaBlock (Read-TextLines $postgres.HbaFile) $role 'hc360')
Write-TextLines $postgres.IdentFile (Set-IdentBlock (Read-TextLines $postgres.IdentFile) 'hc360' $account $role)
Write-Host "Mapped $account to $role. The block PostgreSQL reads:"
Get-Content -LiteralPath $postgres.HbaFile | Where-Object { $_ -match 'HyFib|hc360' } | ForEach-Object { Write-Host "  $_" }

$null = Invoke-Sql 'postgres' 'SELECT pg_reload_conf()'
$errors = @(Invoke-Sql 'postgres' "SELECT 'pg_hba.conf line ' || line_number || ': ' || error FROM pg_hba_file_rules WHERE error IS NOT NULL UNION ALL SELECT 'pg_ident.conf line ' || line_number || ': ' || error FROM pg_ident_file_mappings WHERE error IS NOT NULL")
if ($errors) { throw "PostgreSQL could not read what the installer wrote: $($errors -join '; ')" }

"HC360_TEST_WINDOWS_SIGN_IN=Host=localhost;Port=$port;Database=$role;Username=$role" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
