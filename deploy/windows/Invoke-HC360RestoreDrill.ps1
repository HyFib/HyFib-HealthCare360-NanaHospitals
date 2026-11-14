#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    The restore drill (DEPLOYMENT.md section 8): restores a backup from one USB disk or NAS folder on a spare machine,
    checks that the hospital's data is all there, times each step, and writes a report for the go-live records.

.DESCRIPTION
    Run it on a clean spare Windows machine where PostgreSQL and HC360 are installed as for new hardware (DEPLOYMENT.md
    section 8, "To restore after a disk failure", step 1), with the USB disk or NAS folder of the backups attached. It
    never touches the live server.

    1. Checks the backup in its folder, every document included ("backup verify").
    2. Restores it with Restore-HC360.ps1, as after a disk failure.
    3. Checks every stored document the restored database records ("backup check-documents"). Its file must be in
       place, decrypt with the restored keys, and hold what was saved: the SHA-256 recorded when it was saved, not the
       backup's own list.
    4. Counts the rows of every table in the restored database.
    5. With -ReferenceFile: compares the counts with those taken on the live server right after that night's backup
       (-ExportReference). A table missing, or with more rows than the live server had, fails the drill. A table with
       fewer is reported: rows added after the backup was made are not in it.
    6. Writes the report (JSON and text) beside it: the backup, each step's time, the documents, the row counts, and
       whether the drill passed. Exit code 0 when it passed, 1 when it did not.

    With -ExportReference, run on the live Server Node right after the night's backup: it only counts the rows of every
    table and writes them to -ReportPath, for the drill to compare with. It changes nothing.

.EXAMPLE
    .\Invoke-HC360RestoreDrill.ps1 -ExportReference -ReportPath E:\drill-reference.json
    On the live server, after the night's backup: the row counts to compare with.

.EXAMPLE
    .\Invoke-HC360RestoreDrill.ps1 -BackupFile E:\HC360-Backups\hc360-20270115-013000-scheduled.hc360bak -ReferenceFile E:\drill-reference.json
    On the spare machine: the drill, from the USB disk.
#>
[CmdletBinding(DefaultParameterSetName = 'Drill')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Drill')] [string] $BackupFile,
    [Parameter(ParameterSetName = 'Drill')] [string] $ReferenceFile,
    [Parameter(Mandatory, ParameterSetName = 'Reference')] [switch] $ExportReference,
    [string] $ReportPath,
    [securestring] $RecoveryKey,
    [securestring] $PostgresSuperuserPassword,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'HyFib\HealthCare360\Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'HyFib\HealthCare360'),
    [string] $DatabaseName = 'hc360',
    [string] $PostgresHost = 'localhost',
    [int] $PostgresPort = 5432
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Every table in the database's own schemas, with its exact row count: one line "schema.table|count" each.
$RowCountQuery = @"
SELECT table_schema || '.' || table_name || '|' ||
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text
FROM information_schema.tables
WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema') AND table_schema NOT LIKE 'pg_toast%'
ORDER BY 1
"@

# Reads psql's "schema.table|count" lines into a table name -> count map (ordered by name).
function ConvertFrom-RowCountLines([string[]] $Lines) {
    $counts = [ordered]@{}
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Trim().Split('|')
        if ($parts.Count -ne 2) { throw "Unexpected row count line: $line" }
        $counts[$parts[0]] = [long] $parts[1]
    }
    return $counts
}

# Reads the summary line 'backup check-documents' prints (DocumentIntegrityCheck.Summary).
function ConvertFrom-DocumentCheck([string[]] $Lines) {
    $pattern = '^Documents checked: (\d+); missing: (\d+); unreadable: (\d+); not as saved: (\d+); without a recorded hash: (\d+)\.$'
    foreach ($line in $Lines) {
        $match = [regex]::Match([string] $line, $pattern)
        if ($match.Success) {
            return [pscustomobject]@{
                Checked     = [long] $match.Groups[1].Value
                Missing     = [long] $match.Groups[2].Value
                Unreadable  = [long] $match.Groups[3].Value
                NotAsSaved  = [long] $match.Groups[4].Value
                WithoutHash = [long] $match.Groups[5].Value
                Ok          = ([long] $match.Groups[2].Value + [long] $match.Groups[3].Value + [long] $match.Groups[4].Value) -eq 0
            }
        }
    }
    return $null
}

