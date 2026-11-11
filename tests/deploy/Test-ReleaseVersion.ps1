<#
.SYNOPSIS
    Checks tools/release/Test-ReleaseVersion.ps1, which the release workflow runs before it builds: the version in
    Directory.Build.props and the tag that starts the release must agree. CI runs it in Windows PowerShell 5.1 and in
    PowerShell 7; the release workflow runs the script in PowerShell 7.

.DESCRIPTION
    Writes a Directory.Build.props for each case to a folder of its own and runs the script on it in a process of its
    own, as the workflow does, then checks its exit code and what it says. Ends with the repository's own
    Directory.Build.props, which must pass without a tag. Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$checker = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\tools\release\Test-ReleaseVersion.ps1')).Path
$shell = (Get-Process -Id $PID).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-version-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $root | Out-Null

# Runs the checker on a Directory.Build.props with these settings (a setting left $null is left out of the file).
function Invoke-Checker([string] $Version, [string] $Informational, [string] $Tag) {
    $props = Join-Path $root ([guid]::NewGuid().ToString('N') + '.props')
    $settings = ''
    if ($null -ne $Version) { $settings += "    <Version>$Version</Version>`n" }
    if ($null -ne $Informational) { $settings += "    <InformationalVersion>$Informational</InformationalVersion>`n" }
    [System.IO.File]::WriteAllText($props, "<Project>`n  <PropertyGroup>`n$settings  </PropertyGroup>`n</Project>`n")
    $arguments = @('-PropsPath', $props)
    if ($Tag) { $arguments += @('-Tag', $Tag) }
    $ErrorActionPreference = 'Continue'
    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $checker @arguments 2>&1 | ForEach-Object { "$_" } | Out-String
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Test-Case([string] $Name, [string] $Version, [string] $Informational, [string] $Tag, [int] $Expected, [string] $Says) {
    $run = Invoke-Checker $Version $Informational $Tag
    Assert ($run.ExitCode -eq $Expected) "$Name (exit code $($run.ExitCode), expected $Expected)"
    if ($Says) { Assert ($run.Output -like "*$Says*") "  and says: $Says" }
    if ($run.ExitCode -ne $Expected) { Write-Host $run.Output }
}

try {
    Write-Host 'A build no tag started (a pull request)'
    Test-Case 'a release version passes' '1.0.0' '1.0.0' '' 0 'No tag started this build'
    Test-Case 'a pre-release version passes' '1.0.0-rc.1' '1.0.0-rc.1' '' 0
    Test-Case 'a fourth number passes' '1.0.0.1' '1.0.0.1' '' 0
    Test-Case 'no InformationalVersion passes (the SDK takes Version)' '1.2.0' $null '' 0
    Test-Case 'build metadata on InformationalVersion passes' '1.0.0' '1.0.0+abc123' '' 0
    Test-Case 'two numbers fail' '1.0' '1.0' '' 1 'is not a release version'
    Test-Case 'a version with a space fails' '1.0.0 beta' '1.0.0 beta' '' 1 'is not a release version'
    Test-Case 'no Version fails' $null '1.0.0' '' 1 'sets no <Version>'
    Test-Case 'an InformationalVersion that differs fails' '1.0.0' '1.0.1' '' 1 'is not <Version> 1.0.0'

    Write-Host 'A tag started the build'
    Test-Case 'the tag of the version passes' '1.0.0' '1.0.0' 'v1.0.0' 0 'names the version being built'
    Test-Case 'the tag of a pre-release passes' '1.0.0-rc.1' '1.0.0-rc.1' 'v1.0.0-rc.1' 0
    Test-Case 'a tag of the next version fails' '1.0.0' '1.0.0' 'v1.0.1' 1 'does not name the version being built, 1.0.0'
    Test-Case 'a release tag on a pre-release version fails' '1.0.0-rc.1' '1.0.0-rc.1' 'v1.0.0' 1 'does not name'
    Test-Case 'a pre-release tag on a release version fails' '1.0.0' '1.0.0' 'v1.0.0-rc.1' 1 'does not name'
    Test-Case 'a tag without its v fails' '1.0.0' '1.0.0' '1.0.0' 1 'does not name'
    Test-Case 'a capital V fails' '1.0.0' '1.0.0' 'V1.0.0' 1 'does not name'

    Write-Host "The repository's own Directory.Build.props"
    $ErrorActionPreference = 'Continue'
    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $checker 2>&1 | ForEach-Object { "$_" } | Out-String
    Assert ($LASTEXITCODE -eq 0) "passes without a tag (exit code $LASTEXITCODE)"
    if ($LASTEXITCODE -ne 0) { Write-Host $output }
}
finally {
    $ErrorActionPreference = 'Stop'
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) check(s) failed."; exit 1 }
Write-Host 'Every check passed.'
