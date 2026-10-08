<#
.SYNOPSIS
    Checks that Install-HC360Server.ps1 keeps the site's settings when it runs again, in the PowerShell that runs this
    script. CI runs it in Windows PowerShell 5.1 and in PowerShell 7, the two the installer runs in.

.DESCRIPTION
    Takes the installer's settings functions, and the statement that sets its own settings, from the script itself
    (running it would install HC360), applies them to settings files like a hospital server's, and reads the result
    back. Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Install-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$functions = 'Read-SettingsFile', 'ConvertTo-SettingTable', 'Find-Setting', 'Set-Setting', 'ConvertTo-SettingsJson', 'Save-SettingsFile'
foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($functions -contains $function.Name) { . ([scriptblock]::Create($function.Extent.Text)) }
}
foreach ($name in $functions) {
    if (-not (Get-Command -Name $name -CommandType Function -ErrorAction SilentlyContinue)) { throw "Install-HC360Server.ps1 no longer defines $name." }
}

# The installer's "$changed = @(...)" statement, taking the parameters it reads: $PSBoundParameters then holds those a
# test gives, as when an administrator runs the installer with them.
$statement = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$changed' })
if ($statement.Count -ne 1) { throw 'Install-HC360Server.ps1 no longer sets its own settings in one "$changed = @(...)" statement.' }
$setOwnSettings = [scriptblock]::Create(
    'param($settings, [string] $DataDir, [string] $pgBin, [int] $HttpsPort = 7360, [string] $FacilityCode = ''NH'', ' +
    '[string] $FacilityName = ''Nana Hospitals'', [string] $SecondaryBackupDir, [switch] $CreateDatabase, [string] $connectionString) ' +
    [Environment]::NewLine + $statement[0].Extent.Text + [Environment]::NewLine + '$changed')

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

function Write-TextFile([string] $Path, [string] $Text, [bool] $ByteOrderMark) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $ByteOrderMark))
}

function Test-ByteOrderMark([string] $Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
}

# One run of the installer's configuration step: read the file, set the installer's settings with the parameters given,
# write the file only when one changed. Returns the settings that changed.
function Invoke-ConfigurationStep([string] $Path, [hashtable] $Parameters = @{}) {
    $settings = Read-SettingsFile $Path
    $before = ConvertTo-SettingTable $settings
    $null = ConvertTo-SettingsJson $settings $before @()
    $changed = @(& $setOwnSettings -settings $settings -DataDir $root -pgBin $pgBin @Parameters)
    if ($changed) { Save-SettingsFile $settings $before $changed $Path }
    return , $changed
}

function Read-Table([string] $Path) { ConvertTo-SettingTable (Read-SettingsFile $Path) }

# "Nana Hospital" in Devanagari, from its code points: Windows PowerShell reads this script as ANSI.
$hindi = -join [char[]](0x0928, 0x093E, 0x0928, 0x093E, 0x0020, 0x0939, 0x0949, 0x0938, 0x094D, 0x092A, 0x093F, 0x091F, 0x0932)

# As the server writes the file (ProtectedSettings), with settings added by hand and protected values.
$serverFile = @'
{
  "ConnectionStrings": {
    "Hospital": "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA",
    "Analytics": "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sCAAAA"
  },
  "HealthCare360:Analytics:ExtractDirectory": "E:\\BI\\Extracts",
  "HealthCare360": {
    "HttpsPort": 7443,
    "DefaultFacilityCode": "NH",
    "DefaultFacilityName": "{hindi}",
    "Role": "api",
    "Pdf": { "BrowserPath": "D:\\Edge\\msedge.exe", "MaxConcurrent": 4 },
    "Network": {
      "CertificatePath": "D:\\certs\\hc360.pfx",
      "CertificatePassword": "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sDAAAA",
      "ApprovedSsids": [ "HC360-Clinical" ],
      "AdditionalHostNames": []
    },
    "Security": { "MfaRequiredRoles": [ "SuperAdmin", "HospitalAdmin", "Doctor" ], "SessionIdleMinutes": 90, "EnforceMfa": true },
    "Backup": {
      "Enabled": false,
      "Directory": "E:\\HC360\\backups",
      "SecondaryDirectory": "\\\\NAS\\HC360-Backups",
      "PgDumpPath": "C:\\Program Files\\PostgreSQL\\16\\bin\\pg_dump.exe",
      "RetentionDays": 45,
      "PgRestorePath": null
    },
    "Notes": "Ward 3 & 4 <east> 'new' \"wing\""
  },
  "Serilog": { "MinimumLevel": { "Default": "Information", "Override": { "Microsoft.AspNetCore": "Warning" } } },
  "Ratio": 0.75,
  "Offset": -5
}
'@
$serverFile = $serverFile.Replace('{hindi}', $hindi)

