<#
.SYNOPSIS
    Checks the restore marker Restore-HC360.ps1 leaves for HC360's next start (restored.json in the data folder;
    docs/deployment/DEPLOYMENT.md, "After a restore"). CI runs it in Windows PowerShell 5.1 and in PowerShell 7, the two the
    script runs in.

.DESCRIPTION
    Takes Write-RestoreMarker from the script itself (running the script would restore a database) and checks the JSON it
    writes, which the server reads (RestoreMarker), and that the script writes it once the restore has succeeded, before it
    starts HC360. What the server then does is checked by the integration tests (NumberSeriesRestoreTests). Exit code 1
    when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$restore = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Restore-HC360.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($restore, [ref] $null, [ref] $null)
$function = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-RestoreMarker' }, $true))
if ($function.Count -ne 1) { throw 'Restore-HC360.ps1 no longer defines Write-RestoreMarker.' }
. ([scriptblock]::Create($function[0].Extent.Text))

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$folder = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-restore-marker-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
try {
    $path = Join-Path $folder 'restored.json'
    $restoredAt = [datetime]::new(2026, 11, 14, 9, 10, 0, [System.DateTimeKind]::Utc)

    Write-Host 'A backup that says when it was made'
    $printed = @(
        "Scheduled backup of 'hc360' taken 2026-11-13 20:00 UTC by HC360 1.0.0: database, encryption key and 12 documents.",
        'Documents: 12 restored, 0 already present.')
    Write-RestoreMarker $path 'D:\HC360-Backups\hc360-20261113-200000-scheduled.hc360bak' $printed $restoredAt
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.IO.File]::ReadAllText($path)
    Assert ($bytes[0] -ne 0xEF) 'UTF-8 without a byte order mark'
    Assert ($text -match '"restoredAt":\s*"2026-11-14T09:10:00Z"') 'restoredAt: when the restore ended, in UTC'
    Assert ($text -match '"backupTakenAt":\s*"2026-11-13T20:00:00Z"') 'backupTakenAt: when the backup says it was made, in UTC'
    Assert (($text | ConvertFrom-Json).backupFile -eq 'D:\HC360-Backups\hc360-20261113-200000-scheduled.hc360bak') 'backupFile: the backup restored'

    Write-Host 'A backup that does not say'
    Write-RestoreMarker $path 'E:\old.hc360bak' @('This older backup holds the database only: there was nothing else to put back.') $restoredAt
    $text = [System.IO.File]::ReadAllText($path)
    Assert ($text -match '"backupTakenAt":\s*null') 'backupTakenAt is null'
    Assert (($text | ConvertFrom-Json).backupFile -eq 'E:\old.hc360bak') 'the backup is still named'

    Write-Host 'The time of the machine'
    $local = [datetime]::new(2026, 11, 14, 14, 40, 0, [System.DateTimeKind]::Local)
    Write-RestoreMarker $path 'F:\b.hc360bak' @() $local
    $expected = $local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    Assert ([System.IO.File]::ReadAllText($path) -match ('"restoredAt":\s*"' + [regex]::Escape($expected) + '"')) 'a local time is written in UTC'
}
finally {
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'When the script writes it'
$text = Get-Content -LiteralPath $restore -Raw
$keysAt = $text.IndexOf("@('backup', 'restore'")
$markerAt = $text.IndexOf('Write-RestoreMarker (Join-Path $DataDir ''restored.json'')')
$startAt = $text.LastIndexOf("Write-Step 'Starting HC360'")
Assert ($keysAt -gt 0 -and $markerAt -gt $keysAt) 'once the documents and keys are back: the restore has succeeded'
Assert ($startAt -gt $markerAt) 'before HC360 starts, which acts on it'
Assert ($text -match '\$script:CliOutput = \$lines\.ToArray\(\)') 'with what the server CLI printed last, from backup restore'

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
