<#
.SYNOPSIS
    Checks Install-HC360Server.ps1's Windows Firewall rules for the Server Node (SRS DED-14): HC360 is opened on Private
    and Domain networks only, for its own program, on its configured HTTPS ports (7360, and 7361 for the patient portal)
    and mDNS (UDP 5353), and with -LabPorts on the ports network analysers connect to. CI runs it in Windows PowerShell
    5.1 and in PowerShell 7, the two the installer runs in.

.DESCRIPTION
    Takes Set-HC360Firewall from the installer (running it would install HC360) and runs it with stand-ins for the
    firewall cmdlets that only record the rules they are given, so the machine's firewall is left alone and neither the
    NetSecurity module nor administrator rights are needed. It checks each rule's networks, program, protocol and ports;
    that HC360's rules from before are replaced; the analysers' rule, limited to -LabAnalyserAddresses when given and left
    as it is without -LabPorts; the warning about a network marked Public; and that the installer makes its firewall rules
    through the function, with its own ports, unless -SkipFirewall. Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../deploy/windows/Install-HC360Server.ps1')).Path
$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
$definitions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-HC360Firewall' }, $true))
if ($definitions.Count -ne 1) { throw 'Install-HC360Server.ps1 no longer defines Set-HC360Firewall.' }
. ([scriptblock]::Create($definitions[0].Extent.Text))

# Stand-ins for the NetSecurity cmdlets the function uses, which record what it asks for and change nothing: a function
# comes before a cmdlet of the same name, so these are the ones it runs, where the module is installed too.
$script:rules = New-Object System.Collections.Generic.List[hashtable]
$script:replaced = New-Object System.Collections.Generic.List[string]
$script:networks = @()
function Get-NetFirewallRule {
    [CmdletBinding()]
    param([string] $Group)
    [pscustomobject] @{ Group = $Group }
}
function Remove-NetFirewallRule {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline = $true)] $InputObject)
    process { $script:replaced.Add($InputObject.Group) }
}
function New-NetFirewallRule {
    [CmdletBinding()]
    param([string] $DisplayName, [string] $Group, [string] $Direction, [string] $Protocol, [string[]] $LocalPort, [string] $Program,
        [string[]] $Profile, [string] $Action, [string[]] $RemoteAddress)
    $rule = @{}
    foreach ($name in $PSBoundParameters.Keys) { $rule[$name] = $PSBoundParameters[$name] }
    $script:rules.Add($rule)
    [pscustomobject] $rule
}
function Get-NetConnectionProfile { $script:networks }

$program = 'C:\Program Files\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe'

# Runs Set-HC360Firewall afresh and returns what it said (its warnings included).
function Invoke-Firewall([hashtable] $Arguments) {
    $script:rules.Clear()
    $script:replaced.Clear()
    @(Set-HC360Firewall @Arguments 3>&1 6>&1)
}

function Find-Rule([string] $DisplayName) { @($script:rules | Where-Object { $_.DisplayName -eq $DisplayName }) }

function Join-Text($Values) { (@($Values) | ForEach-Object { "$_" }) -join ',' }

Write-Host 'HC360 is opened on its ports, for its own program, on Private and Domain networks only'
$null = Invoke-Firewall @{ HttpsPort = 7360; PortalHttpsPort = 7361; Program = $program }
Assert ((Join-Text $script:replaced) -eq 'HyFib HealthCare360') "HC360's rules from before are removed first, so they are replaced (removed: $(Join-Text $script:replaced))"
Assert ($script:rules.Count -eq 3) "three rules and no more (found: $(Join-Text ($script:rules | ForEach-Object { $_.DisplayName })))"
$expected = @(
    @('HC360 Server (HTTPS)', 'TCP', '7360', 'the staff app'),
    @('HC360 Server (patient portal, HTTPS)', 'TCP', '7361', 'the patient portal'),
    @('HC360 Server discovery (mDNS)', 'UDP', '5353', 'workstations finding the server'))
