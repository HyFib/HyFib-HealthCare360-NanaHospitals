#Requires -Version 5.1
<#
.SYNOPSIS
    Reads a Windows machine that is to become the HC360 Server Node and says what it lacks, before anything is installed.
    It changes nothing.

.DESCRIPTION
    A photograph of Settings > About shows the processor, the memory and the total storage. It does not show what decides
    whether HC360 runs well and safely on the machine, so this script reads it from Windows:
      - which disk holds Windows (PostgreSQL and HC360's data go there) and whether it is an NVMe SSD, its size, its
        health and wear, and whether there is another disk for backup copies;
      - BitLocker on every fixed disk, and the TPM;
      - the network adapter in use (Ethernet or WiFi, and the WiFi standard), its profile (Private or Public), its address
        and MAC address (for the DHCP reservation);
      - a UPS reporting over USB, and how often in the last 30 days the machine lost power or was shut down or restarted;
      - the computer name, the time zone, a restart Windows is waiting for;
      - the ports HC360 needs, other database engines (the current software's data may be on this machine), remote-access
        tools, Microsoft Edge (PDFs), WebView2 and winget.
    Each is graded against docs/deployment/DEPLOYMENT.md (section 1) and the SRS server sizing (section 3.2) for the number
    of staff using HC360 at once: Pass, Warn, Fail, Unknown (not readable, usually for want of Administrator rights) or
    Info (to know, not graded).

    It writes nothing but its report: a table on screen and a JSON file. The file holds no patient data. It holds the
    machine's name, model, disks, network addresses and MAC address, so send it to HyFib and keep it with the site's
    records, not in a public place.

    Run it from an elevated prompt. Without Administrator rights, BitLocker, the TPM and the disks' wear read as Unknown.

    Exit code 0 when no check failed, 1 when one did. Warn, Unknown and Info do not fail.

.PARAMETER ConcurrentUsers
    The most staff using HC360 at once, at the busiest hour of the day. It chooses the SRS sizing profile: up to 25 users
    Small, up to 75 Medium, up to 150 Large.

.PARAMETER OutFile
    Where to write the JSON report. Default: hc360-preflight-<computer>-<date and time>.json in the current folder.

.PARAMETER HistoryDays
    How many days of the System event log to read for power losses, shutdowns and sleep. Default 30.

.EXAMPLE
    .\Test-HC360ServerNode.ps1 -ConcurrentUsers 40
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 1000)] [int] $ConcurrentUsers = 75,
    [string] $OutFile,
    [ValidateRange(1, 365)] [int] $HistoryDays = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The SRS server sizing (section 3.2) for this many staff at once. Disk sizes are in GB as drives are sold (10^9 bytes),
# a little under the label so that a "1 TB" drive counts as 1 TB. Above 150 users no profile applies: the load test on
# the machine decides (docs/testing/LOAD-TESTS.md).
function Get-SizingProfile([int] $ConcurrentUsers) {
    if ($ConcurrentUsers -le 25) {
        return [pscustomobject]@{ Name = 'Small'; Users = 25; Cores = 6; MemoryGB = 16; DiskGB = 480; DiskLabel = '512 GB NVMe SSD'; Mirrored = $false }
    }
    if ($ConcurrentUsers -le 75) {
        return [pscustomobject]@{ Name = 'Medium'; Users = 75; Cores = 8; MemoryGB = 32; DiskGB = 960; DiskLabel = '1 TB NVMe SSD, mirrored'; Mirrored = $true }
    }
    $name = if ($ConcurrentUsers -le 150) { 'Large' } else { 'Beyond Large' }
    return [pscustomobject]@{ Name = $name; Users = 150; Cores = 12; MemoryGB = 64; DiskGB = 1920; DiskLabel = '2 TB NVMe SSD, RAID-1'; Mirrored = $true }
}

# Pass when $Found reaches $Needed, Warn when it reaches only $Floor (the Small profile's figure), Fail below that.
function Get-MinimumVerdict([double] $Found, [double] $Needed, [double] $Floor) {
    if ($Found -ge $Needed) { return 'Pass' }
    if ($Found -ge $Floor) { return 'Warn' }
    return 'Fail'
}

# PostgreSQL waits for the disk at every commit. A hard disk makes every save in the hospital wait for its platter.
function Get-DiskMediaVerdict([string] $MediaType, [string] $BusType) {
    if ($MediaType -eq 'HDD') { return 'Fail' }
    if ($BusType -eq 'NVMe') { return 'Pass' }
    if ($MediaType -eq 'SSD') { return 'Warn' }
    return 'Unknown'
}

