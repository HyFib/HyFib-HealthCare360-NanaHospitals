#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the version a release is built as, and that the tag that starts the release names it.

.DESCRIPTION
    Publish-HC360.ps1 names the packages after <Version> in Directory.Build.props. Sign-HC360Release.ps1 signs HC360
    Desktop's package only when the program inside reports the same version, and the program takes it from
    <InformationalVersion>. The tag only starts the release workflow. So:
      - <Version> is a release version as HC360 reads one: three numbers, an optional fourth, and an optional
        pre-release label after a dash (1.0.0, 1.3.0-rc.1);
      - <InformationalVersion>, when set, is the same version (build metadata after a plus aside);
      - a tag is "v" followed by exactly that version.
    Without -Tag (a build no tag started, such as a pull request) only the version is checked. Exit code 1, with the
    reasons, when a check fails.

.EXAMPLE
    .\tools\release\Test-ReleaseVersion.ps1 -Tag v1.0.0
#>
[CmdletBinding()]
param(
    # The tag that started the release, such as v1.0.0. Empty when no tag started the build.
    [string] $Tag,
    # Directory.Build.props; by default the repository's.
    [string] $PropsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Here, not as the parameter's default: Windows PowerShell 5.1 leaves $PSScriptRoot empty while it reads the defaults.
if (-not $PropsPath) { $PropsPath = Join-Path $PSScriptRoot '..\..\Directory.Build.props' }

$ReleaseVersionPattern = '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$'

function Get-Setting([xml] $Props, [string] $Name) {
    $node = $Props.SelectSingleNode("//$Name")
    if ($null -eq $node) { return '' }
    return $node.InnerText.Trim()
}

[xml] $props = Get-Content -LiteralPath $PropsPath -Raw
$version = Get-Setting $props 'Version'
$informational = Get-Setting $props 'InformationalVersion'
$problems = New-Object System.Collections.Generic.List[string]

if (-not $version) {
    $problems.Add('Directory.Build.props sets no <Version>.')
}
elseif ($version -notmatch $ReleaseVersionPattern) {
    $problems.Add("<Version> $version is not a release version: three numbers, an optional fourth, and an optional label after a dash (1.0.0, 1.3.0-rc.1).")
}

if ($version -and $informational -and (($informational -split '\+')[0] -cne $version)) {
    $problems.Add("<InformationalVersion> $informational is not <Version> $version. HC360 Desktop would report another version than its package's name, and the package would not be signed.")
}

if ($Tag -and $version -and ($Tag -cne "v$version")) {
    $problems.Add("The tag $Tag does not name the version being built, $version (Directory.Build.props). Tag v$version, or set <Version> and <InformationalVersion> to the version the tag names and tag that commit.")
}

if ($problems.Count -gt 0) {
    foreach ($problem in $problems) { Write-Host "FAIL  $problem" }
    exit 1
}

if ($Tag) { Write-Host "ok    The tag $Tag names the version being built." }
else { Write-Host "ok    $version is a release version. No tag started this build, so there is none to check." }
