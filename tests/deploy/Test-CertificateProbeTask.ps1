<#
.SYNOPSIS
    Registers the installer's daily certificate probe task under a name of its own, has Task Scheduler run it as SYSTEM, as
    it does every day, and checks what it did (SRS DED-04): the probe ran with the arguments the installer gave it, its exit
    code is the task's result, and what it found is in the Application event log, under the source the installer registers.

.DESCRIPTION
    Takes Register-CertificateProbe, Get-CertificateProbePin and Invoke-CertificateProbeTask from Install-HC360Server.ps1
    (running it would install HC360) and points the probe at a port of this machine nothing listens on: it must find no TLS
    connection there and exit 2, critical. A task whose program does not exist is run the same way, to show that the checks
    tell a probe that ran from a task that did not. It runs the commands CA-ROLLOVER.md gives for the day of a switch,
    which give the task a new fingerprint, and checks that Uninstall-HC360Server.ps1 removes every scheduled task the
    installer registers. Needs administrator rights (Task Scheduler, SYSTEM, the event log): CI runs it on its
    Windows runner, in Windows PowerShell 5.1 and in PowerShell 7. Exit code 1 when a check fails.

.PARAMETER Server
    The built HyFib.HealthCare360.Server.exe.
#>
param([Parameter(Mandatory)] [string] $Server)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$deploy = Join-Path $PSScriptRoot '..\..\deploy\windows'
$installer = (Resolve-Path -LiteralPath (Join-Path $deploy 'Install-HC360Server.ps1')).Path
$uninstaller = (Resolve-Path -LiteralPath (Join-Path $deploy 'Uninstall-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$functions = 'Register-CertificateProbe', 'Get-CertificateProbePin', 'Invoke-CertificateProbeTask'
$found = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) | Where-Object { $functions -contains $_.Name })
if ($found.Count -ne $functions.Count) { throw "Install-HC360Server.ps1 no longer defines each of $($functions -join ', ')." }
foreach ($function in $found) { . ([scriptblock]::Create($function.Extent.Text)) }

# The names the installer gives Register-ScheduledTask: a string, or the default of the parameter of its function.
function Get-InstallerTaskNames($Ast) {
    $names = New-Object System.Collections.Generic.List[string]
    $registers = $Ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Register-ScheduledTask' }, $true)
    foreach ($command in $registers) {
        $elements = $command.CommandElements
        for ($i = 1; $i -lt $elements.Count - 1; $i++) {
            if (-not ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -eq 'TaskName')) { continue }
            $value = $elements[$i + 1]
            if ($value -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $names.Add($value.Value); continue }
            if (-not ($value -is [System.Management.Automation.Language.VariableExpressionAst])) { $names.Add("(a name this test cannot read: $($value.Extent.Text))"); continue }
            $function = $command.Parent
            while ($function -and -not ($function -is [System.Management.Automation.Language.FunctionDefinitionAst])) { $function = $function.Parent }
            $parameters = if ($function -and $function.Parameters) { @($function.Parameters) } else { @() }
            foreach ($parameter in $parameters) {
                if ($parameter.Name.VariablePath.UserPath -eq $value.VariablePath.UserPath -and $parameter.DefaultValue) { $names.Add($parameter.DefaultValue.Value) }
            }
        }
    }
    return $names.ToArray()
}

Write-Host 'The uninstaller removes every scheduled task the installer registers'
$registered = @(Get-InstallerTaskNames $ast)
$uninstallerAst = [System.Management.Automation.Language.Parser]::ParseFile($uninstaller, [ref] $null, [ref] $null)
$strings = $uninstallerAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true)
$removed = @($strings | ForEach-Object { $_.Value } | Where-Object { $_ -like 'HC360 *' })
Assert ($registered -contains 'HC360 certificate probe' -and $registered.Count -ge 3) "the installer's tasks are found ($($registered -join '; '))"
foreach ($name in $registered) { Assert ($removed -contains $name) "Uninstall-HC360Server.ps1 removes '$name'" }

$exe = (Resolve-Path -LiteralPath $Server).Path
$source = 'HyFib HealthCare360'
if (-not [System.Diagnostics.EventLog]::SourceExists($source)) {
    # As the installer registers it, before its tasks run.
    [System.Diagnostics.EventLog]::CreateEventSource($source, 'Application')
}
$listener = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback), 0
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
# Freed again: nothing listens on it now.
$address = "https://127.0.0.1:$port/"
$fingerprint = -join (1..32 | ForEach-Object { 'AB' })
$name = 'HC360 certificate probe test ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$missing = $name + ' (no program)'
# A time already past today (except in the first minutes after midnight): a first run there would count as missed.
$at = (Get-Date).AddMinutes(-5).ToString('HH:mm')

