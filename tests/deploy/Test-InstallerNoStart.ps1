<#
.SYNOPSIS
    Checks that Install-HC360Server.ps1 -NoStart leaves the HC360 service stopped and not starting with Windows: a node
    of several servers must not start before its own settings (its role, the shared folder, the database) are in place
    (DEPLOYMENT.md, section 10). CI runs it in Windows PowerShell 5.1 and in PowerShell 7, the two the installer runs in.

.DESCRIPTION
    Reads the installer's syntax tree (running it would install HC360): every command that starts or restarts the HC360
    service must be after an if clause on $NoStart, which -NoStart stops at; the service's start mode is worked out for
    each switch; and the steps that need no started service (PostgreSQL's restart when it fails) still run. Exit code 1
    when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Install-HC360Server.ps1')).Path
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $parseErrors)
if ($parseErrors) { throw "Install-HC360Server.ps1 does not parse: $($parseErrors[0].Message)" }

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

# Whether the node runs only when an if clause on $NoStart before it was false: it is in a later clause of that if, or in
# its else.
function Test-AfterNoStart($Node) {
    for ($child = $Node; $null -ne $child.Parent; $child = $child.Parent) {
        $parent = $child.Parent
        if ($parent -isnot [System.Management.Automation.Language.IfStatementAst]) { continue }
        $before = @()
        $inClause = $false
        foreach ($clause in $parent.Clauses) {
            if ([object]::ReferenceEquals($clause.Item2, $child)) { $inClause = $true; break }
            $before += $clause.Item1.Extent.Text
        }
        if (($inClause -or [object]::ReferenceEquals($parent.ElseClause, $child)) -and $before -contains '$NoStart') { return $true }
    }
    return $false
}

# The commands named one of $Names with $Argument among their arguments (wrap the call in @(): one comes back alone).
function Find-Commands([string[]] $Names, [string] $Argument) {
    @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and $Names -contains $node.GetCommandName() -and
            @($node.CommandElements | Where-Object { $_.Extent.Text -eq $Argument }).Count -gt 0
    }, $true))
}

Write-Host 'The switch'
$parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'NoStart' })
Assert ($parameter.Count -eq 1 -and $parameter[0].StaticType -eq [switch]) 'the installer takes -NoStart, a switch'

Write-Host 'Nothing starts the service with -NoStart'
$starts = @(Find-Commands @('Start-Service', 'Restart-Service') '$ServiceName')
Assert ($starts.Count -ge 2) "the installer starts the service in its start-up step and in its database sign-in steps (found $($starts.Count))"
foreach ($start in $starts) {
    Assert (Test-AfterNoStart $start) "line $($start.Extent.StartLineNumber), '$($start.Extent.Text)', runs only without -NoStart"
}

Write-Host 'With -NoStart, Windows does not start it either'
$startMode = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$startMode'
}, $true))
Assert ($startMode.Count -eq 1) 'the installer works out the start mode once'
if ($startMode.Count -eq 1) {
    $mode = [scriptblock]::Create('param([bool] $Standby, [bool] $NoStart) ' + $startMode[0].Right.Extent.Text)
    Assert ((& $mode $false $false) -ceq 'delayed-auto') 'a Server Node starts with Windows'
    Assert ((& $mode $false $true) -ceq 'demand') 'with -NoStart it is started by hand only, so a restart before its settings are in place does not start it'
    Assert ((& $mode $true $false) -ceq 'disabled') 'a warm standby never starts'
}

Write-Host 'What needs no started service is still done'
$postgresRestarts = @(Find-Commands @('Set-RestartOnFailure') '$postgres.Name')
Assert ($postgresRestarts.Count -eq 1 -and -not (Test-AfterNoStart $postgresRestarts[0])) 'Windows starts a PostgreSQL on this machine again when it fails, with -NoStart too'
$readSignIn = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and $node.Extent.Text -like '& $exe database sign-in show*'
}, $true))
Assert ($readSignIn.Count -eq 1 -and -not (Test-AfterNoStart $readSignIn[0])) 'the database sign-in is read with -NoStart too, to find the PostgreSQL on this machine'

if ($script:failures) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
