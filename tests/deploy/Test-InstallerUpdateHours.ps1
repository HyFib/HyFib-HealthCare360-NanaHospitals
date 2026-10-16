<#
.SYNOPSIS
    Checks Install-HC360Server.ps1's Windows Update active hours (SRS DED-15), which keep update restarts out of clinical
    hours. CI runs it in Windows PowerShell 5.1 and in PowerShell 7, the two the installer runs in.

.DESCRIPTION
    Takes Get-ActiveHoursProblem and Set-UpdateActiveHours from the installer (running it would install HC360), checks
    which hours Windows would refuse, and has Set-UpdateActiveHours write to throwaway keys under HKCU (so no
    administrator rights are needed and the machine's own update settings are left alone), then reads them back.
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

. ([scriptblock]::Create((Get-FunctionText 'Install-HC360Server.ps1' 'Get-ActiveHoursProblem')))
. ([scriptblock]::Create((Get-FunctionText 'Install-HC360Server.ps1' 'Set-UpdateActiveHours')))

Write-Host 'Windows takes the hours the installer takes'
Assert ($null -eq (Get-ActiveHoursProblem 6 23)) 'the default, 06:00 to 23:00 (17 hours)'
Assert ($null -eq (Get-ActiveHoursProblem 22 16)) 'hours past midnight, 22:00 to 16:00 (18 hours)'
Assert ($null -eq (Get-ActiveHoursProblem 0 18)) '00:00 to 18:00 (18 hours)'
Assert ($null -ne (Get-ActiveHoursProblem 6 1)) 'not 06:00 to 01:00 (19 hours, more than Windows allows)'
Assert ($null -ne (Get-ActiveHoursProblem 0 23)) 'not 00:00 to 23:00 (23 hours)'
Assert ($null -ne (Get-ActiveHoursProblem 8 8)) 'not a start and an end at the same hour'

Write-Host 'The active hours are written as Windows Update reads them'
$root = 'HKCU:\Software\HC360ActiveHoursTest' + [guid]::NewGuid().ToString('N').Substring(0, 8)
try {
    $policy = "$root\Policies\WindowsUpdate"
    $settings = "$root\UX\Settings"
    Set-UpdateActiveHours 7 1 -PolicyKey $policy -SettingsKey $settings
    $p = Get-ItemProperty -LiteralPath $policy
    $s = Get-ItemProperty -LiteralPath $settings
    Assert ($p.SetActiveHours -eq 1) 'the policy turns active hours on'
    Assert ($p.ActiveHoursStart -eq 7 -and $p.ActiveHoursEnd -eq 1) "the policy has 7 to 1 (found $($p.ActiveHoursStart) to $($p.ActiveHoursEnd))"
    Assert ($s.ActiveHoursStart -eq 7 -and $s.ActiveHoursEnd -eq 1) "the machine's own setting has 7 to 1 (found $($s.ActiveHoursStart) to $($s.ActiveHoursEnd))"
    $kind = (Get-Item -LiteralPath $policy).GetValueKind('ActiveHoursStart')
    Assert ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) "the hours are DWORDs (found $kind)"

    Set-UpdateActiveHours 6 23 -PolicyKey $policy -SettingsKey $settings
    $p = Get-ItemProperty -LiteralPath $policy
    Assert ($p.ActiveHoursStart -eq 6 -and $p.ActiveHoursEnd -eq 23) 'running it again replaces the hours'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