# Windows' own names (DESKTOP-XXXXXXX, WIN-XXXXXXXXXXX) are placeholders. Workstations, HC360's server certificate and
# its mDNS announcement use the name, so it is chosen before HC360 is installed.
function Get-ComputerNameVerdict([string] $Name) {
    if ($Name -match '^(DESKTOP|LAPTOP|WIN)-[A-Z0-9]{7,11}$') { return 'Warn' }
    return 'Pass'
}

# Install-HC360Server.ps1 refuses Windows older than build 19041. Windows Home has no BitLocker (SRS SEC-05).
function Get-OsVerdict([string] $Caption, [int] $Build) {
    if ($Build -lt 19041) { return 'Fail' }
    if ($Caption -match '\bHome\b') { return 'Fail' }
    return 'Pass'
}

# A server stays on: the backup runs at 01:30. Power lost without a shutdown, or shut down or restarted more than about
# once a week, says the machine is not yet run as one.
function Get-PowerHistoryVerdict([int] $PowerLosses, [int] $Shutdowns, [int] $Days) {
    if ($PowerLosses -gt 0) { return 'Warn' }
    if ($Shutdowns -gt [math]::Ceiling($Days / 7)) { return 'Warn' }
    return 'Pass'
}

# What to do so that the address workstations reach stays this machine's. Only "Disabled" means a fixed address: when
# Windows does not say, the address is not called fixed.
function Get-AddressNote([string] $Dhcp, [string] $IPv4, [string] $MacAddress) {
    if ($Dhcp -eq 'Enabled') { return "The address comes from DHCP. Reserve $IPv4 for MAC $MacAddress on the router, so it never changes (this script cannot see whether it is reserved)." }
    if ($Dhcp -eq 'Disabled') { return 'A fixed address set on this machine: keep it out of the router''s DHCP range.' }
    return "Whether the address comes from DHCP could not be read. Keep $IPv4 for this machine: reserve it for MAC $MacAddress on the router, or set it here, outside the router's DHCP range."
}

# A property's value, or $null when the object or the property is missing (strict mode refuses either).
function Get-Value($Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Format-When($Value) {
    if ($Value -is [datetime]) { return $Value.ToString('yyyy-MM-ddTHH:mm:ss') }
    return $null
}

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $Area, [string] $Check, [string] $Found, [string] $Needed, [string] $Verdict, [string] $Note = '') {
    $checks.Add([pscustomobject][ordered]@{ Verdict = $Verdict; Area = $Area; Check = $Check; Found = $Found; Needed = $Needed; Note = $Note })
}

