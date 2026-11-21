#Requires -Version 5.1
<#
.SYNOPSIS
    Checks that a self-contained package carries a .NET runtime at least as new as the ASP.NET Core packages HC360 is
    built with.

.DESCRIPTION
    HC360's packages are self-contained: each takes the .NET runtime the SDK in global.json carries for net10.0, and the
    hospital's machines run that runtime and no other. .NET ships its security fixes as runtime patches (10.0.15 is the
    security release of 10 November 2026), on the same day as the ASP.NET Core packages of the same number, which
    Dependabot then proposes. A package built with an SDK older than those packages would carry the runtime before the
    fix, with nothing else to say so.

    So: the published program's runtimeconfig.json names the runtimes it carries (includedFrameworks), and each of them
    must be at least the newest Microsoft.AspNetCore package in Directory.Packages.props: a patch any of those packages
    has reached exists, and the runtime must carry it too. Publish-HC360.ps1 runs this after each self-contained publish.
    Exit code 1, with the reasons, when a check fails.

.EXAMPLE
    .\tools\release\Test-BundledRuntime.ps1 -PublishDir .\artifacts\server -Program HyFib.HealthCare360.Server
#>
[CmdletBinding()]
param(
    # The folder dotnet publish wrote the self-contained program to.
    [Parameter(Mandatory = $true)] [string] $PublishDir,
    # The program's name, without an extension: its runtimeconfig.json is <Program>.runtimeconfig.json.
    [Parameter(Mandatory = $true)] [string] $Program,
    # Directory.Packages.props; by default the repository's.
    [string] $PackagesPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Here, not as the parameter's default: Windows PowerShell 5.1 leaves $PSScriptRoot empty while it reads the defaults.
if (-not $PackagesPath) { $PackagesPath = Join-Path $PSScriptRoot '..\..\Directory.Packages.props' }

# A runtime's version as numbers, without a pre-release label (10.0.15, 11.0.0-rc.1 → 11.0.0); $null when it is none.
function ConvertTo-RuntimeVersion([string] $Text) {
    $plain = ($Text -split '[-+]')[0]
    $parsed = $null
    if ([System.Version]::TryParse($plain, [ref] $parsed)) { return $parsed }
    return $null
}

$problems = New-Object System.Collections.Generic.List[string]

# The newest patch any Microsoft.AspNetCore package is at. A package left behind (one only the tests use, say) must not
# let a runtime older than the others' patch through: that patch exists, so the runtime has to carry it.
[xml] $packages = Get-Content -LiteralPath $PackagesPath -Raw
$floor = $null
foreach ($node in $packages.SelectNodes("//PackageVersion[starts-with(@Include, 'Microsoft.AspNetCore.')]")) {
    $version = ConvertTo-RuntimeVersion $node.GetAttribute('Version')
    if ($null -ne $version -and ($null -eq $floor -or $version -gt $floor)) { $floor = $version }
}

if ($null -eq $floor) {
    $problems.Add("$PackagesPath names no Microsoft.AspNetCore package, so there is no runtime patch to check against.")
}

$configPath = Join-Path $PublishDir "$Program.runtimeconfig.json"
$included = @()
if (-not (Test-Path -LiteralPath $configPath)) {
    $problems.Add("$configPath does not exist: publish $Program first, self-contained, to $PublishDir.")
}
else {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $options = $config.runtimeOptions
    if ($null -ne $options -and ($options.PSObject.Properties.Name -contains 'includedFrameworks')) { $included = @($options.includedFrameworks) }
    if ($included.Count -eq 0) {
        $problems.Add("$Program is not self-contained: its runtimeconfig.json names no runtime it carries (includedFrameworks). The hospital's machines have no .NET runtime of their own; publish it with --self-contained true.")
    }
}

if ($null -ne $floor) {
    foreach ($framework in $included) {
        $version = ConvertTo-RuntimeVersion ([string] $framework.version)
        if ($null -eq $version) {
            $problems.Add("$Program carries $($framework.name) $($framework.version), which is not a version this check can read.")
        }
        elseif ($version -lt $floor) {
            $problems.Add("$Program carries $($framework.name) $($framework.version), older than the ASP.NET Core packages it is built with ($floor): it would miss the runtime's fixes of that patch. Build with an SDK that carries $floor or later for net10.0 (global.json).")
        }
    }
}

if ($problems.Count -gt 0) {
    foreach ($problem in $problems) { Write-Host "FAIL  $problem" }
    exit 1
}

$carried = ($included | ForEach-Object { "$($_.name) $($_.version)" }) -join ', '
Write-Host "ok    $Program carries $carried, at least the ASP.NET Core packages' $floor."
