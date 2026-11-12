<#
.SYNOPSIS
    Checks Test-HC360ServerNode.ps1, the preflight run on a machine before it becomes the Server Node: that it only reads
    the machine, that it grades as docs/deployment/DEPLOYMENT.md and the SRS sizing (section 3.2) say, and that a run on
    this machine writes the report it promises. CI runs it in Windows PowerShell 5.1 and in PowerShell 7.

.DESCRIPTION
    1. Reads the preflight's syntax tree: every command it runs must be one that reads (Get-, Test-, Select-, ...), apart
       from writing its own report to -OutFile, and the only program it starts is "netsh.exe wlan show interfaces". No
       method that writes, deletes or starts anything is called.
    2. Takes its grading functions (running it would read this machine, not a hospital's) and checks them against the
       machine in the photographs that prompted it: a 6-core i5-12400 with 32 GB under Windows 11 Pro, named
       DESKTOP-RMI9PCM, not activated, on Nana Hospitals' cabled LAN.
    3. Runs it here, as the hospital would, and reads the JSON report back.
    Exit code 1 when a check fails.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0
function Assert([bool] $Condition, [string] $Message) {
    if ($Condition) { Write-Host "  ok    $Message" }
    else { Write-Host "  FAIL  $Message" -ForegroundColor Red; $script:failures++ }
}

$preflight = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\deploy\windows\Test-HC360ServerNode.ps1')).Path
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($preflight, [ref] $tokens, [ref] $errors)

Write-Host 'The preflight only reads the machine'
Assert ($errors.Count -eq 0) "it parses ($(($errors | ForEach-Object { $_.Message }) -join '; '))"
$defined = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
$readingVerbs = 'Get', 'Test', 'Select', 'Where', 'ForEach', 'Measure', 'Sort', 'Group', 'ConvertTo', 'Join'
$allowed = 'Set-StrictMode', 'New-Object', 'Format-Table', 'Out-String', 'Write-Host'
$writes = New-Object System.Collections.Generic.List[string]
foreach ($command in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)) {
    $name = $command.GetCommandName()
    $text = $command.Extent.Text
    # Invoke-Section runs each section's own script block; anything else named at run time could be anything.
    if (-not $name) { if ($text -ne '& $Body') { $writes.Add("a command named at run time: $text") }; continue }
    if ($defined -contains $name -or $allowed -contains $name) { continue }
    if ($name -eq 'netsh.exe') {
        if ($text -notmatch '^netsh\.exe wlan show interfaces$') { $writes.Add($text) }
        continue
    }
    if ($name -eq 'Set-Content') {
        if ($text -notmatch '^Set-Content -LiteralPath \$OutFile ') { $writes.Add($text) }
        continue
    }
    $verb = $name.Split('-')[0]
    if ($name.Contains('-') -and $readingVerbs -contains $verb) { continue }
    $writes.Add($text)
}
Assert ($writes.Count -eq 0) "every command reads, apart from its report ($($writes -join ' | '))"
$methods = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true) |
        Where-Object { $_.Member.Extent.Text -match '^(Write|Delete|Create|Move|Copy|Kill|Start|Set|Remove|Open)' } | ForEach-Object { $_.Extent.Text })
Assert ($methods.Count -eq 0) "no method writes, deletes or starts anything ($($methods -join ' | '))"

$names = 'Get-SizingProfile', 'Get-MinimumVerdict', 'Get-DiskMediaVerdict', 'Get-ComputerNameVerdict', 'Get-OsVerdict', 'Get-PowerHistoryVerdict', 'Get-AddressNote',
    'Get-LinkSpeedMbps', 'Get-LinkSpeedVerdict', 'Get-LicenseStatusName', 'Get-ActivationVerdict'
foreach ($name in $names) {
    $found = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $false))
    if ($found.Count -ne 1) { throw "Test-HC360ServerNode.ps1 no longer defines $name." }
    . ([scriptblock]::Create($found[0].Extent.Text))
}

Write-Host 'The SRS sizing profiles (section 3.2)'
Assert ((Get-SizingProfile 25).Name -eq 'Small') 'up to 25 users at once is Small'
Assert ((Get-SizingProfile 26).Name -eq 'Medium') '26 is Medium'
Assert ((Get-SizingProfile 75).Cores -eq 8 -and (Get-SizingProfile 75).MemoryGB -eq 32) 'Medium asks for 8 cores and 32 GB'
Assert ((Get-SizingProfile 150).Name -eq 'Large') '150 is Large'
Assert ((Get-SizingProfile 151).Name -eq 'Beyond Large') 'over 150 is beyond the profiles'

