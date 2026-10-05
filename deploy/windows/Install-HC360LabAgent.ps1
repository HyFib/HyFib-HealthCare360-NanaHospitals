#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs (or upgrades) the HC360 lab agent on the PC a serial lab analyser is cabled to.

.DESCRIPTION
    The lab agent relays a serial analyser's COM port to the HC360 server, which runs the analyser's protocol. It is a
    Windows service, so it keeps relaying when nobody is signed in to the PC.
    - Copies the agent to Program Files.
    - Registers the "HC360LabAgent" service: its own virtual account (NT SERVICE\HC360LabAgent), automatic start, and
      restart on failure. Registers its Event Log source.
    - With -EnrolmentCode: enrols this PC with the server. The server must present a certificate from the hospital CA
      whose fingerprint you give (the server installer prints it). The device secret is protected for this PC, and the
      configuration folder can be read only by SYSTEM, Administrators and the agent's service account.
    - Starts the service.
    Then, in HC360: Lab setup > Analysers > Connection, choose "Serial port through a lab agent", this agent and the COM
    port.

.EXAMPLE
    # First installation: create the code in Administration > Devices > Enrol a device (device type: Lab analyser agent)
    .\Install-HC360LabAgent.ps1 -ServerUrl https://HC360-SRV:7360/ -CaFingerprint 'AB12 CD34 ...' -EnrolmentCode ABCD-EFGH `
        -AgentName 'Haematology PC'

.EXAMPLE
    # Upgrade in place (the enrolment is kept)
    .\Install-HC360LabAgent.ps1

.EXAMPLE
    # Remove the agent (revoke the device in Administration > Devices as well)
    .\Install-HC360LabAgent.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $PackagePath = $PSScriptRoot,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'HyFib\HC360 Lab Agent'),
    [string] $ServerUrl,
    [string] $CaFingerprint,
    [string] $EnrolmentCode,
    [string] $AgentName = "$env:COMPUTERNAME lab agent",
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'HC360LabAgent'
$ServiceAccount = "NT SERVICE\$ServiceName"
$ExeName = 'HC360LabAgent.exe'
$EventSource = 'HyFib HC360 Lab Agent'
$DataDir = Join-Path $env:ProgramData 'HyFib\HC360LabAgent'
$ConfigPath = Join-Path $DataDir 'agent.json'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# Only SYSTEM, Administrators and the agent's own service account; nothing inherited from the parent folder.
function Set-AgentAcl([string] $Path) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    foreach ($identity in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators')) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inherit, $none, 'Allow')))
    }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($ServiceAccount, 'ReadAndExecute', $inherit, $none, 'Allow')))
    Set-Acl -Path $Path -AclObject $acl
}

function Stop-Agent {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Step 'Stopping the running lab agent'
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    return $service
}

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    if (Stop-Agent) {
        Write-Step "Removing the '$ServiceName' service"
        sc.exe delete $ServiceName | Out-Null
    }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    Write-Host ''
    Write-Host "The lab agent is removed. Its enrolment is still in $DataDir." -ForegroundColor Green
    Write-Host 'If this PC no longer relays analysers, delete that folder and revoke the device in Administration > Devices.'
    return
}

# ---------------------------------------------------------------- checks
if (-not (Test-Path (Join-Path $PackagePath $ExeName))) {
    throw "The lab agent package was not found at '$PackagePath' (expected $ExeName). Extract HC360-LabAgent-<version>-win-x64.zip and run this script from it."
}
$enrol = -not [string]::IsNullOrWhiteSpace($EnrolmentCode)
if ($enrol) {
    if (-not $ServerUrl -or $ServerUrl -notmatch '^https://') { throw 'Give the server address, for example -ServerUrl https://HC360-SRV:7360/' }
    if (-not $CaFingerprint) { throw 'Give the hospital CA fingerprint that the server installer printed, for example -CaFingerprint "AB12 CD34 ..."' }
}
elseif (-not (Test-Path $ConfigPath)) {
    throw 'This PC is not enrolled yet. Create a code in Administration > Devices (device type: Lab analyser agent), then run again with -ServerUrl, -CaFingerprint and -EnrolmentCode.'
}

$existing = Stop-Agent

# ---------------------------------------------------------------- files
Write-Step "Copying the lab agent to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
robocopy $PackagePath $InstallDir /MIR /XF 'Install-HC360LabAgent.ps1' /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy exit code $LASTEXITCODE)." }
$exePath = Join-Path $InstallDir $ExeName

# ---------------------------------------------------------------- service (its account must exist before the folder rights name it)
if (-not $existing) {
    Write-Step "Registering the Windows service '$ServiceName'"
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$exePath`"" `
        -DisplayName 'HyFib HC360 Lab Agent' `
        -Description 'Relays serial laboratory analysers to the HyFib HealthCare360 server. Runs without anyone signed in.' `
        -StartupType Automatic | Out-Null
}
sc.exe config $ServiceName obj= $ServiceAccount start= auto | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    [System.Diagnostics.EventLog]::CreateEventSource($EventSource, 'Application')
}

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Set-AgentAcl -Path $InstallDir
Set-AgentAcl -Path $DataDir

# ---------------------------------------------------------------- enrolment
if ($enrol) {
    Write-Step 'Enrolling this PC with the HC360 server'
    & $exePath enrol --server $ServerUrl --code $EnrolmentCode --ca-fingerprint $CaFingerprint --name $AgentName --config $ConfigPath
    if ($LASTEXITCODE -ne 0) { throw 'Enrolment failed; the reason is shown above. Create a new code if this one has expired.' }
}

# ---------------------------------------------------------------- start
Write-Step 'Starting the lab agent'
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

Write-Host ''
Write-Host 'The HC360 lab agent is running.' -ForegroundColor Green
Write-Host 'Next, in HC360: Lab setup > Analysers > Connection, choose "Serial port through a lab agent", this agent and the COM port.'
Write-Host 'The agent shows as online there within a minute. Problems are written to Event Viewer > Windows Logs > Application (source "HyFib HC360 Lab Agent").'
