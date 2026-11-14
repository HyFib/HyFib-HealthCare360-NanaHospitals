<#
.SYNOPSIS
    Checks what the restore drill (deploy/windows/Invoke-HC360RestoreDrill.ps1) decides from what it reads: the row
    counts psql prints, the summary 'backup check-documents' prints, and the comparison with the live server's counts.
    CI runs it in Windows PowerShell 5.1 and in PowerShell 7, the two the script runs in.

.DESCRIPTION
    Takes the drill's functions from the script itself (running the script would restore a database) and checks them.
    The summary line is the one DocumentIntegrityCheck.Summary writes, which DocumentIntegrityCheckTests pins on the
    server's side. Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$drill = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Invoke-HC360RestoreDrill.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($drill, [ref] $null, [ref] $null)
foreach ($name in 'ConvertFrom-RowCountLines', 'ConvertFrom-DocumentCheck', 'Compare-RowCounts', 'Test-RowCountsFail') {
    $function = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($function.Count -ne 1) { throw "Invoke-HC360RestoreDrill.ps1 no longer defines $name." }
    . ([scriptblock]::Create($function[0].Extent.Text))
}

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

Write-Host 'Row counts as psql prints them'
$counts = ConvertFrom-RowCountLines @('clinical.encounters|1250', '', 'patients.patients|431', 'platform.stored_files|0')
Assert ($counts.Count -eq 3) 'three tables, the blank line skipped'
Assert ($counts['patients.patients'] -eq 431) 'a count is read as a number'
Assert ($counts['platform.stored_files'] -eq 0) 'an empty table counts 0'
$threw = $false
try { ConvertFrom-RowCountLines @('not a row count') | Out-Null } catch { $threw = $true }
Assert $threw 'a line that is not "table|count" stops the drill'

Write-Host 'The documents check'
$passed = ConvertFrom-DocumentCheck @('some log line', 'Documents checked: 1520; missing: 0; unreadable: 0; not as saved: 0; without a recorded hash: 3.')
Assert ($null -ne $passed) 'the summary is found among other lines'
Assert ($passed.Checked -eq 1520 -and $passed.WithoutHash -eq 3) 'its numbers are read'
Assert ($passed.Ok) 'nothing missing, unreadable or changed passes'
$failed = ConvertFrom-DocumentCheck @('Documents checked: 1520; missing: 2; unreadable: 0; not as saved: 1; without a recorded hash: 0.')
Assert (-not $failed.Ok) 'a missing or changed document fails'
Assert ($null -eq (ConvertFrom-DocumentCheck @('The server could not be reached.'))) 'no summary is no result, not a pass'

Write-Host 'Comparing with the live server'
$reference = [ordered]@{ 'a.same' = 10L; 'a.fewer' = 20L; 'a.more' = 5L; 'a.missing' = 7L }
$restored = [ordered]@{ 'a.same' = 10L; 'a.fewer' = 18L; 'a.more' = 6L; 'a.new' = 1L }
$comparison = @(Compare-RowCounts $reference $restored)
function StatusOf([string] $Table) { return @($comparison | Where-Object { $_.Table -eq $Table })[0].Status }
Assert ((StatusOf 'a.same') -eq 'same') 'the same count is the same'
Assert ((StatusOf 'a.fewer') -eq 'fewer (added after the backup?)') 'fewer rows are reported, as rows added after the backup'
Assert ((StatusOf 'a.more') -eq 'more than the live server') 'more rows than the live server had are flagged'
Assert ((StatusOf 'a.missing') -eq 'missing') 'a table missing is flagged'
Assert ((StatusOf 'a.new') -eq 'not in the reference') 'a table only in the restored database is reported'
Assert (Test-RowCountsFail $comparison) 'a missing table or more rows fails the drill'
Assert (-not (Test-RowCountsFail @(Compare-RowCounts ([ordered]@{ 'a.x' = 3L; 'a.y' = 4L }) ([ordered]@{ 'a.x' = 3L; 'a.y' = 2L })))) 'fewer rows alone do not'

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.'