Write-Host 'The machine in the photographs: i5-12400 (6 cores), 32 GB, Windows 11 Pro 25H2, DESKTOP-RMI9PCM'
$small = Get-SizingProfile 25
$medium = Get-SizingProfile 75
$smallest = Get-SizingProfile 1
Assert ((Get-MinimumVerdict 6 $small.Cores $smallest.Cores) -eq 'Pass') 'six cores pass for up to 25 users'
Assert ((Get-MinimumVerdict 6 $medium.Cores $smallest.Cores) -eq 'Warn') 'six cores warn for up to 75 users: the load test decides'
Assert ((Get-MinimumVerdict 4 $medium.Cores $smallest.Cores) -eq 'Fail') 'four cores fail'
Assert ((Get-MinimumVerdict 32 $medium.MemoryGB $smallest.MemoryGB) -eq 'Pass') '32 GB passes for up to 75 users'
Assert ((Get-OsVerdict 'Microsoft Windows 11 Pro' 26200) -eq 'Pass') 'Windows 11 Pro, build 26200, passes'
Assert ((Get-OsVerdict 'Microsoft Windows 11 Home Single Language' 26200) -eq 'Fail') 'Windows 11 Home fails (no BitLocker)'
Assert ((Get-OsVerdict 'Microsoft Windows 10 Pro' 18363) -eq 'Fail') 'a build before 19041 fails, as the installer refuses it'
Assert ((Get-OsVerdict 'Microsoft Windows Server 2022 Standard' 20348) -eq 'Pass') 'Windows Server 2022 passes'
Assert ((Get-ComputerNameVerdict 'DESKTOP-RMI9PCM') -eq 'Warn') "Windows' own name DESKTOP-RMI9PCM is to be changed"
Assert ((Get-ComputerNameVerdict 'WIN-1A2B3C4D5E6') -eq 'Warn') "Windows Server's own name too"
Assert ((Get-ComputerNameVerdict 'HC360-SRV') -eq 'Pass') 'HC360-SRV is kept'

Write-Host 'The disk that holds Windows, PostgreSQL and the data'
Assert ((Get-DiskMediaVerdict 'SSD' 'NVMe') -eq 'Pass') 'an NVMe SSD passes'
Assert ((Get-DiskMediaVerdict 'Unspecified' 'NVMe') -eq 'Pass') 'NVMe passes when Windows does not name the media'
Assert ((Get-DiskMediaVerdict 'SSD' 'SATA') -eq 'Warn') 'a SATA SSD warns'
Assert ((Get-DiskMediaVerdict 'HDD' 'SATA') -eq 'Fail') 'a hard disk fails'
Assert ((Get-DiskMediaVerdict 'Unspecified' 'SAS') -eq 'Unknown') 'an unnamed disk on another bus is unknown'
Assert ((Get-MinimumVerdict 512 $small.DiskGB $smallest.DiskGB) -eq 'Pass') 'a 512 GB disk passes for up to 25 users'
Assert ((Get-MinimumVerdict 512 $medium.DiskGB $smallest.DiskGB) -eq 'Warn') 'and warns for up to 75'
Assert ((Get-MinimumVerdict 1000 $medium.DiskGB $smallest.DiskGB) -eq 'Pass') 'a 1 TB disk passes for up to 75'

Write-Host 'Power: a server stays on'
Assert ((Get-PowerHistoryVerdict 0 2 30) -eq 'Pass') 'two restarts in 30 days pass'
Assert ((Get-PowerHistoryVerdict 1 0 30) -eq 'Warn') 'one power loss warns'
Assert ((Get-PowerHistoryVerdict 0 25 30) -eq 'Warn') 'switched off most nights warns'

Write-Host 'The address the workstations reach'
Assert ((Get-AddressNote 'Enabled' '192.168.1.20' 'AA-BB-CC-DD-EE-FF') -match 'Reserve 192\.168\.1\.20 for MAC AA-BB-CC-DD-EE-FF') 'an address from DHCP is to be reserved for its MAC'
Assert ((Get-AddressNote 'Disabled' '192.168.1.20' 'AA-BB-CC-DD-EE-FF') -match 'fixed address') 'a fixed address is kept out of the DHCP range'
Assert ((Get-AddressNote '' '192.168.1.20' 'AA-BB-CC-DD-EE-FF') -notmatch 'fixed address set') 'an address whose source Windows did not give is not called fixed'

