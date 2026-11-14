#Requires -Version 5.1
<#
.SYNOPSIS
    Measures what HC360's backup space depends on, on the Server Node, and projects it for one and three years
    (docs/design/incremental-backup.md, Appendix A; NANA-THENI-READINESS.md, "How much backup space").

.DESCRIPTION
    Reads only: the database through a read-only session (default_transaction_read_only), and the sizes of files and
    disks. The report holds aggregates only, so it can be sent to HyFib as it is: no file name, no patient, no document
    id, nothing typed by a user.

    1. The stored documents: count and bytes, by content type and category; size percentiles; documents and bytes a
       day over the last 90 days and a month over the last 24; the daily rate from a least-squares fit.
    2. The database: its size now; the size of each backup taken before an update (database only), which shows how the
       dump grows; with -MeasureDump, the real compressed dump (pg_dump -Fc to a temporary file, measured and deleted).
    3. The documents folder against the database: file count and bytes against what the database records, plus each
       file's encryption header (29 bytes, or 37 with a rotated key). A difference means files missing or left over.
    4. The backups: each recorded backup whose file is in the backup folder, with its size as recorded.
    5. The disks: each volume's file system, cluster size, size and free space, and which folders are on it.
    6. Reading speed: a sample of documents read and timed, for the first night's hashing and a restore's read time.
    7. The projections, with the model the growth benchmark checks (BackupGrowthBenchmarkTests): the server's backup
       folder, each of two USB disks swapped weekly, and the off-site bucket, now, in one year and in three, and what the
       backups would take if every backup still held every document.

    Writes hc360-backup-sizing-<machine>-<date>.json and .md in -OutputFolder (the current folder by default).

.EXAMPLE
    .\Measure-HC360BackupSizing.ps1
    .\Measure-HC360BackupSizing.ps1 -MeasureDump -SecondaryDirectory E:\HC360-Backups