# Compares the restored row counts with the live server's. A table missing, or with more rows than the live server had
# after the backup, fails; fewer rows are expected (rows added after the backup), and are reported. Tables only in the
# restored database are reported too (a migration since the reference).
function Compare-RowCounts($Reference, $Restored) {
    $rows = @()
    foreach ($table in $Reference.Keys) {
        if (-not $Restored.Contains($table)) {
            $rows += [pscustomobject]@{ Table = $table; Reference = $Reference[$table]; Restored = $null; Status = 'missing' }
        }
        elseif ($Restored[$table] -gt $Reference[$table]) {
            $rows += [pscustomobject]@{ Table = $table; Reference = $Reference[$table]; Restored = $Restored[$table]; Status = 'more than the live server' }
        }
        elseif ($Restored[$table] -lt $Reference[$table]) {
            $rows += [pscustomobject]@{ Table = $table; Reference = $Reference[$table]; Restored = $Restored[$table]; Status = 'fewer (added after the backup?)' }
        }
        else {
            $rows += [pscustomobject]@{ Table = $table; Reference = $Reference[$table]; Restored = $Restored[$table]; Status = 'same' }
        }
    }

    foreach ($table in $Restored.Keys) {
        if (-not $Reference.Contains($table)) {
            $rows += [pscustomobject]@{ Table = $table; Reference = $null; Restored = $Restored[$table]; Status = 'not in the reference' }
        }
    }

    return $rows
}

# Whether the comparison fails the drill: a table missing, or with more rows than the live server had.
function Test-RowCountsFail($Comparison) {
    return @($Comparison | Where-Object { $_.Status -eq 'missing' -or $_.Status -eq 'more than the live server' }).Count -gt 0
}

$pgBin = Get-ChildItem (Join-Path $env:ProgramFiles 'PostgreSQL') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | ForEach-Object { Join-Path $_.FullName 'bin' } |
    Where-Object { Test-Path (Join-Path $_ 'psql.exe') } | Select-Object -First 1
if (-not $pgBin) { throw 'PostgreSQL client tools were not found.' }
$psql = Join-Path $pgBin 'psql.exe'
$server = Join-Path $InstallDir 'HyFib.HealthCare360.Server.exe'
if (-not $PostgresSuperuserPassword) { $PostgresSuperuserPassword = Read-Host -AsSecureString 'PostgreSQL superuser (postgres) password' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Get-RowCounts {
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    try { $lines = @(& $psql -h $PostgresHost -p $PostgresPort -U postgres -d $DatabaseName -tA -v ON_ERROR_STOP=1 -c $RowCountQuery) }
    finally { Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "psql could not count the rows of $DatabaseName." }
    return ConvertFrom-RowCountLines $lines
}

if ($ExportReference) {
    if (-not $ReportPath) { $ReportPath = Join-Path (Get-Location) "hc360-drill-reference-$stamp.json" }
    $reference = [ordered]@{
        takenAt   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
        machine   = $env:COMPUTERNAME
        database  = $DatabaseName
        rowCounts = (Get-RowCounts)
    }
    $reference | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host "Row counts of $($reference.rowCounts.Count) tables written to $ReportPath. Take it to the restore drill with the backup."
    return
}

if (-not (Test-Path -LiteralPath $BackupFile)) { throw "Backup file not found: $BackupFile" }
if (-not $RecoveryKey) { $RecoveryKey = Read-Host -AsSecureString 'Backup recovery key' }
if (-not $ReportPath) { $ReportPath = Join-Path $DataDir "drills\restore-drill-$stamp.json" }
New-Item -ItemType Directory -Force -Path (Split-Path $ReportPath) | Out-Null

$steps = [ordered]@{}
function Measure-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try { return & $Action }
    finally {
        $watch.Stop()
        $steps[$Name] = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        Write-Host ("    {0:N1} s" -f $watch.Elapsed.TotalSeconds)
    }
}

function Invoke-ServerCli([string[]] $Arguments) {
    $env:HC360_RECOVERY_KEY = [System.Net.NetworkCredential]::new('', $RecoveryKey).Password
    # Only what it prints to standard output is kept (the summary is there); its errors go to the screen. Redirecting them
    # (2>&1) would make Windows PowerShell 5.1 stop the drill at the first line a failing check prints.
    try { $lines = @(& $server @Arguments | ForEach-Object { [string] $_ }) }
    finally { Remove-Item Env:\HC360_RECOVERY_KEY -ErrorAction SilentlyContinue }
    $lines | Out-Host
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = $lines }
}