# Runs one section. When Windows will not say, the check is Unknown with the reason, and the next section runs.
function Invoke-Section([string] $Area, [string] $Check, [scriptblock] $Body) {
    try { & $Body }
    catch { Add-Check $Area $Check 'Could not be read' '' 'Unknown' $_.Exception.Message }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$sizing = Get-SizingProfile $ConcurrentUsers
$smallest = Get-SizingProfile 1
$needsAdmin = 'Run this script from an elevated prompt (Run as administrator) to read it.'
$facts = [ordered]@{}

if (-not $isAdmin) {
    Add-Check 'Preflight' 'Run as Administrator' 'No' 'Yes' 'Warn' $needsAdmin
}

Invoke-Section 'Windows' 'Edition and build' {
    $os = Get-CimInstance Win32_OperatingSystem
    $build = [int]$os.BuildNumber
    $current = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $facts.Windows = [ordered]@{
        Caption = $os.Caption
        DisplayVersion = Get-Value $current 'DisplayVersion'
        Build = '{0}.{1}' -f $build, (Get-Value $current 'UBR')
        Architecture = $os.OSArchitecture
        InstalledOn = Format-When $os.InstallDate
        LastBoot = Format-When $os.LastBootUpTime
    }
    Add-Check 'Windows' 'Edition and build' "$($os.Caption) $($facts.Windows.DisplayVersion), build $($facts.Windows.Build)" `
        'Windows 10 2004 or Server 2022 or later, Pro or above (BitLocker)' (Get-OsVerdict $os.Caption $build)
}

Invoke-Section 'Windows' 'Computer name' {
    $system = Get-CimInstance Win32_ComputerSystem
    $facts.Computer = [ordered]@{
        Name = $env:COMPUTERNAME
        Domain = $system.Domain
        PartOfDomain = $system.PartOfDomain
        Manufacturer = $system.Manufacturer
        Model = $system.Model
        Laptop = ($system.PCSystemType -eq 2)
    }
    $verdict = Get-ComputerNameVerdict $env:COMPUTERNAME
    $note = if ($verdict -eq 'Pass') { '' } else { 'Windows chose this name. Rename the computer (Settings > System > About > Rename this PC), e.g. HC360-SRV, and restart, before installing HC360.' }
    Add-Check 'Windows' 'Computer name' $env:COMPUTERNAME 'A fixed name, e.g. HC360-SRV, set before installing' $verdict $note
}

Invoke-Section 'Windows' 'Time zone' {
    $zone = Get-TimeZone
    $verdict = if ($zone.Id -eq 'India Standard Time') { 'Pass' } else { 'Warn' }
    $note = if ($verdict -eq 'Pass') { '' } else { "Set Windows to India Standard Time, so that the times of HC360's scheduled tasks and Windows Update's active hours are IST." }
    Add-Check 'Windows' 'Time zone' $zone.Id 'India Standard Time' $verdict $note
}

Invoke-Section 'Windows' 'Pending restart' {
    $pending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
        (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
    $verdict = if ($pending) { 'Warn' } else { 'Pass' }
    $note = if ($pending) { 'Windows is waiting to restart for updates. Install them and restart before installing HC360.' } else { '' }
    Add-Check 'Windows' 'Pending restart' $(if ($pending) { 'Yes' } else { 'No' }) 'No' $verdict $note
}

Invoke-Section 'Hardware' 'Processor cores' {
    $processors = @(Get-CimInstance Win32_Processor)
    $cores = [int](($processors | Measure-Object -Property NumberOfCores -Sum).Sum)
    $threads = [int](($processors | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum)
    $names = ($processors | ForEach-Object { ([string]$_.Name).Trim() }) -join '; '
    $facts.Processor = [ordered]@{ Name = $names; Cores = $cores; Threads = $threads }
    $verdict = Get-MinimumVerdict $cores $sizing.Cores $smallest.Cores
    $note = if ($verdict -eq 'Warn') { "Enough for the Small profile (up to $($smallest.Users) users at once). Whether it is enough for $ConcurrentUsers is for the load test on this machine to show (docs/testing/LOAD-TESTS.md)." } else { '' }
    Add-Check 'Hardware' 'Processor cores' "$cores cores, $threads threads ($names)" "$($sizing.Cores) cores ($($sizing.Name) profile, $ConcurrentUsers users at once)" $verdict $note
}

Invoke-Section 'Hardware' 'Memory' {
    $installed = [double](@(Get-CimInstance Win32_PhysicalMemory) | Measure-Object -Property Capacity -Sum).Sum
    $usable = [double](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    $gb = if ($installed -gt 0) { [math]::Round($installed / 1GB) } else { [math]::Ceiling($usable / 1GB) }
    $facts.Memory = [ordered]@{ InstalledGB = $gb; UsableGB = [math]::Round($usable / 1GB, 1) }
    Add-Check 'Hardware' 'Memory' "$gb GB installed ($($facts.Memory.UsableGB) GB usable)" "$($sizing.MemoryGB) GB" (Get-MinimumVerdict $gb $sizing.MemoryGB $smallest.MemoryGB)
}

Invoke-Section 'Storage' 'System disk type' {
    $systemDiskNumber = (Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':') | Select-Object -First 1).DiskNumber
    $physical = @(Get-PhysicalDisk)
    $disks = foreach ($disk in @(Get-Disk)) {
        $match = $physical | Where-Object { [string]$_.DeviceId -eq [string]$disk.Number } | Select-Object -First 1
        $wear = $null; $hours = $null; $temperature = $null
        if ($match -and $isAdmin) {
            try {
                $counters = $match | Get-StorageReliabilityCounter
                $wear = Get-Value $counters 'Wear'; $hours = Get-Value $counters 'PowerOnHours'; $temperature = Get-Value $counters 'Temperature'
            }
            catch { }
        }
        [pscustomobject][ordered]@{
            Number = $disk.Number
            Model = $disk.FriendlyName
            BusType = [string]$disk.BusType
            MediaType = if ($match) { [string]$match.MediaType } else { 'Unknown' }
            SizeGB = [math]::Round($disk.Size / 1e9)
            Health = if ($match) { [string]$match.HealthStatus } else { [string]$disk.HealthStatus }
            HoldsWindows = ($disk.Number -eq $systemDiskNumber)
            WearPercent = $wear
            PowerOnHours = $hours
            TemperatureC = $temperature
        }
    }
    $facts.Disks = @($disks)
    $system = $facts.Disks | Where-Object { $_.HoldsWindows } | Select-Object -First 1
    if (-not $system) {
        Add-Check 'Storage' 'System disk type' "No disk found for $env:SystemDrive" 'NVMe SSD' 'Unknown'
        return
    }

    $verdict = Get-DiskMediaVerdict $system.MediaType $system.BusType
    $note = switch ($verdict) {
        'Fail' { "Windows is on a hard disk. PostgreSQL installs under $env:ProgramFiles and HC360's data under $env:ProgramData, on this disk: move Windows to an NVMe SSD (or give the server another machine) before go-live." }
        'Warn' { 'A SATA SSD works, more slowly than NVMe under many users at once. The load test decides.' }
        'Unknown' { 'Windows does not say what this disk is. Look it up by its model.' }
        default { '' }
    }
    Add-Check 'Storage' 'System disk type' "$env:SystemDrive on disk $($system.Number), $($system.Model): $($system.MediaType) on $($system.BusType)" 'NVMe SSD' $verdict $note
    Add-Check 'Storage' 'System disk size' "$($system.SizeGB) GB" $sizing.DiskLabel (Get-MinimumVerdict $system.SizeGB $sizing.DiskGB $smallest.DiskGB)
    if ($sizing.Mirrored) {
        $mirrored = $system.BusType -in 'RAID', 'Spaces'
        $note = if ($mirrored) { '' } else { "The $($sizing.Name) profile asks for a mirror. With one disk, a disk failure means restoring last night's backup on new hardware, or failing over to a warm standby (DEPLOYMENT.md section 9)." }
        Add-Check 'Storage' 'System disk mirrored' $(if ($mirrored) { "Yes ($($system.BusType))" } else { 'No' }) 'A mirror (RAID-1)' $(if ($mirrored) { 'Pass' } else { 'Warn' }) $note
    }

    $others = @($facts.Disks | Where-Object { -not $_.HoldsWindows })
    if ($others.Count -gt 0) {
        $list = ($others | ForEach-Object { "disk $($_.Number), $($_.Model): $($_.MediaType) on $($_.BusType), $($_.SizeGB) GB" }) -join '; '
        Add-Check 'Storage' 'Second disk for backup copies' $list 'A second disk or a NAS share (-SecondaryBackupDir)' 'Pass' `
            'A disk inside the server survives the other disk failing, not a theft or a fire: keep a copy away from it too (a NAS elsewhere, or a USB disk taken off site).'
    }
    else {
        Add-Check 'Storage' 'Second disk for backup copies' 'Only the system disk' 'A second disk or a NAS share (-SecondaryBackupDir)' 'Warn' `
            'Give Install-HC360Server.ps1 -SecondaryBackupDir a NAS share or a USB disk, or the only backups are on the disk they protect.'
    }

    $unhealthy = @($facts.Disks | Where-Object { $_.Health -and $_.Health -ne 'Healthy' })
    $worn = @($facts.Disks | Where-Object { $null -ne $_.WearPercent -and $_.WearPercent -ge 80 })
    $found = ($facts.Disks | ForEach-Object {
            $wear = if ($null -ne $_.WearPercent) { ", $($_.WearPercent)% worn" } else { '' }
            $hours = if ($null -ne $_.PowerOnHours) { ", $($_.PowerOnHours) h powered on" } else { '' }
            "disk $($_.Number): $($_.Health)$wear$hours"
        }) -join '; '
    $verdict = if ($unhealthy.Count -gt 0) { 'Fail' } elseif ($worn.Count -gt 0) { 'Warn' } elseif (-not $isAdmin) { 'Unknown' } else { 'Pass' }
    $note = switch ($verdict) {
        'Fail' { 'Windows reports a disk as not healthy. Replace it before it holds the database or its backups.' }
        'Warn' { 'An SSD at 80% of its rated writes or more: plan its replacement.' }
        'Unknown' { "The health shown is Windows' own; the wear counters need $needsAdmin" }
        default { '' }
    }
    Add-Check 'Storage' 'Disk health' $found 'Healthy, under 80% worn' $verdict $note
}

Invoke-Section 'Storage' 'Free space' {
    $volume = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'"
    $free = [math]::Round($volume.FreeSpace / 1e9)
    $verdict = if ($free -ge 200) { 'Pass' } elseif ($free -ge 100) { 'Warn' } else { 'Fail' }
    Add-Check 'Storage' 'Free space' "$free GB free on $env:SystemDrive" '200 GB (database, documents, 30 nightly backups)' $verdict
}

Invoke-Section 'Security' 'BitLocker on every fixed disk' {
    $needed = 'On, with the TPM, on every fixed disk, before HC360 holds patient data'
    if (-not $isAdmin) { Add-Check 'Security' 'BitLocker on every fixed disk' 'Not read' $needed 'Unknown' $needsAdmin; return }
    if (-not (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue)) {
        Add-Check 'Security' 'BitLocker on every fixed disk' 'BitLocker is not available on this Windows' $needed 'Fail' 'HC360 keeps the keys that open its stored data on this machine (SRS SEC-05): use Windows Pro or above, with BitLocker.'
        return
    }
    # USB disks are left out, as HC360's own daily check leaves them out (DiskEncryption.cs): the backup copies on them
    # are encrypted by HC360, and BitLocker counts a USB disk as removable even where Windows lists it as fixed.
    $usb = @(Get-Disk | Where-Object { [string]$_.BusType -eq 'USB' } | Get-Partition -ErrorAction SilentlyContinue |
            Where-Object { [string]$_.DriveLetter -match '^[A-Z]$' } | ForEach-Object { "$($_.DriveLetter):" })
    $letters = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { $_.DeviceID } | Where-Object { $usb -notcontains $_ })
    $volumes = @(Get-BitLockerVolume -MountPoint $letters)
    $facts.BitLocker = @($volumes | ForEach-Object {
            [pscustomobject][ordered]@{
                Volume = $_.MountPoint
                Protection = [string]$_.ProtectionStatus
                Status = [string]$_.VolumeStatus
                Protectors = (@($_.KeyProtector) | ForEach-Object { [string]$_.KeyProtectorType }) -join ', '
            }
        })
    $off = @($facts.BitLocker | Where-Object { $_.Protection -ne 'On' })
    $found = ($facts.BitLocker | ForEach-Object { "$($_.Volume) $($_.Protection) ($($_.Status))" }) -join '; '
    if ($usb.Count -gt 0) { $found += "; on USB, left out: $($usb -join ', ')" }
    $note = if ($off.Count -eq 0) { '' } else { "Turn BitLocker on, with the TPM, for $(($off | ForEach-Object { $_.Volume }) -join ', '), and keep the recovery keys in the hospital's password safe, not on this machine. A disk that shows FullyEncrypted but Off is waiting for its key to be saved: finish turning it on." }
    Add-Check 'Security' 'BitLocker on every fixed disk' $found $needed $(if ($off.Count -eq 0) { 'Pass' } else { 'Fail' }) $note
}

Invoke-Section 'Security' 'TPM' {
    if (-not $isAdmin) { Add-Check 'Security' 'TPM' 'Not read' 'TPM 2.0, ready' 'Unknown' $needsAdmin; return }
    $tpm = Get-Tpm
    $version = try { [string](Get-CimInstance -Namespace 'root/cimv2/Security/MicrosoftTpm' -ClassName Win32_Tpm).SpecVersion } catch { '' }
    $facts.Tpm = [ordered]@{ Present = [bool]$tpm.TpmPresent; Ready = [bool]$tpm.TpmReady; SpecVersion = $version }
    $verdict = if ($tpm.TpmPresent -and $tpm.TpmReady) { 'Pass' } elseif ($tpm.TpmPresent) { 'Warn' } else { 'Fail' }
    $note = switch ($verdict) {
        'Warn' { 'The TPM is there but not ready: prepare it (tpm.msc) before turning BitLocker on.' }
        'Fail' { 'No TPM: turn it on in the firmware settings (Intel PTT), or BitLocker needs a password at every start.' }
        default { '' }
    }
    Add-Check 'Security' 'TPM' "Present: $($tpm.TpmPresent), ready: $($tpm.TpmReady)$(if ($version) { ", version $($version.Split(',')[0])" })" 'TPM 2.0, ready' $verdict $note
}

Invoke-Section 'Security' 'Remote access' {
    $pattern = 'AnyDesk|TeamViewer|RustDesk|UltraViewer|Splashtop|chromoting|Chrome Remote Desktop|ScreenConnect|RemotePC|AeroAdmin|LogMeIn|Zoho Assist'
    $tools = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $pattern -or $_.DisplayName -match $pattern } |
            ForEach-Object { "$($_.DisplayName) ($($_.Status))" })
    $terminal = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
    $rdp = (Get-Value $terminal 'fDenyTSConnections') -eq 0
    $facts.RemoteAccess = [ordered]@{ Tools = $tools; RemoteDesktop = $rdp }
    $found = @($tools)
    if ($rdp) { $found += 'Remote Desktop is on' }
    if ($found.Count -eq 0) {
        Add-Check 'Security' 'Remote access' 'None found' 'Only what IT uses, known and protected' 'Pass'
        return
    }
    Add-Check 'Security' 'Remote access' ($found -join '; ') 'Only what IT uses, known and protected' 'Warn' `
        "Whoever reaches this machine remotely reaches the keys to the hospital's records. Keep one tool, the one IT uses, with a strong password and two-step sign-in for unattended access, write down who uses it, and remove the rest."
}

Invoke-Section 'Network' 'Network connection' {
    $adapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' })
    if ($adapters.Count -eq 0) {
        $adapters = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' -and -not (Get-Value $_ 'Virtual') })
    }
    $radio = $null; $signal = $null
    try {
        $wlan = @(netsh.exe wlan show interfaces)
        foreach ($line in $wlan) {
            if (-not $radio -and $line -match 'Radio type\s*:\s*(802\.11\S+)') { $radio = $Matches[1] }
            if (-not $signal -and $line -match 'Signal\s*:\s*(\d+)\s*%') { $signal = [int]$Matches[1] }
        }
    }
    catch { }

    $facts.Network = @(foreach ($adapter in $adapters) {
            $physicalMedium = [string](Get-Value $adapter 'PhysicalMediaType')
            $wifi = $physicalMedium -match '802\.11|Wireless' -or $adapter.InterfaceDescription -match 'Wi-?Fi|Wireless|WLAN'
            $category = try { [string](Get-NetConnectionProfile -InterfaceIndex $adapter.ifIndex).NetworkCategory } catch { '' }
            $ipv4 = try { @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 | ForEach-Object { $_.IPAddress }) } catch { @() }
            $dhcp = try { [string](Get-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4).Dhcp } catch { '' }
            [pscustomobject][ordered]@{
                Name = $adapter.Name
                Description = $adapter.InterfaceDescription
                Kind = if ($wifi) { 'WiFi' } else { 'Ethernet' }
                LinkSpeed = [string]$adapter.LinkSpeed
                MacAddress = $adapter.MacAddress
                IPv4 = $ipv4 -join ', '
                Dhcp = $dhcp
                Profile = $category
                WiFiStandard = if ($wifi) { $radio } else { $null }
                SignalPercent = if ($wifi) { $signal } else { $null }
            }
        })

    # Graded: the adapters with an IPv4 address. One that is up without an address carries nothing to the workstations.
    $addressed = @($facts.Network | Where-Object { $_.IPv4 })
    $wired = @($addressed | Where-Object { $_.Kind -eq 'Ethernet' })
    $wireless = @($addressed | Where-Object { $_.Kind -eq 'WiFi' })
    $needed = 'An Ethernet cable to the access point, or WiFi 6 (802.11ax) or later'
    if ($wired.Count -gt 0) {
        Add-Check 'Network' 'Network connection' (($wired | ForEach-Object { "Ethernet ($($_.Name), $($_.LinkSpeed))" }) -join '; ') $needed 'Pass'
    }
    elseif ($wireless.Count -gt 0) {
        $standard = if ($radio) { $radio } else { 'standard not read' }
        $strength = if ($null -ne $signal) { ", signal $signal%" } else { '' }
        $modern = $radio -match '^802\.11(ax|be)$'
        $note = "Every workstation's request passes through this one link. An Ethernet cable to the access point is better (DEPLOYMENT.md section 1)$(if (-not $modern) { '; WiFi 6 (802.11ax) at least' })."
        Add-Check 'Network' 'Network connection' "WiFi only ($standard$strength)" $needed 'Warn' $note
    }
    else {
        Add-Check 'Network' 'Network connection' 'No network adapter is connected with an address' $needed 'Fail'
    }

    $public = @($addressed | Where-Object { $_.Profile -eq 'Public' })
    $profiles = ($addressed | ForEach-Object { "$($_.Name): $(if ($_.Profile) { $_.Profile } else { 'not read' })" }) -join '; '
    if ($addressed.Count -eq 0) {
        Add-Check 'Network' 'Network profile' 'No connected adapter' 'Private' 'Unknown'
    }
    elseif ($public.Count -gt 0) {
        Add-Check 'Network' 'Network profile' $profiles 'Private' 'Fail' `
            "HC360's firewall rules are for Private networks: workstations cannot reach a server on a Public one. Mark it Private: Set-NetConnectionProfile -InterfaceAlias '$($public[0].Name)' -NetworkCategory Private"
    }
    else {
        Add-Check 'Network' 'Network profile' $profiles 'Private' 'Pass'
    }

    foreach ($adapter in $addressed) {
        $dhcp = if ($adapter.Dhcp) { $adapter.Dhcp } else { 'not read' }
        Add-Check 'Network' 'Address' "$($adapter.Name): $($adapter.IPv4), MAC $($adapter.MacAddress), DHCP $dhcp" 'The same address every day' 'Info' `
            (Get-AddressNote $adapter.Dhcp $adapter.IPv4 $adapter.MacAddress)
    }
}

