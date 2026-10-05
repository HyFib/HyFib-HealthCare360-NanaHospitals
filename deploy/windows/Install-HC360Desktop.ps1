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
    [switch] $DesktopShortcut
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Exe = 'HC360Desktop.exe'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $PackagePath $Exe))) { throw "HC360 Desktop package not found at '$PackagePath'." }
if ($ServerUrl -and $ServerUrl -notmatch '^https://') { throw 'The server address must start with https://' }
if ($StartPath -and ($StartPath -notmatch '^/' -or $StartPath -match '^//|://')) { throw 'StartPath must be an app path such as /display/queue' }

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
if ($ServerUrl -or $CaFingerprint -or $Kiosk -or $StartPath) {
    Write-Step 'Writing the machine policy'
    $policyDir = Join-Path $env:ProgramData 'HyFib\HC360Desktop'
    New-Item -ItemType Directory -Force -Path $policyDir | Out-Null
    $policy = [ordered]@{}
    if ($ServerUrl) { $policy.serverUrl = $ServerUrl.TrimEnd('/') + '/' }
    if ($CaFingerprint) { $policy.caFingerprint = ($CaFingerprint -replace '[\s:]', '').ToUpperInvariant() }
    if ($Kiosk) { $policy.kiosk = $true; $policy.disableDevTools = $true }
    if ($StartPath) { $policy.startPath = $StartPath }
    $policyPath = Join-Path $policyDir 'policy.json'
    $policy | ConvertTo-Json | Set-Content -Path $policyPath -Encoding UTF8
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