foreach ($wanted in $expected) {
    $found = @(Find-Rule $wanted[0])
    if ($found.Count -ne 1) { Assert $false "a rule for $($wanted[3]) ('$($wanted[0])')"; continue }
    $rule = $found[0]
    Assert ($rule.Protocol -eq $wanted[1] -and (Join-Text $rule.LocalPort) -eq $wanted[2]) "$($wanted[3]): $($wanted[1]) $($wanted[2]) (found: $($rule.Protocol) $(Join-Text $rule.LocalPort))"
}
foreach ($rule in $script:rules) {
    $name = $rule.DisplayName
    Assert ((Join-Text $rule.Profile) -eq 'Private,Domain') "${name}: Private and Domain networks only, never Public (found: $(Join-Text $rule.Profile))"
    Assert ($rule.Direction -eq 'Inbound' -and $rule.Action -eq 'Allow') "${name}: lets connections in"
    Assert ($rule.Program -eq $program) "${name}: for the HC360 server's program alone (found: $($rule.Program))"
    Assert ($rule.Group -eq 'HyFib HealthCare360') "${name}: in HC360's group, which the next upgrade replaces"
    Assert (-not $rule.ContainsKey('RemoteAddress')) "${name}: from any address on those networks"
}

Write-Host 'The ports the installer was given are the ones opened'
$null = Invoke-Firewall @{ HttpsPort = 8443; PortalHttpsPort = 8444; Program = $program }
Assert ((Join-Text (@(Find-Rule 'HC360 Server (HTTPS)')[0].LocalPort)) -eq '8443') 'the staff app on -HttpsPort'
Assert ((Join-Text (@(Find-Rule 'HC360 Server (patient portal, HTTPS)')[0].LocalPort)) -eq '8444') 'the patient portal on -PortalHttpsPort'
Assert ((Join-Text (@(Find-Rule 'HC360 Server discovery (mDNS)')[0].LocalPort)) -eq '5353') 'mDNS on its own port'

Write-Host 'Without -LabPorts the analysers'' rule is left as it is'
$null = Invoke-Firewall @{ HttpsPort = 7360; PortalHttpsPort = 7361; Program = $program; LabPorts = $null; LabAnalyserAddresses = $null }
Assert ($script:replaced -notcontains 'HyFib HealthCare360 lab analysers') 'their group is not touched'
Assert (@(Find-Rule 'HC360 Server (network lab analysers)').Count -eq 0) 'and no rule is made for them'

Write-Host 'With -LabPorts the analysers'' ports are opened, from their addresses only when they are given'
$said = Invoke-Firewall @{ HttpsPort = 7360; PortalHttpsPort = 7361; Program = $program; LabPorts = @(5100, 5200); LabAnalyserAddresses = @('192.168.1.50', '192.168.1.51') }
$lab = @(Find-Rule 'HC360 Server (network lab analysers)')
Assert ($lab.Count -eq 1) 'one rule for the analysers'
if ($lab.Count -eq 1) {
    $rule = $lab[0]
    Assert ($rule.Protocol -eq 'TCP' -and (Join-Text $rule.LocalPort) -eq '5100,5200') "on their TCP ports (found: $($rule.Protocol) $(Join-Text $rule.LocalPort))"
    Assert ((Join-Text $rule.RemoteAddress) -eq '192.168.1.50,192.168.1.51') "from the analysers' addresses only (found: $(Join-Text $rule.RemoteAddress))"
    Assert ((Join-Text $rule.Profile) -eq 'Private,Domain') 'on Private and Domain networks only'
    Assert ($rule.Program -eq $program -and $rule.Direction -eq 'Inbound' -and $rule.Action -eq 'Allow') 'for the HC360 server''s program, inbound'
    Assert ($rule.Group -eq 'HyFib HealthCare360 lab analysers') 'in a group of their own'
}
Assert ((Join-Text $script:replaced) -eq 'HyFib HealthCare360,HyFib HealthCare360 lab analysers') "the analysers' rule from before is replaced too (removed: $(Join-Text $script:replaced))"
Assert ($script:rules.Count -eq 4) 'and nothing else is opened'
Assert ((Join-Text $said) -match 'Network analysers may connect on port\(s\) 5100, 5200\.') 'it says which ports the analysers may use'

