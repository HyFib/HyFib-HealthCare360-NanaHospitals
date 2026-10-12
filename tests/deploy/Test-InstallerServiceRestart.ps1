<#
.SYNOPSIS
    Checks that the installers have Windows restart a service when it fails: Install-HC360Server.ps1 (for HC360 and for
    PostgreSQL) and Install-PostgreSQL.ps1 set the same failure actions, and the database watchdog task runs every
    minute. CI runs it in Windows PowerShell 5.1 and in PowerShell 7, the two the installers run in.

.DESCRIPTION
    Takes Set-RestartOnFailure from both scripts (running them would install HC360 or PostgreSQL), checks they are the
    same, and applies it to a throwaway service that never runs, then reads the settings back with sc.exe. It also
    registers a throwaway task with the watchdog task's triggers, and reads them back. Those parts need administrator
    rights, which CI has; without them they are skipped. Exit code 1 when a check fails.
    Test-DatabaseWatchdog.ps1 checks what happens when PostgreSQL itself crashes.
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

Write-Host 'Both installers set the same failure actions'
$server = Get-FunctionText 'Install-HC360Server.ps1' 'Set-RestartOnFailure'
$postgres = Get-FunctionText 'Install-PostgreSQL.ps1' 'Set-RestartOnFailure'
Assert ($server -ceq $postgres) 'Install-PostgreSQL.ps1 has the same Set-RestartOnFailure as Install-HC360Server.ps1'
. ([scriptblock]::Create($server))
. ([scriptblock]::Create((Get-FunctionText 'Install-HC360Server.ps1' 'New-EveryMinuteTriggers')))

$principal = New-Object System.Security.Principal.WindowsPrincipal ([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '  skip  the failure actions and the watchdog triggers: changing services and tasks needs administrator rights (CI has them)' -ForegroundColor Yellow
}
else {
    Write-Host 'Windows takes the failure actions'
    $name = 'HC360RestartTest' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    sc.exe create $name binPath= "`"$env:SystemRoot\System32\cmd.exe`" /c exit 1" start= demand | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The throwaway service $name could not be made (sc.exe exit code $LASTEXITCODE)." }
    try {
        Set-RestartOnFailure $name
        $failure = (sc.exe qfailure $name) -join "`n"
        $flag = (sc.exe qfailureflag $name) -join "`n"
        Assert ($failure -match 'RESET_PERIOD[^:]*:\s*86400') 'the failure count starts again after a day'
        $delays = @([regex]::Matches($failure, 'RESTART -- Delay = (\d+)') | ForEach-Object { [int] $_.Groups[1].Value })
        Assert (($delays -join ',') -eq '5000,15000,60000') "it restarts after 5 s, 15 s, then every minute (found: $($delays -join ', '))"
        Assert ($flag -match 'FAILURE_ACTIONS_ON_NONCRASH_FAILURES:\s*TRUE') 'a stop with an error counts as a failure too'
    }
    finally {
        sc.exe delete $name | Out-Null
    }

    Write-Host 'The database watchdog task runs every minute'
    $taskPath = '\HC360Test\'
    $taskName = 'Watchdog triggers ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    Register-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Action (New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument '/c exit 0') `
        -Trigger (New-EveryMinuteTriggers) -Principal (New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount) | Out-Null
    try {
        $triggers = @((Get-ScheduledTask -TaskName $taskName -TaskPath $taskPath).Triggers)
        $repetition = $triggers[0].Repetition
        Assert ($repetition.Interval -eq 'PT1M') "it repeats every minute (found: $($repetition.Interval))"
        Assert ([string]::IsNullOrEmpty($repetition.Duration)) "for as long as Windows runs (found a duration of: $($repetition.Duration))"
        Assert ($triggers.Count -eq 2 -and $triggers[1].CimClass.CimClassName -eq 'MSFT_TaskBootTrigger') 'and from each start-up'
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Confirm:$false
    }
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