Write-Host 'The network cable every workstation reaches the server over'
Assert ((Get-LinkSpeedMbps '1 Gbps') -eq 1000) '"1 Gbps", as Windows gives it, is 1000 Mbps'
Assert ((Get-LinkSpeedMbps '100 Mbps') -eq 100) '"100 Mbps" is 100'
Assert ((Get-LinkSpeedMbps '2.5 Gbps') -eq 2500) '"2.5 Gbps" is 2500'
Assert ((Get-LinkSpeedMbps '2,5 Gbps') -eq 2500) 'and so is "2,5 Gbps", where a comma is the decimal separator'
Assert ($null -eq (Get-LinkSpeedMbps '')) 'a speed Windows did not give is not read as 0'
Assert ((Get-LinkSpeedVerdict 1000) -eq 'Pass') 'a cable at 1 Gbps passes'
Assert ((Get-LinkSpeedVerdict 2500) -eq 'Pass') 'and one at 2.5 Gbps'
Assert ((Get-LinkSpeedVerdict 100) -eq 'Warn') 'at 100 Mbps it warns: a broken pair, a cable older than Cat5e or a 10/100 switch port'
Assert ((Get-LinkSpeedVerdict 500) -eq 'Warn') 'at 500 Mbps too'
Assert ((Get-LinkSpeedVerdict 10) -eq 'Fail') 'at 10 Mbps it fails'
Assert ((Get-LinkSpeedVerdict $null) -eq 'Unknown') 'a speed not read is unknown, not a pass'

Write-Host 'Windows activation: the server in the photographs shows "Activate Windows"'
Assert ((Get-ActivationVerdict 1) -eq 'Pass') 'activated Windows passes'
Assert ((Get-ActivationVerdict 5) -eq 'Warn') 'Windows not activated, as in the photographs, warns: to be activated before go-live'
Assert ((Get-ActivationVerdict 0) -eq 'Warn') 'unlicensed Windows warns'
Assert ((Get-ActivationVerdict 2) -eq 'Warn') 'a new installation not yet activated warns'
Assert ((Get-LicenseStatusName 1) -eq 'Activated' -and (Get-LicenseStatusName 5) -eq 'Not activated') 'the licence state is said in words'
Assert ((Get-LicenseStatusName 9) -eq 'Licence state 9') 'a state Windows adds later is shown as it is'

Write-Host 'A run on this machine'
$out = Join-Path ([System.IO.Path]::GetTempPath()) ('hc360-preflight-test-{0}.json' -f [guid]::NewGuid().ToString('N'))
$shell = (Get-Process -Id $PID).Path
try {
    & $shell -NoProfile -ExecutionPolicy Bypass -File $preflight -ConcurrentUsers 25 -OutFile $out | Out-Host
    $code = $LASTEXITCODE
    Assert ($code -eq 0 -or $code -eq 1) "it ends with 0 or 1 (it ended with $code)"
    Assert (Test-Path -LiteralPath $out) 'it writes its report to -OutFile'
    if (Test-Path -LiteralPath $out) {
        $report = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
        Assert ($report.ComputerName -eq $env:COMPUTERNAME) 'the report names this machine'
        Assert ($report.SizingProfile -eq 'Small') 'with -ConcurrentUsers 25 it grades against the Small profile'
        $checks = @($report.Checks)
        $verdicts = 'Pass', 'Warn', 'Fail', 'Unknown', 'Info'
        $odd = @($checks | Where-Object { $verdicts -notcontains $_.Verdict } | ForEach-Object { "$($_.Check): $($_.Verdict)" })
        Assert ($odd.Count -eq 0) "every verdict is Pass, Warn, Fail, Unknown or Info ($($odd -join '; '))"
        $graded = @($checks | ForEach-Object { $_.Check })
        foreach ($expected in 'Edition and build', 'Computer name', 'Time zone', 'Pending restart', 'Processor cores', 'Memory', 'System disk type',
            'Free space', 'BitLocker on every fixed disk', 'TPM', 'Remote access', 'Network connection', 'Ports', 'Database engines',
            'Microsoft Edge (PDFs)', 'UPS on USB', 'Power history', 'Activation') {
            Assert ($graded -contains $expected) "it grades $expected"
        }
        $failed = @($checks | Where-Object { $_.Verdict -eq 'Fail' }).Count
        Assert (($code -eq 1) -eq ($failed -gt 0)) "its exit code says whether a check failed ($failed failed, exit code $code)"
        Assert ($report.Summary.Fail -eq $failed) 'its summary counts the failures'
    }
}
finally {
    Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All checks passed.' -ForegroundColor Green
