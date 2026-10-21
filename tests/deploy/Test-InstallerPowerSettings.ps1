<#
.SYNOPSIS
    Checks Install-HC360Server.ps1's power settings for the always-on Server Node (SRS DED-15): no sleep, hibernation,
    disk spin-down or WiFi power saving, on mains (AC) and on a USB UPS's battery (DC) in a power cut. CI runs it in
    Windows PowerShell 5.1 and in PowerShell 7, the two the installer runs in.

.DESCRIPTION
    Takes Set-AlwaysOnPower from the installer (running it would install HC360) and runs it with a powercfg that only
    records what it is asked, so the machine's own power plan is left alone and no administrator rights are needed. It
    checks every timeout and the WiFi adapter's power saving for both AC and DC, that the critical-battery action is left
    alone, that a setting Windows refuses is reported without stopping the others, and that the installer makes its
    power settings through the function. Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../deploy/windows/Install-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$definitions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-AlwaysOnPower' }, $true))
if ($definitions.Count -ne 1) { throw 'Install-HC360Server.ps1 no longer defines Set-AlwaysOnPower.' }
. ([scriptblock]::Create($definitions[0].Extent.Text))

# The Wireless Adapter Settings subgroup and its Power Saving Mode setting, in the active plan.
$wifi = 'SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a'
$script:calls = New-Object System.Collections.Generic.List[string]

Write-Host 'The Server Node stays awake on mains and on the UPS''s battery'
$taken = Set-AlwaysOnPower -PowerCfg { $script:calls.Add($args -join ' '); 0 }
Assert ($taken -eq $true) 'it says Windows took every setting'
foreach ($timeout in 'standby-timeout', 'hibernate-timeout', 'disk-timeout') {
    Assert ($script:calls -contains "/change $timeout-ac 0") "$timeout-ac is never (0), on mains"
    Assert ($script:calls -contains "/change $timeout-dc 0") "$timeout-dc is never (0), on battery"
}
Assert ($script:calls -contains "/setacvalueindex $wifi 0") 'the WiFi adapter saves no power (Maximum Performance) on mains'
Assert ($script:calls -contains "/setdcvalueindex $wifi 0") 'the WiFi adapter saves no power (Maximum Performance) on battery'
$applied = $script:calls.LastIndexOf('/setactive SCHEME_CURRENT')
Assert ($applied -eq $script:calls.Count - 1) 'the active plan is applied last, after its values are set'
$battery = @($script:calls | Where-Object { $_ -match 'e73a048d-bf27-4f12-9731-8b2076e8891f|SUB_BATTERY|BATACTION|/hibernate ' })
Assert ($battery.Count -eq 0) "the critical-battery action is left alone, so the UPS can still have the server shut down cleanly (found: $($battery -join '; '))"
Assert ($script:calls.Count -eq 9) "nothing else is changed (found $($script:calls.Count) settings: $($script:calls -join '; '))"

Write-Host 'A setting Windows refuses is reported, and the others are still made'
$script:calls.Clear()
$output = @(Set-AlwaysOnPower -PowerCfg { $script:calls.Add($args -join ' '); if ($args -contains 'disk-timeout-dc') { 1 } else { 0 } } 3>&1)
$warnings = @($output | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
$returned = @($output | Where-Object { $_ -is [bool] })
Assert ($warnings.Count -eq 1 -and "$($warnings[0])" -match 'disk-timeout-dc.*exit code 1') "one warning names it (found: $($warnings -join '; '))"
Assert ($returned.Count -eq 1 -and -not $returned[0]) 'it says not every setting was taken, so the installer does not claim they were'
Assert ($script:calls.Count -eq 9) 'the settings after it are still made'

Write-Host 'The installer makes its power settings through Set-AlwaysOnPower'
$definition = $definitions[0].Extent
$commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
$outside = @($commands | Where-Object { $_.Extent.StartOffset -lt $definition.StartOffset -or $_.Extent.EndOffset -gt $definition.EndOffset })
Assert (@($outside | Where-Object { $_.GetCommandName() -eq 'Set-AlwaysOnPower' }).Count -eq 1) 'it runs Set-AlwaysOnPower once'
$stray = @($outside | Where-Object { $_.GetCommandName() -match '^powercfg(\.exe)?$' })
Assert ($stray.Count -eq 0) "it runs powercfg nowhere else, where it would not be tested (found: $($stray -join '; '))"

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
