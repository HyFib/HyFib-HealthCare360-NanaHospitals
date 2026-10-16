#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs HC360 Desktop on a hospital workstation, kiosk or waiting-area TV PC.

.DESCRIPTION
    - Installs the Microsoft Edge WebView2 Runtime if it is missing (from the package, offline).
    - Copies HC360 Desktop to Program Files and creates Start menu (and optionally desktop) shortcuts.
    - Registers hc360:// links so other applications can open HC360 screens.
    - Optionally writes a machine policy that pins the server and its CA fingerprint (no setup questions for staff),
      turns on kiosk mode and starts HC360 at sign-in (queue TVs, registration kiosks).
    - Prepares the folder where HC360 keeps this computer's printers (Printers on this computer), so the printers set
      there are the same for every Windows account on the computer.
    - Sets up self-update from the Server Node (SRS DED-18): installs the updater beside HC360 Desktop
      ("<install folder> Updater") and registers the "HC360 Desktop update" scheduled task, which runs it as SYSTEM at
      start-up, at each sign-in and every 4 hours. HC360 Desktop downloads a newer version the server offers into
      %ProgramData%\HyFib\HC360Desktop\Updates; the updater installs it, after checking HyFib's signature itself, while
      HC360 Desktop is closed, and puts the previous version back if that fails. Users may start the task (HC360
      Desktop's "Restart to update") but not change it. -NoAutoUpdate turns self-update off in the machine policy, for
      computers whose software IT distributes itself.
    Installing again with some of -ServerUrl, -CaFingerprint, -Kiosk, -StartPath and -NoAutoUpdate changes those
    entries of the machine policy (policy.json) and keeps the others, a "printers" entry included.

.EXAMPLE
    # Reception workstation, pre-configured
    .\Install-HC360Desktop.ps1 -ServerUrl https://HC360-SRV:7360/ -CaFingerprint 'AB12 CD34 ...'

.EXAMPLE
    # Waiting-area TV showing the OPD queue board
    .\Install-HC360Desktop.ps1 -ServerUrl https://HC360-SRV:7360/ -CaFingerprint 'AB12...' -Kiosk -StartPath '/display/queue' -StartWithWindows
#>
[CmdletBinding()]
param(
    [string] $PackagePath = (Join-Path $PSScriptRoot '..\desktop'),
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'HyFib\HC360 Desktop'),
    [string] $ServerUrl,
    [string] $CaFingerprint,
    [switch] $Kiosk,
    [string] $StartPath,
    [switch] $StartWithWindows,
    [switch] $DesktopShortcut,
    # HC360 Desktop does not update itself from the server; IT upgrades it with this script or its own tools.
    [switch] $NoAutoUpdate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Exe = 'HC360Desktop.exe'
$UpdaterExe = 'HC360DesktopUpdater.exe'
$UpdateTaskPath = '\HyFib\'
$UpdateTaskName = 'HC360 Desktop update'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# The update task's triggers: at each start-up and each sign-in, before HC360 Desktop is open, and every 4 hours from
# now for as long as Windows runs (SRS DED-18).
function New-DesktopUpdateTriggers {
    return @(
        (New-ScheduledTaskTrigger -AtStartup),
        (New-ScheduledTaskTrigger -AtLogOn),
        (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 4)))
}

