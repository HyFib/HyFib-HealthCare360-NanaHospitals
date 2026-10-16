#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, tests and packages a HyFib HealthCare360 release.

.DESCRIPTION
    Produces, in .\artifacts:
      HC360-Server-<version>-win-x64.zip   server (self-contained, web apps included) + deployment scripts
      HC360-Desktop-<version>-win-x64.zip  HC360 Desktop (self-contained) + its updater + workstation installer
      HC360-LabAgent-<version>-win-x64.zip HC360 lab agent (self-contained) + installer, for PCs with serial analysers
      SHA256SUMS.txt                       checksums to verify the downloads on the hospital network
    The release workflow then signs the HC360 Desktop package (tools/release/Sign-HC360Release.ps1), so that workstations
    can update themselves from the Server Node (SRS DED-18).

.EXAMPLE
    .\deploy\windows\Publish-HC360.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $OutputDir,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'artifacts' }
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = $props.SelectSingleNode('//Version').InnerText

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Invoke-Checked([string] $Description, [scriptblock] $Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed (exit code $LASTEXITCODE)." }
}

Push-Location $repo
try {
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    Write-Step "Web apps (version $version)"
    Push-Location (Join-Path $repo 'src\web')
    try {
        Invoke-Checked 'pnpm install' { pnpm install --frozen-lockfile }
        Invoke-Checked 'Type checking' { pnpm -r typecheck }
        if (-not $SkipTests) { Invoke-Checked 'Web tests' { pnpm -r test } }
        Invoke-Checked 'Web build' { pnpm -r --filter './apps/**' build }
    }
    finally { Pop-Location }

    if (-not $SkipTests) {
        Write-Step '.NET tests'
        Invoke-Checked '.NET tests' { dotnet test --solution HyFib.HealthCare360.slnx -c $Configuration }
    }

    Write-Step 'Publishing the server'
    $serverOut = Join-Path $OutputDir 'server'
    Invoke-Checked 'Server publish' {
        dotnet publish src\server\HyFib.HealthCare360.Server\HyFib.HealthCare360.Server.csproj -c $Configuration -r $Runtime --self-contained true -o $serverOut `
            -p:DebugType=None -p:DebugSymbols=false
    }

    Write-Step 'Publishing HC360 Desktop'
    $desktopOut = Join-Path $OutputDir 'desktop'
    Invoke-Checked 'Desktop publish' {
        dotnet publish src\desktop\HyFib.HealthCare360.Desktop\HyFib.HealthCare360.Desktop.csproj -c $Configuration -r $Runtime --self-contained true -o $desktopOut `
            -p:DebugType=None -p:DebugSymbols=false
    }
    Copy-Item (Join-Path $PSScriptRoot 'Install-HC360Desktop.ps1') $desktopOut

    # The updater installs HC360 Desktop's updates as SYSTEM (SRS DED-18): one self-contained file, so it can replace
    # HC360 Desktop's folder from a folder of its own, and itself.
    Write-Step 'Publishing the HC360 Desktop updater'
    $updaterOut = Join-Path $OutputDir 'desktop-updater'
    Invoke-Checked 'Desktop updater publish' {
        dotnet publish src\desktop\HyFib.HealthCare360.DesktopUpdater\HyFib.HealthCare360.DesktopUpdater.csproj -c $Configuration -r $Runtime --self-contained true -o $updaterOut `
            -p:DebugType=None -p:DebugSymbols=false
    }
    Copy-Item (Join-Path $updaterOut 'HC360DesktopUpdater.exe') $desktopOut

    Write-Step 'Publishing the HC360 lab agent'
    $agentOut = Join-Path $OutputDir 'labagent'
    Invoke-Checked 'Lab agent publish' {
        dotnet publish src\agent\HyFib.HealthCare360.LabAgent\HyFib.HealthCare360.LabAgent.csproj -c $Configuration -r $Runtime --self-contained true -o $agentOut `
            -p:DebugType=None -p:DebugSymbols=false
    }
    Copy-Item (Join-Path $PSScriptRoot 'Install-HC360LabAgent.ps1') $agentOut

    Write-Step 'Packaging'
    $deployOut = Join-Path $OutputDir 'deploy'
    New-Item -ItemType Directory -Force -Path $deployOut | Out-Null
    Get-ChildItem $PSScriptRoot -Filter '*.ps1' | Where-Object Name -ne 'Publish-HC360.ps1' | Copy-Item -Destination $deployOut
    Copy-Item (Join-Path $repo 'docs\deployment\DEPLOYMENT.md') $deployOut -ErrorAction SilentlyContinue

    $serverZip = Join-Path $OutputDir "HC360-Server-$version-$Runtime.zip"
    $desktopZip = Join-Path $OutputDir "HC360-Desktop-$version-$Runtime.zip"
    $agentZip = Join-Path $OutputDir "HC360-LabAgent-$version-$Runtime.zip"
    Compress-Archive -Path $serverOut, $deployOut -DestinationPath $serverZip
    Compress-Archive -Path (Join-Path $desktopOut '*') -DestinationPath $desktopZip
    Compress-Archive -Path (Join-Path $agentOut '*') -DestinationPath $agentZip

    Get-ChildItem $OutputDir -Filter '*.zip' | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content (Join-Path $OutputDir 'SHA256SUMS.txt') -Encoding ASCII

    Write-Host ''
    Write-Host "Release $version packaged in $OutputDir" -ForegroundColor Green
}
finally { Pop-Location }