$null = Invoke-Firewall @{ HttpsPort = 7360; PortalHttpsPort = 7361; Program = $program; LabPorts = @(5100) }
Assert (-not @(Find-Rule 'HC360 Server (network lab analysers)')[0].ContainsKey('RemoteAddress')) 'without -LabAnalyserAddresses, from any address on those networks'

Write-Host 'A network marked Public, where workstations cannot connect, is named in a warning'
$script:networks = @(
    [pscustomobject] @{ Name = 'Hospital-Staff'; InterfaceAlias = 'Wi-Fi'; NetworkCategory = 'Public' },
    [pscustomobject] @{ Name = 'Server room'; InterfaceAlias = 'Ethernet'; NetworkCategory = 'Private' })
$said = Invoke-Firewall @{ HttpsPort = 7360; PortalHttpsPort = 7361; Program = $program }
$warnings = @($said | Where-Object { $_ -is [System.Management.Automation.WarningRecord] })
Assert ($warnings.Count -eq 1) "one warning, for the Public network (found $($warnings.Count))"
Assert ($warnings.Count -eq 1 -and "$($warnings[0])" -match "'Hospital-Staff' \(Wi-Fi\) is marked Public" -and
    "$($warnings[0])" -match "Set-NetConnectionProfile -InterfaceAlias 'Wi-Fi' -NetworkCategory Private") 'it names the network and how to mark it Private'
Assert ($script:rules.Count -eq 3 -and @($script:rules | Where-Object { (Join-Text $_.Profile) -ne 'Private,Domain' }).Count -eq 0) 'and the rules still leave Public networks closed'
$script:networks = @()

Write-Host 'The installer makes its firewall rules through Set-HC360Firewall, with its own ports'
$definition = $definitions[0].Extent
$commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
$outside = @($commands | Where-Object { $_.Extent.StartOffset -lt $definition.StartOffset -or $_.Extent.EndOffset -gt $definition.EndOffset })
$calls = @($outside | Where-Object { $_.GetCommandName() -eq 'Set-HC360Firewall' })
Assert ($calls.Count -eq 1) 'it runs Set-HC360Firewall once'
if ($calls.Count -eq 1) {
    $call = $calls[0].Extent.Text
    foreach ($argument in '-HttpsPort $HttpsPort', '-PortalHttpsPort $PortalHttpsPort', '-Program (Join-Path $InstallDir $ExeName)',
        '-LabPorts $LabPorts', '-LabAnalyserAddresses $LabAnalyserAddresses') {
        Assert ($call.Contains($argument)) "it passes $argument"
    }
    $guard = $calls[0].Parent
    while ($guard -and $guard -isnot [System.Management.Automation.Language.IfStatementAst]) { $guard = $guard.Parent }
    Assert ($guard -and $guard.Clauses[0].Item1.Extent.Text -eq '-not $SkipFirewall') 'it leaves the firewall alone with -SkipFirewall'
}
$stray = @($outside | Where-Object { $_.GetCommandName() -match '-Net(Firewall|Connection)' })
Assert ($stray.Count -eq 0) "it changes the firewall nowhere else, where it would not be tested (found: $(Join-Text ($stray | ForEach-Object { $_.GetCommandName() })))"
$parameters = @{}
foreach ($parameter in $ast.ParamBlock.Parameters) { $parameters[$parameter.Name.VariablePath.UserPath] = $parameter }
Assert ("$($parameters['HttpsPort'].DefaultValue)" -eq '7360' -and "$($parameters['PortalHttpsPort'].DefaultValue)" -eq '7361') 'its ports are 7360 and 7361 unless it is given others'

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