# Registers the "HC360 Desktop update" task: SYSTEM runs the updater, which installs the package HC360 Desktop staged
# once it has verified it itself, while HC360 Desktop is closed. Authenticated users may read and start the task, for
# HC360 Desktop's "Restart to update", and not change it: what it runs and trusts is fixed here.
function Register-DesktopUpdateTask([string] $Updater, [string] $InstallDir, [string] $TaskPath = '\HyFib\', [string] $TaskName = 'HC360 Desktop update') {
    $action = New-ScheduledTaskAction -Execute $Updater -Argument "apply --install-dir `"$($InstallDir.TrimEnd('\'))`""
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Action $action -Trigger (New-DesktopUpdateTriggers) -Principal $principal `
        -Settings $settings -Force -Description 'Installs the update of HC360 Desktop it downloaded from the HC360 server, once HyFib''s signature checks out (SRS DED-18).' | Out-Null
    $scheduler = New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    $task = $scheduler.GetFolder($TaskPath.TrimEnd('\')).GetTask($TaskName)
    # The DACL (4) in SDDL; read and execute (GRGX) for Authenticated Users goes first, with the other explicit entries.
    $dacl = $task.GetSecurityDescriptor(4)
    if ($dacl -notmatch ';;;AU\)') { $task.SetSecurityDescriptor(($dacl -replace '^(D:[A-Z]*)', '$1(A;;GRGX;;;AU)'), 0) }
}

# The updater's folder: only administrators and SYSTEM may change it, because SYSTEM runs what is in it.
function Set-UpdaterAcl([string] $Path) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @([System.Security.Principal.WellKnownSidType]::LocalSystemSid, [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid)) {
        $identity = New-Object System.Security.Principal.SecurityIdentifier($sid, $null)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inherit, $none, 'Allow')))
    }
    $users = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $none, 'Allow')))
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# The machine policy already on this computer, as an ordered table of its entries, so installing again with some of the
# options changes those and keeps the rest (the pinned server and CA, kiosk mode, the self-update opt-out, printers).
function Read-MachinePolicy([string] $Path) {
    $policy = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $policy }
    $text = Get-Content -LiteralPath $Path -Raw
    if (-not $text -or -not $text.Trim()) { return $policy }
    try { $existing = $text | ConvertFrom-Json }
    catch { throw "$Path is not valid JSON: $($_.Exception.Message) Correct it and run the installer again. Nothing has been changed." }
    if ($existing -isnot [System.Management.Automation.PSCustomObject]) {
        throw "$Path does not hold a JSON object. Correct it and run the installer again. Nothing has been changed."
    }
    foreach ($property in $existing.PSObject.Properties) { $policy[$property.Name] = $property.Value }
    return $policy
}

if (-not (Test-Path (Join-Path $PackagePath $Exe))) { throw "HC360 Desktop package not found at '$PackagePath'." }
if ($ServerUrl -and $ServerUrl -notmatch '^https://') { throw 'The server address must start with https://' }
if ($StartPath -and ($StartPath -notmatch '^/' -or $StartPath -match '^//|://')) { throw 'StartPath must be an app path such as /display/queue' }
$policyPath = Join-Path $env:ProgramData 'HyFib\HC360Desktop\policy.json'
$policy = Read-MachinePolicy $policyPath

# ---------------------------------------------------------------- WebView2 runtime
$webView2Key = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$webView2 = if (Test-Path $webView2Key) { Get-ItemProperty -Path $webView2Key -ErrorAction SilentlyContinue } else { $null }
$webView2Version = if ($webView2 -and ($webView2.PSObject.Properties.Name -contains 'pv')) { $webView2.pv } else { $null }
$hasWebView2 = $webView2Version -and $webView2Version -ne '0.0.0.0'
if (-not $hasWebView2) {
    $bootstrapper = Get-ChildItem -Path $PackagePath -Filter 'MicrosoftEdgeWebview2Setup*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $bootstrapper) {
        throw 'The Microsoft Edge WebView2 Runtime is not installed and no installer was found in the package. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and re-run.'
    }
    Write-Step 'Installing the Microsoft Edge WebView2 Runtime'
    Start-Process -FilePath $bootstrapper.FullName -ArgumentList '/silent', '/install' -Wait
}

# ---------------------------------------------------------------- files
# An update being installed would move the folder while it is copied.
if (Get-ScheduledTask -TaskPath $UpdateTaskPath -TaskName $UpdateTaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskPath $UpdateTaskPath -TaskName $UpdateTaskName -ErrorAction SilentlyContinue
}
$running = Get-Process -Name 'HC360Desktop' -ErrorAction SilentlyContinue
if ($running) {
    Write-Step 'Closing running copies of HC360 Desktop'
    $running | Stop-Process -Force
}

Write-Step "Copying HC360 Desktop to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
robocopy $PackagePath $InstallDir /MIR /XF 'MicrosoftEdgeWebview2Setup*.exe' 'Install-HC360Desktop.ps1' /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy exit code $LASTEXITCODE)." }
$exePath = Join-Path $InstallDir $Exe

# ---------------------------------------------------------------- shortcuts
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string] $Path) {
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $exePath
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation = "$exePath,0"
    $link.Description = 'HyFib HealthCare360'
    $link.Save()
}
New-Shortcut (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\HealthCare360.lnk')
if ($DesktopShortcut) { New-Shortcut (Join-Path $env:PUBLIC 'Desktop\HealthCare360.lnk') }

# ---------------------------------------------------------------- hc360:// links
Write-Step 'Registering hc360:// links'
$protocol = 'HKLM:\SOFTWARE\Classes\hc360'
New-Item -Path "$protocol\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path $protocol -Name '(default)' -Value 'URL:HealthCare360'
Set-ItemProperty -Path $protocol -Name 'URL Protocol' -Value ''
New-Item -Path "$protocol\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$protocol\DefaultIcon" -Name '(default)' -Value "`"$exePath`",0"
Set-ItemProperty -Path "$protocol\shell\open\command" -Name '(default)' -Value "`"$exePath`" `"%1`""

# ---------------------------------------------------------------- machine policy
if ($ServerUrl -or $CaFingerprint -or $Kiosk -or $StartPath -or $NoAutoUpdate) {
    Write-Step 'Writing the machine policy'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $policyPath) | Out-Null
    if ($ServerUrl) { $policy.serverUrl = $ServerUrl.TrimEnd('/') + '/' }
    if ($CaFingerprint) { $policy.caFingerprint = ($CaFingerprint -replace '[\s:]', '').ToUpperInvariant() }
    if ($Kiosk) { $policy.kiosk = $true; $policy.disableDevTools = $true }
    if ($StartPath) { $policy.startPath = $StartPath }
    if ($NoAutoUpdate) { $policy.autoUpdate = $false }
    $policy | ConvertTo-Json -Depth 10 | Set-Content -Path $policyPath -Encoding UTF8
    # Everyone may read the policy; only administrators may change it.
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', 'Allow')))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('NT AUTHORITY\SYSTEM', 'FullControl', 'Allow')))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Users', 'Read', 'Allow')))
    Set-Acl -Path $policyPath -AclObject $acl
}

