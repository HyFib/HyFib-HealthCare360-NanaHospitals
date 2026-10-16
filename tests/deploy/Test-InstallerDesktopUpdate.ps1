<#
.SYNOPSIS
    Checks the installers' set-up of HC360 Desktop's self-update from the Server Node (SRS DED-18), in the PowerShell
    that runs this script. CI runs it in Windows PowerShell 5.1 and in PowerShell 7, the two the installers run in.

.DESCRIPTION
    Takes the functions from the installers themselves (running them would install HC360):
    - Find-DesktopPackage (Install-HC360Server.ps1): which HC360 Desktop package beside the server package the server
      is given to offer, in folders like a release's.
    - Set-UpdaterAcl and Register-DesktopUpdateTask (Install-HC360Desktop.ps1): the updater's folder is open to change
      by administrators and SYSTEM only, and a throwaway task registered as the update task is runs as SYSTEM, at
      start-up, at sign-in and every 4 hours, and may be started by users. Those parts need administrator rights, which
      CI has; without them they are skipped.
    Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

function Get-FunctionText([string] $Script, [string] $Name) {
    $path = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\deploy\windows\$Script")).Path
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $null, [ref] $null)
    $found = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $false))
    if ($found.Count -ne 1) { throw "$Script no longer defines $Name." }
    return $found[0].Extent.Text
}