#>
[CmdletBinding()]
param(
    [string] $OutputFolder = (Get-Location).Path,
    [switch] $MeasureDump,
    [string] $SecondaryDirectory,
    [int] $RetentionDays = 30,
    [int] $OffsiteRetainDays = 35,
    [int] $ReadSample = 200,
    [securestring] $PostgresSuperuserPassword,
    [string] $DataDir = (Join-Path $env:ProgramData 'HyFib\HealthCare360'),
    [string] $BackupDir,
    [string] $DatabaseName = 'hc360',
    [string] $PostgresHost = 'localhost',
    [int] $PostgresPort = 5432
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# docs/design/incremental-backup.md: a generation lasts 7 days; the document index takes about 60 bytes a document.
$GenerationDays = 7
$IndexBytesPerDocument = 60

# Least squares y = Intercept + Slope * x.
function Get-LinearFit([double[]] $X, [double[]] $Y) {
    $n = $X.Count
    if ($n -lt 2 -or $n -ne $Y.Count) { return [pscustomobject]@{ Slope = 0.0; Intercept = $(if ($n -ge 1) { $Y[0] } else { 0.0 }) } }
    $meanX = ($X | Measure-Object -Average).Average
    $meanY = ($Y | Measure-Object -Average).Average
    $sxy = 0.0
    $sxx = 0.0
    for ($i = 0; $i -lt $n; $i++) {
        $sxy += ($X[$i] - $meanX) * ($Y[$i] - $meanY)
        $sxx += ($X[$i] - $meanX) * ($X[$i] - $meanX)
    }
    if ($sxx -eq 0) { return [pscustomobject]@{ Slope = 0.0; Intercept = $meanY } }
    $slope = $sxy / $sxx
    return [pscustomobject]@{ Slope = $slope; Intercept = $meanY - $slope * $meanX }
}

# The backup space at a time, by the model (docs/design/incremental-backup.md, section 2.2; DEPLOYMENT.md section 8):
#   server folder  (R + 1) x (D + I) + Docs        (+ Docs for 30 days after the data key is rotated)
#   each USB disk  ceil((R + 1) / 2) x (D + I) + Docs  (two disks swapped weekly)
#   bucket         retainDays x (D + I) + ((retainDays + 7) / 7) x Docs
#   before         (R + 1) x (D + Docs)            (every backup holding every document)
function Get-BackupSpace([double] $Dump, [double] $Documents, [double] $DocumentCount, [int] $Retention, [int] $Retain) {
    $kept = $Retention + 1
    $index = $DocumentCount * $IndexBytesPerDocument
    $generations = [math]::Ceiling(($Retain + $GenerationDays) / $GenerationDays)
    return [pscustomobject]@{
        ServerFolder        = $kept * ($Dump + $index) + $Documents
        ServerAfterRotation = $kept * ($Dump + $index) + 2 * $Documents
        UsbDisk             = [math]::Ceiling($kept / 2) * ($Dump + $index) + $Documents
        Bucket              = $Retain * ($Dump + $index) + $generations * $Documents
        Before              = $kept * ($Dump + $Documents)
    }
}

# The measurements carried forward: documents at their daily rate, the dump at its own (or kept as it is).
function Get-SizingProjection($Now, [int[]] $Days, [int] $Retention, [int] $Retain) {
    $rows = @()
    foreach ($day in $Days) {
        $documents = $Now.DocumentBytes + $Now.DocumentBytesPerDay * $day
        $count = $Now.DocumentCount + $Now.DocumentsPerDay * $day
        $dump = [math]::Max($Now.DumpBytes + $Now.DumpBytesPerDay * $day, 0)
        $space = Get-BackupSpace $dump $documents $count $Retention $Retain
        $rows += [pscustomobject]@{
            Days = $day; Documents = $documents; DocumentCount = $count; Dump = $dump
            ServerFolder = $space.ServerFolder; ServerAfterRotation = $space.ServerAfterRotation
            UsbDisk = $space.UsbDisk; Bucket = $space.Bucket; Before = $space.Before
        }
    }
    return $rows
}

# The documents folder against what the database records: each file is its content plus the encryption header.
function Test-DocumentReconciliation([long] $DbCount, [long] $DbBytesWithHeaders, [long] $DiskCount, [long] $DiskBytes) {
    $problems = @()
    if ($DiskCount -lt $DbCount) { $problems += "$($DbCount - $DiskCount) document(s) the database records have no file" }
    if ($DiskCount -gt $DbCount) { $problems += "$($DiskCount - $DbCount) file(s) are not in the database" }
    if ($DiskCount -eq $DbCount -and $DiskBytes -ne $DbBytesWithHeaders) {
        $problems += "the files hold $DiskBytes bytes; the database's sizes and headers come to $DbBytesWithHeaders"
    }
    return [pscustomobject]@{ Matches = ($problems.Count -eq 0); Problems = $problems }
}

function Format-Size([double] $Bytes) {
    if ($Bytes -ge 1TB) { return ('{0:N2} TB' -f ($Bytes / 1TB)) }
    if ($Bytes -ge 1GB) { return ('{0:N1} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N0} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} kB' -f ($Bytes / 1KB))
}

# Reads psql's "a|b|c" lines into objects with the names given.
function ConvertFrom-PsqlRows([string[]] $Lines, [string[]] $Names) {
    $rows = @()
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $values = $line.Split('|')
        if ($values.Count -ne $Names.Count) { throw "Unexpected psql line: $line" }
        $row = [ordered]@{}
        for ($i = 0; $i -lt $Names.Count; $i++) { $row[$Names[$i]] = $values[$i] }
        $rows += [pscustomobject]$row
    }
    return $rows
}

# ---------------------------------------------------------------------------------------------------- measuring

$pgBin = Get-ChildItem (Join-Path $env:ProgramFiles 'PostgreSQL') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | ForEach-Object { Join-Path $_.FullName 'bin' } |
    Where-Object { Test-Path (Join-Path $_ 'psql.exe') } | Select-Object -First 1
if (-not $pgBin) { throw 'PostgreSQL client tools were not found.' }
$psql = Join-Path $pgBin 'psql.exe'
if (-not $PostgresSuperuserPassword) { $PostgresSuperuserPassword = Read-Host -AsSecureString 'PostgreSQL superuser (postgres) password' }
if (-not $BackupDir) { $BackupDir = Join-Path $DataDir 'backups' }
$filesDir = Join-Path $DataDir 'files'

function Invoke-ReadOnlySql([string] $Sql, [string[]] $Names) {
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    try { $lines = @(& $psql -h $PostgresHost -p $PostgresPort -U postgres -d $DatabaseName -tA -v ON_ERROR_STOP=1 -c $Sql) }
    finally { Remove-Item Env:\PGPASSWORD, Env:\PGOPTIONS -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "psql failed on: $Sql" }
    return ConvertFrom-PsqlRows $lines $Names
}

Write-Host '==> The stored documents' -ForegroundColor Cyan
$totals = @(Invoke-ReadOnlySql @"
SELECT count(*), coalesce(sum(size_bytes), 0),
       coalesce(sum(size_bytes + CASE WHEN encryption_key_id IS NULL THEN 29 ELSE 37 END), 0),
       count(*) FILTER (WHERE is_deleted),
       coalesce(percentile_cont(0.5) WITHIN GROUP (ORDER BY size_bytes), 0)::bigint,
       coalesce(percentile_cont(0.9) WITHIN GROUP (ORDER BY size_bytes), 0)::bigint,
       coalesce(percentile_cont(0.99) WITHIN GROUP (ORDER BY size_bytes), 0)::bigint,
       coalesce(max(size_bytes), 0), coalesce(to_char(min(created_at), 'YYYY-MM-DD'), '')
FROM platform.stored_files
"@ @('Count', 'Bytes', 'BytesWithHeaders', 'Deleted', 'P50', 'P90', 'P99', 'Max', 'Since'))[0]
$byType = @(Invoke-ReadOnlySql @"
SELECT content_type, category, count(*), sum(size_bytes) FROM platform.stored_files GROUP BY 1, 2 ORDER BY 4 DESC
"@ @('ContentType', 'Category', 'Count', 'Bytes'))
$daily = @(Invoke-ReadOnlySql @"
SELECT d::date - current_date, coalesce(count(f.id), 0), coalesce(sum(f.size_bytes), 0)
FROM generate_series(current_date - 89, current_date, interval '1 day') d
LEFT JOIN platform.stored_files f ON (f.created_at AT TIME ZONE 'Asia/Kolkata')::date = d::date
GROUP BY d ORDER BY d
"@ @('Day', 'Count', 'Bytes'))
$monthly = @(Invoke-ReadOnlySql @"
SELECT to_char(date_trunc('month', created_at AT TIME ZONE 'Asia/Kolkata'), 'YYYY-MM'), count(*), sum(size_bytes)
FROM platform.stored_files WHERE created_at > now() - interval '24 months' GROUP BY 1 ORDER BY 1
"@ @('Month', 'Count', 'Bytes'))

# The rate from the days since the first document, within the last 90: a new installation's empty days do not count.
$firstDay = if ($totals.Since) { ([datetime]::ParseExact($totals.Since, 'yyyy-MM-dd', $null) - (Get-Date).Date).Days } else { 0 }
$window = @($daily | Where-Object { [int] $_.Day -ge $firstDay })
$days = [double[]] @($window | ForEach-Object { [double] $_.Day })
$bytesFit = Get-LinearFit $days ([double[]] @($window | ForEach-Object { [double] $_.Bytes }))
$bytesPerDay = if ($window.Count -gt 0) { (($window | ForEach-Object { [double] $_.Bytes }) | Measure-Object -Sum).Sum / $window.Count } else { 0 }
$documentsPerDay = if ($window.Count -gt 0) { (($window | ForEach-Object { [double] $_.Count }) | Measure-Object -Sum).Sum / $window.Count } else { 0 }

Write-Host '==> The database' -ForegroundColor Cyan
$databaseBytes = [long] (Invoke-ReadOnlySql 'SELECT pg_database_size(current_database())' @('Bytes'))[0].Bytes
$runs = @(Invoke-ReadOnlySql @"
SELECT to_char(started_at, 'YYYY-MM-DD"T"HH24:MI:SS'), kind, coalesce(size_bytes, 0), coalesce(file_name, ''), coalesce(document_count, -1)
FROM platform.backup_runs WHERE status = 1 ORDER BY started_at
"@ @('StartedAt', 'Kind', 'Bytes', 'File', 'Documents'))

# The backups taken before an update hold the database only, in every archive format: their sizes show the dump.
$databaseOnly = @($runs | Where-Object { $_.Kind -eq '2' })
$dumpBytes = if ($databaseOnly.Count -gt 0) { [double] $databaseOnly[-1].Bytes } else { 0 }
$dumpSource = if ($databaseOnly.Count -gt 0) { "the backup before the update of $($databaseOnly[-1].StartedAt)" } else { 'not known' }
$dumpPerDay = 0.0
if ($databaseOnly.Count -ge 2) {
    $first = [datetime] $databaseOnly[0].StartedAt
    $points = @($databaseOnly | ForEach-Object { [pscustomobject]@{ X = ([datetime] $_.StartedAt - $first).TotalDays; Y = [double] $_.Bytes } })
    if (($points[-1].X - $points[0].X) -ge 30) {
        $dumpPerDay = [math]::Max((Get-LinearFit ([double[]] @($points | ForEach-Object { $_.X })) ([double[]] @($points | ForEach-Object { $_.Y }))).Slope, 0)
    }
}

if ($MeasureDump) {
    Write-Host '==> The compressed dump (pg_dump -Fc, then deleted)' -ForegroundColor Cyan
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("hc360-sizing-" + [guid]::NewGuid().ToString('N') + '.dump')
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $PostgresSuperuserPassword).Password
    try {
        & (Join-Path $pgBin 'pg_dump.exe') -h $PostgresHost -p $PostgresPort -U postgres -Fc -f $temp $DatabaseName
        if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed.' }
        $dumpBytes = (Get-Item -LiteralPath $temp).Length
        $dumpSource = 'pg_dump -Fc today'
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '==> The documents folder against the database' -ForegroundColor Cyan
$files = @(if (Test-Path -LiteralPath $filesDir) { Get-ChildItem -LiteralPath $filesDir -Recurse -File } else { @() })
$diskBytes = [long] (($files | Measure-Object Length -Sum).Sum)
$reconciliation = Test-DocumentReconciliation ([long] $totals.Count) ([long] $totals.BytesWithHeaders) ([long] $files.Count) $diskBytes

Write-Host '==> The backups' -ForegroundColor Cyan
$present = @($runs | Where-Object { $_.File -and (Test-Path -LiteralPath (Join-Path $BackupDir $_.File)) })
$sizeMismatches = @($present | Where-Object { (Get-Item -LiteralPath (Join-Path $BackupDir $_.File)).Length -ne [long] $_.Bytes }).Count
$backupFolderBytes = if (Test-Path -LiteralPath $BackupDir) { [long] ((Get-ChildItem -LiteralPath $BackupDir -Recurse -File | Measure-Object Length -Sum).Sum) } else { 0 }

Write-Host '==> The disks' -ForegroundColor Cyan
$folders = [ordered]@{ Windows = $env:SystemRoot; Data = $DataDir; Backups = $BackupDir }
if ($SecondaryDirectory) { $folders['Second copy'] = $SecondaryDirectory }
$volumes = @()
foreach ($volume in @(Get-Volume | Where-Object { $_.DriveLetter })) {
    $letter = "$($volume.DriveLetter):"
    $on = @($folders.Keys | Where-Object { ([string] $folders[$_]).StartsWith($letter, [System.StringComparison]::OrdinalIgnoreCase) })
    $volumes += [pscustomobject]@{
        Drive = $letter; FileSystem = $volume.FileSystem; ClusterBytes = $volume.AllocationUnitSize
        SizeBytes = $volume.Size; FreeBytes = $volume.SizeRemaining; Holds = ($on -join ', ')
    }
}

Write-Host '==> Reading speed' -ForegroundColor Cyan
$sample = @(if ($files.Count -gt 0) { $files | Get-Random -Count ([math]::Min($ReadSample, $files.Count)) })
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$sampleBytes = 0L
foreach ($file in $sample) { $sampleBytes += ([System.IO.File]::ReadAllBytes($file.FullName)).LongLength }
$watch.Stop()
$readBytesPerSecond = if ($watch.Elapsed.TotalSeconds -gt 0) { $sampleBytes / $watch.Elapsed.TotalSeconds } else { 0 }
$firstNightMinutes = if ($readBytesPerSecond -gt 0) { [math]::Ceiling([double] $totals.Bytes / $readBytesPerSecond / 60) } else { 0 }

# ---------------------------------------------------------------------------------------------------- projecting

$now = [pscustomobject]@{
    DocumentBytes = [double] $totals.Bytes; DocumentCount = [double] $totals.Count
    DocumentBytesPerDay = $bytesPerDay; DocumentsPerDay = $documentsPerDay
    DumpBytes = $dumpBytes; DumpBytesPerDay = $dumpPerDay
}
$projection = @(Get-SizingProjection $now @(0, 365, 1095) $RetentionDays $OffsiteRetainDays)

$warnings = @()
foreach ($volume in $volumes) {
    if ($volume.FileSystem -match '^(exFAT|FAT)' -and $volume.Holds -match 'Second copy|Backups') { $warnings += "$($volume.Drive) is $($volume.FileSystem): format it NTFS." }
}
$backupVolume = @($volumes | Where-Object { $_.Holds -match 'Backups' }) | Select-Object -First 1
if ($backupVolume -and $backupVolume.Holds -match 'Windows|Data') {
    $needed = $projection[2].ServerFolder + $projection[2].Documents + $databaseBytes
    if ($needed -gt $backupVolume.SizeBytes) {
        $warnings += "By year 3 the backups, documents and database ($(Format-Size $needed)) no longer fit on $($backupVolume.Drive) ($(Format-Size $backupVolume.SizeBytes)), which they share: move the backup folder to a disk of its own."
    }
}
if (-not $reconciliation.Matches) { $warnings += 'The documents folder does not match the database: ' + ($reconciliation.Problems -join '; ') + '.' }
if ($sizeMismatches -gt 0) { $warnings += "$sizeMismatches backup file(s) differ in size from what was recorded when they were made." }
if ($dumpBytes -eq 0) { $warnings += 'The dump size is not known (no backup before an update yet): run again with -MeasureDump.' }

$report = [ordered]@{
    measuredAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    machine = $env:COMPUTERNAME
    documents = [ordered]@{
        count = [long] $totals.Count; bytes = [long] $totals.Bytes; deleted = [long] $totals.Deleted; since = $totals.Since
        p50 = [long] $totals.P50; p90 = [long] $totals.P90; p99 = [long] $totals.P99; max = [long] $totals.Max
        perDay = [math]::Round($documentsPerDay, 1); bytesPerDay = [math]::Round($bytesPerDay); bytesPerDayTrend = [math]::Round($bytesFit.Slope, 1)
        byType = $byType; monthly = $monthly
    }
    database = [ordered]@{ bytes = $databaseBytes; dumpBytes = $dumpBytes; dumpFrom = $dumpSource; dumpBytesPerDay = [math]::Round($dumpPerDay) }
    reconciliation = [ordered]@{ files = $files.Count; fileBytes = $diskBytes; expectedBytes = [long] $totals.BytesWithHeaders; matches = $reconciliation.Matches; problems = $reconciliation.Problems }
    backups = [ordered]@{ recorded = $runs.Count; inFolder = $present.Count; sizeMismatches = $sizeMismatches; folderBytes = $backupFolderBytes }
    volumes = $volumes
    readBytesPerSecond = [math]::Round($readBytesPerSecond)
    assumptions = [ordered]@{ retentionDays = $RetentionDays; offsiteRetainDays = $OffsiteRetainDays; generationDays = $GenerationDays; indexBytesPerDocument = $IndexBytesPerDocument }
    projection = $projection
    warnings = $warnings
}

$stamp = Get-Date -Format 'yyyyMMdd'
$base = Join-Path $OutputFolder "hc360-backup-sizing-$($env:COMPUTERNAME)-$stamp"
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$base.json" -Encoding UTF8

$md = [System.Collections.Generic.List[string]]::new()
$md.Add("### Backup sizing measured on $($env:COMPUTERNAME), $($report.measuredAt)")
$md.Add('')
$md.Add("Documents: $($totals.Count) ($(Format-Size $totals.Bytes)) since $($totals.Since); $([math]::Round($documentsPerDay, 1)) a day, $(Format-Size $bytesPerDay) a day over the last $($window.Count) days. Sizes: half under $(Format-Size $totals.P50), 90% under $(Format-Size $totals.P90), 99% under $(Format-Size $totals.P99).")
$md.Add("Database: $(Format-Size $databaseBytes); dump $(Format-Size $dumpBytes) ($dumpSource). Reading speed of the documents disk: $(Format-Size $readBytesPerSecond)/s, so the first night reads every document in about $firstNightMinutes minutes.")
$md.Add('')
$md.Add('| | Now | Year 1 | Year 3 |')
$md.Add('|---|---|---|---|')
foreach ($line in @(
        @('Documents', 'Documents'), @('Server''s backup folder', 'ServerFolder'), @('... in the 30 days after a key rotation', 'ServerAfterRotation'),
        @('Each USB disk (two, swapped weekly)', 'UsbDisk'), @('Off-site bucket', 'Bucket'), @('Before: every document in every backup', 'Before'))) {
    $md.Add("| $($line[0]) | $(Format-Size $projection[0].($line[1])) | $(Format-Size $projection[1].($line[1])) | $(Format-Size $projection[2].($line[1])) |")
}
$md.Add('')
foreach ($volume in $volumes) { $md.Add("- $($volume.Drive) $($volume.FileSystem), clusters of $($volume.ClusterBytes) bytes, $(Format-Size $volume.FreeBytes) free of $(Format-Size $volume.SizeBytes)$(if ($volume.Holds) { ": $($volume.Holds)" })") }
foreach ($warning in $warnings) { $md.Add("- **$warning**") }
$md | Set-Content -LiteralPath "$base.md" -Encoding UTF8
$md | Out-Host
Write-Host "Written: $base.json and $base.md. They hold totals only, no patient or document: send them to HyFib as they are."