Invoke-Section 'Network' 'Ports' {
    $wanted = @{ 7360 = 'staff app and API'; 7361 = 'patient portal'; 5432 = 'PostgreSQL'; 7362 = 'API reference, if IT turns it on' }
    $listening = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $wanted.ContainsKey([int]$_.LocalPort) } | Sort-Object -Property LocalPort -Unique)
    $lines = @()
    $notes = @()
    $verdict = 'Pass'
    foreach ($connection in $listening) {
        $port = [int]$connection.LocalPort
        $process = try { (Get-Process -Id $connection.OwningProcess).ProcessName } catch { "process $($connection.OwningProcess)" }
        $lines += "$port ($($wanted[$port])): $process"
        if ($process -eq 'HyFib.HealthCare360.Server') {
            if ($verdict -eq 'Pass') { $verdict = 'Info' }
            $notes += 'HC360 is already installed here: run the installer as an upgrade (DEPLOYMENT.md section 7).'
        }
        elseif ($port -eq 5432 -and $process -eq 'postgres') {
            if ($verdict -eq 'Pass') { $verdict = 'Info' }
            $notes += 'A PostgreSQL already runs on 5432. If it belongs to other software, give HC360 one of its own: Install-PostgreSQL.ps1 -Port 5433, then Install-HC360Server.ps1 -PostgresPort 5433.'
        }
        elseif ($port -eq 7362) {
            if ($verdict -in 'Pass', 'Info') { $verdict = 'Warn' }
            $notes += "Port 7362 is used by $($process): choose another port for the API reference if IT turns it on."
        }
        else {
            $verdict = 'Fail'
            $notes += "Port $port is used by $($process). Stop that program, or install HC360 with -HttpsPort or -PortalHttpsPort set to free ports."
        }
    }
    $facts.Ports = $lines
    $found = if ($lines.Count -gt 0) { $lines -join '; ' } else { 'All free' }
    Add-Check 'Network' 'Ports' $found '7360, 7361 and 5432 free (7362 if the API reference is on)' $verdict ($notes -join ' ')
}