try {
    Write-Host 'The task is registered as the installer registers it'
    Assert ($null -eq (Register-CertificateProbe $exe $address $fingerprint $at $name)) 'a first registration had no fingerprint before'
    # 267011 (0x41303): Task Scheduler's "has not yet run".
    Start-Sleep -Seconds 3
    $first = Get-ScheduledTaskInfo -TaskName $name -TaskPath '\HyFib\'
    Assert ($first.LastTaskResult -eq 267011) "registering it does not run it at once, at $at already past today (found: result $($first.LastTaskResult), run $($first.LastRunTime))"
    $task = Get-ScheduledTask -TaskName $name -TaskPath '\HyFib\'
    $action = @($task.Actions)[0]
    Assert ($task.Principal.UserId -match 'SYSTEM$' -and "$($task.Principal.RunLevel)" -eq 'Highest') "it runs as SYSTEM (found: $($task.Principal.UserId), $($task.Principal.RunLevel))"
    Assert ($action.Execute -eq $exe) "it runs the server's program ($($action.Execute))"
    Assert ($action.Arguments -eq "certificates probe $address --ca-fingerprint $fingerprint --event-log") "with the probe's arguments ($($action.Arguments))"
    $trigger = @($task.Triggers)
    Assert ($trigger.Count -eq 1 -and $trigger[0].CimClass.CimClassName -eq 'MSFT_TaskDailyTrigger' -and ([datetime] $trigger[0].StartBoundary).ToString('HH:mm') -eq $at `
            -and ([datetime] $trigger[0].StartBoundary) -gt (Get-Date)) `
        "every day at $at, from the next one to come (found: $(@($trigger | ForEach-Object { "$($_.CimClass.CimClassName) $($_.StartBoundary)" }) -join '; '))"
    Assert ($task.Settings.StartWhenAvailable -eq $true) 'on a day the machine was off at that time, it runs once the machine starts'
    Assert ((Get-CertificateProbePin $name) -eq $fingerprint) 'the fingerprint it checks for is read back'

    Write-Host 'Task Scheduler runs the probe as SYSTEM, and what it finds reaches the event log'
    $since = (Get-Date).AddSeconds(-1)
    $result = Invoke-CertificateProbeTask $name
    $seen = Get-ScheduledTaskInfo -TaskName $name -TaskPath '\HyFib\'
    $state = (Get-ScheduledTask -TaskName $name -TaskPath '\HyFib\').State
    Assert ($result -eq 2) ("the task's result is the probe's exit code 2, critical: nothing serves $address " +
        "(found: $result; Task Scheduler reports $state, last run $($seen.LastRunTime), result $($seen.LastTaskResult))")
    # The text the probe wrote, as the event's own string: the message file may not render it the same in both PowerShells.
    $written = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $source; StartTime = $since } -ErrorAction SilentlyContinue |
            ForEach-Object { [pscustomobject] @{ Level = $_.Level; Text = (@($_.Properties) | ForEach-Object { [string] $_.Value }) -join ' ' } } |
            Where-Object { $_.Text -like "HC360 certificate probe, $address*" })
    Assert ($written.Count -eq 1 -and $written[0].Level -eq 2 -and $written[0].Text -like '*No TLS connection*') `
        "one error in the Application event log says so (found: $(@($written | ForEach-Object { "level $($_.Level): $($_.Text)" }) -join ' | '))"

    Write-Host 'Run again, it keeps one task and says what it checked for before'
    $other = -join (1..32 | ForEach-Object { 'CD' })
    Assert ((Register-CertificateProbe $exe $address $other $at $name) -eq $fingerprint) 'the fingerprint before is returned'
    Assert ((Get-CertificateProbePin $name) -eq $other) 'and the new one is checked for'
    Assert (@(Get-ScheduledTask -TaskPath '\HyFib\' | Where-Object { $_.TaskName -eq $name }).Count -eq 1) 'one task'

    Write-Host "CA-ROLLOVER.md's commands for the day of a switch give the task the new fingerprint"
    $doc = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\deployment\CA-ROLLOVER.md') -Raw
    $step = [regex]::Match($doc, '\$probe = Get-ScheduledTask[\s\S]*?\| Set-ScheduledTask \| Out-Null').Value
    Assert ($step -ne '') 'they are in CA-ROLLOVER.md'
    $rolled = -join (1..32 | ForEach-Object { 'EF' })
    if ($step) { & ([scriptblock]::Create($step.Replace("'HC360 certificate probe'", "'$name'").Replace('<new>', $rolled))) }
    Assert ((Get-CertificateProbePin $name) -eq $rolled) 'the task checks for the new fingerprint'
    $after = Get-ScheduledTask -TaskName $name -TaskPath '\HyFib\'
    Assert ($after.Principal.UserId -match 'SYSTEM$' -and @($after.Triggers).Count -eq 1 -and @($after.Actions)[0].Execute -eq $exe -and $after.Settings.StartWhenAvailable -eq $true) 'and runs as before'

    Write-Host 'A task that cannot start the probe is told apart'
    $null = Register-CertificateProbe (Join-Path $env:TEMP 'no-such-program.exe') $address $fingerprint $at $missing
    $failed = Invoke-CertificateProbeTask $missing '\HyFib\' 30
    Assert ($failed -ne 0 -and $failed -ne 1 -and $failed -ne 2) "its result is none of the probe's exit codes (found: $(if ($null -eq $failed) { 'no result within 30 seconds' } else { '0x{0:X8}' -f $failed }))"
}
finally {
    foreach ($task in $name, $missing) {
        Get-ScheduledTask -TaskName $task -TaskPath '\HyFib\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
    }
}

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
