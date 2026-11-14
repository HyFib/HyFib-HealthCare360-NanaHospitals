<#
.SYNOPSIS
    Checks the arithmetic of the backup sizing script (deploy/windows/Measure-HC360BackupSizing.ps1): the sizing model, the
    projection, the least-squares rate, and the documents folder's reconciliation with the database. CI runs it in
    Windows PowerShell 5.1 and in PowerShell 7.

.DESCRIPTION
    Takes the script's functions from the script itself (running it would measure this machine) and checks them against
    figures worked out by hand: the year-3 figures in NANA-THENI-READINESS.md, "How much backup space", come from the
    same model. The model itself is checked against real backup runs by BackupGrowthBenchmarkTests. Exit code 1 when a
    check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Measure-HC360BackupSizing.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $null, [ref] $null)
foreach ($name in 'Get-LinearFit', 'Get-BackupSpace', 'Get-SizingProjection', 'Test-DocumentReconciliation', 'Format-Size', 'ConvertFrom-PsqlRows') {
    $function = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($function.Count -ne 1) { throw "Measure-HC360BackupSizing.ps1 no longer defines $name." }
    . ([scriptblock]::Create($function[0].Extent.Text))
}

# The script's constants, which the model functions read.
$text = [System.IO.File]::ReadAllText($path)
$GenerationDays = 7
$IndexBytesPerDocument = 60

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}
function Near([double] $Actual, [double] $Expected, [double] $Tolerance) { return [math]::Abs($Actual - $Expected) -le $Tolerance }

Assert ($text -match '(?m)^\$GenerationDays = 7$') 'the script keeps documents in generations of 7 days, as the design says'
Assert ($text -match '(?m)^\$IndexBytesPerDocument = 60$') 'and counts 60 bytes a document for the index'

Write-Host 'The model, with the year-3 figures of the readiness doc'
$GB = [double] 1GB
$space = Get-BackupSpace (15 * $GB) (274 * $GB) 0 30 35
Assert (Near $space.ServerFolder (31 * 15 * $GB + 274 * $GB) 1) 'server folder: 31 backups of the dump, and the documents once'
Assert (Near $space.ServerAfterRotation (31 * 15 * $GB + 548 * $GB) 1) 'after a key rotation: the documents twice'
Assert (Near $space.UsbDisk (16 * 15 * $GB + 274 * $GB) 1) 'each USB disk: 16 backups (two disks swapped weekly), and the documents once'
Assert (Near $space.Bucket (35 * 15 * $GB + 6 * 274 * $GB) 1) 'bucket: 35 backups and six generations of the documents'
Assert (Near $space.Before (31 * (15 + 274) * $GB) 1) 'before: every backup with every document'
Assert ($space.Before / $space.ServerFolder -gt 10) 'more than ten times the space before'
$indexed = Get-BackupSpace 0 0 1000000 30 35
Assert (Near $indexed.ServerFolder (31 * 60000000) 1) 'the index: 60 bytes a document in each kept backup'

Write-Host 'The projection'
$now = [pscustomobject]@{ DocumentBytes = 0.0; DocumentCount = 0.0; DocumentBytesPerDay = 0.25 * $GB; DocumentsPerDay = 0.0; DumpBytes = 5 * $GB; DumpBytesPerDay = 0.0 }
$rows = @(Get-SizingProjection $now @(0, 365, 1095) 30 35)
Assert ($rows.Count -eq 3) 'now, year 1, year 3'
Assert (Near $rows[1].Documents (365 * 0.25 * $GB) 1) 'year 1: 365 days of documents'
Assert (Near $rows[2].Documents (1095 * 0.25 * $GB) 1) 'year 3: 1095 days of documents'
Assert (Near $rows[2].ServerFolder (31 * 5 * $GB + 1095 * 0.25 * $GB) 1) 'a dump that does not grow stays as it is'
$shrinking = [pscustomobject]@{ DocumentBytes = 0.0; DocumentCount = 0.0; DocumentBytesPerDay = 0.0; DocumentsPerDay = 0.0; DumpBytes = 1 * $GB; DumpBytesPerDay = -1 * $GB }
Assert ((@(Get-SizingProjection $shrinking @(10) 30 35))[0].Dump -eq 0) 'a dump is never projected below nothing'

Write-Host 'The least-squares rate'
$fit = Get-LinearFit ([double[]] @(0, 1, 2, 3)) ([double[]] @(10, 12, 14, 16))
Assert ((Near $fit.Slope 2 1e-9) -and (Near $fit.Intercept 10 1e-9)) 'a straight line is found exactly'
$flat = Get-LinearFit ([double[]] @(5)) ([double[]] @(7))
Assert ($flat.Slope -eq 0 -and $flat.Intercept -eq 7) 'one point: no slope'
$same = Get-LinearFit ([double[]] @(2, 2, 2)) ([double[]] @(1, 2, 3))
Assert ($same.Slope -eq 0 -and (Near $same.Intercept 2 1e-9)) 'points on one day: no slope, their mean'

Write-Host 'The documents folder against the database'
Assert (Test-DocumentReconciliation 2 158 2 158).Matches 'the same files, each its content and its header'
$missing = Test-DocumentReconciliation 3 200 2 158
Assert ((-not $missing.Matches) -and ($missing.Problems[0] -like '1 document(s) the database records have no file')) 'a file missing is named'
$extra = Test-DocumentReconciliation 2 158 3 300
Assert ((-not $extra.Matches) -and ($extra.Problems[0] -like '1 file(s) are not in the database')) 'a file left over is named'
$changed = Test-DocumentReconciliation 2 158 2 160
Assert ((-not $changed.Matches) -and ($changed.Problems[0] -like 'the files hold 160 bytes*')) 'the same count with other sizes is named'

Write-Host 'Reading psql and writing sizes'
$parsed = @(ConvertFrom-PsqlRows @('application/pdf|document|12|3400', '', 'image/jpeg|photo|3|900') @('ContentType', 'Category', 'Count', 'Bytes'))
Assert ($parsed.Count -eq 2 -and $parsed[1].Category -eq 'photo') 'rows are read by name, the blank line skipped'
$threw = $false
try { ConvertFrom-PsqlRows @('a|b') @('One', 'Two', 'Three') | Out-Null } catch { $threw = $true }
Assert $threw 'a row with the wrong number of fields stops the script'
Assert ((Format-Size (2.5 * 1TB)) -eq ('{0:N2} TB' -f 2.5)) 'terabytes'
Assert ((Format-Size (300 * 1MB)) -eq ('{0:N0} MB' -f 300)) 'megabytes'

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
