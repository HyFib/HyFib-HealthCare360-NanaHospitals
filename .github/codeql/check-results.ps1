<#
.SYNOPSIS
    Reports CodeQL findings from a job's SARIF files, and fails on high or critical ones.

.DESCRIPTION
    Run by .github/workflows/codeql.yml after the analysis. While GitHub Code Security is off for the repository, GitHub
    refuses the results, so they are not uploaded and this is the check: every finding is listed in the job summary and
    annotated on its line, and the job fails for a finding of high or critical security severity (7.0 or more), or of
    error level for a query without a security severity. These are the levels at which code scanning fails a pull
    request by default.

    It is also the check when GitHub refuses an upload because CodeQL default setup is on as well.

    With -ReportOnly (Code Security on, results uploaded), findings are only listed: code scanning then decides, and an
    alert dismissed there must not fail the job here.

    A finding suppressed in the source, by a "// codeql[<query id>]" comment on the line just before it with the reason
    beside it, is listed as suppressed and does not fail the job.
#>
param(
    [Parameter(Mandatory)] [string] $SarifDirectory,
    [Parameter(Mandatory)] [string] $Language,
    [switch] $ReportOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Property($Object, [string] $Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name } else { $null }
}

# Workflow commands (annotations) give %, CR and LF a meaning, and, in a property, also : and ,.
function Format-CommandData([string] $Text) { $Text -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A' }
function Format-CommandProperty([string] $Text) { (Format-CommandData $Text) -replace ':', '%3A' -replace ',', '%2C' }

function Get-SeverityLabel([double] $Score, [string] $Level) {
    if ($Score -ge 9.0) { 'critical' }
    elseif ($Score -ge 7.0) { 'high' }
    elseif ($Score -ge 4.0) { 'medium' }
    elseif ($Score -gt 0) { 'low' }
    else { $Level }
}

$files = @(Get-ChildItem -Path $SarifDirectory -Filter '*.sarif' -File -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Write-Output "::error::No CodeQL results (SARIF) in ${SarifDirectory}: the analysis did not finish."
    exit 1
}

$findings = foreach ($file in $files) {
    $sarif = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json -Depth 100
    foreach ($run in @($sarif.runs)) {
        # CodeQL lists its rules in the query packs it ran (tool.extensions); other tools in tool.driver.
        $rules = @{}
        foreach ($component in @($run.tool.driver) + @(Get-Property $run.tool 'extensions')) {
            foreach ($rule in @(Get-Property $component 'rules')) {
                if ($null -ne $rule) { $rules[$rule.id] = $rule }
            }
        }

        foreach ($result in @(Get-Property $run 'results')) {
            if ($null -eq $result) { continue }
            $ruleId = Get-Property $result 'ruleId'
            if (-not $ruleId) { $ruleId = Get-Property (Get-Property $result 'rule') 'id' }
            $rule = $rules[$ruleId]
            $properties = Get-Property $rule 'properties'
            $securitySeverity = Get-Property $properties 'security-severity'
            $score = if ($securitySeverity) { [double]$securitySeverity } else { 0.0 }
            $level = Get-Property $result 'level'
            if (-not $level) { $level = Get-Property (Get-Property $rule 'defaultConfiguration') 'level' }
            if (-not $level) { $level = 'warning' }

            $location = Get-Property (@(Get-Property $result 'locations')[0]) 'physicalLocation'
            $path = Get-Property (Get-Property $location 'artifactLocation') 'uri'
            $line = Get-Property (Get-Property $location 'region') 'startLine'
            # The alert suppression query marks a result a "// codeql[<query id>]" comment suppresses: kind inSource,
            # no status. A suppression another tool rejected or still reviews does not count.
            $suppressed = @(Get-Property $result 'suppressions').Where({ $null -ne $_ -and (Get-Property $_ 'status') -in $null, '', 'accepted' }).Count -gt 0
            $name = Get-Property (Get-Property $rule 'shortDescription') 'text'
            if (-not $name) { $name = $ruleId }

            [pscustomobject]@{
                Severity   = Get-SeverityLabel $score $level
                Score      = $score
                Blocking   = -not $suppressed -and ($score -ge 7.0 -or ($score -eq 0 -and $level -eq 'error'))
                Suppressed = $suppressed
                Rule       = $ruleId
                Name       = $name
                Path       = $path
                Line       = $line
                Message    = ((Get-Property (Get-Property $result 'message') 'text') -replace '\s+', ' ').Trim()
            }
        }
    }
}

$findings = @($findings | Sort-Object -Property @{ Expression = 'Blocking'; Descending = $true }, @{ Expression = 'Score'; Descending = $true }, Path, Line)
$blocking = @($findings.Where({ $_.Blocking }))
$suppressedCount = @($findings.Where({ $_.Suppressed })).Count

foreach ($finding in $findings.Where({ -not $_.Suppressed })) {
    $kind = if ($finding.Blocking -and -not $ReportOnly) { 'error' } else { 'warning' }
    $where = if ($finding.Path) { "file=$(Format-CommandProperty $finding.Path)" + $(if ($finding.Line) { ",line=$($finding.Line)" } else { '' }) + ',' } else { '' }
    Write-Output "::${kind} ${where}title=$(Format-CommandProperty "CodeQL $($finding.Severity): $($finding.Rule)")::$(Format-CommandData $finding.Message)"
}

$heading = if ($findings.Count -eq 0) { "No findings." }
    else { "$($findings.Count) finding(s): $($blocking.Count) high or critical" + $(if ($suppressedCount) { ", $suppressedCount suppressed in the source" } else { '' }) + '.' }
$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("### CodeQL ($Language)")
$summary.Add('')
$summary.Add($heading)
if ($findings.Count -gt 0) {
    $summary.Add('')
    $summary.Add('| Severity | Query | Where | Finding |')
    $summary.Add('|---|---|---|---|')
    foreach ($finding in $findings | Select-Object -First 200) {
        $severity = if ($finding.Suppressed) { "$($finding.Severity) (suppressed)" } else { $finding.Severity }
        $name = $finding.Name -replace '\|', '\|'
        $message = $finding.Message -replace '\|', '\|'
        $summary.Add("| $severity | $name (``$($finding.Rule)``) | ``$($finding.Path):$($finding.Line)`` | $message |")
    }
    if ($findings.Count -gt 200) { $summary.Add("| | | | And $($findings.Count - 200) more. |") }
}

$summary.Add('')
$summary.Add($(if ($ReportOnly) { 'Results are uploaded to Security › Code scanning, which decides the check.' }
    else { 'The results are not uploaded to code scanning, so this job is the check: high and critical findings fail it.' }))

if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
$summary | Write-Output

if ($blocking.Count -gt 0 -and -not $ReportOnly) {
    Write-Output "::error::CodeQL found $($blocking.Count) high or critical finding(s) in $Language. Fix them, or mark one reviewed as a false positive or an accepted risk with a // codeql[<query id>] comment on the line just before it, giving the reason (docs/security/SECURITY.md)."
    exit 1
}