Invoke-Section 'Software' 'Database engines' {
    $engines = @(Get-Service -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^(postgresql|MSSQL|SQLAgent|MySQL|MariaDB|OracleService|FirebirdServer|FirebirdGuardian|MongoDB)' } |
            ForEach-Object { "$($_.Name) ($($_.Status))" })
    $facts.DatabaseEngines = $engines
    if ($engines.Count -eq 0) {
        Add-Check 'Software' 'Database engines' 'None' 'Nothing in the way of PostgreSQL' 'Info' "The hospital's current software keeps its data elsewhere. Find where: its item master and its sales are what HC360's item master and the parallel run are checked against."
        return
    }
    Add-Check 'Software' 'Database engines' ($engines -join '; ') 'Nothing in the way of PostgreSQL' 'Info' `
        "The current pharmacy or billing software may keep its data here. Back it up before anything is installed, and leave it running: its item master and its sales are what HC360's item master and the parallel run are checked against."
}

Invoke-Section 'Software' 'Microsoft Edge (PDFs)' {
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($edge) { Add-Check 'Software' 'Microsoft Edge (PDFs)' $edge 'Installed for all users' 'Pass' }
    else { Add-Check 'Software' 'Microsoft Edge (PDFs)' 'Not found' 'Installed for all users' 'Fail' "HC360 makes reports, prescriptions and bills into PDFs with Edge on the server. Install Edge, or set HealthCare360:Pdf:BrowserPath (DEPLOYMENT.md section 1)." }
}

Invoke-Section 'Software' 'WebView2 (HC360 Desktop on this machine)' {
    $keys = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $version = $null
    foreach ($key in $keys) {
        if (-not $version -and (Test-Path -LiteralPath $key)) { $version = Get-Value (Get-ItemProperty -LiteralPath $key) 'pv' }
    }
    $found = if ($version) { "Version $version" } else { 'Not found' }
    Add-Check 'Software' 'WebView2 (HC360 Desktop on this machine)' $found 'Needed only if staff also use HC360 Desktop here' 'Info'
}

Invoke-Section 'Software' 'winget (Install-PostgreSQL.ps1)' {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) { Add-Check 'Software' 'winget (Install-PostgreSQL.ps1)' 'Available' 'Available' 'Pass' }
    else { Add-Check 'Software' 'winget (Install-PostgreSQL.ps1)' 'Not found' 'Available' 'Warn' 'Install-PostgreSQL.ps1 installs PostgreSQL with winget: install "App Installer" from the Microsoft Store, or install PostgreSQL by hand.' }
    $service = Get-Service -Name 'HC360Server' -ErrorAction SilentlyContinue
    if ($service) { Add-Check 'Software' 'HC360 already installed' "HC360Server ($($service.Status))" 'A new installation' 'Info' 'Run the installer as an upgrade (DEPLOYMENT.md section 7).' }
}

Invoke-Section 'Power' 'UPS on USB' {
    $batteries = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
    $laptop = $facts.Contains('Computer') -and [bool]$facts['Computer']['Laptop']
    $facts.Batteries = @($batteries | ForEach-Object { [pscustomobject][ordered]@{ Name = $_.Name; ChargePercent = $_.EstimatedChargeRemaining; Status = [string]$_.Status } })
    if ($batteries.Count -gt 0 -and -not $laptop) {
        Add-Check 'Power' 'UPS on USB' (($facts.Batteries | ForEach-Object { "$($_.Name), $($_.ChargePercent)% charged" }) -join '; ') 'A UPS of 30 minutes or more, on USB' 'Pass'
    }
    elseif ($laptop) {
        Add-Check 'Power' 'UPS on USB' 'A laptop: its own battery' 'A UPS of 30 minutes or more, on USB' 'Warn' 'A laptop is not a server: it is carried away, and its battery wears out. Use a desktop on a UPS.'
    }
    else {
        Add-Check 'Power' 'UPS on USB' 'No UPS reports to Windows' 'A UPS of 30 minutes or more, on USB' 'Warn' `
            "Connect the UPS's USB cable, so that Windows shuts down cleanly before the battery runs out (which protects the database) and System health shows power cuts. A UPS without USB keeps the power on, but nobody hears when it is about to stop."
    }
}