Write-Host "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-settings-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$path = Join-Path $root 'hc360.settings.json'
$pgBin = Join-Path $root 'PostgreSQL\17\bin'
$pgDump = Join-Path $pgBin 'pg_dump.exe'
try {
    Write-Host 'Fresh install'
    $changed = Invoke-ConfigurationStep $path @{ CreateDatabase = $true; connectionString = 'Host=localhost;Database=hc360;Username=hc360_app;Password=p+/&w'; FacilityName = $hindi; SecondaryBackupDir = '\\NAS\HC360-Backups' }
    Assert ($changed.Count -eq 8) "sets the installer's eight settings ($($changed -join ', '))"
    $table = Read-Table $path
    Assert ($table['ConnectionStrings:Hospital'] -ceq 'Host=localhost;Database=hc360;Username=hc360_app;Password=p+/&w') 'the database connection'
    Assert ($table['HealthCare360:HttpsPort'] -ceq '7360') 'HttpsPort 7360'
    Assert ($table['HealthCare360:DefaultFacilityName'] -ceq $hindi) 'a facility name in Devanagari'
    Assert ($table['HealthCare360:Backup:Enabled'] -ceq 'True') 'Backup:Enabled'
    Assert ($table['HealthCare360:Backup:SecondaryDirectory'] -ceq '\\NAS\HC360-Backups') 'Backup:SecondaryDirectory'
    Assert ($table['HealthCare360:Backup:PgDumpPath'] -ceq $pgDump) 'Backup:PgDumpPath'
    Assert (Test-ByteOrderMark $path) 'UTF-8 with a byte order mark'

    Write-Host 'Upgrade without parameters'
    Write-TextFile $path $serverFile $true
    $before = Read-Table $path
    Assert ($before.Count -eq 27) "the server's file holds 27 settings ($($before.Count))"
    $changed = Invoke-ConfigurationStep $path
    Assert (($changed -join ', ') -ceq 'HealthCare360:Backup:PgDumpPath') "changes only PgDumpPath, to the PostgreSQL found ($($changed -join ', '))"
    $after = Read-Table $path
    $differ = @($before.Keys | Where-Object { $_ -ne 'HealthCare360:Backup:PgDumpPath' -and -not ($after.ContainsKey($_) -and $after[$_] -ceq $before[$_]) })
    Assert ($differ.Count -eq 0 -and $after.Count -eq 27) "keeps every other setting exactly ($($differ -join ', '))"
    Assert ($after['HealthCare360:Backup:PgDumpPath'] -ceq $pgDump) 'PgDumpPath follows the PostgreSQL found'
    Assert ($after['ConnectionStrings:Hospital'] -ceq 'dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA') 'a protected connection as it was'
    Assert ($after['HealthCare360:Network:CertificatePassword'] -ceq 'dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sDAAAA') 'a protected password as it was'
    Assert ($after['HealthCare360:Pdf:BrowserPath'] -ceq 'D:\Edge\msedge.exe') 'HealthCare360:Pdf:BrowserPath'
    Assert ($after['HealthCare360:Role'] -ceq 'api') 'HealthCare360:Role'
    Assert ($after['HealthCare360:Analytics:ExtractDirectory'] -ceq 'E:\BI\Extracts') 'a setting with colons in its name'
    Assert ($after['HealthCare360:Network:ApprovedSsids:0'] -ceq 'HC360-Clinical') 'a one-item array'
    Assert ($after['HealthCare360:Security:MfaRequiredRoles:2'] -ceq 'Doctor') 'MfaRequiredRoles'
    Assert ($after['HealthCare360:HttpsPort'] -ceq '7443') 'the port, without -HttpsPort'
    Assert ($after['HealthCare360:Backup:Enabled'] -ceq 'False') 'the nightly backup left off'
    Assert ($after['HealthCare360:Backup:SecondaryDirectory'] -ceq '\\NAS\HC360-Backups') 'the NAS copy, without -SecondaryBackupDir'
    Assert ($after['HealthCare360:DefaultFacilityName'] -ceq $hindi) 'the facility name in Devanagari'
    Assert ($after['HealthCare360:Notes'] -ceq 'Ward 3 & 4 <east> ''new'' "wing"') 'special characters'
    Assert ($after['Ratio'] -ceq '0.75' -and $after['Offset'] -ceq '-5') 'numbers'
    Assert (Test-ByteOrderMark $path) 'UTF-8 with a byte order mark'

    Write-Host 'Run again'
    Assert ((Invoke-ConfigurationStep $path).Count -eq 0) 'nothing to change, so the installer leaves the file alone'

    Write-Host 'Parameters given again'
    $changed = Invoke-ConfigurationStep $path @{ HttpsPort = 7360; FacilityCode = 'NHX'; SecondaryBackupDir = '' }
    Assert (($changed -join ', ') -ceq 'HealthCare360:HttpsPort, HealthCare360:DefaultFacilityCode, HealthCare360:Backup:SecondaryDirectory') "replace theirs ($($changed -join ', '))"
    $table = Read-Table $path
    Assert ($table['HealthCare360:HttpsPort'] -ceq '7360' -and $table['HealthCare360:DefaultFacilityCode'] -ceq 'NHX' -and $table['HealthCare360:Backup:SecondaryDirectory'] -ceq '') 'with the values given'
    $differ = @($after.Keys | Where-Object { $changed -notcontains $_ -and $table[$_] -cne $after[$_] })
    Assert ($differ.Count -eq 0) "and nothing else ($($differ -join ', '))"

    Write-Host 'Encoding'
    Write-TextFile $path ('{ "HealthCare360": { "DefaultFacilityName": "' + $hindi + '" } }') $false
    Assert ((Read-Table $path)['HealthCare360:DefaultFacilityName'] -ceq $hindi) 'UTF-8 without a byte order mark is read as UTF-8'

    Write-Host 'Deep settings'
    Write-TextFile $path ('{ ' + ('"Level": { ' * 40) + '"Leaf": "deep"' + (' }' * 40) + ' }') $true
    $null = Invoke-ConfigurationStep $path
    Assert ((Read-Table $path)[(@('Level') * 40 + 'Leaf') -join ':'] -ceq 'deep') 'forty levels deep are written back'

    Write-Host 'Settings with colons in their names'
    Write-TextFile $path '{ "ConnectionStrings:Hospital": "dpapi:eA==", "HealthCare360:HttpsPort": 7443 }' $true
    $changed = Invoke-ConfigurationStep $path @{ HttpsPort = 8443 }
    $settings = Read-SettingsFile $path
    Assert ($null -ne $settings.PSObject.Properties['HealthCare360:HttpsPort'] -and $null -eq $settings.HealthCare360.PSObject.Properties['HttpsPort']) 'are replaced where they are'
    $table = ConvertTo-SettingTable $settings
    Assert ($table['HealthCare360:HttpsPort'] -ceq '8443' -and $table['ConnectionStrings:Hospital'] -ceq 'dpapi:eA==') 'with the value given, the others kept'

    Write-Host 'Files the installer stops at'
    Write-TextFile $path '{ "HealthCare360": { "HttpsPort": 7360 }, "HealthCare360:HttpsPort": 7443 }' $true
    $message = $null
    try { $null = Read-Table $path } catch { $message = $_.Exception.Message }
    Assert ("$message" -like '*HttpsPort is in hc360.settings.json twice*') "a setting written twice ($message)"

    Write-TextFile $path '{ "HealthCare360": { "HttpsPort": 7360, } oops }' $true
    $message = $null
    try { $null = Read-SettingsFile $path } catch { $message = $_.Exception.Message }
    Assert ("$message" -like '*is not valid JSON*Nothing has been changed.') "a file that is not JSON ($message)"

    Write-TextFile $path ('{' + [Environment]::NewLine + '  // added by hand' + [Environment]::NewLine + '  "HealthCare360": { "HttpsPort": 7360 }' + [Environment]::NewLine + '}') $true
    $message = $null
    try { $null = Read-SettingsFile $path } catch { $message = $_.Exception.Message }
    if ($PSVersionTable.PSVersion.Major -ge 6) { Assert ($null -eq $message) "PowerShell 7 reads a comment ($message)" }
    else { Assert ("$message" -like '*is not valid JSON*') "Windows PowerShell stops at a comment ($message)" }

    Write-Host 'Writing'
    Write-TextFile $path $serverFile $true
    $settings = Read-SettingsFile $path
    $before = ConvertTo-SettingTable $settings
    $before['HealthCare360:Lost'] = 'a value ConvertTo-Json would have dropped'
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $message = $null
    try { Save-SettingsFile $settings $before @() $path } catch { $message = $_.Exception.Message }
    Assert ("$message" -like '*without changing HealthCare360:Lost*') "a setting that does not read back stops the write ($message)"
    Assert ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($path)) -ceq [Convert]::ToBase64String($bytes)) 'and leaves the file as it was'

    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
        $acl = Get-Acl -LiteralPath $path
        $acl.SetAccessRuleProtection($true, $true)
        $users = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-545'
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'Read', 'Allow')))
        Set-Acl -LiteralPath $path -AclObject $acl
        $sddl = (Get-Acl -LiteralPath $path).Sddl
        $changed = Invoke-ConfigurationStep $path @{ FacilityCode = 'NHY' }
        Assert ($changed.Count -eq 2 -and (Read-Table $path)['HealthCare360:DefaultFacilityCode'] -ceq 'NHY') "the file is written ($($changed -join ', '))"
        Assert ((Get-Acl -LiteralPath $path).Sddl -ceq $sddl) 'and keeps its ACL'
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