$failures = @()
$verify = Measure-Step 'Check the backup in its folder' { Invoke-ServerCli @('backup', 'verify', '--in', $BackupFile) }
if ($verify.ExitCode -ne 0) { $failures += "backup verify failed (exit code $($verify.ExitCode))" }

if ($failures.Count -eq 0) {
    try {
        Measure-Step 'Restore it' {
            & (Join-Path $PSScriptRoot 'Restore-HC360.ps1') -BackupFile $BackupFile -RecoveryKey $RecoveryKey -PostgresSuperuserPassword $PostgresSuperuserPassword `
                -InstallDir $InstallDir -DataDir $DataDir -DatabaseName $DatabaseName -PostgresHost $PostgresHost -PostgresPort $PostgresPort -Confirm:$false
        } | Out-Null
    }
    catch {
        $failures += "the restore failed: $($_.Exception.Message)"
    }
}

$documents = $null
$counts = $null
$comparison = @()
if ($failures.Count -eq 0) {
    $checked = Measure-Step 'Check every stored document' { Invoke-ServerCli @('backup', 'check-documents') }
    $documents = ConvertFrom-DocumentCheck $checked.Lines
    if ($null -eq $documents) { $failures += 'backup check-documents gave no summary' }
    elseif (-not $documents.Ok) { $failures += "documents: $($documents.Missing) missing, $($documents.Unreadable) unreadable, $($documents.NotAsSaved) not as saved" }

    $counts = Measure-Step 'Count the rows of every table' { Get-RowCounts }
    if ($ReferenceFile) {
        $reference = Get-Content -LiteralPath $ReferenceFile -Raw | ConvertFrom-Json
        $referenceCounts = [ordered]@{}
        foreach ($property in $reference.rowCounts.PSObject.Properties) { $referenceCounts[$property.Name] = [long] $property.Value }
        $comparison = Compare-RowCounts $referenceCounts $counts
        if (Test-RowCountsFail $comparison) { $failures += 'row counts: a table is missing, or has more rows than the live server had' }
    }
}

$backup = Get-Item -LiteralPath $BackupFile
$report = [ordered]@{
    drillAt      = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    machine      = $env:COMPUTERNAME
    backup       = $backup.FullName
    backupBytes  = $backup.Length
    folderBytes  = (Get-ChildItem -LiteralPath $backup.DirectoryName -Recurse -File | Measure-Object Length -Sum).Sum
    stepsSeconds = $steps
    totalSeconds = [math]::Round(($steps.Values | Measure-Object -Sum).Sum, 1)
    documents    = $documents
    rowCounts    = $counts
    comparedWith = $ReferenceFile
    comparison   = $comparison
    passed       = ($failures.Count -eq 0)
    failures     = $failures
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8

$text = [System.Collections.Generic.List[string]]::new()
$text.Add("HC360 restore drill, $($report.drillAt), on $($report.machine)")
$text.Add("Backup: $($report.backup) ($([math]::Round($report.backupBytes / 1MB, 1)) MB; its folder $([math]::Round($report.folderBytes / 1GB, 2)) GB)")
foreach ($name in $steps.Keys) { $text.Add(("  {0,-34} {1,8:N1} s" -f $name, $steps[$name])) }
$text.Add(("  {0,-34} {1,8:N1} s" -f 'In all', $report.totalSeconds))
if ($documents) {
    $text.Add("Documents: $($documents.Checked) checked; $($documents.Missing) missing; $($documents.Unreadable) unreadable; $($documents.NotAsSaved) not as saved; $($documents.WithoutHash) without a recorded hash")
}
if ($counts) { $text.Add("Tables: $($counts.Count); rows: $(($counts.Values | Measure-Object -Sum).Sum)") }
foreach ($row in @($comparison | Where-Object { $_.Status -ne 'same' })) { $text.Add("  $($row.Table): $($row.Restored) restored, $($row.Reference) on the live server ($($row.Status))") }
$text.Add($(if ($report.passed) { 'PASSED' } else { 'FAILED: ' + ($failures -join '; ') }))
$text | Set-Content -LiteralPath ([System.IO.Path]::ChangeExtension($ReportPath, '.txt')) -Encoding UTF8
$text | Out-Host
Write-Host "Report: $ReportPath (and .txt). Keep it with the go-live records."
if (-not $report.passed) { exit 1 }