Invoke-Section 'Power' 'Power history' {
    $since = (Get-Date).AddDays(-$HistoryDays)
    $events = @(Get-WinEvent -FilterHashtable @{
                LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power', 'User32', 'EventLog'; Id = 41, 42, 1074, 6008; StartTime = $since
            } -ErrorAction SilentlyContinue)
    $kernelPower = @($events | Where-Object { $_.Id -eq 41 }).Count
    $unexpected = @($events | Where-Object { $_.Id -eq 6008 }).Count
    $losses = [math]::Max($kernelPower, $unexpected)
    $shutdowns = @($events | Where-Object { $_.Id -eq 1074 }).Count
    $sleeps = @($events | Where-Object { $_.Id -eq 42 }).Count
    $facts.PowerHistory = [ordered]@{ Days = $HistoryDays; PowerLosses = $losses; ShutdownsAndRestarts = $shutdowns; Sleeps = $sleeps }
    $verdict = Get-PowerHistoryVerdict $losses $shutdowns $HistoryDays
    $notes = @()
    if ($losses -gt 0) { $notes += "Windows stopped without shutting down $losses time(s): a power cut or a forced restart. On the server that can damage the database; a UPS on USB prevents it." }
    if ($shutdowns -gt [math]::Ceiling($HistoryDays / 7)) { $notes += "Shut down or restarted $shutdowns times: if staff switch it off at night, the 01:30 backup never runs. A server stays on." }
    if ($sleeps -gt 0) { $notes += "Went to sleep $sleeps time(s). Install-HC360Server.ps1 turns sleep off." }
    Add-Check 'Power' 'Power history' "Last $HistoryDays days: $losses power loss(es), $shutdowns shutdown(s) or restart(s), $sleeps sleep(s)" 'No power loss; on day and night' $verdict ($notes -join ' ')
}