# ---------------------------------------------------------------- this computer's printers
# HC360 Desktop saves the printers of this computer (labels, wristbands, receipts, documents) for whoever is signed in
# to Windows, so every user may change the files in this folder. HC360 offers the change only to staff allowed to set
# up printers. To fix the printers instead, add a "printers" entry to policy.json (see docs/deployment/DEPLOYMENT.md).
Write-Step 'Preparing the folder for this computer''s printers'
$printersDir = Join-Path $env:ProgramData 'HyFib\HC360Desktop\Workstation'
New-Item -ItemType Directory -Force -Path $printersDir | Out-Null
$printersAcl = Get-Acl -Path $printersDir
$usersSid = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
$printersAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($usersSid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl -Path $printersDir -AclObject $printersAcl

# ---------------------------------------------------------------- self-update from the Server Node (SRS DED-18)
# HC360 Desktop downloads a newer version the server offers into the staging folder, which every user may write to; the
# updater, run by the task as SYSTEM from a folder only administrators may change, verifies HyFib's signature itself and
# installs it while HC360 Desktop is closed. With -NoAutoUpdate the policy turns this off, and the task does nothing.
$updaterSource = Join-Path $PackagePath $UpdaterExe
if (Test-Path -LiteralPath $updaterSource) {
    Write-Step 'Setting up self-update from the HC360 server'
    $updaterDir = "$($InstallDir.TrimEnd('\')) Updater"
    New-Item -ItemType Directory -Force -Path $updaterDir | Out-Null
    Set-UpdaterAcl $updaterDir
    Copy-Item -LiteralPath $updaterSource -Destination (Join-Path $updaterDir $UpdaterExe) -Force
    Remove-Item -LiteralPath (Join-Path $updaterDir "$UpdaterExe.old") -ErrorAction SilentlyContinue

    $stagingDir = Join-Path $env:ProgramData 'HyFib\HC360Desktop\Updates'
    New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
    $stagingAcl = Get-Acl -Path $stagingDir
    $stagingAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($usersSid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -Path $stagingDir -AclObject $stagingAcl

    Register-DesktopUpdateTask -Updater (Join-Path $updaterDir $UpdaterExe) -InstallDir $InstallDir
    if ($NoAutoUpdate) { Write-Host '    Self-update is turned off in the machine policy (-NoAutoUpdate).' }
}
else {
    Write-Warning "The package has no $UpdaterExe, so HC360 Desktop will not update itself from the server: upgrade it with this script."
}

# ---------------------------------------------------------------- start with Windows (kiosks / TV boards)
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
if ($StartWithWindows) {
    Set-ItemProperty -Path $runKey -Name 'HC360Desktop' -Value "`"$exePath`""
}
else {
    Remove-ItemProperty -Path $runKey -Name 'HC360Desktop' -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'HC360 Desktop is installed. Open "HealthCare360" from the Start menu.' -ForegroundColor Green
if (-not $ServerUrl) {
    Write-Host 'On first start it finds the hospital server on the WiFi and asks for an enrolment code (Administration > Devices).'
}
