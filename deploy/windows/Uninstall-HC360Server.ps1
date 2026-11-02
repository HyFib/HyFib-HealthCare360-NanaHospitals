#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the HC360 Server service, its scheduled tasks, firewall rules and program files. Hospital data is KEPT unless
    -RemoveData is given and confirmed by typing the facility code.

.EXAMPLE
    .\Uninstall-HC360Server.ps1
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'HyFib\HealthCare360\Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'HyFib\HealthCare360'),
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ServiceName = 'HC360Server'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Delete Windows service')) {
        sc.exe delete $ServiceName | Out-Null
    }
}

# Every scheduled task Install-HC360Server.ps1 registers, by name: HC360 Desktop's update task, in the same folder, is left
# to its own uninstaller (tests/deploy/Test-CertificateProbeTask.ps1 checks the list against the installer).
foreach ($task in 'HC360 disk encryption check', 'HC360 database watchdog', 'HC360 certificate probe') {
    Get-ScheduledTask -TaskName $task -TaskPath '\HyFib\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
}
Get-NetFirewallRule -Group 'HyFib HealthCare360' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if ((Test-Path $InstallDir) -and $PSCmdlet.ShouldProcess($InstallDir, 'Remove program files')) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($RemoveData) {
    Write-Warning "This permanently deletes $($DataDir): encryption keys, certificates, uploaded documents and local backups."
    Write-Warning 'Without the keys, existing backups and encrypted fields can no longer be read. The PostgreSQL database is not touched.'
    $answer = Read-Host 'Type DELETE HOSPITAL DATA to continue'
    if ($answer -ceq 'DELETE HOSPITAL DATA') {
        Remove-Item -Path $DataDir -Recurse -Force
        Write-Host 'Data folder removed.'
    }
    else {
        Write-Host 'Kept the data folder.'
    }
}
else {
    Write-Host "Kept hospital data in $DataDir (configuration, keys, certificates, documents, backups)."
}