$report = [ordered]@{
    Tool = 'Test-HC360ServerNode.ps1'
    ReportVersion = 1
    CollectedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    ComputerName = $env:COMPUTERNAME
    RanAsAdministrator = $isAdmin
    ConcurrentUsers = $ConcurrentUsers
    SizingProfile = $sizing.Name
    Summary = [ordered]@{}
    Facts = $facts
    Checks = $checks.ToArray()
}
foreach ($verdict in 'Pass', 'Warn', 'Fail', 'Unknown', 'Info') {
    $report.Summary[$verdict] = @($checks | Where-Object { $_.Verdict -eq $verdict }).Count
}

if (-not $OutFile) {
    $OutFile = Join-Path (Get-Location).Path ('hc360-preflight-{0}-{1}.json' -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd-HHmm'))
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutFile -Encoding UTF8

$checks | Format-Table -AutoSize -Wrap -Property Verdict, Area, Check, Found, Needed | Out-String -Width 220 | Write-Host
$toDo = @($checks | Where-Object { $_.Verdict -ne 'Pass' -and $_.Note })
if ($toDo.Count -gt 0) {
    Write-Host 'What to do:'
    foreach ($check in $toDo) { Write-Host "  [$($check.Verdict)] $($check.Check): $($check.Note)" }
    Write-Host ''
}
$summary = ($report.Summary.GetEnumerator() | ForEach-Object { "$($_.Value) $($_.Key)" }) -join ', '
Write-Host "$summary. Profile: $($sizing.Name) ($ConcurrentUsers users at once)." -ForegroundColor Cyan
Write-Host "Report: $OutFile (no patient data; it names this machine and its network addresses)." -ForegroundColor Cyan

if ($report.Summary['Fail'] -gt 0) { exit 1 }
exit 0
