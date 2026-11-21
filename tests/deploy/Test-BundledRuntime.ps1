<#
.SYNOPSIS
    Checks tools/release/Test-BundledRuntime.ps1, which Publish-HC360.ps1 runs after each self-contained publish: a
    package must carry a .NET runtime at least as new as the ASP.NET Core packages HC360 is built with. CI runs it in
    Windows PowerShell 5.1 and in PowerShell 7.

.DESCRIPTION
    Writes a Directory.Packages.props and a published program's runtimeconfig.json for each case to a folder of its own,
    runs the script on them in a process of its own, as the release does, then checks its exit code and what it says.
    Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$checker = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\tools\release\Test-BundledRuntime.ps1')).Path
$shell = (Get-Process -Id $PID).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-runtime-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $root | Out-Null

# Runs the checker on a published program carrying $Frameworks (name = version; $null for a runtimeconfig.json with no
# includedFrameworks, 'missing' for no runtimeconfig.json at all), built with ASP.NET Core packages of $AspNetCore.
function Invoke-Checker([string[]] $AspNetCore, $Frameworks) {
    $case = Join-Path $root ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $case | Out-Null
    $lines = ($AspNetCore | ForEach-Object -Begin { $i = 0 } -Process {
            $i++
            "    <PackageVersion Include=`"Microsoft.AspNetCore.Example$i`" Version=`"$_`" />"
        }) -join "`n"
    $packages = Join-Path $case 'Directory.Packages.props'
    [System.IO.File]::WriteAllText($packages, "<Project>`n  <ItemGroup>`n    <PackageVersion Include=`"Npgsql`" Version=`"10.0.1`" />`n$lines`n  </ItemGroup>`n</Project>`n")

    if ($Frameworks -ne 'missing') {
        $options = '"tfm": "net10.0"'
        if ($null -ne $Frameworks) {
            $items = ($Frameworks.GetEnumerator() | Sort-Object Name | ForEach-Object { "{ `"name`": `"$($_.Name)`", `"version`": `"$($_.Value)`" }" }) -join ', '
            $options += ", `"includedFrameworks`": [ $items ]"
        }
        else {
            $options += ', "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }'
        }

        [System.IO.File]::WriteAllText((Join-Path $case 'Example.runtimeconfig.json'), "{ `"runtimeOptions`": { $options } }")
    }

    $ErrorActionPreference = 'Continue'
    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $checker -PublishDir $case -Program 'Example' -PackagesPath $packages 2>&1 | ForEach-Object { "$_" } | Out-String
    $ErrorActionPreference = 'Stop'
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Test-Case([string] $Name, [string[]] $AspNetCore, $Frameworks, [int] $Expected, [string] $Says) {
    $run = Invoke-Checker $AspNetCore $Frameworks
    Assert ($run.ExitCode -eq $Expected) "$Name (exit code $($run.ExitCode), expected $Expected)"
    if ($Says) { Assert ($run.Output -like "*$Says*") "  and says: $Says" }
    if ($run.ExitCode -ne $Expected) { Write-Host $run.Output }
}

$server = @{ 'Microsoft.NETCore.App' = '10.0.15'; 'Microsoft.AspNetCore.App' = '10.0.15' }
try {
    Write-Host 'A package carrying the runtime of its ASP.NET Core packages'
    Test-Case 'the same patch passes' @('10.0.15') $server 0 'at least the ASP.NET Core packages'' 10.0.15'
    Test-Case 'a newer runtime passes' @('10.0.15') @{ 'Microsoft.NETCore.App' = '10.0.16'; 'Microsoft.AspNetCore.App' = '10.0.16' } 0
    Test-Case 'a desktop runtime of the same patch passes' @('10.0.15') @{ 'Microsoft.NETCore.App' = '10.0.15'; 'Microsoft.WindowsDesktop.App' = '10.0.15' } 0
    Test-Case 'the lowest ASP.NET Core package sets the bar' @('10.0.15', '10.0.14') @{ 'Microsoft.NETCore.App' = '10.0.14' } 0

    Write-Host 'A package missing the patch'
    Test-Case 'an older runtime fails' @('10.0.15') @{ 'Microsoft.NETCore.App' = '10.0.14'; 'Microsoft.AspNetCore.App' = '10.0.14' } 1 'carries Microsoft.NETCore.App 10.0.14, older than the ASP.NET Core packages it is built with (10.0.15)'
    Test-Case 'one older runtime among them fails' @('10.0.15') @{ 'Microsoft.NETCore.App' = '10.0.15'; 'Microsoft.AspNetCore.App' = '10.0.14' } 1 'Microsoft.AspNetCore.App 10.0.14'
    Test-Case 'a framework-dependent program fails' @('10.0.15') $null 1 'is not self-contained'
    Test-Case 'no runtimeconfig.json fails' @('10.0.15') 'missing' 1 'does not exist'
    Test-Case 'no ASP.NET Core package fails' @() $server 1 'names no Microsoft.AspNetCore package'
}
finally {
    $ErrorActionPreference = 'Stop'
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) check(s) failed."; exit 1 }
Write-Host 'Every check passed.'