. ([scriptblock]::Create((Get-FunctionText 'Install-HC360Server.ps1' 'Find-DesktopPackage')))
. ([scriptblock]::Create((Get-FunctionText 'Install-HC360Desktop.ps1' 'New-DesktopUpdateTriggers')))
. ([scriptblock]::Create((Get-FunctionText 'Install-HC360Desktop.ps1' 'Register-DesktopUpdateTask')))
. ([scriptblock]::Create((Get-FunctionText 'Install-HC360Desktop.ps1' 'Set-UpdaterAcl')))

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-update-install-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    Write-Host 'The server installer finds the HC360 Desktop package of its release'
    # As an administrator downloads a release and extracts the server package: <downloads>\HC360-Server-1.2.0-win-x64\server.
    $downloads = Join-Path $root 'downloads'
    $extracted = Join-Path $downloads 'HC360-Server-1.2.0-win-x64'
    $server = Join-Path $extracted 'server'
    New-Item -ItemType Directory -Force -Path $server, (Join-Path $extracted 'deploy') | Out-Null
    Assert ($null -eq (Find-DesktopPackage $server)) 'none when the release has no HC360 Desktop package'

    foreach ($name in @('HC360-Server-1.2.0-win-x64.zip', 'HC360-LabAgent-1.2.0-win-x64.zip', 'HC360-Desktop-1.2.0-win-x64.manifest.json', 'HC360-Desktop-latest.zip')) {
        Set-Content -LiteralPath (Join-Path $downloads $name) -Value 'x'
    }
    Assert ($null -eq (Find-DesktopPackage $server)) 'other packages and files are not taken for it'

    Set-Content -LiteralPath (Join-Path $downloads 'HC360-Desktop-1.2.0-win-x64.zip') -Value 'x'
    Assert ((Find-DesktopPackage $server) -eq (Join-Path $downloads 'HC360-Desktop-1.2.0-win-x64.zip')) 'the one in the folder the release was downloaded to'
    Assert ((Find-DesktopPackage "$server\") -eq (Join-Path $downloads 'HC360-Desktop-1.2.0-win-x64.zip')) 'also when the package path ends in a backslash'

    Set-Content -LiteralPath (Join-Path $extracted 'HC360-Desktop-1.10.0-win-x64.zip') -Value 'x'
    Set-Content -LiteralPath (Join-Path $downloads 'HC360-Desktop-1.9.0-win-x64.zip') -Value 'x'
    Assert ((Find-DesktopPackage $server) -eq (Join-Path $extracted 'HC360-Desktop-1.10.0-win-x64.zip')) 'the newest, by version number (1.10.0 after 1.9.0), beside the extracted package too'

    $principal = New-Object System.Security.Principal.WindowsPrincipal ([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host '  skip  the updater folder''s permissions and the update task: they need administrator rights (CI has them)' -ForegroundColor Yellow
    }
    else {
        Write-Host 'Only administrators and SYSTEM may change the updater'
        $updater = Join-Path $root 'HC360 Desktop Updater'
        New-Item -ItemType Directory -Force -Path $updater | Out-Null
        Set-UpdaterAcl $updater
        $acl = Get-Acl -LiteralPath $updater
        $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        $system = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $admins = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        $users = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
        $writes = [System.Security.AccessControl.FileSystemRights]'Write, Delete, ChangePermissions, TakeOwnership'
        Assert $acl.AreAccessRulesProtected 'nothing is inherited from the folder above'
        Assert ($acl.GetOwner([System.Security.Principal.SecurityIdentifier]) -eq $admins) 'Administrators own it'
        Assert (@($rules | Where-Object { $_.IdentityReference -ne $system -and $_.IdentityReference -ne $admins -and ($_.FileSystemRights -band $writes) }).Count -eq 0) 'nobody else may change it'
        Assert (@($rules | Where-Object { $_.IdentityReference -eq $users -and ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [System.Security.AccessControl.FileSystemRights]::ReadAndExecute }).Count -eq 1) 'users may run it (for Restart to update)'

        Write-Host 'The update task runs the updater as SYSTEM, at start-up, at sign-in and every 4 hours'
        $taskPath = '\HC360Test\'
        $taskName = 'Desktop update ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $install = 'C:\Program Files\HyFib\HC360 Desktop\'
        Register-DesktopUpdateTask -Updater "$env:SystemRoot\System32\cmd.exe" -InstallDir $install -TaskPath $taskPath -TaskName $taskName
        try {
            $task = Get-ScheduledTask -TaskName $taskName -TaskPath $taskPath
            Assert ($task.Actions[0].Execute -eq "$env:SystemRoot\System32\cmd.exe") 'it runs the updater'
            Assert ($task.Actions[0].Arguments -eq 'apply --install-dir "C:\Program Files\HyFib\HC360 Desktop"') "for the install folder, quoted, without a trailing backslash (found: $($task.Actions[0].Arguments))"
            Assert ($task.Principal.UserId -match 'SYSTEM$' -and "$($task.Principal.RunLevel)" -eq 'Highest') 'as SYSTEM'
            $classes = @($task.Triggers | ForEach-Object { $_.CimClass.CimClassName })
            Assert ($classes -contains 'MSFT_TaskBootTrigger' -and $classes -contains 'MSFT_TaskLogonTrigger') "at start-up and at sign-in (found: $($classes -join ', '))"
            $repeating = @($task.Triggers | Where-Object { $_.Repetition -and $_.Repetition.Interval -eq 'PT4H' })
            Assert ($repeating.Count -eq 1 -and [string]::IsNullOrEmpty($repeating[0].Repetition.Duration)) 'and every 4 hours for as long as Windows runs'
            Assert ("$($task.Settings.MultipleInstances)" -eq 'IgnoreNew') "one run at a time (found: $($task.Settings.MultipleInstances))"

            $scheduler = New-Object -ComObject Schedule.Service
            $scheduler.Connect()
            $dacl = $scheduler.GetFolder($taskPath.TrimEnd('\')).GetTask($taskName).GetSecurityDescriptor(4)
            Assert ($dacl -match '\(A;;[^;)]+;;;AU\)') "authenticated users may start it (found: $dacl)"
            Assert ($dacl -notmatch '\(A;;[^;)]*(GA|GW|FA|FW|WD|WO)[^;)]*;;;AU\)') 'and not change it'

            # Installing again does not add the users' permission twice.
            Register-DesktopUpdateTask -Updater "$env:SystemRoot\System32\cmd.exe" -InstallDir $install -TaskPath $taskPath -TaskName $taskName
            $dacl = $scheduler.GetFolder($taskPath.TrimEnd('\')).GetTask($taskName).GetSecurityDescriptor(4)
            Assert ([regex]::Matches($dacl, ';;;AU\)').Count -eq 1) 'installing again leaves one permission for users'
        }
        finally {
            Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Confirm:$false
        }
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
